using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RideSharing.Api.Models.Requests;
using RideSharing.Application.Interfaces;
using RideSharing.Infrastructure.Cache;
using RideSharing.Infrastructure.Messaging;

namespace RideSharing.Application.Services;

/// <summary>
/// Driver location service — handles location updates and persistence.
/// Hot path: updates to Redis are synchronous, DB persistence is async via Kafka.
/// </summary>
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
        // TODO: Implement DriverLocationUpdatedEvent and publish through a dedicated event bus
    }
}
