namespace RideSharing.Domain.Entities;

public class Ride
{
    public string Id { get; private set; } = string.Empty;
    public string RiderId { get; private set; } = string.Empty;
    public string? DriverId { get; private set; }
    public RideStatus Status { get; private set; }
    public double PickupLat { get; private set; }
    public double PickupLng { get; private set; }
    public double DropoffLat { get; private set; }
    public double DropoffLng { get; private set; }
    public decimal? Fare { get; private set; }
    public DateTimeOffset RequestedAt { get; private set; }
    public DateTimeOffset? AcceptedAt { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    // State machine transitions — implemented Day 13
    public void Accept(string driverId) => throw new NotImplementedException();
    public void Start() => throw new NotImplementedException();
    public void Complete(decimal fare) => throw new NotImplementedException();
    public void Cancel() => throw new NotImplementedException();
}

public enum RideStatus
{
    Requested, Matching, Accepted, InProgress, Completed, Cancelled
}