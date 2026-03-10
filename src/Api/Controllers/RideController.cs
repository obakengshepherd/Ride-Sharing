using RideSharing.Application.Interfaces;
using RideSharing.Api.Models.Requests;
using RideSharing.Api.Models.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace RideSharing.Api.Controllers;

[ApiController]
[Route("api/v1")]
[Authorize]
public class RideController : ControllerBase
{
    private readonly IRideService _rideService;
    private readonly IMatchingService _matchingService;

    public RideController(IRideService rideService, IMatchingService matchingService)
    {
        _rideService = rideService;
        _matchingService = matchingService;
    }

    // POST /api/v1/rides/request
    [HttpPost("rides/request")]
    public async Task<IActionResult> RequestRide(
        [FromBody] RequestRideRequest request,
        CancellationToken cancellationToken)
    {
        var riderId = User.GetUserId();
        var result = await _rideService.RequestRideAsync(riderId, request, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, ApiResponse.Success(result));
    }

    // POST /api/v1/rides/{id}/accept
    [HttpPost("rides/{id}/accept")]
    public async Task<IActionResult> AcceptRide(
        [FromRoute] string id,
        CancellationToken cancellationToken)
    {
        var driverId = User.GetUserId();
        var result = await _rideService.AcceptRideAsync(id, driverId, cancellationToken);
        return Ok(ApiResponse.Success(result));
    }

    // POST /api/v1/rides/{id}/start
    [HttpPost("rides/{id}/start")]
    public async Task<IActionResult> StartRide(
        [FromRoute] string id,
        CancellationToken cancellationToken)
    {
        var driverId = User.GetUserId();
        var result = await _rideService.StartRideAsync(id, driverId, cancellationToken);
        return Ok(ApiResponse.Success(result));
    }

    // POST /api/v1/rides/{id}/complete
    [HttpPost("rides/{id}/complete")]
    public async Task<IActionResult> CompleteRide(
        [FromRoute] string id,
        CancellationToken cancellationToken)
    {
        var driverId = User.GetUserId();
        var result = await _rideService.CompleteRideAsync(id, driverId, cancellationToken);
        return Ok(ApiResponse.Success(result));
    }

    // GET /api/v1/rides/{id}
    [HttpGet("rides/{id}")]
    public async Task<IActionResult> GetRide(
        [FromRoute] string id,
        CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var result = await _rideService.GetRideAsync(id, userId, cancellationToken);
        return Ok(ApiResponse.Success(result));
    }

    // GET /api/v1/drivers/nearby
    [HttpGet("drivers/nearby")]
    public async Task<IActionResult> GetNearbyDrivers(
        [FromQuery] NearbyDriversRequest query,
        CancellationToken cancellationToken)
    {
        var result = await _matchingService.GetNearbyDriversAsync(query, cancellationToken);
        return Ok(ApiResponse.Success(result));
    }
}

[ApiController]
[Route("api/v1/drivers")]
[Authorize]
public class DriverLocationController : ControllerBase
{
    private readonly IDriverLocationService _locationService;

    public DriverLocationController(IDriverLocationService locationService)
    {
        _locationService = locationService;
    }

    // PATCH /api/v1/drivers/{id}/location
    [HttpPatch("{id}/location")]
    public async Task<IActionResult> UpdateLocation(
        [FromRoute] string id,
        [FromBody] UpdateLocationRequest request,
        CancellationToken cancellationToken)
    {
        var driverId = User.GetUserId();
        await _locationService.UpdateLocationAsync(driverId, id, request, cancellationToken);
        return NoContent();
    }
}