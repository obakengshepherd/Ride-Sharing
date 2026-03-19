// Program.cs registration pattern matches digital-wallet.
// Services: IRideService, IMatchingService, IDriverLocationService
// Middleware: GlobalExceptionHandler, RequestLoggingMiddleware, JWT auth
// Rate limiting: PATCH /drivers/{id}/location at 30/min per driver

using RideSharing.Infrastructure.Cache;
using RideSharing.Infrastructure.Messaging;
using StackExchange.Redis;

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