# Architecture — Ride Sharing System

---

## Overview

The Ride Sharing System is built around two fundamentally different data access patterns that
must coexist: real-time geospatial queries against a continuously mutating driver location
index (low-latency, high-write, ephemeral data), and durable ride lifecycle management
(consistent state, immutable event log, long-lived records). Redis handles the former;
PostgreSQL handles the latter. The matching service bridges them, and Kafka propagates ride
lifecycle events to all interested parties.

---

## Architecture Diagram

```
┌───────────────────────────────────────────────────────────┐
│              Clients (Rider App / Driver App)             │
└───────────────────────┬───────────────────────────────────┘
                        │ HTTPS
┌───────────────────────▼───────────────────────────────────┐
│                    Load Balancer                           │
│       (Least-Connection, TLS Termination, Auth Headers)   │
└──────────┬────────────────────────────────┬───────────────┘
           │                                │
┌──────────▼──────────┐         ┌───────────▼──────────────┐
│      Ride API        │         │      Driver Location API  │
│  (rides, requests)   │         │  (location pings, status) │
└──────────┬──────────┘         └───────────┬──────────────┘
           │                                │
┌──────────▼──────────┐         ┌───────────▼──────────────┐
│   RideLifecycle     │         │   DriverLocationService   │
│      Service        │         │   (geo-index writes)      │
└──────────┬──────────┘         └───────────┬──────────────┘
           │                                │
┌──────────▼──────────┐         ┌───────────▼──────────────┐
│   MatchingService   │◄────────│         Redis             │
│  (geo-radius query) │         │  (GEOADD, availability)   │
└──────────┬──────────┘         └───────────────────────────┘
           │
┌──────────▼──────────┐
│     PostgreSQL       │
│  (rides, drivers,   │
│   users, vehicles)  │
└──────────┬──────────┘
           │
┌──────────▼──────────┐
│        Kafka         │
│  (ride.events topic) │
└─────────────────────┘
```

---

## Layer-by-Layer Description

### Load Balancer

The load balancer terminates TLS and distributes traffic across Ride API and Driver Location
API instances. For rider-facing traffic it uses round-robin — requests are stateless and any
instance can serve any rider. For driver WebSocket connections (if real-time push is added),
it would use IP-hash affinity, but in this implementation all driver communication is
poll-based REST, so round-robin applies throughout. Health checks ping `/health` every 10
seconds; failed instances are removed from rotation after three consecutive failures.

### Ride API

The Ride API handles ride requests, status queries, and ride lifecycle transitions. It is
stateless: every request carries the rider or driver identity in the Bearer token, and the
service layer fetches all state from the database on demand. The API validates inputs, enforces
authentication, applies rate limits, and delegates all business logic to the service layer.
It does not write to the location index and has no direct Redis dependency.

### Driver Location API

The Driver Location API accepts high-frequency location pings from the driver app
(`PATCH /drivers/{id}/location`) and status updates (`PATCH /drivers/{id}/status`). It is
separated from the Ride API because its traffic pattern is fundamentally different: thousands
of concurrent drivers each sending a location update every 5 seconds. Isolating this endpoint
allows it to be scaled independently and ensures a driver app flood cannot degrade the rider
booking experience.

### Ride Lifecycle Service

RideLifecycleService owns the state machine for every ride. It accepts and validates state
transition requests and enforces that each transition is legal from the current state. On
every successful transition, it writes the new state to PostgreSQL and publishes a ride event
to Kafka. It delegates the matching step to MatchingService when a ride enters the MATCHING
state.

### Matching Service

MatchingService is invoked by RideLifecycleService when a new ride is requested. It queries
Redis for available drivers within a configurable radius of the pickup coordinates using the
`GEORADIUS` command (or `GEOSEARCH` in Redis 6.2+). It ranks candidates by distance and
returns the ordered list to RideLifecycleService, which sends acceptance requests to the top
three. The first driver to accept claims the ride; the rest receive a cancellation signal.
MatchingService has no database dependency on the hot path — it reads exclusively from Redis.

### Driver Location Service

DriverLocationService accepts location pings from driver apps and writes them to the Redis
geospatial index using `GEOADD drivers {lng} {lat} {driver_id}`. It also sets a Redis key
`driver:{id}:available` with a 30-second TTL, renewed on each ping. When a driver's TTL
expires, they are automatically removed from the available pool — no explicit logout is
required. Location history is written to PostgreSQL asynchronously via a Kafka event; the
immediate Redis write is what matters for matching latency.

### Cache — Redis

Redis serves two distinct purposes here. First, it is the geospatial index for driver
locations, using Redis's native `GEO` commands which store coordinates in a sorted set with
Geohash encoding. Second, it stores driver availability state as a keyed TTL (`driver:{id}:available`).
Both are ephemeral: Redis holds no data that cannot be reconstructed from driver location
republications or the PostgreSQL ride store. If Redis is lost, drivers re-register their
locations within 5 seconds (one location ping interval), restoring the index automatically.

### Database — PostgreSQL

PostgreSQL stores all durable ride and driver data: the ride record, every ride state
transition, driver profiles, vehicle records, and user accounts. The `ride_events` table
is an append-only log of every state change with its timestamp and payload. The `drivers`
table holds the current `is_available` flag and last known coordinates — the latter updated
asynchronously from the Kafka stream, not on the hot location ping path.

### Message Queue — Kafka

The `rides.events` topic receives a `RideStateChanged` event for every ride lifecycle
transition. The topic is partitioned by `ride_id` to preserve per-ride event ordering.
Consumers include a billing service (triggers payment on COMPLETED), a notification service
(pushes updates to rider and driver apps), and a analytics pipeline. Event retention is
7 days.

---

## Component Responsibilities Summary.

| Component             | Responsibility                                   | Communicates Via      |
| --------------------- | ------------------------------------------------ | --------------------- |
| Load Balancer         | TLS termination, routing, health checks          | HTTPS (inbound)       |
| Ride API              | Ride request, status, lifecycle transitions      | HTTP (internal)       |
| Driver Location API   | High-frequency location + availability ingestion | HTTP (internal)       |
| RideLifecycleService  | Ride state machine, event publishing             | PostgreSQL + Kafka    |
| MatchingService       | Geospatial driver candidate query and ranking    | Redis                 |
| DriverLocationService | Geo-index writes, availability TTL management    | Redis + Kafka (async) |
| Redis                 | Driver geo-index + availability TTL store        | In-memory             |
| PostgreSQL            | Durable ride, driver, user, and event records    | TCP                   |
| Kafka                 | Ride lifecycle event stream                      | Kafka protocol        |
