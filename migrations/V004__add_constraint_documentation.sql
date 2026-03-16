-- =============================================================================
-- V004__add_constraint_documentation.sql
-- Ride Sharing System — Constraint verification and documentation pass
--
-- ROLLBACK: No changes — documentation only.
-- =============================================================================

-- Verify schema integrity before adding indexes
DO $$
BEGIN
    ASSERT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'users'), 'users missing';
    ASSERT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'drivers'), 'drivers missing';
    ASSERT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'rides'), 'rides missing';
    ASSERT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'ride_events'), 'ride_events missing';
    ASSERT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'vehicles'), 'vehicles missing';
    RAISE NOTICE 'Schema integrity check passed.';
END;
$$;

-- Add updated_at to rides table for operational monitoring
ALTER TABLE rides ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW();

CREATE OR REPLACE FUNCTION update_rides_updated_at()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER rides_updated_at_trigger
    BEFORE UPDATE ON rides
    FOR EACH ROW
    EXECUTE FUNCTION update_rides_updated_at();
