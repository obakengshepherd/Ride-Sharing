using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RideSharing.Api.Models.Requests;
using RideSharing.Api.Models.Responses;
using RideSharing.Application.Interfaces;
using RideSharing.Infrastructure.Cache;
using RideSharing.Infrastructure.Persistence;

namespace RideSharing.Application.Services;

/// <summary>
/// Matching service — finds candidate drivers for a ride request.
/// Uses Redis geo-index for fast spatial queries.
/// </summary>
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
