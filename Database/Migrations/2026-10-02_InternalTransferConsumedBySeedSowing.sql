-- ============================================================================
-- 2026-10-02_ConsumedBySowing.sql
--
-- WHY THIS IS NEEDED: Confirm Cutting Sowing now anchors "Confirmed Cutting
-- Quantity" to ONE specific dbo.InternalTransfers delivery (its own
-- ConfirmedQuantity), reached straight from Main Office Confirm Receipt --
-- never the pooled dbo.CuttingStock.AvailableQuantity, which mixes multiple
-- deliveries of the same species/Area together. Without a persisted marker,
-- that SAME delivery (same TransferId, e.g. via a stale/bookmarked/reused
-- URL or a resubmitted POST) could be used to create a SECOND SeedSowing of
-- its full confirmed quantity again, as long as the pool still happened to
-- hold enough stock from other, unrelated deliveries -- there is no existing
-- column anywhere that marks "this delivery has already been sown", and
-- comparing against the pool's current balance cannot detect this (the
-- pool's balance is a fungible aggregate, not delivery-specific).
--
-- WHAT THIS DOES: adds exactly one nullable column and its FK, nothing else.
--   dbo.InternalTransfers.ConsumedBySeedSowingId INT NULL
--     REFERENCES dbo.SeedSowings(Id)
--   NULL (the default for every existing row) means "not yet used for a
--   Cutting Sowing" -- true for all 40 current rows, since this tracking
--   never existed before. Set exactly once, by
--   SeedSowingRepository.InsertFromCuttingAsync, in the SAME transaction and
--   under the SAME row lock as the sowing insert it belongs to
--   (UPDATE ... WHERE Id = @TransferId AND ConsumedBySeedSowingId IS NULL),
--   which is what makes two concurrent submissions of the same TransferId
--   race-safe -- the second one finds 0 rows affected and is refused.
--
-- NOT changed: any existing column, constraint, trigger, row, or any other
-- table. No existing query against dbo.InternalTransfers needs updating --
-- the new column is simply ignored by anything that doesn't ask for it.
--
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy. One
-- transaction. Run with sqlcmd -v MODE=DRYRUN (rolls back) or MODE=COMMIT.
-- Idempotent (checks INFORMATION_SCHEMA.COLUMNS before adding).
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 51301, '2026-10-02_ConsumedBySowing.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 51302, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION ConsumedBySowing;

BEGIN TRY

    PRINT '=== Before: row count (must be unchanged after) ===';
    SELECT COUNT(*) AS InternalTransfersRowCount FROM dbo.InternalTransfers;

    IF NOT EXISTS (
        SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
        WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'InternalTransfers' AND COLUMN_NAME = 'ConsumedBySeedSowingId'
    )
    BEGIN
        ALTER TABLE dbo.InternalTransfers ADD ConsumedBySeedSowingId INT NULL;

        ALTER TABLE dbo.InternalTransfers
            ADD CONSTRAINT FK_InternalTransfers_ConsumedBySeedSowing
            FOREIGN KEY (ConsumedBySeedSowingId) REFERENCES dbo.SeedSowings(Id);

        PRINT '=== Column + FK added ===';
    END
    ELSE
    BEGIN
        PRINT '=== Column already exists -- no-op (idempotent re-run) ===';
    END

    ----------------------------------------------------------------------
    -- VERIFICATION
    ----------------------------------------------------------------------
    PRINT '=== After: column exists, nullable (expect 1 row, IS_NULLABLE = YES) ===';
    SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'InternalTransfers' AND COLUMN_NAME = 'ConsumedBySeedSowingId';

    PRINT '=== FK exists, points at dbo.SeedSowings (expect 1 row) ===';
    SELECT fk.name AS ForeignKey, OBJECT_NAME(fk.referenced_object_id) AS ReferencedTable
    FROM sys.foreign_keys fk
    WHERE fk.parent_object_id = OBJECT_ID('dbo.InternalTransfers') AND fk.name = 'FK_InternalTransfers_ConsumedBySeedSowing';

    -- Dynamic SQL: ConsumedBySeedSowingId was added by the ALTER TABLE above
    -- in this SAME batch, so SQL Server's compiler does not yet know about it
    -- for a direct reference here (classic "Invalid column name" same-batch
    -- gotcha) -- EXEC defers parsing to run time, after the column exists.
    PRINT '=== Every existing row is NULL (expect this to equal the row count above -- nothing pre-marked as consumed) ===';
    EXEC(N'SELECT COUNT(*) AS RowsWithNullConsumedBy FROM dbo.InternalTransfers WHERE ConsumedBySeedSowingId IS NULL');

    PRINT '=== Row count unchanged (compare to the Before count above) ===';
    SELECT COUNT(*) AS InternalTransfersRowCount FROM dbo.InternalTransfers;

    PRINT '=== No other InternalTransfers column/constraint touched (compare manually to a before-listing) ===';
    SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'InternalTransfers' ORDER BY ORDINAL_POSITION;

END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION ConsumedBySowing;
    THROW;
END CATCH;

----------------------------------------------------------------------------
-- Approved 2026-10-02: user reviewed the dry run (column + FK added, all 40
-- existing rows NULL, row count unchanged, no other column/constraint
-- touched) and explicitly approved committing.
-- Verified backup: PlantsIMS2_Test_PreInternalTransferConsumedBySeedSowing_20261002.bak
----------------------------------------------------------------------------
IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION ConsumedBySowing;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION ConsumedBySowing;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
