# Failure Scenarios — Ride Sharing System

> **Status**: Complete — Days 25–27 implementation. Replaces Phase 1 skeleton.

---

## Scenario 1 — Driver App Disconnects During Active Ride

**Trigger**
A driver's mobile device loses network connectivity after a ride transitions to
`IN_PROGRESS`. Location pings stop arriving. The driver's availability key
(`driver:{id}:available`) expires in Redis after 30 seconds.

**Affected Components**
Redis (geo-index, availability TTL), DriverLocationService, RideService.

**User-Visible Impact**
The rider's map stops updating the driver's position. The ETA shown becomes stale.
If the disconnect persists, the rider has no way to track the driver's approach.

**System Behaviour Without Mitigation**
The ride remains `IN_PROGRESS` indefinitely. The driver cannot complete the ride
without reconnecting. The rider waits with no feedback. Support has no visibility.

**Mitigation**

1. **Automatic staleness detection via TTL:** The `driver:{id}:available` key
   expires 30 seconds after the last ping. `RideShareCacheService` treats an
   expired availability key as "driver unreachable."

2. **Grace period before escalation:** A background watchdog checks in-progress
   rides every 60 seconds. If `driver:{id}:available` is expired AND the ride
   has been `IN_PROGRESS` for more than 2 minutes without a ping, the system:
   - Sends the rider a push notification: "Driver connection lost — we are
     monitoring your ride."
   - Shows the last known driver location (stored in `rides.driver_last_lat/lng`).
   - After a further 5 minutes with no ping: escalates to the support queue.

3. **Ride auto-completion on reconnect:** When the driver reconnects and
   sends location pings again, their availability TTL renews and the ride
   state is unchanged. The driver can complete normally.

4. **Partial fare protection:** If the ride must be force-cancelled due to
   prolonged disconnect, the rider is charged only for the confirmed distance
   (calculated from last known location to pickup).

**Detection**
- Metric: `driver_ping_gap_seconds` gauge — alert at > 45s for in-progress rides.
- Alert: `rides_in_progress_no_driver_ping_count > 0` → investigate immediately.
- Log: `"Driver {DriverId} unreachable for ride {RideId}"` at WARN level.

---

## Scenario 2 — Matching Service Cannot Find Available Driver

**Trigger**
A ride request arrives in an area with no available drivers, or the Redis geo-index
is temporarily empty following a Redis restart. `GEOSEARCH` returns zero results
even after the 5km radius expansion.

**Affected Components**
MatchingService, Redis geo-index, RideService, rider notification.

**User-Visible Impact**
The rider submitted a request but receives no driver match. Without mitigation,
the ride stays in `MATCHING` state forever with no feedback.

**System Behaviour Without Mitigation**
`FindCandidatesAsync` returns an empty list. `RunMatchingAsync` sets the ride to
`CANCELLED` immediately — no retry, no notification. Rider must manually retry.

**Mitigation**

1. **Retry with expanding radius:** MatchingService first queries within 2km,
   then automatically expands to 5km on zero results. This handles genuinely
   sparse driver availability without requiring a client retry.

2. **Scheduled retry every 30 seconds:** If 5km also returns no results,
   the ride stays in `MATCHING` state. A background job retries `FindCandidatesAsync`
   every 30 seconds for up to 10 minutes.

3. **Redis fallback to PostgreSQL:** If Redis is unavailable entirely, the
   matching fallback queries `drivers WHERE is_available = TRUE` in PostgreSQL
   using the partial index `drivers_available_partial_idx`. Location distance is
   calculated from the `last_location_lat/lng` columns (async-updated from Kafka).
   This is less accurate but prevents a complete service failure.

4. **Timeout and notification:** After 10 minutes with no match, the ride is
   cancelled and the rider receives: "We could not find a driver in your area.
   Please try again in a few minutes."

**Detection**
- Metric: `ride_match_timeout_total` counter.
- Alert: `matching_failure_rate > 20%` sustained for 5 minutes → insufficient
  driver supply in the region; operations team should recruit or rebalance.
- Alert: Redis geo-index empty → `GEOSEARCH` on an empty set always returns 0.
  Detect via `INFO keyspace` showing geo key missing.

---

## Scenario 3 — Redis Geo-Index Corruption or Data Loss

**Trigger**
Redis is restarted without AOF persistence enabled, or a memory eviction policy
(such as `allkeys-lru`) evicts the `drivers` geo sorted set under memory pressure.
All driver location data is lost.

**Affected Components**
Redis (geo-index), MatchingService, all ride requests.

**User-Visible Impact**
All new ride requests fail to find drivers for up to 30 seconds (one driver ping
cycle) after Redis restarts. Existing in-progress rides are unaffected (their state
is in PostgreSQL).

**System Behaviour Without Mitigation**
If matching falls back to PostgreSQL, it works but is slower. If the service
has no PostgreSQL fallback, all matching fails during the recovery window.

**Mitigation**

1. **Self-healing via driver republish:** Drivers publish location pings every
   5 seconds. The geo-index rebuilds naturally — within one ping cycle (5 seconds
   for any individual driver, 30 seconds for full density recovery) without any
   explicit recovery action.

2. **Redis AOF persistence:** Enable `appendonly yes` in Redis config. This
   writes every GEOADD to disk and survives a clean restart. Recovery from AOF
   takes seconds, not minutes.

3. **PostgreSQL fallback during recovery window:** During the 30-second rebuild
   window, MatchingService falls back to `SELECT drivers WHERE is_available = TRUE
   AND last_location_lat IS NOT NULL` with Haversine distance calculated in the
   application layer.

4. **Health check monitoring:** The Ride Sharing health endpoint checks that the
   `drivers` geo key exists and has at least one member. If empty, it returns
   `Degraded` status.

**Detection**
- Alert: Redis health check returns `Degraded`.
- Metric: `geosearch_results_count` histogram — alert if p50 drops to zero.
- Alert: `matching_fallback_to_postgresql_total > 0` — indicates Redis geo-index
  unavailable.

---

## Scenario 4 — Double-Accept Race Condition

**Trigger**
Two riders' requests both match to the same driver within milliseconds of each
other. Both dispatch `AcceptRideAsync` before either has updated the driver's
availability status.

**Affected Components**
RideService, Redis availability key, drivers table.

**User-Visible Impact**
Without mitigation: one driver is assigned to two simultaneous rides. Both riders
receive an acceptance response. One is immediately stranded when the driver
contacts the other.

**Mitigation**

1. **Atomic Redis availability clear on accept:** `RideService.AcceptRideAsync`
   calls `SetDriverUnavailableAsync` as the first operation after setting the
   ride to `ACCEPTED`. This uses `KeyDeleteAsync` which is atomic — only one
   caller can transition from available to unavailable.

2. **Application-layer idempotency check:** Before accepting, the service checks
   `driver.IsAvailable == true` in the locked driver record. If two requests
   race, the second sees `IsAvailable = false` and throws
   `DriverNoLongerAvailableException`, returning a clean 409 to the dispatcher.

3. **Ride status lock:** The `rides.status` column uses a database CHECK constraint.
   An atomic `UPDATE rides SET status = 'accepted', driver_id = @id WHERE status = 'matching'`
   ensures exactly one driver is set — the UPDATE affects 0 rows for the second
   racer and the application detects this.

**Detection**
- Alert: `double_accept_violations_total > 0` → race condition occurring.
- Metric: `ride_accept_conflicts_total` — should be near zero in production.

---

## Scenario 5 — Kafka Event Publish Failure on Ride State Transition

**Trigger**
RideService commits a state transition to PostgreSQL (e.g., `COMPLETED`) but the
subsequent `RideStateChanged` Kafka publish fails because the broker is unreachable.

**Affected Components**
RideEventPublisher, Kafka broker, billing and notification downstream consumers.

**User-Visible Impact**
The ride is correctly marked complete in the database. The rider and driver see
the correct state. Internally: the billing service does not receive the completion
event and cannot charge the rider's payment method.

**Mitigation**

1. **Retry with exponential backoff:** `RideEventPublisher.PublishAsync` retries
   up to 3 times with 100ms → 200ms → 400ms jitter-added delays.

2. **Reconciliation job:** A scheduled job (every 5 minutes) queries for rides
   with `status = 'completed'` and `completed_at < NOW() - 10 minutes` that have
   no corresponding billing event. It re-publishes the `RideCompleted` event.

3. **Fire-and-forget acknowledgement:** The ride state transition is acknowledged
   to the client before the event publish attempt. Event loss is a backend concern,
   not a user experience concern.

**Detection**
- Alert: `kafka_publish_errors_total` for `rides.events` topic > 0 for > 60s.
- Alert: `rides_without_billing_event_count > 0` from reconciliation job.

---

## Universal Scenarios (see Digital Wallet for full specification)

### U1 — Kafka Consumer Lag
**Detection:** Consumer group lag on `rides.events` > 10,000 messages → add consumer
instances (max 12 — partition count). Alert at lag > 5,000.

### U2 — Database Connection Pool Exhaustion
**Mitigation:** Circuit breaker on PgBouncer connections; pool sized at 15/instance.
**Detection:** `pgbouncer_wait_time_p99 > 100ms`.

### U3 — Downstream Service Timeout (payment processor on fare charge)
**Mitigation:** 5-second timeout + retry policy + circuit breaker. Fare stored
locally; payment retried asynchronously even if the initial charge times out.
