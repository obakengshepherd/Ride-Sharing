using System;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using RideSharing.Api;
using RideSharing.Api.Controllers;
using RideSharing.Api.Middleware;
using RideSharing.Application.Interfaces;
using RideSharing.Application.Resilience;
using RideSharing.Application.Services;
using RideSharing.Infrastructure.Cache;
using RideSharing.Infrastructure.Health;
using RideSharing.Infrastructure.Persistence;
using RideSharing.Infrastructure.RateLimit;
using RideSharing.Infrastructure.Messaging;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// ────────────────────────────────────────────────────────────────────────────
// CORE SERVICES
// ────────────────────────────────────────────────────────────────────────────

// Redis (singleton — shared connection multiplexer)
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    var redisUrl = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6380";
    try
    {
        return ConnectionMultiplexer.Connect(redisUrl);
    }
    catch (Exception ex)
    {
        sp.GetRequiredService<ILogger<Program>>()
            .LogError(ex, "Failed to connect to Redis at {RedisUrl}", redisUrl);
        throw;
    }
});

// ────────────────────────────────────────────────────────────────────────────
// INFRASTRUCTURE SERVICES
// ────────────────────────────────────────────────────────────────────────────

// Repositories
builder.Services.AddScoped<RideRepository>();

// Cache services (Redis-backed, singleton)
builder.Services.AddSingleton<RideShareCacheService>();
builder.Services.AddSingleton<DriverGeoIndexService>();

// Messaging
builder.Services.AddSingleton<RideEventPublisher>();

// ────────────────────────────────────────────────────────────────────────────
// APPLICATION SERVICES
// ────────────────────────────────────────────────────────────────────────────

builder.Services.AddScoped<IRideService, RideService>();
builder.Services.AddScoped<IMatchingService, MatchingService>();
builder.Services.AddScoped<IDriverLocationService, DriverLocationService>();

// ────────────────────────────────────────────────────────────────────────────
// RESILIENCE POLICIES
// ────────────────────────────────────────────────────────────────────────────

builder.Services.AddResiliencePolicies(builder.Configuration);

// ────────────────────────────────────────────────────────────────────────────
// HEALTH CHECKS
// ────────────────────────────────────────────────────────────────────────────

builder.Services.AddSingleton<RedisHealthCheck>(sp =>
    new RedisHealthCheck(
        sp.GetRequiredService<IConnectionMultiplexer>(),
        sp.GetRequiredService<ILogger<RedisHealthCheck>>()));

builder.Services.AddSingleton<PostgreSqlHealthCheck>(sp =>
    new PostgreSqlHealthCheck(
        builder.Configuration.GetConnectionString("PostgreSQL") ?? "Host=localhost;Database=ride_sharing",
        sp.GetRequiredService<ILogger<PostgreSqlHealthCheck>>()));

builder.Services.AddSingleton<KafkaHealthCheck>(sp =>
    new KafkaHealthCheck(
        builder.Configuration.GetConnectionString("Kafka") ?? "localhost:9092",
        sp.GetRequiredService<ILogger<KafkaHealthCheck>>()));

builder.Services.AddHealthChecks()
    .AddCheck<RedisHealthCheck>("Redis", HealthStatus.Degraded, ["cache", "realtime"])
    .AddCheck<PostgreSqlHealthCheck>("PostgreSQL", HealthStatus.Unhealthy, ["database"])
    .AddCheck<KafkaHealthCheck>("Kafka", HealthStatus.Degraded, ["messaging"]);

// ────────────────────────────────────────────────────────────────────────────
// AUTHENTICATION & AUTHORIZATION
// ────────────────────────────────────────────────────────────────────────────

var disableAuth = builder.Configuration.GetValue<bool>("Authentication:DisableAuthentication");

if (!disableAuth)
{
    var authority = builder.Configuration["Authentication:Authority"] 
        ?? builder.Configuration["Jwt:Authority"]
        ?? throw new InvalidOperationException("Jwt:Authority not configured");

    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = authority;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateAudience = builder.Configuration.GetValue<bool>("Jwt:ValidateAudience"),
                ValidateLifetime = builder.Configuration.GetValue<bool>("Jwt:ValidateLifetime"),
                ValidateIssuerSigningKey = builder.Configuration.GetValue<bool>("Jwt:ValidateIssuerSigningKey")
            };
        });
}
else
{
    builder.Services.AddAuthentication("DevAuth")
        .AddScheme<DevAuthenticationSchemeOptions, DevAuthenticationHandler>("DevAuth", null);
}

builder.Services.AddAuthorization();

// ────────────────────────────────────────────────────────────────────────────
// CONTROLLERS & API
// ────────────────────────────────────────────────────────────────────────────

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "Ride Sharing API",
        Version = "v1",
        Description = "Event-driven ride-hailing backend with Redis geo-indexing and Kafka event streaming"
    });
});

// ────────────────────────────────────────────────────────────────────────────
// BUILD APPLICATION
// ────────────────────────────────────────────────────────────────────────────

var app = builder.Build();

// Middleware pipeline (order matters!)
app.UseMiddleware<GlobalExceptionHandler>();
app.UseMiddleware<RequestLoggingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Ride Sharing API v1");
    });
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<RedisRateLimitMiddleware>();

// Endpoints
app.MapControllers();
app.MapHealthChecks("/health");
app.MapCircuitBreakerEndpoints();

await app.RunAsync();