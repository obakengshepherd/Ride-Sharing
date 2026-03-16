-- =============================================================================
-- V001__create_users_and_vehicles.sql
-- Ride Sharing System — Custom types + users + vehicles tables
--
-- ROLLBACK:
--   DROP TABLE IF EXISTS vehicles CASCADE;
--   DROP TABLE IF EXISTS users CASCADE;
--   DROP TYPE IF EXISTS ride_status;
--   DROP TYPE IF EXISTS user_type;
--   DROP TYPE IF EXISTS user_status;
-- =============================================================================

-- Custom enum types
CREATE TYPE user_type    AS ENUM ('rider', 'driver');
CREATE TYPE user_status  AS ENUM ('active', 'suspended', 'deleted');
CREATE TYPE ride_status  AS ENUM ('requested', 'matching', 'accepted', 'in_progress', 'completed', 'cancelled');

-- Why ride_status is an enum: The ride lifecycle must follow a strict valid
-- sequence. A VARCHAR column would accept any string — a bug writing
-- status = 'complted' (typo) would be stored silently. An enum rejects
-- anything outside the defined set immediately at the storage layer.

-- -----------------------------------------------------------------------------
-- users
-- Base identity for both riders and drivers.
-- -----------------------------------------------------------------------------
CREATE TABLE users (
    id          VARCHAR(36)   NOT NULL,
    name        VARCHAR(128)  NOT NULL,
    email       VARCHAR(255)  NOT NULL,
    phone       VARCHAR(20)   NOT NULL,
    type        user_type     NOT NULL,
    status      user_status   NOT NULL DEFAULT 'active',
    created_at  TIMESTAMPTZ   NOT NULL DEFAULT NOW(),

    CONSTRAINT users_pkey PRIMARY KEY (id),
    CONSTRAINT users_email_unique UNIQUE (email),
    CONSTRAINT users_phone_unique UNIQUE (phone)
);

COMMENT ON TABLE users IS
    'Base identity for riders and drivers. '
    'Soft-deleted via status=deleted — hard delete would orphan ride history.';

-- -----------------------------------------------------------------------------
-- vehicles
-- Driver vehicle registration. A driver has one active vehicle.
-- -----------------------------------------------------------------------------
CREATE TABLE vehicles (
    id          VARCHAR(36)  NOT NULL,
    driver_id   VARCHAR(36)  NOT NULL,
    make        VARCHAR(64)  NOT NULL,
    model       VARCHAR(64)  NOT NULL,
    plate       VARCHAR(16)  NOT NULL,
    colour      VARCHAR(32)  NOT NULL,
    year        SMALLINT     NOT NULL,

    CONSTRAINT vehicles_pkey PRIMARY KEY (id),
    CONSTRAINT vehicles_plate_unique UNIQUE (plate),
    CONSTRAINT vehicles_year_check CHECK (year >= 2000),

    -- vehicles.driver_id added as FK in V002 after drivers table exists
    CONSTRAINT vehicles_model_year_check CHECK (year <= EXTRACT(YEAR FROM NOW()) + 1)
);

COMMENT ON TABLE vehicles IS
    'Vehicle registered to a driver. plate is globally unique.';
