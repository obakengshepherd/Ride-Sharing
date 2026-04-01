using System;
using System.Collections.Generic;
using System.Text.Json;
using Confluent.Kafka;
using Dapper;
using Microsoft.Extensions.Logging;
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
// KAFKA CONSUMER MOVED TO DriverLocationPersistenceConsumer.cs
// ════════════════════════════════════════════════════════════════════════════
// See Infrastructure/Messaging/DriverLocationPersistenceConsumer.cs for implementation.
