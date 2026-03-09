# Failure Scenarios — Ride Sharing System

> **Status**: Skeleton — stubs defined on Day 2. Full mitigations and implementations added on Day 27.

---

## Scenario 1 — Driver Disconnects Mid-Ride

**Trigger**: A driver's mobile device loses connectivity after a ride has started. Location
pings stop arriving. The driver's availability TTL expires in Redis.

**Component that fails**: Driver app network connectivity / Redis TTL.

**Impact**: User-facing — the rider's app can no longer show the driver's real-time location.
The ride record in PostgreSQL still shows IN_PROGRESS.

**Mitigation strategy**: TBD Day 27 — involves TTL-based detection, rider notification,
grace period before escalation to support queue.

---

## Scenario 2 — Matching Service Cannot Find a Driver

**Trigger**: A ride request arrives in a geographic area with no available drivers within
the search radius, or the Redis geo-index is temporarily empty due to a Redis restart.

**Component that fails**: Redis geo-index (empty or unavailable) / no available drivers.

**Impact**: User-facing — rider receives no match. Ride stays in REQUESTED state.

**Mitigation strategy**: TBD Day 27 — involves radius expansion retry, fallback to database
for driver lookup on Redis failure, timeout and cancellation with notification.

---

## Scenario 3 — Double-Accept Race Condition

**Trigger**: Two riders request rides at nearly the same time. Both match to the same nearby
driver. Both dispatch an acceptance request before the driver's availability flag is updated.

**Component that fails**: Driver availability state management under concurrency.

**Impact**: User-facing — driver is assigned to two rides simultaneously.

**Mitigation strategy**: TBD Day 27 — involves atomic availability flag update using Redis
`SETNX` or PostgreSQL row-level lock on driver record during assignment.

---

## Scenario 4 — Kafka Event Publish Failure

**Trigger**: The Kafka broker is unavailable when RideLifecycleService attempts to publish
a `RideStateChanged` event after committing a state transition to PostgreSQL.

**Component that fails**: Kafka broker / network.

**Impact**: Internal — ride state is correct in PostgreSQL, but downstream consumers
(billing, notifications) do not receive the state change event.

**Mitigation strategy**: TBD Day 27 — involves retry with exponential backoff, outbox
pattern as fallback, and reconciliation job for missed events.

---

## Scenario 5 — Redis Geo-Index Loss

**Trigger**: Redis is restarted without persistence enabled, or a memory eviction clears
the driver geo-index.

**Component that fails**: Redis (geo-index data).

**Impact**: Internal — MatchingService returns no results for any ride request until the
index is rebuilt. Recovery is automatic: drivers republish their location within 5 seconds.

**Mitigation strategy**: TBD Day 27 — involves Redis persistence configuration (AOF), health
check monitoring, and graceful degradation to PostgreSQL fallback during recovery window.
