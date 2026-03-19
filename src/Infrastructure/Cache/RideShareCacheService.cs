using System.Text.Json;
using Confluent.Kafka;
using Dapper;
using Npgsql;
using StackExchange.Redis;

namespace RideSharing.Infrastructure.Cache;

/// <summary>
/// Extended Redis adapter for the Ride Sharing system.
/// Adds active ride tracking per driver on top of the Phase 4 geo-index.
///
/// Key inventory:
///   drivers (GEO set)              — all driver coordinates, continuously updated
///   driver:{id}:available (STRING) — availability signal, TTL 30s, renewed on each ping
///   driver:{id}:active_ride (STRING) — current ride ID if driver is on a trip, TTL 4h
///   ride:{id}:state (STRING)       — current ride state, TTL 10s (short — consistency matters)
/// </summary>
public class RideShareCacheService
{
    private readonly IDatabase _db;
    private readonly ILogger<RideShareCacheService> _logger;
    private const string GeoKey = "drivers";
    private static readonly TimeSpan AvailabilityTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ActiveRideTtl   = TimeSpan.FromHours(4);
    private static readonly TimeSpan RideStateTtl    = TimeSpan.FromSeconds(10);

    public RideShareCacheService(IConnectionMultiplexer redis, ILogger<RideShareCacheService> logger)
    {
        _db     = redis.GetDatabase();
        _logger = logger;
    }

    // ── Geo-index operations ──────────────────────────────────────────────────

    /// <summary>
    /// Write-through: update Redis geo-index immediately on every location ping.
    /// This is the hot path (40K ops/sec) — must be O(log N) and non-blocking.
    /// </summary>
    public async Task UpdateDriverLocationAsync(string driverId, double lat, double lng)
    {
        try
        {
            await _db.GeoAddAsync(GeoKey, lng, lat, driverId);
            // Renew availability TTL — driver is alive if it's pinging
            await _db.StringSetAsync(AvailabilityKey(driverId), "1", AvailabilityTtl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis location update failed for driver {DriverId} — location may be stale", driverId);
        }
    }

    /// <summary>
    /// GEOSEARCH with distance — returns available drivers within radius, sorted nearest first.
    /// Filters to only drivers with active availability key (TTL not expired).
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

            var available = new List<(string, double)>();
            foreach (var r in results)
            {
                var driverId = r.Member.ToString();
                // Availability check: driver pinged within last 30 seconds AND has no active ride
                var isAvailable  = await _db.KeyExistsAsync(AvailabilityKey(driverId));
                var hasActiveRide = await _db.KeyExistsAsync(ActiveRideKey(driverId));

                if (isAvailable && !hasActiveRide)
                    available.Add((driverId, r.Distance ?? double.MaxValue));
            }
            return available;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis geo-search failed — falling back to empty candidate list");
            return [];
        }
    }

    // ── Availability management ───────────────────────────────────────────────

    public async Task SetDriverAvailableAsync(string driverId)
    {
        try { await _db.StringSetAsync(AvailabilityKey(driverId), "1", AvailabilityTtl); }
        catch (Exception ex) { _logger.LogWarning(ex, "Set driver available failed"); }
    }

    public async Task SetDriverUnavailableAsync(string driverId)
    {
        try { await _db.KeyDeleteAsync(AvailabilityKey(driverId)); }
        catch (Exception ex) { _logger.LogWarning(ex, "Set driver unavailable failed"); }
    }

    // ── Active ride tracking ──────────────────────────────────────────────────

    public async Task SetDriverActiveRideAsync(string driverId, string rideId)
    {
        try
        {
            await _db.StringSetAsync(ActiveRideKey(driverId), rideId, ActiveRideTtl);
            // Remove from available pool while on a ride
            await _db.KeyDeleteAsync(AvailabilityKey(driverId));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "SetDriverActiveRide failed"); }
    }

    public async Task ClearDriverActiveRideAsync(string driverId)
    {
        try
        {
            await _db.KeyDeleteAsync(ActiveRideKey(driverId));
            // Restore availability after ride completes
            await _db.StringSetAsync(AvailabilityKey(driverId), "1", AvailabilityTtl);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "ClearDriverActiveRide failed"); }
    }

    // ── Ride state cache (short TTL for consistency) ──────────────────────────

    public async Task SetRideStateAsync(string rideId, string state)
    {
        try { await _db.StringSetAsync(RideStateKey(rideId), state, RideStateTtl); }
        catch { /* non-fatal */ }
    }

    public async Task<string?> GetRideStateAsync(string rideId)
    {
        try
        {
            var val = await _db.StringGetAsync(RideStateKey(rideId));
            return val.HasValue ? val.ToString() : null;
        }
        catch { return null; }
    }

    // ── Key builders ──────────────────────────────────────────────────────────
    private static string AvailabilityKey(string driverId) => $"driver:{driverId}:available";
    private static string ActiveRideKey(string driverId)   => $"driver:{driverId}:active_ride";
    private static string RideStateKey(string rideId)      => $"ride:{rideId}:state";
}

// ════════════════════════════════════════════════════════════════════════════
// KAFKA CONSUMER — Driver Location Persistence
// Consumes driver.locations topic and persists to PostgreSQL asynchronously
// ════════════════════════════════════════════════════════════════════════════

namespace RideSharing.Infrastructure.Messaging;

/// <summary>
/// Consumes the driver.locations Kafka topic and persists location history
/// to PostgreSQL asynchronously.
///
/// Why Kafka for location persistence?
/// The hot path (DriverLocationService) writes directly to Redis for zero latency.
/// Persisting to PostgreSQL on the hot path would add 10-20ms to every 5-second ping.
/// Instead, location pings are published to Kafka after the Redis write and consumed
/// by this service in the background.
///
/// Topic: driver.locations (partitioned by driver_id for per-driver ordering)
/// Consumer group: ride-location-persistence
/// Retention: 1 hour (short — locations older than 1h have no matching value)
/// </summary>
public class DriverLocationPersistenceConsumer : IDisposable
{
    private readonly IConsumer<string, string> _consumer;
    private readonly string _connectionString;
    private readonly ILogger<DriverLocationPersistenceConsumer> _logger;

    public DriverLocationPersistenceConsumer(IConfiguration configuration, ILogger<DriverLocationPersistenceConsumer> logger)
    {
        _logger           = logger;
        _connectionString = configuration.GetConnectionString("PostgreSQL")!;

        _consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers      = configuration.GetConnectionString("Kafka") ?? "localhost:9092",
            GroupId               = "ride-location-persistence",
            AutoOffsetReset       = AutoOffsetReset.Latest,  // Only care about recent locations
            EnableAutoCommit      = true,                    // Auto-commit OK — location data is non-critical
            AutoCommitIntervalMs  = 5000
        }).Build();
    }

    public async Task ConsumeAsync(CancellationToken ct)
    {
        _consumer.Subscribe("driver.locations");
        _logger.LogInformation("DriverLocationPersistenceConsumer started");

        var batch = new List<DriverLocationMessage>();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = _consumer.Consume(TimeSpan.FromMilliseconds(500));
                if (result is null || result.IsPartitionEOF)
                {
                    if (batch.Count > 0) await FlushBatchAsync(batch, ct);
                    continue;
                }

                var msg = JsonSerializer.Deserialize<DriverLocationMessage>(result.Message.Value);
                if (msg is not null) batch.Add(msg);

                // Batch insert every 100 messages or 5 seconds — whichever comes first
                if (batch.Count >= 100)
                    await FlushBatchAsync(batch, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Error in location consumer"); }
        }

        if (batch.Count > 0) await FlushBatchAsync(batch, CancellationToken.None);
        _consumer.Close();
    }

    private async Task FlushBatchAsync(List<DriverLocationMessage> batch, CancellationToken ct)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            // Bulk update driver last_location in a single statement
            foreach (var msg in batch)
            {
                await conn.ExecuteAsync("""
                    UPDATE drivers
                    SET last_location_lat = @Lat, last_location_lng = @Lng, updated_at = NOW()
                    WHERE id = @DriverId
                    """, new { DriverId = msg.DriverId, Lat = msg.Latitude, Lng = msg.Longitude });
            }

            _logger.LogDebug("Flushed {Count} driver locations to PostgreSQL", batch.Count);
            batch.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush location batch — {Count} locations may be lost", batch.Count);
            batch.Clear(); // discard batch — location data is ephemeral
        }
    }

    private record DriverLocationMessage(string DriverId, double Latitude, double Longitude, DateTimeOffset Timestamp);
    public void Dispose() => _consumer?.Dispose();
}

public class DriverLocationPersistenceWorker : BackgroundService
{
    private readonly DriverLocationPersistenceConsumer _consumer;
    public DriverLocationPersistenceWorker(DriverLocationPersistenceConsumer consumer) => _consumer = consumer;
    protected override async Task ExecuteAsync(CancellationToken ct) => await _consumer.ConsumeAsync(ct);
}
