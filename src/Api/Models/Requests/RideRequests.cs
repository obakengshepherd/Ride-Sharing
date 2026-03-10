using System.ComponentModel.DataAnnotations;

namespace RideSharing.Api.Models.Requests;

public record RequestRideRequest
{
    [Required][Range(-90, 90)] public double PickupLat { get; init; }
    [Required][Range(-180, 180)] public double PickupLng { get; init; }
    [Required][Range(-90, 90)] public double DropoffLat { get; init; }
    [Required][Range(-180, 180)] public double DropoffLng { get; init; }
    [StringLength(256)] public string? PickupAddress { get; init; }
    [StringLength(256)] public string? DropoffAddress { get; init; }
}

public record UpdateLocationRequest
{
    [Required][Range(-90, 90)] public double Latitude { get; init; }
    [Required][Range(-180, 180)] public double Longitude { get; init; }
    [Range(0, 360)] public double? Heading { get; init; }
    [Range(0, 300)] public double? SpeedKmh { get; init; }
}

public record NearbyDriversRequest
{
    [Required][Range(-90, 90)] public double Lat { get; init; }
    [Required][Range(-180, 180)] public double Lng { get; init; }
    [Range(0.1, 10)] public double Radius { get; init; } = 2.0;
}
