using System.Text.Json;
using Confluent.Kafka;
using Dapper;
using Npgsql;
using RideSharing.Api.Models.Requests;
using RideSharing.Api.Models.Responses;
using RideSharing.Application.Interfaces;
using RideSharing.Domain.Entities;
using RideSharing.Domain.Events;
using StackExchange.Redis;

namespace RideSharing.Infrastructure.Cache;

// ════════════════════════════════════════════════════════════════════════════
// DRIVER GEO-INDEX SERVICE (Redis)
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Redis geospatial adapter for real-time driver location management.
///
/// Key patterns:
///   drivers (GEOADD geo-set)           — all driver locations
///   driver:{id}:available (string TTL) — availability signal, TTL 30s
///
/// A driver is considered available only if:
///   1. Their availability key exists (TTL not expired), AND
///   2. They appear in the geo-set
/// Drivers who stop pinging automatically expire from the available pool.
/// </summary>
public class DriverGeoIndexService
{
    private readonly IDatabase _db;
    private readonly ILogger<DriverGeoIndexService> _logger;
    private const string GeoKey = "drivers";
    private static readonly TimeSpan AvailabilityTtl = TimeSpan.FromSeconds(30);

    public DriverGeoIndexService(IConnectionMultiplexer redis, ILogger<DriverGeoIndexService> logger)
    {
        _db     = redis.GetDatabase();
        _logger = logger;
    }

    public async Task UpdateLocationAsync(string driverId, double lat, double lng)
    {
        try
        {
            // GEOADD stores (lng, lat) — note the order
            await _db.GeoAddAsync(GeoKey, lng, lat, driverId);
            await _db.StringSetAsync(AvailabilityKey(driverId), "1", AvailabilityTtl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis location update failed for driver {DriverId}", driverId);
        }
    }

    public async Task SetAvailabilityAsync(string driverId, bool available)
    {
        if (available)
            await _db.StringSetAsync(AvailabilityKey(driverId), "1", AvailabilityTtl);
        else
            await _db.KeyDeleteAsync(AvailabilityKey(driverId));
    }

    /// <summary>
    /// Returns candidate driver IDs within radiusKm, sorted by distance ascending.
    /// Filters to only drivers with a valid availability key.
    /// </summary>
    public async Task<IEnumerable<(string DriverId, double DistanceKm)>> FindNearbyAvailableAsync(
        double lat, double lng, double radiusKm)
    {
        try
        {
            var results = await _db.GeoSearchAsync(
                GeoKey,
                lng, lat,
                new GeoSearchCircle(radiusKm, GeoUnit.Kilometers),
                order: Order.Ascending,
                options: GeoRadiusOptions.WithDistance);

            var candidates = new List<(string, double)>();
            foreach (var r in results)
            {
                var driverId = r.Member.ToString();
                // Only include drivers with active availability key
                if (await _db.KeyExistsAsync(AvailabilityKey(driverId)))
                    candidates.Add((driverId, r.Distance ?? double.MaxValue));
            }
            return candidates;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis geo-search failed — returning empty candidates");
            return [];
        }
    }

    private static string AvailabilityKey(string driverId) => $"driver:{driverId}:available";
}

namespace RideSharing.Infrastructure.Persistence;

public class RideRepository
{
    private readonly string _connectionString;

    public RideRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("PostgreSQL")
            ?? throw new InvalidOperationException("PostgreSQL connection string missing.");
    }

    public NpgsqlConnection CreateConnection() => new(_connectionString);

    public async Task<RideRecord?> FindByIdAsync(string rideId)
    {
        using var conn = CreateConnection();
        const string sql = "SELECT * FROM rides WHERE id = @RideId";
        return await conn.QuerySingleOrDefaultAsync<RideRecord>(sql, new { RideId = rideId });
    }

    public async Task InsertRideAsync(RideRecord ride, NpgsqlConnection conn)
    {
        const string sql = """
            INSERT INTO rides
                (id, rider_id, driver_id, status, pickup_lat, pickup_lng,
                 dropoff_lat, dropoff_lng, pickup_address, dropoff_address,
                 currency, requested_at)
            VALUES
                (@Id, @RiderId, @DriverId, @Status::ride_status, @PickupLat, @PickupLng,
                 @DropoffLat, @DropoffLng, @PickupAddress, @DropoffAddress,
                 @Currency, @RequestedAt)
            """;
        await conn.ExecuteAsync(sql, ride);
    }

    public async Task UpdateRideStatusAsync(string rideId, string status,
        string? driverId = null, decimal? fare = null,
        NpgsqlConnection? conn = null)
    {
        using var connection = conn ?? CreateConnection();
        var timestampCol = status switch
        {
            "accepted"    => ", accepted_at = NOW()",
            "in_progress" => ", started_at = NOW()",
            "completed"   => ", completed_at = NOW()",
            "cancelled"   => ", cancelled_at = NOW()",
            _             => string.Empty
        };
        var driverClause = driverId is not null ? ", driver_id = @DriverId" : string.Empty;
        var fareClause   = fare.HasValue ? ", fare = @Fare" : string.Empty;

        var sql = $"""
            UPDATE rides
            SET status = @Status::ride_status, updated_at = NOW()
                {driverClause}{fareClause}{timestampCol}
            WHERE id = @RideId
            """;
        await connection.ExecuteAsync(sql, new { RideId = rideId, Status = status, DriverId = driverId, Fare = fare });
    }

    public async Task InsertRideEventAsync(RideEventRecord evt, NpgsqlConnection conn)
    {
        const string sql = """
            INSERT INTO ride_events (id, ride_id, event_type, payload, occurred_at)
            VALUES (@Id, @RideId, @EventType, @Payload::jsonb, @OccurredAt)
            """;
        await conn.ExecuteAsync(sql, evt);
    }

    public async Task<DriverRecord?> FindDriverByIdAsync(string driverId)
    {
        using var conn = CreateConnection();
        const string sql = """
            SELECT d.id, d.user_id, d.vehicle_id, d.rating, d.is_available,
                   u.name AS driver_name,
                   v.make, v.model, v.plate
            FROM drivers d
            JOIN users u ON u.id = d.user_id
            JOIN vehicles v ON v.id = d.vehicle_id
            WHERE d.id = @DriverId
            """;
        return await conn.QuerySingleOrDefaultAsync<DriverRecord>(sql, new { DriverId = driverId });
    }
}

public record RideRecord
{
    public string Id { get; init; } = string.Empty;
    public string RiderId { get; init; } = string.Empty;
    public string? DriverId { get; init; }
    public string Status { get; init; } = string.Empty;
    public double PickupLat { get; init; }
    public double PickupLng { get; init; }
    public double DropoffLat { get; init; }
    public double DropoffLng { get; init; }
    public string? PickupAddress { get; init; }
    public string? DropoffAddress { get; init; }
    public decimal? Fare { get; init; }
    public string Currency { get; init; } = "ZAR";
    public DateTimeOffset RequestedAt { get; init; }
    public DateTimeOffset? AcceptedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}

public record DriverRecord
{
    public string Id { get; init; } = string.Empty;
    public string UserId { get; init; } = string.Empty;
    public string VehicleId { get; init; } = string.Empty;
    public string DriverName { get; init; } = string.Empty;
    public string Make { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string Plate { get; init; } = string.Empty;
    public decimal Rating { get; init; }
    public bool IsAvailable { get; init; }
}

public record RideEventRecord
{
    public string Id { get; init; } = string.Empty;
    public string RideId { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public string Payload { get; init; } = "{}";
    public DateTimeOffset OccurredAt { get; init; }
}

namespace RideSharing.Infrastructure.Messaging;

public class RideEventPublisher : IDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly ILogger<RideEventPublisher> _logger;
    private const string Topic = "rides.events";

    public RideEventPublisher(IConfiguration configuration, ILogger<RideEventPublisher> logger)
    {
        _logger = logger;
        _producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers  = configuration.GetConnectionString("Kafka") ?? "localhost:9092",
            Acks              = Acks.Leader,
            EnableIdempotence = false
        }).Build();
    }

    public async Task PublishAsync(RideStateChangedEvent evt)
    {
        try
        {
            await _producer.ProduceAsync(Topic, new Message<string, string>
            {
                Key   = evt.RideId,
                Value = JsonSerializer.Serialize(evt)
            });
        }
        catch (ProduceException<string, string> ex)
        {
            _logger.LogError(ex, "Failed to publish ride event {EventType} for ride {RideId}",
                evt.EventType, evt.RideId);
        }
    }

    public void Dispose() => _producer?.Dispose();
}

namespace RideSharing.Application.Services;

// ════════════════════════════════════════════════════════════════════════════
// MATCHING SERVICE
// ════════════════════════════════════════════════════════════════════════════

public class MatchingService : IMatchingService
{
    private readonly DriverGeoIndexService _geoIndex;
    private readonly RideRepository _rideRepo;

    public MatchingService(DriverGeoIndexService geoIndex, RideRepository rideRepo)
    {
        _geoIndex = geoIndex;
        _rideRepo = rideRepo;
    }

    public async Task<IEnumerable<string>> FindCandidatesAsync(double lat, double lng, double radiusKm, CancellationToken ct)
    {
        // Step 1: Query Redis geo-index — start at 2km, expand to 5km if no results
        var candidates = (await _geoIndex.FindNearbyAvailableAsync(lat, lng, radiusKm)).ToList();

        if (candidates.Count == 0 && radiusKm <= 2.0)
            candidates = (await _geoIndex.FindNearbyAvailableAsync(lat, lng, 5.0)).ToList();

        // Step 2 & 3: Already filtered to available + ranked by distance in geo-search
        // Step 4: Return top N candidates (top 3)
        return candidates.Take(3).Select(c => c.DriverId);
    }

    public async Task<IEnumerable<NearbyDriverResponse>> GetNearbyDriversAsync(
        NearbyDriversRequest query, CancellationToken ct)
    {
        var candidates = (await _geoIndex.FindNearbyAvailableAsync(query.Lat, query.Lng, query.Radius)).ToList();

        var results = new List<NearbyDriverResponse>();
        foreach (var (driverId, distanceKm) in candidates.Take(10))
        {
            var driver = await _rideRepo.FindDriverByIdAsync(driverId);
            if (driver is null) continue;

            results.Add(new NearbyDriverResponse
            {
                DriverId    = driverId,
                DistanceKm  = (decimal)distanceKm,
                EtaMinutes  = (int)Math.Ceiling(distanceKm / 0.5), // ~30km/h average city speed
                Vehicle     = new VehicleInfo
                {
                    Make  = driver.Make,
                    Model = driver.Model,
                    Plate = driver.Plate
                }
            });
        }
        return results;
    }
}

// ════════════════════════════════════════════════════════════════════════════
// DRIVER LOCATION SERVICE
// ════════════════════════════════════════════════════════════════════════════

public class DriverLocationService : IDriverLocationService
{
    private readonly DriverGeoIndexService _geoIndex;
    private readonly RideEventPublisher _publisher;
    private readonly ILogger<DriverLocationService> _logger;

    public DriverLocationService(
        DriverGeoIndexService geoIndex,
        RideEventPublisher publisher,
        ILogger<DriverLocationService> logger)
    {
        _geoIndex  = geoIndex;
        _publisher = publisher;
        _logger    = logger;
    }

    public async Task UpdateLocationAsync(
        string authenticatedDriverId,
        string routeDriverId,
        UpdateLocationRequest request,
        CancellationToken ct)
    {
        if (authenticatedDriverId != routeDriverId)
            throw new UnauthorizedAccessException($"Driver {authenticatedDriverId} cannot update location for {routeDriverId}.");

        // Write to Redis GEOADD — overwrites previous location
        // This is the hot path: must be fast, must not block on DB writes
        await _geoIndex.UpdateLocationAsync(routeDriverId, request.Latitude, request.Longitude);

        // DB persistence happens async via Kafka — not on the hot path
        _ = _publisher.PublishAsync(new RideStateChangedEvent
        {
            RideId         = routeDriverId, // reusing field for driver location events
            EventType      = "DriverLocationUpdated",
            PreviousStatus = string.Empty,
            NewStatus      = $"{request.Latitude},{request.Longitude}"
        });
    }
}

// ════════════════════════════════════════════════════════════════════════════
// RIDE SERVICE (Lifecycle state machine)
// ════════════════════════════════════════════════════════════════════════════

public class RideService : IRideService
{
    private readonly RideRepository _rideRepo;
    private readonly MatchingService _matching;
    private readonly DriverGeoIndexService _geoIndex;
    private readonly RideEventPublisher _publisher;
    private readonly ILogger<RideService> _logger;

    // Valid state machine transitions
    private static readonly Dictionary<string, string[]> ValidTransitions = new()
    {
        ["requested"]   = ["matching", "cancelled"],
        ["matching"]    = ["accepted", "cancelled"],
        ["accepted"]    = ["in_progress", "cancelled"],
        ["in_progress"] = ["completed", "cancelled"],
    };

    public RideService(
        RideRepository rideRepo,
        MatchingService matching,
        DriverGeoIndexService geoIndex,
        RideEventPublisher publisher,
        ILogger<RideService> logger)
    {
        _rideRepo  = rideRepo;
        _matching  = matching;
        _geoIndex  = geoIndex;
        _publisher = publisher;
        _logger    = logger;
    }

    public async Task<RideResponse> RequestRideAsync(
        string riderId, RequestRideRequest request, CancellationToken ct)
    {
        var rideId = $"ride_{Guid.NewGuid():N}";
        var ride   = new RideRecord
        {
            Id             = rideId,
            RiderId        = riderId,
            Status         = "requested",
            PickupLat      = request.PickupLat,
            PickupLng      = request.PickupLng,
            DropoffLat     = request.DropoffLat,
            DropoffLng     = request.DropoffLng,
            PickupAddress  = request.PickupAddress,
            DropoffAddress = request.DropoffAddress,
            RequestedAt    = DateTimeOffset.UtcNow
        };

        await using var conn = _rideRepo.CreateConnection();
        await conn.OpenAsync(ct);
        await _rideRepo.InsertRideAsync(ride, conn);
        await _rideRepo.InsertRideEventAsync(BuildEvent(rideId, "RideRequested",
            new { ride.PickupLat, ride.PickupLng }), conn);

        await PublishStateChange(rideId, string.Empty, "requested", null);

        // Kick off matching asynchronously — client polls for status
        _ = Task.Run(() => RunMatchingAsync(rideId, request.PickupLat, request.PickupLng), ct);

        return MapRide(ride);
    }

    private async Task RunMatchingAsync(string rideId, double lat, double lng)
    {
        try
        {
            await _rideRepo.UpdateRideStatusAsync(rideId, "matching");
            var candidates = (await _matching.FindCandidatesAsync(lat, lng, 2.0, CancellationToken.None)).ToList();

            if (!candidates.Any())
            {
                _logger.LogInformation("No drivers found for ride {RideId} — cancelling", rideId);
                await _rideRepo.UpdateRideStatusAsync(rideId, "cancelled");
                return;
            }

            _logger.LogInformation("Found {Count} candidates for ride {RideId}", candidates.Count, rideId);
            // Dispatch notifications to candidates — handled by notification service
            await PublishStateChange(rideId, "requested", "matching", null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Matching failed for ride {RideId}", rideId);
        }
    }

    public async Task<RideAcceptedResponse> AcceptRideAsync(
        string rideId, string driverId, CancellationToken ct)
    {
        var ride = await _rideRepo.FindByIdAsync(rideId)
            ?? throw new RideNotFoundException(rideId);

        if (ride.Status != "matching")
            throw new InvalidRideTransitionException(rideId, ride.Status, "accepted");

        await using var conn = _rideRepo.CreateConnection();
        await conn.OpenAsync(ct);
        await _rideRepo.UpdateRideStatusAsync(rideId, "accepted", driverId: driverId, conn: conn);
        await _rideRepo.InsertRideEventAsync(BuildEvent(rideId, "RideAccepted", new { DriverId = driverId }), conn);

        // Remove driver from available pool
        await _geoIndex.SetAvailabilityAsync(driverId, false);
        await PublishStateChange(rideId, "matching", "accepted", driverId);

        var driver = await _rideRepo.FindDriverByIdAsync(driverId);
        return new RideAcceptedResponse
        {
            RideId     = rideId,
            Status     = "ACCEPTED",
            DriverId   = driverId,
            DriverName = driver?.DriverName ?? string.Empty,
            Vehicle    = new VehicleInfo
            {
                Make  = driver?.Make ?? string.Empty,
                Model = driver?.Model ?? string.Empty,
                Plate = driver?.Plate ?? string.Empty
            },
            EtaMinutes = 4,
            AcceptedAt = DateTimeOffset.UtcNow
        };
    }

    public async Task<RideResponse> StartRideAsync(
        string rideId, string driverId, CancellationToken ct)
    {
        var ride = await _rideRepo.FindByIdAsync(rideId)
            ?? throw new RideNotFoundException(rideId);

        if (ride.Status != "accepted")
            throw new InvalidRideTransitionException(rideId, ride.Status, "in_progress");
        if (ride.DriverId != driverId)
            throw new UnauthorizedAccessException($"Driver {driverId} is not assigned to ride {rideId}.");

        await using var conn = _rideRepo.CreateConnection();
        await conn.OpenAsync(ct);
        await _rideRepo.UpdateRideStatusAsync(rideId, "in_progress", conn: conn);
        await _rideRepo.InsertRideEventAsync(BuildEvent(rideId, "RideStarted", new { }), conn);

        await PublishStateChange(rideId, "accepted", "in_progress", driverId);
        return MapRide(ride with { Status = "in_progress", StartedAt = DateTimeOffset.UtcNow });
    }

    public async Task<RideCompletedResponse> CompleteRideAsync(
        string rideId, string driverId, CancellationToken ct)
    {
        var ride = await _rideRepo.FindByIdAsync(rideId)
            ?? throw new RideNotFoundException(rideId);

        if (ride.Status != "in_progress")
            throw new InvalidRideTransitionException(rideId, ride.Status, "completed");
        if (ride.DriverId != driverId)
            throw new UnauthorizedAccessException($"Driver {driverId} is not assigned to ride {rideId}.");

        // Simple fare calculation: base + distance
        var distanceKm = CalculateDistance(ride.PickupLat, ride.PickupLng, ride.DropoffLat, ride.DropoffLng);
        var fare       = 20m + (decimal)distanceKm * 12m; // ZAR base fare + per km
        var durationMin= (int)Math.Ceiling(distanceKm / 0.5); // ~30km/h

        await using var conn = _rideRepo.CreateConnection();
        await conn.OpenAsync(ct);
        await _rideRepo.UpdateRideStatusAsync(rideId, "completed", fare: fare, conn: conn);
        await _rideRepo.InsertRideEventAsync(BuildEvent(rideId, "RideCompleted",
            new { Fare = fare, DistanceKm = distanceKm }), conn);

        // Driver is available again
        await _geoIndex.SetAvailabilityAsync(driverId, true);
        await PublishStateChange(rideId, "in_progress", "completed", driverId);

        return new RideCompletedResponse
        {
            RideId          = rideId,
            Status          = "COMPLETED",
            Fare            = fare,
            Currency        = "ZAR",
            DistanceKm      = (decimal)distanceKm,
            DurationMinutes = durationMin,
            CompletedAt     = DateTimeOffset.UtcNow
        };
    }

    public async Task<RideResponse> GetRideAsync(
        string rideId, string requestingUserId, CancellationToken ct)
    {
        var ride = await _rideRepo.FindByIdAsync(rideId)
            ?? throw new RideNotFoundException(rideId);

        if (ride.RiderId != requestingUserId && ride.DriverId != requestingUserId)
            throw new UnauthorizedAccessException($"User {requestingUserId} is not a participant in ride {rideId}.");

        return MapRide(ride);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task PublishStateChange(string rideId, string from, string to, string? driverId)
    {
        await _publisher.PublishAsync(new RideStateChangedEvent
        {
            RideId         = rideId,
            PreviousStatus = from,
            NewStatus      = to,
            DriverId       = driverId
        });
    }

    private static RideEventRecord BuildEvent(string rideId, string eventType, object payload) => new()
    {
        Id         = $"rev_{Guid.NewGuid():N}",
        RideId     = rideId,
        EventType  = eventType,
        Payload    = JsonSerializer.Serialize(payload),
        OccurredAt = DateTimeOffset.UtcNow
    };

    /// <summary>Haversine formula for great-circle distance in km.</summary>
    private static double CalculateDistance(double lat1, double lng1, double lat2, double lng2)
    {
        const double R = 6371.0;
        var dLat = ToRad(lat2 - lat1);
        var dLng = ToRad(lng2 - lng1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2))
              * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double ToRad(double deg) => deg * Math.PI / 180;

    private static RideResponse MapRide(RideRecord r) => new()
    {
        RideId      = r.Id,
        Status      = r.Status.ToUpper(),
        RiderId     = r.RiderId,
        DriverId    = r.DriverId,
        PickupLat   = r.PickupLat,
        PickupLng   = r.PickupLng,
        DropoffLat  = r.DropoffLat,
        DropoffLng  = r.DropoffLng,
        RequestedAt = r.RequestedAt,
        AcceptedAt  = r.AcceptedAt,
        StartedAt   = r.StartedAt,
        CompletedAt = r.CompletedAt,
        Fare        = r.Fare
    };
}

// ── Ride exceptions ───────────────────────────────────────────────────────────

public class RideNotFoundException(string id) : Exception($"Ride '{id}' not found.");
public class InvalidRideTransitionException(string id, string from, string to)
    : Exception($"Cannot transition ride '{id}' from '{from}' to '{to}'.");
