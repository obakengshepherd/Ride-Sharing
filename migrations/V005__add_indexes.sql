-- =============================================================================
-- V005__add_indexes.sql
-- Ride Sharing System — All performance indexes
--
-- ROLLBACK (reverse order):
--   DROP INDEX IF EXISTS ride_events_ride_id_occurred_idx;
--   DROP INDEX IF EXISTS rides_status_requested_at_idx;
--   DROP INDEX IF EXISTS rides_driver_id_idx;
--   DROP INDEX IF EXISTS rides_rider_id_idx;
--   DROP INDEX IF EXISTS drivers_available_partial_idx;
-- =============================================================================

-- -----------------------------------------------------------------------------
-- drivers indexes
-- -----------------------------------------------------------------------------

-- PARTIAL INDEX — the most important index in this system.
-- Query: "Find all available drivers near pickup" (matching pre-filter)
-- Only indexes rows where is_available = TRUE.
--
-- Why partial? At any moment the majority of registered drivers are OFFLINE.
-- A full index on is_available would contain mostly FALSE entries — wasted space
-- and slower scans. A partial index where is_available = TRUE is an order of
-- magnitude smaller and fits entirely in memory at scale.
CREATE INDEX drivers_available_partial_idx
    ON drivers (id)
    WHERE is_available = TRUE;

COMMENT ON INDEX drivers_available_partial_idx IS
    'Partial index: only available drivers. Dramatically smaller than a full index. '
    'Used by MatchingService to pre-filter candidates before Redis geo query.';

-- -----------------------------------------------------------------------------
-- rides indexes
-- -----------------------------------------------------------------------------

-- Query: GET /rides/{id} by rider — "What are my recent rides?"
CREATE INDEX rides_rider_id_idx
    ON rides (rider_id, requested_at DESC);

COMMENT ON INDEX rides_rider_id_idx IS
    'Rider ride history ordered newest first.';

-- Query: Driver's ride history
CREATE INDEX rides_driver_id_idx
    ON rides (driver_id, requested_at DESC);

COMMENT ON INDEX rides_driver_id_idx IS
    'Driver ride history ordered newest first.';

-- Query: Operational monitoring — "All active rides by status"
-- Compound index: status + requested_at for filtered range scans
CREATE INDEX rides_status_requested_at_idx
    ON rides (status, requested_at DESC);

COMMENT ON INDEX rides_status_requested_at_idx IS
    'Supports dispatch monitoring: find all MATCHING rides older than 30s. '
    'Also used by the ride timeout job to find stale unmatched requests.';

-- -----------------------------------------------------------------------------
-- ride_events indexes
-- -----------------------------------------------------------------------------

-- Query: "Get all events for ride X in order" — audit, replay, webhook delivery
CREATE INDEX ride_events_ride_id_occurred_idx
    ON ride_events (ride_id, occurred_at ASC);

COMMENT ON INDEX ride_events_ride_id_occurred_idx IS
    'Event replay and audit: fetch all events for a ride in chronological order.';

-- -----------------------------------------------------------------------------
ANALYZE users;
ANALYZE drivers;
ANALYZE vehicles;
ANALYZE rides;
ANALYZE ride_events;
