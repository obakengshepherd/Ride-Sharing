# Performance — Ride Sharing System

---

## Current Bottlenecks

### Bottleneck 1: Driver location write throughput
200,000 drivers × 1 ping/5s = 40,000 GEOADD operations/second to Redis.
Single-threaded Redis handles ~100K simple ops/sec — we are at 40% capacity.
The write throughput is the first Redis bottleneck at full scale.

### Bottleneck 2: Matching under high driver density
In a dense urban market, `GEOSEARCH` within 2km may return thousands of driver
candidates before the availability filter. Ranking and filtering in the
application layer takes O(N) time in the candidate count.

### Bottleneck 3: Ride event write volume
10,000 concurrent rides × ~5 state transitions each = 50,000 ride event INSERT
operations over the life of each ride set. At peak, this is significant
PostgreSQL write pressure.

---

## Cache Hit Rate Targets

| Cache Key                  | Target  | Notes                                       |
|---------------------------|---------|----------------------------------------------|
| `driver:{id}:available`   | N/A     | Written on every ping — always authoritative |
| `driver:{id}:active_ride` | N/A     | Set on accept, cleared on complete           |
| `ride:{id}:state`         | ≥ 80%   | 10s TTL; miss = PostgreSQL read              |

---

## Database Read Replica Routing

| Operation                      | Target       | Reason                             |
|-------------------------------|--------------|-------------------------------------|
| `GET /rides/{id}`             | Read replica | Display query; 10s eventual OK      |
| Ride status write (transitions)| **Primary** | State machine writes                |
| Driver profile lookup (match)  | Read replica | Driver data changes infrequently    |
| Analytics/reporting queries    | Read replica | Non-time-critical                   |

---

## Connection Pool Sizing

| Setting               | Value | Rationale                                         |
|-----------------------|-------|---------------------------------------------------|
| Max pool per instance | 15    | Ride writes are fast (~5ms); lower pool needed    |
| PATCH /location pool  | 5     | Writes only to Redis — minimal DB connections     |
| PgBouncer mode        | Transaction | Releases between statements                  |

---

## Query Performance Targets

| Query                                        | Target p95 | Index Used                         |
|---------------------------------------------|-----------|-------------------------------------|
| Redis GEOSEARCH within 2km                   | < 5ms     | Redis geo sorted set               |
| `SELECT * FROM rides WHERE id = ?`          | < 2ms     | Primary key                        |
| `UPDATE rides SET status = ?`               | < 10ms    | Primary key                        |
| `SELECT drivers WHERE user_id = ?`          | < 2ms     | `drivers_user_id_unique`           |
| `INSERT INTO ride_events`                   | < 5ms     | Sequential insert                  |

---

## Rate Limiting Configuration

| Policy          | Limit | Window  | Endpoint                           |
|-----------------|-------|---------|------------------------------------|
| ride-request    | 5     | 1 min   | `POST /rides/request`              |
| driver-location | 30    | 1 min   | `PATCH /drivers/{id}/location`     |
| authenticated   | 60    | 1 min   | All other authenticated endpoints  |
| unauthenticated | 10    | 1 min   | By IP                              |

---

# Scaling Strategy — Ride Sharing System

## Horizontal Scaling Table

| Component                 | Scales Horizontally? | Notes                                               |
|---------------------------|---------------------|-----------------------------------------------------|
| Ride API                  | ✅ Yes               | Stateless; round-robin LB                           |
| Driver Location API       | ✅ Yes               | Scales independently; highest traffic volume        |
| MatchingService           | ✅ Yes               | Stateless Redis query; scales with Ride API         |
| RideService               | ✅ Yes               | State in PostgreSQL; no in-memory state             |
| Redis (geo-index)         | ✅ Yes (Cluster)     | Shard by city/region hash                          |
| PostgreSQL primary        | ❌ No (writes)       | Single primary; replicas for reads                  |
| Kafka (rides.events)      | ✅ Yes               | Add partitions; scale consumers to match            |

## Load Balancing

**Ride API and Driver Location API are separate deployments:**
Driver Location API is separated because its traffic pattern (40K writes/sec constant)
is fundamentally different from the Ride API (bursty, lower volume). They scale independently.

```
Algorithm:     Round-Robin (both APIs are stateless)
Health check:  GET /health — every 10s, 3 failures = remove, 2 successes = restore
Affinity:      None required
```

## Stateless Design Guarantees

1. No in-process driver location cache — all location state in Redis.
2. No active ride state in memory — all in PostgreSQL.
3. JWT tokens validated per-instance from public key.
4. Matching service reads only from Redis — no application-layer state.

## Scaling Triggers

| Metric                     | Threshold   | Action                                  |
|----------------------------|-------------|-----------------------------------------|
| Driver Location API CPU    | > 70%       | Add instances (scale most aggressively) |
| Redis GEOADD throughput    | > 70K ops/s | Add Redis Cluster nodes                 |
| Ride API p99               | > 300ms     | Add Ride API instances                  |
