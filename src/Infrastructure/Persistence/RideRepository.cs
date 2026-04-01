using System;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using RideSharing.Domain.Entities;

namespace RideSharing.Infrastructure.Persistence;

public class RideRepository
{
    private readonly string _connectionString;

    public RideRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("PostgreSQL")
            ?? throw new InvalidOperationException("PostgreSQL connection string missing.");
    }

    public NpgsqlConnection CreateConnection() => new(_connectionString);

    public async Task<RideRecord?> FindByIdAsync(string rideId)
    {
        using var conn = CreateConnection();
        const string sql = "SELECT * FROM rides WHERE id = @RideId";
        return await conn.QuerySingleOrDefaultAsync<RideRecord>(sql, new { RideId = rideId });
    }

    public async Task InsertRideAsync(RideRecord ride, NpgsqlConnection conn)
    {
        const string sql = """
            INSERT INTO rides
                (id, rider_id, driver_id, status, pickup_lat, pickup_lng,
                 dropoff_lat, dropoff_lng, pickup_address, dropoff_address,
                 currency, requested_at)
            VALUES
                (@Id, @RiderId, @DriverId, @Status::ride_status, @PickupLat, @PickupLng,
                 @DropoffLat, @DropoffLng, @PickupAddress, @DropoffAddress,
                 @Currency, @RequestedAt)
            """;
        await conn.ExecuteAsync(sql, ride);
    }

    public async Task UpdateRideStatusAsync(string rideId, string status,
        string? driverId = null, decimal? fare = null,
        NpgsqlConnection? conn = null)
    {
        using var connection = conn ?? CreateConnection();
        var timestampCol = status switch
        {
            "accepted"    => ", accepted_at = NOW()",
            "in_progress" => ", started_at = NOW()",
            "completed"   => ", completed_at = NOW()",
            "cancelled"   => ", cancelled_at = NOW()",
            _             => string.Empty
        };
        var driverClause = driverId is not null ? ", driver_id = @DriverId" : string.Empty;
        var fareClause   = fare.HasValue ? ", fare = @Fare" : string.Empty;

        var sql = $"""
            UPDATE rides
            SET status = @Status::ride_status, updated_at = NOW()
                {driverClause}{fareClause}{timestampCol}
            WHERE id = @RideId
            """;
        await connection.ExecuteAsync(sql, new { RideId = rideId, Status = status, DriverId = driverId, Fare = fare });
    }

    public async Task InsertRideEventAsync(RideEventRecord evt, NpgsqlConnection conn)
    {
        const string sql = """
            INSERT INTO ride_events (id, ride_id, event_type, payload, occurred_at)
            VALUES (@Id, @RideId, @EventType, @Payload::jsonb, @OccurredAt)
            """;
        await conn.ExecuteAsync(sql, evt);
    }

    public async Task<DriverRecord?> FindDriverByIdAsync(string driverId)
    {
        using var conn = CreateConnection();
        const string sql = """
            SELECT d.id, d.user_id, d.vehicle_id, d.rating, d.is_available,
                   u.name AS driver_name,
                   v.make, v.model, v.plate
            FROM drivers d
            JOIN users u ON u.id = d.user_id
            JOIN vehicles v ON v.id = d.vehicle_id
            WHERE d.id = @DriverId
            """;
        return await conn.QuerySingleOrDefaultAsync<DriverRecord>(sql, new { DriverId = driverId });
    }
}

public record RideRecord
{
    public string Id { get; init; } = string.Empty;
    public string RiderId { get; init; } = string.Empty;
    public string? DriverId { get; init; }
    public string Status { get; init; } = string.Empty;
    public double PickupLat { get; init; }
    public double PickupLng { get; init; }
    public double DropoffLat { get; init; }
    public double DropoffLng { get; init; }
    public string? PickupAddress { get; init; }
    public string? DropoffAddress { get; init; }
    public decimal? Fare { get; init; }
    public string Currency { get; init; } = "ZAR";
    public DateTimeOffset RequestedAt { get; init; }
    public DateTimeOffset? AcceptedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}

public record DriverRecord
{
    public string Id { get; init; } = string.Empty;
    public string UserId { get; init; } = string.Empty;
    public string VehicleId { get; init; } = string.Empty;
    public string DriverName { get; init; } = string.Empty;
    public string Make { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string Plate { get; init; } = string.Empty;
    public decimal Rating { get; init; }
    public bool IsAvailable { get; init; }
}

public record RideEventRecord
{
    public string Id { get; init; } = string.Empty;
    public string RideId { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public string Payload { get; init; } = "{}";
    public DateTimeOffset OccurredAt { get; init; }
}
