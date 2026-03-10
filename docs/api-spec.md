# API Specification — Ride Sharing System

---

## Overview

The Ride Sharing API manages the full lifecycle of a ride: from a rider requesting a match,
through driver acceptance and trip execution, to completion. It also handles the high-frequency
driver location update stream. Consumed by rider mobile apps, driver mobile apps, and internal
services (billing, notifications). Location endpoints are separated from ride lifecycle endpoints
to allow independent scaling.

---

## Base URL and Versioning

```
https://api.rides.internal/api/v1
```

Versioning is path-based. The driver location endpoint (`PATCH /drivers/{id}/location`) is
on a dedicated high-throughput path and may be independently deployed under the same version.

---

## Authentication

```
Authorization: Bearer <jwt_token>
```

The `role` claim in the token determines access: `rider` may request rides; `driver` may
accept, start, and complete rides and update location. Cross-role access is rejected with
**403 Forbidden**.

---

## Common Response Envelope

### Success
```json
{
  "data": { ... },
  "meta": { "request_id": "uuid", "timestamp": "2024-01-15T10:30:00Z" }
}
```

### Error
```json
{
  "error": {
    "code": "DRIVER_NOT_AVAILABLE",
    "message": "No drivers available within the search radius.",
    "details": []
  },
  "meta": { "request_id": "uuid", "timestamp": "2024-01-15T10:30:00Z" }
}
```

---

## Rate Limiting

| Endpoint                       | Limit               | Scope        |
|-------------------------------|---------------------|--------------|
| `POST /rides/request`          | 5 / minute          | Per user     |
| `PATCH /drivers/{id}/location` | 30 / minute         | Per driver   |
| All other endpoints            | 60 / minute         | Per user     |

Headers: `X-RateLimit-Limit`, `X-RateLimit-Remaining`, `X-RateLimit-Reset`, `Retry-After`

---

## Endpoints

---

### POST /rides/request

**Description:** Rider submits a new ride request with pickup and dropoff coordinates.
Returns a ride ID immediately; matching runs asynchronously.

**Request Body:**

| Field              | Type   | Required | Validation                   | Example         |
|--------------------|--------|----------|------------------------------|-----------------|
| `pickup_lat`       | float  | Yes      | -90 to 90                    | `-26.2041`      |
| `pickup_lng`       | float  | Yes      | -180 to 180                  | `28.0473`       |
| `dropoff_lat`      | float  | Yes      | -90 to 90                    | `-26.1929`      |
| `dropoff_lng`      | float  | Yes      | -180 to 180                  | `28.0305`       |
| `pickup_address`   | string | No       | max 256 chars                | `"14 Main St"`  |
| `dropoff_address`  | string | No       | max 256 chars                | `"Park Ave"`    |

**Example Request:**
```json
{
  "pickup_lat": -26.2041,
  "pickup_lng": 28.0473,
  "dropoff_lat": -26.1929,
  "dropoff_lng": 28.0305,
  "pickup_address": "14 Main Street, Johannesburg",
  "dropoff_address": "Park Avenue, Parktown"
}
```

**Response — 201 Created:**
```json
{
  "data": {
    "ride_id": "ride_01j9z3k4m5n6p7q8",
    "status": "REQUESTED",
    "pickup_lat": -26.2041,
    "pickup_lng": 28.0473,
    "dropoff_lat": -26.1929,
    "dropoff_lng": 28.0305,
    "requested_at": "2024-01-15T10:30:00Z"
  },
  "meta": { ... }
}
```

**Status Codes:**

| Code | Condition                                  |
|------|--------------------------------------------|
| 201  | Ride request created, matching in progress |
| 400  | Invalid coordinates or missing fields      |
| 401  | Unauthorized                               |
| 403  | Token role is not `rider`                  |
| 422  | Rider already has an active ride           |
| 429  | Rate limit exceeded                        |

---

### POST /rides/{id}/accept

**Description:** Driver accepts a dispatched ride request. Only the driver who received the
dispatch may accept. First acceptance wins; subsequent calls return 409.

**Path Parameters:** `id` — Ride ID

**Response — 200 OK:**
```json
{
  "data": {
    "ride_id": "ride_01j9z3k4m5n6p7q8",
    "status": "ACCEPTED",
    "driver_id": "drv_xyz789",
    "driver_name": "Sipho Dlamini",
    "vehicle": { "make": "Toyota", "model": "Corolla", "plate": "GP 123-456" },
    "eta_minutes": 4,
    "accepted_at": "2024-01-15T10:30:15Z"
  },
  "meta": { ... }
}
```

**Status Codes:**

| Code | Condition                                           |
|------|-----------------------------------------------------|
| 200  | Ride accepted                                       |
| 401  | Unauthorized                                        |
| 403  | Driver was not dispatched for this ride             |
| 404  | Ride not found                                      |
| 409  | Another driver has already accepted this ride       |
| 422  | Ride is not in MATCHING status                      |

---

### POST /rides/{id}/start

**Description:** Driver marks the ride as started (passenger is in the vehicle).
Ride must be in ACCEPTED status.

**Path Parameters:** `id` — Ride ID

**Response — 200 OK:**
```json
{
  "data": {
    "ride_id": "ride_01j9z3k4m5n6p7q8",
    "status": "IN_PROGRESS",
    "started_at": "2024-01-15T10:36:00Z"
  },
  "meta": { ... }
}
```

**Status Codes:**

| Code | Condition                              |
|------|----------------------------------------|
| 200  | Ride started                           |
| 401  | Unauthorized                           |
| 403  | Caller is not the assigned driver      |
| 404  | Ride not found                         |
| 422  | Ride is not in ACCEPTED status         |

---

### POST /rides/{id}/complete

**Description:** Driver marks the ride as completed. Calculates and records fare.
Ride must be in IN_PROGRESS status.

**Path Parameters:** `id` — Ride ID

**Response — 200 OK:**
```json
{
  "data": {
    "ride_id": "ride_01j9z3k4m5n6p7q8",
    "status": "COMPLETED",
    "fare": "85.50",
    "currency": "ZAR",
    "distance_km": "12.4",
    "duration_minutes": 24,
    "completed_at": "2024-01-15T11:00:00Z"
  },
  "meta": { ... }
}
```

**Status Codes:**

| Code | Condition                              |
|------|----------------------------------------|
| 200  | Ride completed                         |
| 401  | Unauthorized                           |
| 403  | Caller is not the assigned driver      |
| 404  | Ride not found                         |
| 422  | Ride is not in IN_PROGRESS status      |

---

### GET /rides/{id}

**Description:** Returns the current state and details of any ride. Accessible by the
assigned rider or driver.

**Path Parameters:** `id` — Ride ID

**Response — 200 OK:**
```json
{
  "data": {
    "ride_id": "ride_01j9z3k4m5n6p7q8",
    "status": "IN_PROGRESS",
    "rider_id": "usr_abc123",
    "driver_id": "drv_xyz789",
    "pickup_lat": -26.2041,
    "pickup_lng": 28.0473,
    "dropoff_lat": -26.1929,
    "dropoff_lng": 28.0305,
    "requested_at": "2024-01-15T10:30:00Z",
    "accepted_at": "2024-01-15T10:30:15Z",
    "started_at": "2024-01-15T10:36:00Z",
    "completed_at": null,
    "fare": null
  },
  "meta": { ... }
}
```

**Status Codes:**

| Code | Condition                                      |
|------|------------------------------------------------|
| 200  | Success                                        |
| 401  | Unauthorized                                   |
| 403  | Caller is neither the rider nor driver         |
| 404  | Ride not found                                 |

---

### GET /drivers/nearby

**Description:** Returns a list of available drivers near the given coordinates.
Accessible by riders. Used for display purposes — does not trigger a ride request.

**Query Parameters:**

| Parameter | Type  | Required | Default | Description         |
|-----------|-------|----------|---------|---------------------|
| `lat`     | float | Yes      | —       | Latitude            |
| `lng`     | float | Yes      | —       | Longitude           |
| `radius`  | float | No       | `2.0`   | Radius in km, max 10|

**Response — 200 OK:**
```json
{
  "data": [
    {
      "driver_id": "drv_xyz789",
      "distance_km": "0.8",
      "eta_minutes": 3,
      "vehicle": { "make": "Toyota", "model": "Corolla" }
    }
  ],
  "meta": { ... }
}
```

**Status Codes:**

| Code | Condition                    |
|------|------------------------------|
| 200  | Success (empty list = none)  |
| 400  | Invalid coordinates          |
| 401  | Unauthorized                 |

---

### PATCH /drivers/{id}/location

**Description:** Driver app publishes current GPS coordinates. Called every 5 seconds.
Updates the Redis geospatial index. High-frequency endpoint — no response body for speed.

**Path Parameters:** `id` — Driver ID (must match authenticated token)

**Request Body:**

| Field       | Type  | Required | Validation      | Example    |
|-------------|-------|----------|-----------------|------------|
| `latitude`  | float | Yes      | -90 to 90       | `-26.2041` |
| `longitude` | float | Yes      | -180 to 180     | `28.0473`  |
| `heading`   | float | No       | 0 to 360        | `270.0`    |
| `speed_kmh` | float | No       | >= 0            | `42.5`     |

**Response — 204 No Content** (no body for performance)

**Status Codes:**

| Code | Condition                              |
|------|----------------------------------------|
| 204  | Location updated                       |
| 400  | Invalid coordinates                    |
| 401  | Unauthorized                           |
| 403  | Driver ID does not match token         |
| 422  | Driver is not in AVAILABLE status      |
| 429  | Rate limit exceeded (30/min)           |
