-- ============================================================================
-- 2026-10-08_MaterialStockApprovals.sql   (run AFTER 2026-10-08_MaterialStock.sql)
--
-- WHY: a Cutting Sowing at a Main Office Polyhouse may be approved with MORE trays than
-- were sown (Supervisor Approval "overage"). The approval already takes the extra
-- cuttings and the extra physical trays; the extra trays also need growing material:
--     extra trays x the CURRENT kg per tray of the sowing's OWN material,
-- taken from the sowing's OWN Area + Polyhouse, in the approval's own transaction.
-- Example: sown 100 trays (150 kg Cocopeat at 1.5 kg/tray), approved 105 -> +7.5 kg.
--
-- The ledger links that consumption to the APPROVAL (ReferenceType 'ReadyConfirmation',
-- ReferenceId = dbo.ReadyConfirmations.Id), so cancelling just that approval returns
-- exactly its 7.5 kg and cancelling the sowing still returns exactly its own 150 kg.
--
-- CHANGES (no table, column or data change):
--   CK_MaterialTransactions_Sowing   CONSUME / REVERSAL may reference 'SeedSowing' or
--                                    'ReadyConfirmation'; RECEIVE / ADJUSTMENT still none
--   TR_MaterialTransactions_Ledger   a 'ReadyConfirmation' row must belong (via
--                                    ReadyConfirmations.SeedSowingId) to a sowing of the SAME
--                                    material, Area and Polyhouse -- the same rule (51935) as a
--                                    sowing's own consumption; a REVERSAL still returns exactly
--                                    the CONSUME of the same reference (51936)
--   UX_MaterialTransactions_SowingLink (unchanged) -> at most one CONSUME and one REVERSAL
--                                    per approval, as per sowing
--
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy AND to the server named
-- with -v TARGET_SERVER=<@@SERVERNAME> (the live database is ALSO named PlantsIMS2_Test).
-- One transaction. Idempotent. Every existing ledger row satisfies the new constraint
-- (it only widens the allowed ReferenceType); WITH CHECK re-validates them anyway.
-- Undo: 2026-10-08_MaterialStockApprovals_Rollback.sql
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 51951, '2026-10-08_MaterialStockApprovals.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 51952, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 51953, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
IF OBJECT_ID('dbo.MaterialTransactions', 'U') IS NULL
    THROW 51954, 'Run 2026-10-08_MaterialStock.sql first.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION MaterialStockApprovals2026_10_08;

BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';

    PRINT '=== Before: ledger rows by type / reference ===';
    SELECT TransactionType, ISNULL(ReferenceType, '-') AS ReferenceType, COUNT(*) AS Rows
    FROM dbo.MaterialTransactions GROUP BY TransactionType, ReferenceType ORDER BY 1, 2;

    IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_MaterialTransactions_Sowing' AND parent_object_id = OBJECT_ID('dbo.MaterialTransactions'))
        ALTER TABLE dbo.MaterialTransactions DROP CONSTRAINT CK_MaterialTransactions_Sowing;
    ALTER TABLE dbo.MaterialTransactions WITH CHECK ADD CONSTRAINT CK_MaterialTransactions_Sowing
        CHECK ((TransactionType IN ('CONSUME', 'REVERSAL') AND ReferenceType IN ('SeedSowing', 'ReadyConfirmation') AND ReferenceId IS NOT NULL)
            OR (TransactionType IN ('RECEIVE', 'ADJUSTMENT') AND ReferenceType IS NULL AND ReferenceId IS NULL));
    PRINT '=== CK_MaterialTransactions_Sowing: SeedSowing or ReadyConfirmation ===';

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
    -- A consumption (or its reversal) belongs to a sowing of the SAME material, Area and Polyhouse:
    -- the sowing itself (SeedSowing) or the sowing of the approval (ReadyConfirmation, overage trays).
    IF EXISTS (SELECT 1 FROM inserted t
               JOIN dbo.MaterialStock s WITH (FORCESEEK) ON s.Id = t.MaterialStockId
               LEFT JOIN dbo.ReadyConfirmations rc WITH (FORCESEEK) ON t.ReferenceType = ''ReadyConfirmation'' AND rc.Id = t.ReferenceId
               LEFT JOIN dbo.SeedSowings ws WITH (FORCESEEK) ON t.ReferenceType = ''SeedSowing'' AND ws.Id = t.ReferenceId
               LEFT JOIN dbo.SeedSowings wa WITH (FORCESEEK) ON wa.Id = rc.SeedSowingId
               WHERE t.TransactionType IN (''CONSUME'', ''REVERSAL'')
                 AND (COALESCE(ws.Id, wa.Id) IS NULL
                      OR ISNULL(COALESCE(ws.GrowingMaterialTypeId, wa.GrowingMaterialTypeId), -1) <> s.MaterialTypeId
                      OR COALESCE(ws.AreaId, wa.AreaId) <> s.AreaId
                      OR ISNULL(COALESCE(ws.PolyhouseId, wa.PolyhouseId), -1) <> s.PolyhouseId))
        THROW 51935, ''A material consumption must match its sowing''''s growing material, Area and Polyhouse.'', 1;
    -- A reversal returns exactly what that sowing (or approval) consumed, to the same balance.
    IF EXISTS (SELECT 1 FROM inserted t
               WHERE t.TransactionType = ''REVERSAL''
                 AND NOT EXISTS (SELECT 1 FROM dbo.MaterialTransactions c WITH (FORCESEEK)
                                 WHERE c.ReferenceType = t.ReferenceType AND c.ReferenceId = t.ReferenceId AND c.TransactionType = ''CONSUME''
                                   AND c.MaterialStockId = t.MaterialStockId AND c.StockQuantity = t.StockQuantity))
        THROW 51936, ''A reversal must return exactly the quantity its sowing consumed, to the same Polyhouse.'', 1;
END');
    PRINT '=== TR_MaterialTransactions_Ledger updated (approval overage) ===';

    PRINT '=== After: constraint definition, trigger, ledger rows (must equal Before) ===';
    SELECT name, definition FROM sys.check_constraints WHERE name = 'CK_MaterialTransactions_Sowing';
    SELECT name, modify_date FROM sys.triggers WHERE name = 'TR_MaterialTransactions_Ledger';
    SELECT TransactionType, ISNULL(ReferenceType, '-') AS ReferenceType, COUNT(*) AS Rows
    FROM dbo.MaterialTransactions GROUP BY TransactionType, ReferenceType ORDER BY 1, 2;
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION MaterialStockApprovals2026_10_08;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION MaterialStockApprovals2026_10_08;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION MaterialStockApprovals2026_10_08;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
