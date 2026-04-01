using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RideSharing.Api.Models.Requests;
using RideSharing.Api.Models.Responses;
using RideSharing.Application.Interfaces;
using RideSharing.Infrastructure.Cache;
using RideSharing.Infrastructure.Messaging;
using RideSharing.Infrastructure.Persistence;
using RideSharing.Domain.Events;

namespace RideSharing.Application.Services;

/// <summary>
/// Ride service — implements complete ride lifecycle state machine.
/// Transitions: requested → matching → accepted → in_progress → completed
///             (any state) → cancelled
/// </summary>
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
        Payload    = System.Text.Json.JsonSerializer.Serialize(payload),
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

/// <summary>
/// Domain exception — thrown when ride is not found.
/// API returns 404 Not Found.
/// </summary>
public class RideNotFoundException : Exception
{
    public RideNotFoundException(string rideId) 
        : base($"Ride '{rideId}' not found") { }
}

/// <summary>
/// Domain exception — thrown when ride state transition is invalid.
/// API returns 409 Conflict.
/// </summary>
public class InvalidRideTransitionException : Exception
{
    public InvalidRideTransitionException(string rideId, string currentStatus, string attemptedStatus)
        : base($"Cannot transition ride '{rideId}' from '{currentStatus}' to '{attemptedStatus}'") { }
}
