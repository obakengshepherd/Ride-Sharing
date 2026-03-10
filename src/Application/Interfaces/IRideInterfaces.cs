namespace RideSharing.Application.Interfaces;

using RideSharing.Api.Models.Requests;
using RideSharing.Api.Models.Responses;

/// <summary>
/// Manages ride lifecycle state transitions.
/// Publishes a RideStateChanged event to Kafka on every transition.
/// </summary>
public interface IRideService
{
    Task<RideResponse> RequestRideAsync(string riderId, RequestRideRequest request, CancellationToken ct);
    Task<RideAcceptedResponse> AcceptRideAsync(string rideId, string driverId, CancellationToken ct);
    Task<RideResponse> StartRideAsync(string rideId, string driverId, CancellationToken ct);
    Task<RideCompletedResponse> CompleteRideAsync(string rideId, string driverId, CancellationToken ct);
    Task<RideResponse> GetRideAsync(string rideId, string requestingUserId, CancellationToken ct);
}

/// <summary>
/// Queries the Redis geospatial index for nearby available drivers.
/// Does not touch the database on the hot path.
/// </summary>
public interface IMatchingService
{
    Task<IEnumerable<NearbyDriverResponse>> GetNearbyDriversAsync(NearbyDriversRequest query, CancellationToken ct);
    Task<IEnumerable<string>> FindCandidatesAsync(double lat, double lng, double radiusKm, CancellationToken ct);
}

/// <summary>
/// Accepts high-frequency location pings and writes to Redis GEOADD.
/// Persists to PostgreSQL asynchronously via Kafka.
/// </summary>
public interface IDriverLocationService
{
    Task UpdateLocationAsync(string authenticatedDriverId, string routeDriverId, UpdateLocationRequest request, CancellationToken ct);
}