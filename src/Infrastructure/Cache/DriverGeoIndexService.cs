using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace RideSharing.Infrastructure.Cache;

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
