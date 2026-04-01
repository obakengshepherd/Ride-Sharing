using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Npgsql;
using StackExchange.Redis;

namespace RideSharing.Infrastructure.Health;

/// <summary>
/// Custom health checks for Redis, PostgreSQL, and Kafka.
/// Wired into Program.cs via AddHealthChecks().
/// </summary>

/// <summary>
/// Redis health check — tests PING command.
/// Failure status: Degraded (cache failure doesn't require 503 to clients)
/// </summary>
public class RedisHealthCheck : IHealthCheck
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisHealthCheck> _logger;

    public RedisHealthCheck(IConnectionMultiplexer redis, ILogger<RedisHealthCheck> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            var server = _redis.GetServer(_redis.GetEndPoints().First());
            var pong = await server.PingAsync();

            if (pong == TimeSpan.Zero)
                return HealthCheckResult.Unhealthy("Redis PING returned zero duration");

            return HealthCheckResult.Healthy($"Redis OK ({pong.TotalMilliseconds:F0}ms)");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis health check failed");
            return HealthCheckResult.Degraded($"Redis unhealthy: {ex.Message}");
        }
    }
}

/// <summary>
/// PostgreSQL health check — executes SELECT 1 query.
/// Failure status: Unhealthy (database failure blocks ride operations)
/// </summary>
public class PostgreSqlHealthCheck : IHealthCheck
{
    private readonly string _connectionString;
    private readonly ILogger<PostgreSqlHealthCheck> _logger;

    public PostgreSqlHealthCheck(string connectionString, ILogger<PostgreSqlHealthCheck> logger)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(ct);
            return HealthCheckResult.Healthy("PostgreSQL OK");
        }
        catch (OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL health check timed out");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PostgreSQL health check failed");
            return HealthCheckResult.Unhealthy($"PostgreSQL unhealthy: {ex.Message}");
        }
    }
}

/// <summary>
/// Kafka broker health check — tests metadata fetch.
/// Failure status: Degraded (event publishing can be retried; ride matching works without it)
/// </summary>
public class KafkaHealthCheck : IHealthCheck
{
    private readonly string _bootstrapServers;
    private readonly ILogger<KafkaHealthCheck> _logger;
    private const int TimeoutMs = 5000;

    public KafkaHealthCheck(string bootstrapServers, ILogger<KafkaHealthCheck> logger)
    {
        _bootstrapServers = bootstrapServers ?? throw new ArgumentNullException(nameof(bootstrapServers));
        _logger = logger;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeoutMs);

            var config = new AdminClientConfig { BootstrapServers = _bootstrapServers };

            using var admin = new AdminClientBuilder(config).Build();

            var metadata = admin.GetMetadata(
                topic: null,
                timeout: TimeSpan.FromMilliseconds(TimeoutMs));

            if (metadata?.Brokers?.Count > 0)
                return Task.FromResult(HealthCheckResult.Healthy($"Kafka OK ({metadata.Brokers.Count} brokers)"));

            return Task.FromResult(HealthCheckResult.Degraded("Kafka: no brokers available"));
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(HealthCheckResult.Degraded("Kafka health check timed out"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kafka health check failed");
            return Task.FromResult(HealthCheckResult.Degraded($"Kafka unhealthy: {ex.Message}"));
        }
    }
}

/// <summary>
/// Combined detailed health check that returns status of all systems.
/// Returned by GET /health/detail endpoint.
/// </summary>
public class DetailedHealthReport
{
    public string Status { get; set; } = string.Empty;
    public Dictionary<string, HealthCheckDetail> Checks { get; set; } = new();
    public long DurationMs { get; set; }
}

public class HealthCheckDetail
{
    public string Status { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Dictionary<string, object>? Data { get; set; }
}
