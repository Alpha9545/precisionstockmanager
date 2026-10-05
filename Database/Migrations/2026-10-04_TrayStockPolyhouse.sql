-- ============================================================================
-- 2026-10-04_TrayStockPolyhouse.sql
--
-- WHY: business change -- Tray Stock must be tracked POLYHOUSE-WISE. The
-- active Tray Stock key becomes AREA + POLYHOUSE + CAVITY (replacing the
-- Area + Cavity pools created earlier today by 2026-10-04_TrayStockAreaCavity.sql).
--
-- WHAT THIS DOES (one transaction):
--   1. Reverses ledger row 66 -- the 400 x 102 Cavity trays added at Area
--      level (pool 34, no Polyhouse) after the Area + Cavity migration. The
--      Polyhouse is unknown, so it is NOT guessed: a compensating
--      'Adjustment' (-400, ReferenceType 'TrayStockReversal', ReferenceId 66)
--      brings pool 34 to 0. Row 66 itself is kept untouched. The 400 trays
--      are to be re-added through Add Tray Stock with the correct Polyhouse.
--   2. Splits each Area pool back into the ORIGINAL Polyhouse pool rows,
--      using the exact amounts the Area + Cavity migration recorded
--      ('TrayStockMigration' IN rows: Area pool <- legacy pool, quantity):
--      'Adjustment' OUT of the Area pool + 'Adjustment' IN to the original
--      Polyhouse pool, ReferenceType 'TrayStockPolyhouseSplit', ReferenceId =
--      the counterpart pool. The original Polyhouse pool rows are re-activated
--      (continuous history per Polyhouse); the Area pools (PolyhouseId NULL)
--      end at 0 and are retired.
--   3. Uniqueness: UX_TrayStock_Polyhouse_Size (PolyhouseId, TraySize) is
--      replaced by UX_TrayStock_Area_Polyhouse_Size (AreaId, PolyhouseId,
--      TraySize) WHERE PolyhouseId IS NOT NULL. CK_TrayStock_ActivePoolHasPolyhouse:
--      an ACTIVE pool must have a Polyhouse (Area-level pools can never be
--      used again).
--
-- NOT changed: no TrayStockTransactions row is updated or deleted; no column
-- is added or dropped; TrayStockTransactions' schema/CHECK are unchanged
-- ('Adjustment' is already allowed); no permission; no other table.
--
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy. Refuses
-- to run unless the database is in EXACTLY the audited state (ledger ends at
-- row 66, the four Area pools and eight retired Polyhouse pools as expected,
-- no consumption from any Area pool). Run with sqlcmd -I -v MODE=DRYRUN
-- (rolls back) or MODE=COMMIT.
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 51801, '2026-10-04_TrayStockPolyhouse.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 51802, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION TrayStockPolyhouse2026_10_04;

BEGIN TRY

    ----------------------------------------------------------------------
    -- PRE-CHECKS: exactly the audited state, or nothing happens
    ----------------------------------------------------------------------
    DECLARE @TxCountBefore INT = (SELECT COUNT(*) FROM dbo.TrayStockTransactions);
    DECLARE @MaxTxIdBefore INT = (SELECT MAX(Id) FROM dbo.TrayStockTransactions);
    DECLARE @TxChecksumBefore INT = (SELECT CHECKSUM_AGG(CHECKSUM(Id, TrayStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)) FROM dbo.TrayStockTransactions);
    DECLARE @TotalBefore DECIMAL(18,2) = (SELECT SUM(PhysicalQuantity) FROM dbo.TrayStock);

    IF @MaxTxIdBefore <> 66 OR @TxCountBefore <> 38
        THROW 51803, 'Pre-check failed: the Tray Stock ledger has changed since the audit (expected 38 rows ending at Id 66).', 1;
    IF EXISTS (SELECT 1 FROM dbo.TrayStock t WHERE t.PhysicalQuantity <> ISNULL((SELECT SUM(x.Quantity) FROM dbo.TrayStockTransactions x WHERE x.TrayStockId = t.Id), 0))
        THROW 51804, 'Pre-check failed: a Tray Stock balance does not reconcile with its ledger.', 1;
    -- Row 66: the only ledger row of pool 34, an Area-level Add of 400 x 102 Cavity, never consumed.
    IF NOT EXISTS (SELECT 1 FROM dbo.TrayStockTransactions tr JOIN dbo.TrayStock t ON t.Id = tr.TrayStockId
                   WHERE tr.Id = 66 AND tr.TrayStockId = 34 AND tr.TransactionType = N'Allocation' AND tr.ReferenceType = N'TrayStockAdd'
                     AND tr.Quantity = 400 AND tr.BeforeQuantity = 0 AND t.PolyhouseId IS NULL AND t.TraySize = N'102 Cavity'
                     AND t.AreaId = 2 AND t.PhysicalQuantity = 400 AND t.IsActive = 1)
       OR (SELECT COUNT(*) FROM dbo.TrayStockTransactions WHERE TrayStockId = 34) <> 1
        THROW 51805, 'Pre-check failed: ledger row 66 / pool 34 is not the expected unconsumed Area-level 400 x 102 Cavity entry.', 1;
    -- Area pools 28/29/30 hold ONLY their migration carry-over rows (nothing consumed or added since).
    IF EXISTS (SELECT 1 FROM dbo.TrayStockTransactions tr JOIN dbo.TrayStock t ON t.Id = tr.TrayStockId
               WHERE t.PolyhouseId IS NULL AND t.Id <> 34 AND ISNULL(tr.ReferenceType, N'') <> N'TrayStockMigration')
        THROW 51806, 'Pre-check failed: an Area pool has activity other than the migration carry-over -- it cannot be split back exactly.', 1;
    IF (SELECT COUNT(*) FROM dbo.TrayStock WHERE PolyhouseId IS NULL AND IsActive = 1) <> 4
       OR EXISTS (SELECT 1 FROM dbo.TrayStock WHERE PolyhouseId IS NOT NULL AND (IsActive = 1 OR PhysicalQuantity <> 0))
       OR (SELECT COUNT(*) FROM dbo.TrayStock WHERE PolyhouseId IS NOT NULL) <> 8
        THROW 51807, 'Pre-check failed: Tray Stock pools are not in the audited state.', 1;
    -- Every retired Polyhouse pool still sits in its Polyhouse's current Area.
    IF EXISTS (SELECT 1 FROM dbo.TrayStock t JOIN dbo.Polyhouses p ON p.Id = t.PolyhouseId WHERE ISNULL(p.AreaId, -1) <> t.AreaId)
        THROW 51808, 'Pre-check failed: a Polyhouse has moved to another Area since its pool was created.', 1;

    PRINT '=== BEFORE: Tray Stock pools ===';
    SELECT t.Id, a.Name AS Area, ISNULL(p.Name, N'(Area level)') AS Polyhouse, t.TraySize, t.PhysicalQuantity, t.IsActive
    FROM dbo.TrayStock t JOIN dbo.Area a ON a.Id = t.AreaId LEFT JOIN dbo.Polyhouses p ON p.Id = t.PolyhouseId ORDER BY t.PolyhouseId, t.Id;

    DECLARE @Remark NVARCHAR(500);

    ----------------------------------------------------------------------
    -- 1) Reverse row 66 (Area-level 400 x 102 Cavity, Polyhouse unknown)
    ----------------------------------------------------------------------
    INSERT INTO dbo.TrayStockTransactions (TrayStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)
    VALUES (34, SYSUTCDATETIME(), N'Adjustment', N'TrayStockReversal', 66, -400, 400, NULL,
            N'Reversed: Area-level entry without a Polyhouse (ledger row 66) -- re-add through Add Tray Stock with the correct Polyhouse', SYSUTCDATETIME());
    UPDATE dbo.TrayStock SET PhysicalQuantity = 0, ModifiedDate = SYSUTCDATETIME(), ModifiedBy = N'Migration 2026-10-04 Polyhouse' WHERE Id = 34;

    ----------------------------------------------------------------------
    -- 2) Split each Area pool back into its ORIGINAL Polyhouse pool rows,
    --    using the exact amounts the Area + Cavity migration recorded.
    ----------------------------------------------------------------------
    SET @Remark = N'Workflow change 2026-10-04: Tray Stock now held per Area + Polyhouse + Cavity';
    DECLARE @AreaPoolId INT, @PolyPoolId INT, @Qty DECIMAL(18,2), @AreaBefore DECIMAL(18,2), @PolyBefore DECIMAL(18,2);
    DECLARE c CURSOR LOCAL FAST_FORWARD FOR
        SELECT i.TrayStockId, i.ReferenceId, i.Quantity
        FROM dbo.TrayStockTransactions i
        JOIN dbo.TrayStock ap ON ap.Id = i.TrayStockId AND ap.PolyhouseId IS NULL
        WHERE i.ReferenceType = N'TrayStockMigration' AND i.Quantity > 0
        ORDER BY i.Id;
    OPEN c;
    FETCH NEXT FROM c INTO @AreaPoolId, @PolyPoolId, @Qty;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM dbo.TrayStock lp JOIN dbo.TrayStock ap ON ap.Id = @AreaPoolId
                       WHERE lp.Id = @PolyPoolId AND lp.PolyhouseId IS NOT NULL AND lp.AreaId = ap.AreaId AND lp.TraySize = ap.TraySize)
            THROW 51809, 'Split failed: a migration row does not point back to a Polyhouse pool of the same Area and Cavity.', 1;

        SET @AreaBefore = (SELECT PhysicalQuantity FROM dbo.TrayStock WITH (UPDLOCK) WHERE Id = @AreaPoolId);
        SET @PolyBefore = (SELECT PhysicalQuantity FROM dbo.TrayStock WITH (UPDLOCK) WHERE Id = @PolyPoolId);

        INSERT INTO dbo.TrayStockTransactions (TrayStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)
        VALUES (@AreaPoolId, SYSUTCDATETIME(), N'Adjustment', N'TrayStockPolyhouseSplit', @PolyPoolId, -@Qty, @AreaBefore, NULL, @Remark + N' -- moved to Polyhouse pool', SYSUTCDATETIME());
        UPDATE dbo.TrayStock SET PhysicalQuantity = @AreaBefore - @Qty, ModifiedDate = SYSUTCDATETIME(), ModifiedBy = N'Migration 2026-10-04 Polyhouse' WHERE Id = @AreaPoolId;

        INSERT INTO dbo.TrayStockTransactions (TrayStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)
        VALUES (@PolyPoolId, SYSUTCDATETIME(), N'Adjustment', N'TrayStockPolyhouseSplit', @AreaPoolId, @Qty, @PolyBefore, NULL, @Remark + N' -- restored from Area pool', SYSUTCDATETIME());
        UPDATE dbo.TrayStock SET PhysicalQuantity = @PolyBefore + @Qty, ModifiedDate = SYSUTCDATETIME(), ModifiedBy = N'Migration 2026-10-04 Polyhouse' WHERE Id = @PolyPoolId;

        FETCH NEXT FROM c INTO @AreaPoolId, @PolyPoolId, @Qty;
    END
    CLOSE c; DEALLOCATE c;

    -- Original Polyhouse pools active again (incl. Facility-3 / 9 Cavity at 0); Area pools retired.
    UPDATE dbo.TrayStock SET IsActive = 1, ModifiedDate = SYSUTCDATETIME(), ModifiedBy = N'Migration 2026-10-04 Polyhouse' WHERE PolyhouseId IS NOT NULL AND IsActive = 0;
    UPDATE dbo.TrayStock SET IsActive = 0, ModifiedDate = SYSUTCDATETIME(), ModifiedBy = N'Migration 2026-10-04 Polyhouse' WHERE PolyhouseId IS NULL AND IsActive = 1;

    ----------------------------------------------------------------------
    -- 3) Uniqueness + "an active pool has a Polyhouse"
    ----------------------------------------------------------------------
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.TrayStock') AND name = 'UX_TrayStock_Polyhouse_Size')
        DROP INDEX UX_TrayStock_Polyhouse_Size ON dbo.TrayStock;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.TrayStock') AND name = 'UX_TrayStock_Area_Polyhouse_Size')
        CREATE UNIQUE INDEX UX_TrayStock_Area_Polyhouse_Size ON dbo.TrayStock (AreaId, PolyhouseId, TraySize) WHERE PolyhouseId IS NOT NULL;
    IF OBJECT_ID('dbo.CK_TrayStock_ActivePoolHasPolyhouse', 'C') IS NULL
        ALTER TABLE dbo.TrayStock WITH CHECK ADD CONSTRAINT CK_TrayStock_ActivePoolHasPolyhouse CHECK (PolyhouseId IS NOT NULL OR IsActive = 0);

    ----------------------------------------------------------------------
    -- VERIFICATION (any failure throws -> whole transaction rolls back)
    ----------------------------------------------------------------------
    PRINT '=== AFTER: Tray Stock pools ===';
    SELECT t.Id, a.Name AS Area, ISNULL(p.Name, N'(Area level)') AS Polyhouse, t.TraySize, t.PhysicalQuantity, t.IsActive,
           CASE WHEN t.PolyhouseId IS NULL THEN 'Area pool (retired)' ELSE 'Polyhouse pool (active)' END AS Kind
    FROM dbo.TrayStock t JOIN dbo.Area a ON a.Id = t.AreaId LEFT JOIN dbo.Polyhouses p ON p.Id = t.PolyhouseId ORDER BY t.PolyhouseId, t.Id;

    PRINT '=== Polyhouse pools: balance before the Area + Cavity migration vs now (must be equal) ===';
    ;WITH pre AS (
        SELECT t.Id, ISNULL((SELECT SUM(x.Quantity) FROM dbo.TrayStockTransactions x WHERE x.TrayStockId = t.Id
                             AND ISNULL(x.ReferenceType, N'') NOT IN (N'TrayStockMigration', N'TrayStockPolyhouseSplit')), 0) AS OriginalBalance
        FROM dbo.TrayStock t WHERE t.PolyhouseId IS NOT NULL)
    SELECT t.Id, p.Name AS Polyhouse, t.TraySize, pre.OriginalBalance, t.PhysicalQuantity AS Now,
           CASE WHEN pre.OriginalBalance = t.PhysicalQuantity THEN 'OK' ELSE 'MISMATCH' END AS Result
    FROM dbo.TrayStock t JOIN pre ON pre.Id = t.Id JOIN dbo.Polyhouses p ON p.Id = t.PolyhouseId ORDER BY t.TraySize, p.Name;

    PRINT '=== New ledger rows written by this migration ===';
    SELECT Id, TrayStockId, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, Quantity + BeforeQuantity AS AfterQuantity
    FROM dbo.TrayStockTransactions WHERE Id > @MaxTxIdBefore ORDER BY Id;

    IF EXISTS (SELECT 1 FROM dbo.TrayStock t WHERE t.PolyhouseId IS NOT NULL
               AND t.PhysicalQuantity <> ISNULL((SELECT SUM(x.Quantity) FROM dbo.TrayStockTransactions x WHERE x.TrayStockId = t.Id
                                                 AND ISNULL(x.ReferenceType, N'') NOT IN (N'TrayStockMigration', N'TrayStockPolyhouseSplit')), 0))
        THROW 51810, 'Post-check failed: a Polyhouse pool does not hold its original pre-migration balance.', 1;
    IF EXISTS (SELECT 1 FROM dbo.TrayStock t WHERE t.PhysicalQuantity <> ISNULL((SELECT SUM(x.Quantity) FROM dbo.TrayStockTransactions x WHERE x.TrayStockId = t.Id), 0))
        THROW 51811, 'Post-check failed: ledger does not reconcile.', 1;
    IF EXISTS (SELECT 1 FROM dbo.TrayStock WHERE PhysicalQuantity < 0)
        THROW 51812, 'Post-check failed: negative stock.', 1;
    IF EXISTS (SELECT 1 FROM dbo.TrayStock WHERE PolyhouseId IS NULL AND (IsActive = 1 OR PhysicalQuantity <> 0))
        THROW 51813, 'Post-check failed: an Area-level pool is still active or holds stock.', 1;
    IF EXISTS (SELECT 1 FROM dbo.TrayStock WHERE PolyhouseId IS NOT NULL GROUP BY AreaId, PolyhouseId, TraySize HAVING COUNT(*) > 1)
        THROW 51814, 'Post-check failed: duplicate Area + Polyhouse + Cavity pool.', 1;
    IF EXISTS (SELECT 1 FROM dbo.TrayStock t JOIN dbo.Polyhouses p ON p.Id = t.PolyhouseId WHERE t.IsActive = 1 AND ISNULL(p.AreaId, -1) <> t.AreaId)
        THROW 51815, 'Post-check failed: an active pool''s Polyhouse is not in the pool''s Area.', 1;
    IF (SELECT SUM(PhysicalQuantity) FROM dbo.TrayStock) <> @TotalBefore - 400
        THROW 51816, 'Post-check failed: total trays changed by anything other than the 400 reversed.', 1;
    IF (SELECT SUM(Quantity) FROM dbo.TrayStockTransactions WHERE ReferenceType = N'TrayStockPolyhouseSplit') <> 0
        THROW 51817, 'Post-check failed: split OUT/IN rows do not net to zero.', 1;
    IF (SELECT COUNT(*) FROM dbo.TrayStockTransactions WHERE Id <= @MaxTxIdBefore) <> @TxCountBefore
       OR (SELECT CHECKSUM_AGG(CHECKSUM(Id, TrayStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt))
           FROM dbo.TrayStockTransactions WHERE Id <= @MaxTxIdBefore) <> @TxChecksumBefore
        THROW 51818, 'Post-check failed: an existing ledger row was changed or removed.', 1;

    PRINT '=== Totals ===';
    SELECT @TotalBefore AS TotalTraysBefore, CAST(400 AS DECIMAL(18,2)) AS ReversedRow66,
           (SELECT SUM(PhysicalQuantity) FROM dbo.TrayStock) AS TotalTraysAfter,
           (SELECT SUM(PhysicalQuantity) FROM dbo.TrayStock WHERE IsActive = 1) AS InActivePolyhousePools;
    PRINT '=== Existing history preserved: ' + CAST(@TxCountBefore AS VARCHAR(10)) + ' pre-migration ledger rows unchanged (count + checksum) ===';

    PRINT '=== Indexes / constraints on dbo.TrayStock ===';
    SELECT name, is_unique, filter_definition FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.TrayStock') AND name IS NOT NULL;
    SELECT name, definition FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID('dbo.TrayStock');

END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION TrayStockPolyhouse2026_10_04;
    THROW;
END CATCH;

----------------------------------------------------------------------------
-- Approved 2026-10-04: user reviewed the dry run (Polyhouse pools restored to
-- 1914/992/29 (24), 498 (42), 50/700/734 (150); row 66's 400 x 102 Cavity
-- reversed, not assigned to any Polyhouse; total 5317 -> 4917; 38 historical
-- rows unchanged) and explicitly approved committing. The running app was
-- stopped first. Committed against PlantsIMS2_Test.
-- Verified backups: PlantsIMS2_Test_PreTrayStockPolyhouse_20261004_111313.bak
--                   PlantsIMS2_Test_PostTrayStockPolyhouse_20261004_111606.bak
----------------------------------------------------------------------------
IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION TrayStockPolyhouse2026_10_04;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION TrayStockPolyhouse2026_10_04;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
