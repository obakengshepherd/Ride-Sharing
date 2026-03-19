// Program.cs registration pattern matches digital-wallet.
// Services: IRideService, IMatchingService, IDriverLocationService
// Middleware: GlobalExceptionHandler, RequestLoggingMiddleware, JWT auth
// Rate limiting: PATCH /drivers/{id}/location at 30/min per driver

using RideSharing.Infrastructure.Cache;
using RideSharing.Infrastructure.Messaging;
using StackExchange.Redis;
using Shared.Infrastructure.RateLimit;
using Shared.Api.Controllers;
using Microsoft.Extensions.Diagnostics.HealthChecks;

// Rate limit rules for ride sharing
builder.Services.AddSingleton<IEnumerable<RateLimitRule>>(
    _ => RateLimitPolicies.RidePolicies());

builder.Services.AddSingleton<TrueSlidingWindowChecker>();

// Health checks
builder.Services.AddHealthChecks()
    .AddCheck<RedisHealthCheck>("redis",     failureStatus: HealthStatus.Degraded,  tags: ["cache"])
    .AddCheck<KafkaHealthCheck>("kafka",     failureStatus: HealthStatus.Degraded,  tags: ["messaging"])
    .AddCheck<PostgreSqlHealthCheck>("postgresql", failureStatus: HealthStatus.Unhealthy, tags: ["database"]);

builder.Services.AddTransient<RedisHealthCheck>();
builder.Services.AddTransient(_ => new PostgreSqlHealthCheck(builder.Configuration.GetConnectionString("PostgreSQL")!));
builder.Services.AddTransient(_ => new KafkaHealthCheck(builder.Configuration.GetConnectionString("Kafka") ?? "localhost:9092"));

// ── In the middleware pipeline, replace app.UseRateLimiter() with: ──
// app.UseAuthentication();
// app.UseAuthorization();
// app.UseMiddleware<RedisRateLimitMiddleware>();
// app.MapControllers();
// app.MapHealthEndpoints();

// Redis
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(
        builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379"));
builder.Services.AddSingleton<RideShareCacheService>();      // Phase 5 extended version
builder.Services.AddSingleton<DriverGeoIndexService>();       // Phase 4 geo-index

// Kafka
builder.Services.AddSingleton<RideEventPublisher>();

// Kafka consumer (driver location persistence)
builder.Services.AddSingleton<DriverLocationPersistenceConsumer>();
builder.Services.AddHostedService<DriverLocationPersistenceWorker>();

// Repositories and services
builder.Services.AddScoped<RideRepository>();
builder.Services.AddScoped<IRideService, RideService>();
builder.Services.AddScoped<IMatchingService, MatchingService>();
builder.Services.AddScoped<IDriverLocationService, DriverLocationService>();

// Health checks
builder.Services.AddHealthChecks()
    .AddNpgsql(builder.Configuration.GetConnectionString("PostgreSQL")!)
    .AddRedis(builder.Configuration.GetConnectionString("Redis")!);