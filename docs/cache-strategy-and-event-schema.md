# Cache Strategy & Event Schema — Ride Sharing System

---

## Cache Strategy

### Pattern 1: Write-Through (Driver Geo-Index)

The driver geo-index in Redis is the primary store for real-time matching — not a
cache of PostgreSQL data. There is no database equivalent that is kept in sync;
the geo-index is self-contained and ephemeral.

```
Every 5 seconds, driver app sends PATCH /drivers/{id}/location:
  1. GEOADD drivers {lng} {lat} {driver_id}    ← overwrite previous location
  2. SET driver:{id}:available 1 EX 30          ← renew availability TTL
  3. Publish to Kafka driver.locations topic    ← async persistence to PostgreSQL
```

No TTL on the GEO set itself — entries are overwritten on every ping. Availability
TTL (30s) serves as the effective expiry: a driver who stops pinging becomes
unavailable within 30 seconds without any explicit removal.

### Pattern 2: Cache-Aside (Ride State)

Ride state is cached with a 10-second TTL for read performance. The TTL is
deliberately short because ride state transitions must be consistent — a rider
checking their ride status must see the current state, not one that was 60 seconds
old when they accepted/cancelled.

```
GET /rides/{id}:
  1. GET ride:{id}:state
  2. HIT  → return cached state
  3. MISS → query PostgreSQL
         → SET ride:{id}:state {state} EX 10
```

Every state transition invalidates the cache key immediately after the DB commit.

---

## Key Inventory

| Key Pattern                  | Type   | TTL   | Purpose                                  |
|------------------------------|--------|-------|------------------------------------------|
| `drivers` (GEO set)          | Geo    | None  | Real-time driver location index          |
| `driver:{id}:available`      | String | 30s   | Driver availability signal               |
| `driver:{id}:active_ride`    | String | 4h    | Current ride ID (blocks from availability)|
| `ride:{id}:state`            | String | 10s   | Short-TTL ride state cache               |

---

## Event Schema

### `rides.events`
- **Producer:** RideService (on every state transition)
- **Partitioned by:** `ride_id`
- **Consumers:** BillingService, NotificationService, AnalyticsPipeline
- **Partitions:** 12
- **Retention:** 7 days

**Schema:**
```json
{
  "event_id": "uuid",
  "event_type": "RideStateChanged",
  "ride_id": "ride_01j9...",
  "previous_status": "accepted",
  "new_status": "in_progress",
  "driver_id": "drv_xyz789",
  "occurred_at": "2024-01-15T10:36:00Z"
}
```

### `driver.locations`
- **Producer:** DriverLocationService (after Redis GEOADD)
- **Partitioned by:** `driver_id` — all updates for a driver are ordered
- **Consumers:** DriverLocationPersistenceConsumer (group: `ride-location-persistence`)
- **Partitions:** 8
- **Retention:** 1 hour (short — old locations have no value)

**Consumer behaviour:** Uses auto-commit with `AutoOffsetReset.Latest` — location
data from before the consumer started is irrelevant. Missing some pings during a
restart is acceptable; the next ping will catch up.
