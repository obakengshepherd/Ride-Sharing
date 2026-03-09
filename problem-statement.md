# Problem Statement — Ride Sharing System

---

## Section 1 — The Problem

A ride-sharing platform connects people who need a ride with drivers who are available nearby.
The core user experience depends entirely on speed: a rider taps a button and expects to see
a driver accepted and en route within seconds. Drivers need to receive only relevant requests —
ones close enough to be worth accepting. Any delay, mismatch, or race condition in the
matching process directly degrades the product. The business operates on utilisation: matched
rides generate revenue, unmatched requests generate churn.

---

## Section 2 — Why It Is Hard

- **Real-time geospatial matching**: Driver locations change every few seconds. The system
  must maintain a continuously updated spatial index across potentially hundreds of thousands
  of active drivers and query it with sub-second latency to find the nearest available candidates
  for each new ride request.

- **State consistency under concurrency**: A driver can only accept one ride at a time. When
  multiple riders request simultaneously and several are near the same driver, the system must
  ensure that driver is only matched to one ride — without a distributed lock that becomes a
  bottleneck.

- **Event-driven coordination**: The lifecycle of a ride — requested, matched, accepted,
  started, completed — spans multiple services and client connections. Each state transition
  must be durable, ordered, and propagated reliably to all interested parties (rider app,
  driver app, billing service).

- **Location data volume**: 200,000 drivers each publishing a location update every 5 seconds
  is 40,000 writes per second to the location index. This cannot hit a relational database
  directly — it requires a purpose-built in-memory spatial store.

- **Latency requirements**: From the rider's perspective, the time between tapping "Request"
  and receiving a confirmed match must feel near-instant. Any matching algorithm that requires
  multiple round trips to a slow data store will fail this expectation.

---

## Section 3 — Scope of This Implementation.

**In scope:**

- Rider-initiated ride requests with pickup and dropoff coordinates
- Real-time driver location ingestion and geospatial indexing via Redis
- Driver matching algorithm (nearest available driver within configurable radius)
- Ride lifecycle state machine: REQUESTED → MATCHING → ACCEPTED → IN_PROGRESS → COMPLETED / CANCELLED
- Driver availability management (toggle online/offline, auto-expire inactive drivers)
- Ride event publishing for each state transition
- REST API for ride operations and driver location updates

**Out of scope:**

- Fare calculation and dynamic pricing (surge pricing)
- In-app navigation or route optimisation
- Payment processing (assumed handled by the Payment Processing System)
- Driver rating and review system
- Driver background check or onboarding workflows
- Push notification delivery to mobile clients

---

## Section 4 — Success Criteria.

The system is working correctly when:

1. A ride request consistently returns a matched driver (or a clear no-driver-available
   response) within 2 seconds of submission under normal load.

2. No driver is simultaneously assigned to two active rides — concurrent match requests
   resolve to at most one accepted ride per driver.

3. Driver location data in the geospatial index is never more than 10 seconds stale for
   any online driver actively publishing updates.

4. Every ride state transition is persisted durably and the transition event is published
   to the event stream before the API response is returned to the client.

5. When a driver goes offline or stops sending location updates, the system removes them
   from the available pool within one TTL window (≤30 seconds) and does not match them
   to new requests.

6. The system handles 10,000 concurrent active rides and 200,000 tracked driver locations
   without degradation in matching latency.
