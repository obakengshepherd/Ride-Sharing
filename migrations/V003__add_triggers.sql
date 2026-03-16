-- =============================================================================
-- V003__add_triggers.sql
-- Ride Sharing System — Auto-update triggers
--
-- ROLLBACK:
--   DROP TRIGGER IF EXISTS drivers_updated_at_trigger ON drivers;
--   DROP FUNCTION IF EXISTS update_updated_at_column();
-- =============================================================================

CREATE OR REPLACE FUNCTION update_updated_at_column()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER drivers_updated_at_trigger
    BEFORE UPDATE ON drivers
    FOR EACH ROW
    EXECUTE FUNCTION update_updated_at_column();

COMMENT ON TRIGGER drivers_updated_at_trigger ON drivers IS
    'Auto-maintains updated_at on driver profile changes (availability, location sync).';
