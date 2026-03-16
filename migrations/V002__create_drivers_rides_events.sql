-- =============================================================================
-- V002__create_drivers_rides_events.sql
-- Ride Sharing System — drivers, rides, ride_events tables + vehicle FK
--
-- ROLLBACK:
--   DROP TABLE IF EXISTS ride_events CASCADE;
--   DROP TABLE IF EXISTS rides CASCADE;
--   DROP TABLE IF EXISTS drivers CASCADE;
--   ALTER TABLE vehicles DROP CONSTRAINT IF EXISTS vehicles_driver_fk;
-- =============================================================================

-- -----------------------------------------------------------------------------
-- drivers
-- Extends users with driver-specific operational attributes.
-- -----------------------------------------------------------------------------
CREATE TABLE drivers (
    id                  VARCHAR(36)    NOT NULL,
    user_id             VARCHAR(36)    NOT NULL,
    vehicle_id          VARCHAR(36)    NOT NULL,
    license_number      VARCHAR(32)    NOT NULL,
    rating              DECIMAL(3, 2)  NOT NULL DEFAULT 5.00,
    is_available        BOOLEAN        NOT NULL DEFAULT FALSE,
    last_location_lat   DECIMAL(10, 7) NULL,
    last_location_lng   DECIMAL(10, 7) NULL,
    updated_at          TIMESTAMPTZ    NOT NULL DEFAULT NOW(),

    CONSTRAINT drivers_pkey PRIMARY KEY (id),

    CONSTRAINT drivers_user_fk
        FOREIGN KEY (user_id) REFERENCES users (id)
        ON DELETE RESTRICT,

    CONSTRAINT drivers_vehicle_fk
        FOREIGN KEY (vehicle_id) REFERENCES vehicles (id)
        ON DELETE RESTRICT,

    CONSTRAINT drivers_user_id_unique UNIQUE (user_id),
    -- One driver profile per user — enforced at DB level.

    CONSTRAINT drivers_license_unique UNIQUE (license_number),

    CONSTRAINT drivers_rating_range
        CHECK (rating BETWEEN 1.00 AND 5.00),

    CONSTRAINT drivers_location_both_or_neither
        CHECK (
            (last_location_lat IS NULL AND last_location_lng IS NULL)
            OR
            (last_location_lat IS NOT NULL AND last_location_lng IS NOT NULL)
        )
    -- lat and lng must both be present or both be null.
    -- A partial location is meaningless and would corrupt geospatial queries.
);

COMMENT ON COLUMN drivers.last_location_lat IS
    'Asynchronously updated from Kafka stream — NOT the real-time matching index. '
    'Real-time location lives in Redis (GEOADD). This column is for analytics only.';

COMMENT ON COLUMN drivers.is_available IS
    'TRUE = accepting rides. Partial index on (id) WHERE is_available = TRUE '
    'in V005 keeps the matching pre-filter index small and fast.';

-- Now that drivers table exists, add the FK from vehicles to drivers
ALTER TABLE vehicles
    ADD CONSTRAINT vehicles_driver_fk
        FOREIGN KEY (driver_id) REFERENCES drivers (id)
        ON DELETE RESTRICT;

-- -----------------------------------------------------------------------------
-- rides
-- The central ride lifecycle entity.
-- -----------------------------------------------------------------------------
CREATE TABLE rides (
    id               VARCHAR(36)    NOT NULL,
    rider_id         VARCHAR(36)    NOT NULL,
    driver_id        VARCHAR(36)    NULL,
    status           ride_status    NOT NULL DEFAULT 'requested',
    pickup_lat       DECIMAL(10, 7) NOT NULL,
    pickup_lng       DECIMAL(10, 7) NOT NULL,
    dropoff_lat      DECIMAL(10, 7) NOT NULL,
    dropoff_lng      DECIMAL(10, 7) NOT NULL,
    pickup_address   VARCHAR(256)   NULL,
    dropoff_address  VARCHAR(256)   NULL,
    fare             DECIMAL(10, 2) NULL,
    currency         CHAR(3)        NOT NULL DEFAULT 'ZAR',
    distance_km      DECIMAL(8, 3)  NULL,
    requested_at     TIMESTAMPTZ    NOT NULL DEFAULT NOW(),
    accepted_at      TIMESTAMPTZ    NULL,
    started_at       TIMESTAMPTZ    NULL,
    completed_at     TIMESTAMPTZ    NULL,
    cancelled_at     TIMESTAMPTZ    NULL,

    CONSTRAINT rides_pkey PRIMARY KEY (id),

    CONSTRAINT rides_rider_fk
        FOREIGN KEY (rider_id) REFERENCES users (id)
        ON DELETE RESTRICT,

    CONSTRAINT rides_driver_fk
        FOREIGN KEY (driver_id) REFERENCES drivers (id)
        ON DELETE RESTRICT,

    CONSTRAINT rides_fare_positive CHECK (fare IS NULL OR fare > 0),
    CONSTRAINT rides_distance_positive CHECK (distance_km IS NULL OR distance_km > 0),

    -- Temporal consistency: timestamps must be in logical order
    CONSTRAINT rides_accepted_after_requested
        CHECK (accepted_at IS NULL OR accepted_at >= requested_at),
    CONSTRAINT rides_started_after_accepted
        CHECK (started_at IS NULL OR started_at >= accepted_at),
    CONSTRAINT rides_completed_after_started
        CHECK (completed_at IS NULL OR completed_at >= started_at)
);

COMMENT ON TABLE rides IS
    'Central ride lifecycle entity. '
    'status progresses through the ride_status enum in one direction only — '
    'state machine logic enforced at the application service layer. '
    'Timestamp columns record each transition immutably.';

-- -----------------------------------------------------------------------------
-- ride_events
-- Append-only audit log of every ride state transition.
-- -----------------------------------------------------------------------------
CREATE TABLE ride_events (
    id           VARCHAR(36)  NOT NULL,
    ride_id      VARCHAR(36)  NOT NULL,
    event_type   VARCHAR(64)  NOT NULL,
    payload      JSONB        NOT NULL DEFAULT '{}',
    occurred_at  TIMESTAMPTZ  NOT NULL DEFAULT NOW(),

    CONSTRAINT ride_events_pkey PRIMARY KEY (id),

    CONSTRAINT ride_events_ride_fk
        FOREIGN KEY (ride_id) REFERENCES rides (id)
        ON DELETE RESTRICT
);

COMMENT ON TABLE ride_events IS
    'Immutable event log for every ride state transition. '
    'Published to Kafka topic rides.events after each insert. '
    'Never updated or deleted.';
