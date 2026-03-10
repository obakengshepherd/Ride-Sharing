namespace RideSharing.Infrastructure.Persistence;
/// <summary>Ride + RideEvent repository — PostgreSQL via Dapper. Implemented Day 9.</summary>
public class RideRepository { }

namespace RideSharing.Infrastructure.Cache;
/// <summary>
/// Redis geo-index adapter.
/// GEOADD drivers {lng} {lat} {driver_id}
/// SET driver:{id}:available 1 EX 30
/// Implemented Day 17.
/// </summary>
public class DriverGeoIndexService { }

namespace RideSharing.Infrastructure.Messaging;
/// <summary>Kafka producer — topic: rides.events, partitioned by ride_id. Implemented Day 19.</summary>
public class RideEventPublisher { }
