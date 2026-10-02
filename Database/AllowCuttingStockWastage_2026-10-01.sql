-- ============================================================================
-- AllowCuttingStockWastage_2026-10-01.sql
--
-- WHY THIS IS NEEDED (the only schema change this business-rule change
-- requires): Cutting Tray Sowing's sub-tray remainder (CuttingQuantity minus
-- the cuttings placed in complete trays) must now be recorded automatically
-- as WASTE, in dbo.CuttingStockTransactions, exactly mirroring the already-
-- approved Direct Seed Sowing rule (SeedSowingRepository.InsertAsync's own
-- seedRemainder -> dbo.SeedStockTransactions 'Wastage', already allowed by
-- CK_SeedStockTx_Type). dbo.CuttingStockTransactions' own CK_CuttingStockTx_Type
-- CHECK constraint does NOT currently allow 'Wastage' as a TransactionType --
-- confirmed live on PlantsIMS2_Test: the allowed list is TransitLoss, Sown,
-- ReversalReturn, Transplanted, ReversalRemoval, Adjustment, Potted,
-- Transfer, Harvest. Without this change, SeedSowingRepository.
-- InsertFromCuttingAsync's new automatic waste INSERT would fail with
-- "The INSERT statement conflicted with the CHECK constraint" (reproduced
-- and confirmed against an isolated scratch copy before writing this
-- script) and roll back the entire sowing.
--
-- WHAT THIS DOES: adds 'Wastage' to the allowed TransactionType list on
-- dbo.CuttingStockTransactions. Nothing else. No table is created, no column
-- is added or removed, no existing row is touched -- this purely WIDENS what
-- a FUTURE INSERT is allowed to contain (an existing CHECK constraint can
-- only reject rows; relaxing it can never invalidate a row that already
-- satisfied it).
--
-- SAFE / IDEMPOTENT: only drops and recreates the constraint if 'Wastage' is
-- not already in its definition, so running this twice is a no-op the second
-- time.
-- ============================================================================
IF DB_NAME() <> N'PlantsIMS2_Test'
    THROW 51101, 'AllowCuttingStockWastage_2026-10-01.sql may only be run against PlantsIMS2_Test.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION AllowCuttingStockWastage;

BEGIN TRY

    PRINT '=== Before: CK_CuttingStockTx_Type definition ===';
    SELECT definition FROM sys.check_constraints WHERE name = 'CK_CuttingStockTx_Type';

    IF NOT EXISTS (
        SELECT 1 FROM sys.check_constraints
        WHERE name = 'CK_CuttingStockTx_Type' AND definition LIKE '%''Wastage''%'
    )
    BEGIN
        ALTER TABLE dbo.CuttingStockTransactions DROP CONSTRAINT CK_CuttingStockTx_Type;

        ALTER TABLE dbo.CuttingStockTransactions ADD CONSTRAINT CK_CuttingStockTx_Type CHECK (
            [TransactionType] = 'TransitLoss' OR [TransactionType] = 'Sown' OR [TransactionType] = 'ReversalReturn' OR
            [TransactionType] = 'Transplanted' OR [TransactionType] = 'ReversalRemoval' OR [TransactionType] = 'Adjustment' OR
            [TransactionType] = 'Potted' OR [TransactionType] = 'Transfer' OR [TransactionType] = 'Harvest' OR
            [TransactionType] = 'Wastage'
        );

        PRINT '=== Constraint recreated with Wastage added ===';
    END
    ELSE
    BEGIN
        PRINT '=== Already contains Wastage -- no-op (idempotent re-run) ===';
    END

    ----------------------------------------------------------------------
    -- VERIFICATION
    ----------------------------------------------------------------------
    PRINT '=== After: CK_CuttingStockTx_Type definition (must contain Wastage) ===';
    SELECT definition FROM sys.check_constraints WHERE name = 'CK_CuttingStockTx_Type';

    PRINT '=== Row count unchanged (must equal pre-change count -- compare manually) ===';
    SELECT COUNT(*) AS CuttingStockTransactionsRowCount FROM dbo.CuttingStockTransactions;

    PRINT '=== No existing row violates the new constraint (must be 0 -- a relaxed CHECK cannot fail this, included as a hard guarantee) ===';
    SELECT COUNT(*) AS ViolatingRows FROM dbo.CuttingStockTransactions
    WHERE NOT (
        TransactionType = 'TransitLoss' OR TransactionType = 'Sown' OR TransactionType = 'ReversalReturn' OR
        TransactionType = 'Transplanted' OR TransactionType = 'ReversalRemoval' OR TransactionType = 'Adjustment' OR
        TransactionType = 'Potted' OR TransactionType = 'Transfer' OR TransactionType = 'Harvest' OR
        TransactionType = 'Wastage'
    );

    PRINT '=== No other CuttingStockTransactions/CuttingStock constraint touched (must still exist, unchanged) ===';
    SELECT name FROM sys.check_constraints WHERE OBJECT_NAME(parent_object_id) IN ('CuttingStockTransactions', 'CuttingStock') ORDER BY name;

    PRINT '=== Unrelated tables untouched: row counts (compare to before manually) ===';
    SELECT 'CuttingStock' AS TableName, COUNT(*) AS Rows FROM dbo.CuttingStock
    UNION ALL SELECT 'SeedSowings', COUNT(*) FROM dbo.SeedSowings
    UNION ALL SELECT 'SeedStockTransactions', COUNT(*) FROM dbo.SeedStockTransactions
    UNION ALL SELECT 'ReadyConfirmations', COUNT(*) FROM dbo.ReadyConfirmations;

END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION AllowCuttingStockWastage;
    THROW;
END CATCH;

----------------------------------------------------------------------------
-- Approved 2026-10-01: user reviewed the dry run (0 violating rows, no
-- other constraint/table touched, row counts unchanged) twice and explicitly
-- approved committing, to fix a live production error (CK_CuttingStockTx_Type
-- rejecting the new automatic Cutting Tray Sowing wastage INSERT).
-- Verified backup: PlantsIMS2_Test_PreAllowCuttingStockWastage_20261001.bak
----------------------------------------------------------------------------
COMMIT TRANSACTION AllowCuttingStockWastage;
GO
