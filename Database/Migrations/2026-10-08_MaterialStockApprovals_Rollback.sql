-- ============================================================================
-- 2026-10-08_MaterialStockApprovals_Rollback.sql
--
-- Undoes 2026-10-08_MaterialStockApprovals.sql: puts back CK_MaterialTransactions_Sowing
-- ('SeedSowing' only) and the original TR_MaterialTransactions_Ledger. REFUSES while any
-- ledger row references an approval ('ReadyConfirmation'), so no overage history is lost
-- (restore the pre-migration backup if that is really intended). Roll the application
-- code back first. Nothing else is touched.
--
-- SAFETY: same guards as the migration. One transaction. Idempotent.
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 51961, 'This rollback may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 51962, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 51963, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION MaterialStockApprovalsRollback;
BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';
    IF OBJECT_ID('dbo.MaterialTransactions', 'U') IS NULL
    BEGIN
        PRINT '=== dbo.MaterialTransactions does not exist -- nothing to undo ===';
    END
    ELSE
    BEGIN
        IF EXISTS (SELECT 1 FROM dbo.MaterialTransactions WHERE ReferenceType = 'ReadyConfirmation')
            THROW 51964, 'Material transactions reference Supervisor Approvals (overage) -- refusing to remove that rule. Restore the pre-migration backup if that is really intended.', 1;

        IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_MaterialTransactions_Sowing' AND parent_object_id = OBJECT_ID('dbo.MaterialTransactions'))
            ALTER TABLE dbo.MaterialTransactions DROP CONSTRAINT CK_MaterialTransactions_Sowing;
        ALTER TABLE dbo.MaterialTransactions WITH CHECK ADD CONSTRAINT CK_MaterialTransactions_Sowing
            CHECK ((TransactionType IN ('CONSUME', 'REVERSAL') AND ReferenceType = 'SeedSowing' AND ReferenceId IS NOT NULL)
                OR (TransactionType IN ('RECEIVE', 'ADJUSTMENT') AND ReferenceType IS NULL AND ReferenceId IS NULL));

    EXEC (N'
CREATE OR ALTER TRIGGER dbo.TR_MaterialTransactions_Ledger ON dbo.MaterialTransactions
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    -- Insert-only. A scratch copy (never PlantsIMS2_Test / production) may remove its own test rows
    -- when the session explicitly asks for it (end-to-end test clean-up).
    IF EXISTS (SELECT 1 FROM deleted)
    BEGIN
        IF EXISTS (SELECT 1 FROM inserted)
            THROW 51931, ''Material transactions cannot be changed. Record a correcting ADJUSTMENT instead.'', 1;
        IF NOT (DB_NAME() LIKE N''PlantsIMS2[_]Scratch[_]%'' AND ISNULL(CAST(SESSION_CONTEXT(N''MaterialTestCleanup'') AS INT), 0) = 1)
            THROW 51932, ''Material transactions cannot be deleted.'', 1;
        RETURN;
    END
    IF EXISTS (SELECT MaterialStockId FROM inserted GROUP BY MaterialStockId HAVING COUNT(*) > 1)
        THROW 51933, ''One material transaction per stock balance per statement.'', 1;
    -- The balance before must be the current balance (the balance row is updated afterwards).
    IF EXISTS (SELECT 1 FROM inserted t JOIN dbo.MaterialStock s WITH (FORCESEEK) ON s.Id = t.MaterialStockId WHERE t.BalanceBefore <> s.CurrentQuantity)
        THROW 51934, ''Material stock changed meanwhile -- the transaction balance does not match the current stock. Please try again.'', 1;
    -- A consumption (or its reversal) belongs to a sowing of the SAME material, Area and Polyhouse.
    IF EXISTS (SELECT 1 FROM inserted t
               JOIN dbo.MaterialStock s WITH (FORCESEEK) ON s.Id = t.MaterialStockId
               LEFT JOIN dbo.SeedSowings w WITH (FORCESEEK) ON w.Id = t.ReferenceId
               WHERE t.TransactionType IN (''CONSUME'', ''REVERSAL'')
                 AND (w.Id IS NULL OR ISNULL(w.GrowingMaterialTypeId, -1) <> s.MaterialTypeId OR w.AreaId <> s.AreaId OR ISNULL(w.PolyhouseId, -1) <> s.PolyhouseId))
        THROW 51935, ''A material consumption must match its sowing''''s growing material, Area and Polyhouse.'', 1;
    -- A reversal returns exactly what that sowing consumed, to the same balance.
    IF EXISTS (SELECT 1 FROM inserted t
               WHERE t.TransactionType = ''REVERSAL''
                 AND NOT EXISTS (SELECT 1 FROM dbo.MaterialTransactions c WITH (FORCESEEK)
                                 WHERE c.ReferenceType = t.ReferenceType AND c.ReferenceId = t.ReferenceId AND c.TransactionType = ''CONSUME''
                                   AND c.MaterialStockId = t.MaterialStockId AND c.StockQuantity = t.StockQuantity))
        THROW 51936, ''A reversal must return exactly the quantity its sowing consumed, to the same Polyhouse.'', 1;
END');
        PRINT '=== Original CK_MaterialTransactions_Sowing and TR_MaterialTransactions_Ledger restored ===';
    END
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION MaterialStockApprovalsRollback;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION MaterialStockApprovalsRollback;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION MaterialStockApprovalsRollback;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
