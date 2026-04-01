using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace RideSharing.Infrastructure.Messaging;

/// <summary>
/// Consumes the driver.locations Kafka topic and persists location history
/// to PostgreSQL asynchronously.
///
/// Why Kafka for location persistence?
/// The hot path (DriverLocationService) writes directly to Redis for zero latency.
/// Persisting to PostgreSQL on the hot path would add 10-20ms to every 5-second ping.
/// Instead, location pings are published to Kafka after the Redis write and consumed
/// by this service in the background.
///
/// Topic: driver.locations (partitioned by driver_id for per-driver ordering)
/// Consumer group: ride-location-persistence
/// Retention: 1 hour (short — locations older than 1h have no matching value)
/// </summary>
public class DriverLocationPersistenceConsumer : IDisposable
{
    private readonly IConsumer<string, string> _consumer;
    private readonly string _connectionString;
    private readonly ILogger<DriverLocationPersistenceConsumer> _logger;

    public DriverLocationPersistenceConsumer(IConfiguration configuration, ILogger<DriverLocationPersistenceConsumer> logger)
    {
        _logger           = logger;
        _connectionString = configuration.GetConnectionString("PostgreSQL")!;

        _consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers      = configuration.GetConnectionString("Kafka") ?? "localhost:9092",
            GroupId               = "ride-location-persistence",
            AutoOffsetReset       = AutoOffsetReset.Latest,  // Only care about recent locations
            EnableAutoCommit      = true,                    // Auto-commit OK — location data is non-critical
            AutoCommitIntervalMs  = 5000
        }).Build();
    }

    public async Task ConsumeAsync(CancellationToken ct)
    {
        _consumer.Subscribe("driver.locations");
        _logger.LogInformation("DriverLocationPersistenceConsumer started");

        var batch = new List<DriverLocationMessage>();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = _consumer.Consume(TimeSpan.FromMilliseconds(500));
                if (result is null || result.IsPartitionEOF)
                {
                    if (batch.Count > 0) await FlushBatchAsync(batch, ct);
                    continue;
                }

                var msg = JsonSerializer.Deserialize<DriverLocationMessage>(result.Message.Value);
                if (msg is not null) batch.Add(msg);

                // Batch insert every 100 messages or 5 seconds — whichever comes first
                if (batch.Count >= 100)
                    await FlushBatchAsync(batch, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Error in location consumer"); }
        }

        if (batch.Count > 0) await FlushBatchAsync(batch, CancellationToken.None);
        _consumer.Close();
    }

    private async Task FlushBatchAsync(List<DriverLocationMessage> batch, CancellationToken ct)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            // Bulk update driver last_location in a single statement
            foreach (var msg in batch)
            {
                await conn.ExecuteAsync("""
                    UPDATE drivers
                    SET last_location_lat = @Lat, last_location_lng = @Lng, updated_at = NOW()
                    WHERE id = @DriverId
                    """, new { DriverId = msg.DriverId, Lat = msg.Latitude, Lng = msg.Longitude });
            }

            _logger.LogDebug("Flushed {Count} driver locations to PostgreSQL", batch.Count);
            batch.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush location batch — {Count} locations may be lost", batch.Count);
            batch.Clear(); // discard batch — location data is ephemeral
        }
    }

    private record DriverLocationMessage(string DriverId, double Latitude, double Longitude, DateTimeOffset Timestamp);
    public void Dispose() => _consumer?.Dispose();
}

/// <summary>
/// Background worker that runs the location persistence consumer.
/// Registered as a hosted service in Program.cs.
/// </summary>
public class DriverLocationPersistenceWorker : BackgroundService
{
    private readonly DriverLocationPersistenceConsumer _consumer;
    public DriverLocationPersistenceWorker(DriverLocationPersistenceConsumer consumer) => _consumer = consumer;
    protected override async Task ExecuteAsync(CancellationToken ct) => await _consumer.ConsumeAsync(ct);
}
