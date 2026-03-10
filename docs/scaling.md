# Scaling Strategy — Ride Sharing System

---

## Current Single-Node Bottlenecks

- **Driver location write throughput**: 200,000 drivers × 1 ping per 5 seconds = 40,000
  writes per second to the Redis geo-index. A single Redis instance can handle this, but
  it is the highest-volume write path and the first place to monitor for saturation.

- **Matching query latency under dense markets**: In a city with 10,000 active drivers in
  a small geographic area, the `GEORADIUS` query returns a large candidate set before filtering.
  Ranking and filtering this set in the application layer takes linear time relative to
  density. A naive implementation will slow down in high-density markets.

- **PostgreSQL ride writes**: Every state transition in every active ride writes to both
  the `rides` table (update) and `ride_events` table (insert). At 10,000 concurrent rides
  with frequent state changes, this is significant write volume for a single PostgreSQL
  primary.

- **Driver Location API is a hot endpoint**: It receives the most requests by volume.
  Because it is architecturally separated from the Ride API, it can be scaled independently —
  but it must be watched first.

---

## Horizontal Scaling Plan

### Ride API and Driver Location API

Both APIs are fully stateless. Scale by adding instances behind the load balancer. The
Driver Location API should be scaled first and more aggressively, as it receives location
pings from all active drivers continuously regardless of ride activity.

Target: 1 Driver Location API instance per 5,000 active drivers (each instance handling
~8,000 pings/sec sustained).

### Matching Service

MatchingService is stateless — it queries Redis and returns a ranked list. It can be scaled
horizontally alongside the Ride API without any coordination. Ensure Redis connection pooling
is sized to prevent connection saturation under many service instances.

### Redis — Geo-Index

A single Redis instance on capable hardware (>= 32GB RAM, NVMe) handles 200,000 tracked
driver entries trivially — a `GEO` entry in Redis consumes roughly 80 bytes, so 200,000
drivers consumes ~16MB, far below any capacity concern.

Throughput (40,000 `GEOADD` operations/sec) is the scaling concern, not memory. A single
Redis instance on modern hardware handles ~100,000 simple operations/sec. If the 40K write
rate approaches saturation, introduce Redis Cluster with geographic sharding: drivers in
city A hash to shard 1, city B to shard 2, etc. Matching queries then only need to hit the
shard for the relevant city, which also reduces cross-shard latency.

### PostgreSQL

**Phase 1 — Read replicas**: Route `GET /rides/{id}` and driver profile queries to a read
replica. All writes (state transitions, new rides) target the primary.

**Phase 2 — Partition `ride_events`**: Partition by month once the table exceeds 100M rows.
Old partitions can be archived to cold storage; the active partition remains small and fast.

**Phase 3 — Archive completed rides**: Move rides older than 90 days to an archive table or
cold storage. The hot `rides` table should contain only active and recently completed rides.

### Kafka — Ride Events

Partition `rides.events` by `ride_id`. Start with 12 partitions (matching typical city-level
deployment scale). Scale consumer instances up to the partition count as needed. Each consumer
group (billing, notifications, analytics) scales independently.

---

## Cache Hit Rate Targets

| Cache Key                      | TTL   | Purpose                              | Notes                                          |
|-------------------------------|-------|--------------------------------------|------------------------------------------------|
| `driver:{id}:available`       | 30s   | Driver availability signal           | Renewed on every location ping                 |
| GEO index: `drivers`           | N/A   | Continuously updated geo-index       | No TTL — entries expire via availability key   |
| `ride:{id}:state`             | 10s   | Optional read cache for ride status  | Invalidated on every state transition          |

Driver location data is not cached in the traditional sense — it is the canonical data source
in Redis. The geo-index is continuously written, not written-once-read-many. There is no
cache hit rate target for it; what matters is write throughput and query latency.

---

## Queue Throughput Targets

| Topic           | Expected Peak Throughput | Partition Count | Notes                         |
|-----------------|--------------------------|-----------------|-------------------------------|
| `rides.events`  | 5,000 events/sec         | 12              | Partitioned by ride_id        |
| `driver.locations` | 40,000 writes/sec     | N/A             | Written directly to Redis     |

Driver location updates are not queued through Kafka on the hot path — they write directly
to Redis for minimum latency. Kafka receives a sampled location stream asynchronously for
the analytics pipeline, not the real-time matching path.
