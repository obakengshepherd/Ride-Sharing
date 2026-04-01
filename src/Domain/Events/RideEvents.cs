using System;

namespace RideSharing.Domain.Events;

public abstract record DomainEvent
{
    public string EventId { get; init; } = Guid.NewGuid().ToString();
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
    public abstract string EventType { get; }
}

public record RideStateChangedEvent : DomainEvent
{
    public override string EventType => "RideStateChanged";
    public string RideId { get; init; } = string.Empty;
    public string PreviousStatus { get; init; } = string.Empty;
    public string NewStatus { get; init; } = string.Empty;
    public string? DriverId { get; init; }
}
