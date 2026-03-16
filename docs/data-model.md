# Data Model — Ride Sharing System

---

## Database Technology Choices

### PostgreSQL (Durable state)
All ride records, driver profiles, vehicles, and event history live in PostgreSQL. Ride state
transitions require strong consistency — a ride that has been accepted by one driver must
be immediately visible as unavailable to all other drivers attempting to accept it. PostgreSQL
row-level locks and ACID transactions provide this guarantee.

### Redis (Ephemeral geospatial state)
Driver locations and availability are stored exclusively in Redis using its native geospatial
commands (`GEOADD`, `GEOSEARCH`). This data is high-write (40,000 updates/sec), ephemeral
(a location more than 30 seconds old is irrelevant), and self-healing (drivers republish
location every 5 seconds). Storing it in PostgreSQL would create unsustainable write pressure
on the primary. Redis is not the source of truth — it is the working index.

---

## Entity Relationship Overview

A **User** is the base identity for both riders and drivers. The `type` column on the users
table distinguishes them; a user can hold only one type at a time.

A **Driver** extends a user with operational attributes: their current availability flag,
last known coordinates, rating, and a reference to their **Vehicle**.

A **Ride** links a rider (user) to a driver and progresses through a strict state machine.
Each state transition is recorded as a **RideEvent** — the append-only history of what
happened to every ride. The rides table holds current state; ride_events hold the full audit
trail.

---

## Table Definitions

### `users`

| Column       | Type          | Constraints                          | Description                          |
|--------------|---------------|--------------------------------------|--------------------------------------|
| `id`         | `VARCHAR(36)` | PRIMARY KEY                          | Prefixed UUID: `usr_<uuid>`          |
| `name`       | `VARCHAR(128)`| NOT NULL                             | Display name                         |
| `email`      | `VARCHAR(255)`| NOT NULL, UNIQUE                     | Login identifier                     |
| `phone`      | `VARCHAR(20)` | NOT NULL, UNIQUE                     | Contact number                       |
| `type`       | `user_type`   | NOT NULL                             | Enum: `rider`, `driver`              |
| `status`     | `user_status` | NOT NULL, DEFAULT 'active'           | Enum: `active`, `suspended`, `deleted`|
| `created_at` | `TIMESTAMPTZ` | NOT NULL, DEFAULT NOW()              | Registration timestamp               |

### `vehicles`

| Column    | Type          | Constraints               | Description             |
|-----------|---------------|---------------------------|-------------------------|
| `id`      | `VARCHAR(36)` | PRIMARY KEY               | Prefixed UUID: `veh_<uuid>` |
| `driver_id`| `VARCHAR(36)` | NOT NULL, FK → drivers   | Owning driver           |
| `make`    | `VARCHAR(64)` | NOT NULL                  | e.g. Toyota             |
| `model`   | `VARCHAR(64)` | NOT NULL                  | e.g. Corolla            |
| `plate`   | `VARCHAR(16)` | NOT NULL, UNIQUE          | Registration plate      |
| `colour`  | `VARCHAR(32)` | NOT NULL                  | Vehicle colour          |
| `year`    | `SMALLINT`    | NOT NULL, CHECK (year >= 2000) | Model year         |

### `drivers`

| Column               | Type           | Constraints              | Description                                    |
|----------------------|----------------|--------------------------|------------------------------------------------|
| `id`                 | `VARCHAR(36)`  | PRIMARY KEY              | Prefixed UUID: `drv_<uuid>`                    |
| `user_id`            | `VARCHAR(36)`  | NOT NULL, UNIQUE, FK → users | One driver profile per user                |
| `vehicle_id`         | `VARCHAR(36)`  | NOT NULL, FK → vehicles  | Currently registered vehicle                   |
| `license_number`     | `VARCHAR(32)`  | NOT NULL, UNIQUE         | Driver's license number                        |
| `rating`             | `DECIMAL(3,2)` | NOT NULL, DEFAULT 5.00, CHECK (rating BETWEEN 1.00 AND 5.00) | Average rating |
| `is_available`       | `BOOLEAN`      | NOT NULL, DEFAULT FALSE  | Whether the driver is accepting rides          |
| `last_location_lat`  | `DECIMAL(10,7)`| NULL                     | Last known latitude — async from Redis stream  |
| `last_location_lng`  | `DECIMAL(10,7)`| NULL                     | Last known longitude                           |
| `updated_at`         | `TIMESTAMPTZ`  | NOT NULL, DEFAULT NOW()  | Last profile update                            |

**Note on coordinates:** `last_location_lat/lng` in PostgreSQL are NOT the real-time
matching index — that lives in Redis. These columns are updated asynchronously from
Kafka events for analytics and history. The precision of `DECIMAL(10,7)` gives ~1cm
accuracy, sufficient for all matching and analytics purposes.

### `rides`

| Column          | Type           | Constraints                    | Description                               |
|-----------------|----------------|--------------------------------|-------------------------------------------|
| `id`            | `VARCHAR(36)`  | PRIMARY KEY                    | Prefixed UUID: `ride_<uuid>`              |
| `rider_id`      | `VARCHAR(36)`  | NOT NULL, FK → users           | The requesting rider                      |
| `driver_id`     | `VARCHAR(36)`  | NULL, FK → drivers             | Assigned driver (null until accepted)     |
| `status`        | `ride_status`  | NOT NULL, DEFAULT 'requested'  | Enum (see below)                          |
| `pickup_lat`    | `DECIMAL(10,7)`| NOT NULL                       | Pickup latitude                           |
| `pickup_lng`    | `DECIMAL(10,7)`| NOT NULL                       | Pickup longitude                          |
| `dropoff_lat`   | `DECIMAL(10,7)`| NOT NULL                       | Dropoff latitude                          |
| `dropoff_lng`   | `DECIMAL(10,7)`| NOT NULL                       | Dropoff longitude                         |
| `pickup_address`| `VARCHAR(256)` | NULL                           | Human-readable pickup address             |
| `dropoff_address`| `VARCHAR(256)`| NULL                           | Human-readable dropoff address            |
| `fare`          | `DECIMAL(10,2)`| NULL, CHECK (fare > 0)         | Calculated on completion                  |
| `currency`      | `CHAR(3)`      | NOT NULL, DEFAULT 'ZAR'        | Fare currency                             |
| `distance_km`   | `DECIMAL(8,3)` | NULL                           | Calculated on completion                  |
| `requested_at`  | `TIMESTAMPTZ`  | NOT NULL, DEFAULT NOW()        | Immutable                                 |
| `accepted_at`   | `TIMESTAMPTZ`  | NULL                           | Set on ACCEPTED transition                |
| `started_at`    | `TIMESTAMPTZ`  | NULL                           | Set on IN_PROGRESS transition             |
| `completed_at`  | `TIMESTAMPTZ`  | NULL                           | Set on COMPLETED transition               |
| `cancelled_at`  | `TIMESTAMPTZ`  | NULL                           | Set on CANCELLED transition               |

**Ride status enum values:** `requested`, `matching`, `accepted`, `in_progress`, `completed`, `cancelled`

**Why an enum and not a VARCHAR?** The database-level enum type means the PostgreSQL
engine rejects any value outside the defined set at the storage layer, independently of
any application-level validation. A bug in the application code cannot write an invalid
status string to the database.

### `ride_events`

| Column        | Type          | Constraints              | Description                         |
|---------------|---------------|--------------------------|-------------------------------------|
| `id`          | `VARCHAR(36)` | PRIMARY KEY              | Prefixed UUID: `rev_<uuid>`         |
| `ride_id`     | `VARCHAR(36)` | NOT NULL, FK → rides     | The ride this event belongs to      |
| `event_type`  | `VARCHAR(64)` | NOT NULL                 | e.g. `RideRequested`, `RideAccepted`|
| `payload`     | `JSONB`       | NOT NULL, DEFAULT '{}'   | Event-specific data                 |
| `occurred_at` | `TIMESTAMPTZ` | NOT NULL, DEFAULT NOW()  | Immutable                           |

---

## Index Strategy

| Index Name                       | Table         | Columns                    | Type   | Query Pattern                             |
|----------------------------------|---------------|----------------------------|--------|-------------------------------------------|
| `users_email_uniq`               | `users`       | `(email)`                  | UNIQUE | Login lookup                              |
| `rides_rider_id_idx`             | `rides`       | `(rider_id)`               | B-tree | Rider's ride history                      |
| `rides_driver_id_idx`            | `rides`       | `(driver_id)`              | B-tree | Driver's ride history                     |
| `rides_status_requested_at_idx`  | `rides`       | `(status, requested_at DESC)` | B-tree | Active ride monitoring / dispatch         |
| `drivers_available_partial_idx`  | `drivers`     | `(id) WHERE is_available = true` | Partial B-tree | Match only available drivers — filters index to active subset |
| `ride_events_ride_id_idx`        | `ride_events` | `(ride_id, occurred_at)`   | B-tree | Fetch event history for a single ride     |
| `vehicles_plate_uniq`            | `vehicles`    | `(plate)`                  | UNIQUE | Enforce unique registration plates        |

**Why a partial index on `drivers(is_available)`?** At any given time the majority of
drivers are offline. A full index on `is_available` would contain mostly `FALSE` entries
that are useless for matching queries. A partial index where `is_available = TRUE` is
smaller, fits entirely in memory, and answers matching pre-filter queries in microseconds.

---

## Relationship Types

- **User → Driver**: one-to-one (enforced by UNIQUE on `drivers.user_id`).
- **Driver → Vehicle**: many-to-one. A driver has one active vehicle; a vehicle belongs to one driver.
- **User (rider) → Rides**: one-to-many.
- **Driver → Rides**: one-to-many (sequential — a driver is in at most one active ride at a time, enforced at the application layer).
- **Ride → RideEvents**: one-to-many, append-only.

---

## Soft Delete Strategy

Users are given a `status = 'deleted'` flag rather than a hard DELETE. This preserves
referential integrity: deleting a user who has completed rides would orphan `rides.rider_id`
foreign keys. Deleted users cannot log in but their historical ride records remain intact.

Rides, ride_events, and vehicles are never deleted.

---

## Audit Trail

| Table          | `created_at`   | `updated_at` | Notes                                         |
|----------------|----------------|--------------|-----------------------------------------------|
| `users`        | ✓              | ✗            | Immutable after creation (status changes only)|
| `drivers`      | ✗              | ✓            | Updated on availability toggle, location sync |
| `rides`        | `requested_at` | ✗            | Uses specific timestamp columns per transition|
| `ride_events`  | `occurred_at`  | ✗            | Append-only, immutable                        |
| `vehicles`     | ✗              | ✗            | Static reference data                         |
