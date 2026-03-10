namespace RideSharing.Api.Models.Responses;

public record RideResponse
{
    public string RideId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string RiderId { get; init; } = string.Empty;
    public string? DriverId { get; init; }
    public double PickupLat { get; init; }
    public double PickupLng { get; init; }
    public double DropoffLat { get; init; }
    public double DropoffLng { get; init; }
    public DateTimeOffset RequestedAt { get; init; }
    public DateTimeOffset? AcceptedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public decimal? Fare { get; init; }
}

public record RideAcceptedResponse
{
    public string RideId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string DriverId { get; init; } = string.Empty;
    public string DriverName { get; init; } = string.Empty;
    public VehicleInfo Vehicle { get; init; } = new();
    public int EtaMinutes { get; init; }
    public DateTimeOffset AcceptedAt { get; init; }
}

public record VehicleInfo
{
    public string Make { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string Plate { get; init; } = string.Empty;
}

public record RideCompletedResponse
{
    public string RideId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public decimal Fare { get; init; }
    public string Currency { get; init; } = string.Empty;
    public decimal DistanceKm { get; init; }
    public int DurationMinutes { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
}

public record NearbyDriverResponse
{
    public string DriverId { get; init; } = string.Empty;
    public decimal DistanceKm { get; init; }
    public int EtaMinutes { get; init; }
    public VehicleInfo Vehicle { get; init; } = new();
}

public record ApiResponse<T>
{
    public T Data { get; init; } = default!;
    public ApiMeta Meta { get; init; } = new();
}

public record ApiMeta
{
    public string RequestId { get; init; } = Guid.NewGuid().ToString();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public static class ApiResponse
{
    public static ApiResponse<T> Success<T>(T data) => new() { Data = data };
}