namespace RideSharing.Application.Services;

using RideSharing.Application.Interfaces;
using RideSharing.Api.Models.Requests;
using RideSharing.Api.Models.Responses;

public class RideService : IRideService
{
    public Task<RideResponse> RequestRideAsync(string riderId, RequestRideRequest request, CancellationToken ct)
        => throw new NotImplementedException("Implemented Day 13");
    public Task<RideAcceptedResponse> AcceptRideAsync(string rideId, string driverId, CancellationToken ct)
        => throw new NotImplementedException("Implemented Day 13");
    public Task<RideResponse> StartRideAsync(string rideId, string driverId, CancellationToken ct)
        => throw new NotImplementedException("Implemented Day 13");
    public Task<RideCompletedResponse> CompleteRideAsync(string rideId, string driverId, CancellationToken ct)
        => throw new NotImplementedException("Implemented Day 13");
    public Task<RideResponse> GetRideAsync(string rideId, string requestingUserId, CancellationToken ct)
        => throw new NotImplementedException("Implemented Day 13");
}

public class MatchingService : IMatchingService
{
    public Task<IEnumerable<NearbyDriverResponse>> GetNearbyDriversAsync(NearbyDriversRequest query, CancellationToken ct)
        => throw new NotImplementedException("Implemented Day 13");
    public Task<IEnumerable<string>> FindCandidatesAsync(double lat, double lng, double radiusKm, CancellationToken ct)
        => throw new NotImplementedException("Implemented Day 13");
}

public class DriverLocationService : IDriverLocationService
{
    public Task UpdateLocationAsync(string authenticatedDriverId, string routeDriverId, UpdateLocationRequest request, CancellationToken ct)
        => throw new NotImplementedException("Implemented Day 13");
}
