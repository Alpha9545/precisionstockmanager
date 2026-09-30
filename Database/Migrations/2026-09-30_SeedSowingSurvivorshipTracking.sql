-- ============================================================================
-- 2026-09-30_SeedSowingSurvivorshipTracking.sql
--
-- Phase H: the "Sowing Records" replacement identified during the old-system
-- removal (2026-09-30 analysis) -- the useful part of the retired legacy
-- dbo.SeedEntries pipeline (TraysAlive/TraysDate, HardeningAlive/
-- HardeningDate, AliveCount/InventoryDate, location/locationdesc), carried
-- into the new dbo.SeedSowings workflow.
--
-- WHAT THIS DOES NOT DO: "Final Ready/Alive" is NOT rebuilt here -- it
-- already exists as dbo.ReadyConfirmations / SeedSowings.ConfirmedReadyQuantity
-- (Phase 25/K), unchanged, and is AliveCount/InventoryDate's true successor.
-- This script only adds the two INTERMEDIATE checkpoints the new system had
-- no equivalent for (Trays Alive, Hardening Alive) plus Location, as six new
-- NULLABLE columns on dbo.SeedSowings:
--     TraysAliveQuantity      DECIMAL(18,2) NULL
--     TraysAliveDate          DATE          NULL
--     HardeningAliveQuantity  DECIMAL(18,2) NULL
--     HardeningAliveDate      DATE          NULL
--     Location                NVARCHAR(100) NULL
--     LocationDescription     NVARCHAR(200) NULL
--
-- WHY NO TRIGGER CHANGE IS NEEDED: dbo.TR_SeedSowings_ImmutableTrayData
-- (Database/PhaseD_ProductionRestructure.sql) checks an explicit, named list
-- of columns (CavityType, SourceType, SourceSeedStockId, SourceCuttingStockId,
-- SpeciesId, QuantitySown, NumberOfTrays, SeedQuantity, SupervisorId,
-- CreatedById) -- new columns are outside its reach by construction, so these
-- six fields remain editable after the sowing is saved (via the new
-- SeedSowingRepository.UpdateSurvivorshipAsync, same shape as the existing
-- UpdateRemarksAsync), exactly like Remarks/ConfirmedReadyQuantity already are.
--
-- PURELY INFORMATIONAL: none of these six columns feed WastageQuantity,
-- ConfirmedReadyQuantity, RemainingReadyQuantity or any stock/ledger
-- calculation -- same as the old dbo.SeedEntries fields they replace, which
-- were also display-only. Application-side validation
-- (Services/DirectSowingRules.cs: ValidateSurvivorshipCheckpoint) only checks
-- each quantity, if provided, is a non-negative whole number not exceeding
-- the sowing's own QuantitySown.
--
-- Existing data: every existing dbo.SeedSowings row gets NULL in all six new
-- columns (meaning "no checkpoint recorded yet") -- no existing row's
-- existing column values are read or changed. dbo.SeedEntries, dbo.Inventory,
-- dbo.InventoryTransactions are not touched by this script (their row counts
-- are only SELECTed below, for verification).
--
-- SAFETY: guarded to PlantsIMS2_Test only; one transaction; idempotent
-- (IF NOT EXISTS per column, so a re-run adds nothing). Ends in ROLLBACK --
-- this is a DRY RUN. Take a backup, review the verification output below,
-- get explicit approval, THEN change the final line to
-- COMMIT TRANSACTION SeedSowingSurvivorship and re-run, per this project's
-- standing DB-safety rule.
--
-- NOT executed against any database by this script's author.
-- ============================================================================
IF DB_NAME() <> N'PlantsIMS2_Test'
    THROW 50330, '2026-09-30_SeedSowingSurvivorshipTracking.sql may only be run against PlantsIMS2_Test.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION SeedSowingSurvivorship;

BEGIN TRY

    ----------------------------------------------------------------------
    -- 1. Six new nullable columns, added only if missing (idempotent)
    ----------------------------------------------------------------------
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SeedSowings') AND name = 'TraysAliveQuantity')
        ALTER TABLE dbo.SeedSowings ADD TraysAliveQuantity DECIMAL(18,2) NULL;
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SeedSowings') AND name = 'TraysAliveDate')
        ALTER TABLE dbo.SeedSowings ADD TraysAliveDate DATE NULL;
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SeedSowings') AND name = 'HardeningAliveQuantity')
        ALTER TABLE dbo.SeedSowings ADD HardeningAliveQuantity DECIMAL(18,2) NULL;
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SeedSowings') AND name = 'HardeningAliveDate')
        ALTER TABLE dbo.SeedSowings ADD HardeningAliveDate DATE NULL;
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SeedSowings') AND name = 'Location')
        ALTER TABLE dbo.SeedSowings ADD Location NVARCHAR(100) NULL;
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SeedSowings') AND name = 'LocationDescription')
        ALTER TABLE dbo.SeedSowings ADD LocationDescription NVARCHAR(200) NULL;

    ----------------------------------------------------------------------
    -- VERIFICATION (before the ROLLBACK, so it shows what WOULD happen)
    ----------------------------------------------------------------------
    PRINT '=== New columns present on dbo.SeedSowings (expect 6) ===';
    SELECT name, is_nullable, TYPE_NAME(system_type_id) AS DataType
    FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.SeedSowings')
      AND name IN ('TraysAliveQuantity','TraysAliveDate','HardeningAliveQuantity','HardeningAliveDate','Location','LocationDescription')
    ORDER BY name;

    -- Dynamic SQL: a direct SELECT referencing a column added earlier by a
    -- conditional ALTER TABLE in this SAME batch fails deferred name
    -- resolution ("Invalid column name") even though the ALTER already ran --
    -- the same issue Database/Migrations/2026-09-28_CuttingSowingSupervisorByArea.sql
    -- worked around with EXEC(N'...'). Verified empirically before commit: a
    -- direct (non-dynamic) version of this query threw Msg 207 on a dry run
    -- against PlantsIMS2_Test, with zero columns added and the transaction
    -- cleanly rolled back (confirmed via sys.columns + @@TRANCOUNT = 0
    -- afterward) -- so this EXEC wrapping is required, not optional.
    PRINT '=== Existing rows: all six new columns NULL (expect this count = total row count below) ===';
    EXEC(N'
    SELECT COUNT(*) AS RowsWithAllSixNull FROM dbo.SeedSowings
    WHERE TraysAliveQuantity IS NULL AND TraysAliveDate IS NULL
      AND HardeningAliveQuantity IS NULL AND HardeningAliveDate IS NULL
      AND Location IS NULL AND LocationDescription IS NULL;');
    SELECT COUNT(*) AS TotalSeedSowingsRows FROM dbo.SeedSowings;

    PRINT '=== TR_SeedSowings_ImmutableTrayData definition unaffected (expect 1) ===';
    SELECT COUNT(*) AS ImmutabilityTriggerUnaffected FROM sys.triggers
    WHERE name = N'TR_SeedSowings_ImmutableTrayData'
      AND OBJECT_DEFINITION(object_id) NOT LIKE '%TraysAliveQuantity%'
      AND OBJECT_DEFINITION(object_id) NOT LIKE '%HardeningAliveQuantity%';

    PRINT '=== All SeedSowings triggers still present and enabled (expect 3, no change) ===';
    SELECT name, is_disabled FROM sys.triggers WHERE parent_id = OBJECT_ID('dbo.SeedSowings') ORDER BY name;

    PRINT '=== dbo.SeedEntries / dbo.Inventory / dbo.InventoryTransactions row counts (informational only -- untouched by this script) ===';
    SELECT (SELECT COUNT(*) FROM dbo.SeedEntries) AS SeedEntriesRows,
           (SELECT COUNT(*) FROM dbo.Inventory) AS InventoryRows,
           (SELECT COUNT(*) FROM dbo.InventoryTransactions) AS InventoryTransactionsRows;

END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION SeedSowingSurvivorship;
    THROW;
END CATCH;

----------------------------------------------------------------------------
-- Approved 2026-09-30: user reviewed the dry run (only dbo.SeedSowings
-- changed; exactly the 6 approved nullable columns; all 9 existing rows
-- unchanged -- data checksum identical before/after; no permission or other
-- schema changes) and explicitly approved applying it. Verified backup:
-- PlantsIMS2_Test_PreSeedSowingSurvivorshipCommit_20260930_104133.bak
----------------------------------------------------------------------------
COMMIT TRANSACTION SeedSowingSurvivorship;
GO
