-- ============================================================================
-- 2026-10-04_TrayStockAreaCavity.sql
--
-- WHY: business workflow change -- Tray Stock now belongs to AREA + CAVITY
-- (added by the Sowing Supervisor for their own Areas), no longer to a Main
-- Office POLYHOUSE + Tray Size (allocated by Main Office). Every sowing in
-- an active, non-Outlet Area consumes from its Area + Cavity pool.
--
-- WHAT THIS DOES:
--   1. dbo.TrayStock gains AreaId (FK dbo.Area), back-filled for every
--      existing row from its Polyhouse's Area; PolyhouseId becomes NULLable.
--      Existing (legacy) rows KEEP their PolyhouseId; new Area pools have
--      PolyhouseId = NULL.
--   2. UQ_TrayStock_Polyhouse_Size (a UNIQUE constraint -- treats NULLs as
--      equal, so it would allow only ONE Area pool per cavity) is replaced by
--      two filtered unique indexes:
--        UX_TrayStock_Polyhouse_Size (PolyhouseId, TraySize) WHERE PolyhouseId IS NOT NULL  -- legacy
--        UX_TrayStock_Area_Size      (AreaId, TraySize)      WHERE PolyhouseId IS NULL      -- new
--   3. Carries every legacy pool's physical balance into its Area + Cavity
--      pool THROUGH THE LEDGER (no snapshot-only update): per legacy pool
--      with stock, one 'Adjustment' OUT (legacy pool -> 0) and one
--      'Adjustment' IN (Area pool), ReferenceType 'TrayStockMigration',
--      ReferenceId = the counterpart pool. All legacy pools -> IsActive = 0.
--   4. New permission 'TrayStock.Enter', granted to the 'Sowing Supervisor'
--      role ONLY (System Administrator keeps access through FullAccess).
--
-- NOT changed: no TrayStockTransactions row is updated or deleted; no column
-- is dropped; dbo.TrayStockTransactions' schema and CHECK constraint are
-- unchanged ('Adjustment' is already an allowed type); no other table.
--
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy. One
-- transaction. Run with sqlcmd -I -v MODE=DRYRUN (rolls back) or MODE=COMMIT.
-- Re-run safe: steps check whether they were already applied.
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 51701, '2026-10-04_TrayStockAreaCavity.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 51702, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION TrayStockAreaCavity2026_10_04;

BEGIN TRY

    ----------------------------------------------------------------------
    -- BEFORE snapshot
    ----------------------------------------------------------------------
    DECLARE @TxCountBefore INT = (SELECT COUNT(*) FROM dbo.TrayStockTransactions);
    DECLARE @TxChecksumBefore INT = (SELECT CHECKSUM_AGG(CHECKSUM(Id, TrayStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)) FROM dbo.TrayStockTransactions);
    DECLARE @MaxTxIdBefore INT = ISNULL((SELECT MAX(Id) FROM dbo.TrayStockTransactions), 0);

    PRINT '=== BEFORE: Tray Stock pools ===';
    SELECT t.Id, t.PolyhouseId, p.Name AS Polyhouse, p.AreaId, a.Name AS Area, t.TraySize, t.PhysicalQuantity, t.IsActive
    FROM dbo.TrayStock t JOIN dbo.Polyhouses p ON p.Id = t.PolyhouseId LEFT JOIN dbo.Area a ON a.Id = p.AreaId ORDER BY t.Id;

    IF EXISTS (SELECT 1 FROM dbo.TrayStock t
               WHERE t.PhysicalQuantity <> ISNULL((SELECT SUM(x.Quantity) FROM dbo.TrayStockTransactions x WHERE x.TrayStockId = t.Id), 0))
        THROW 51703, 'Pre-check failed: a Tray Stock balance does not reconcile with its ledger.', 1;

    ----------------------------------------------------------------------
    -- 1) AreaId column + back-fill; PolyhouseId NULLable
    ----------------------------------------------------------------------
    IF COL_LENGTH('dbo.TrayStock', 'AreaId') IS NULL
    BEGIN
        ALTER TABLE dbo.TrayStock ADD AreaId INT NULL;
        PRINT '=== AreaId column added ===';
    END

    EXEC (N'UPDATE t SET AreaId = p.AreaId FROM dbo.TrayStock t JOIN dbo.Polyhouses p ON p.Id = t.PolyhouseId WHERE t.AreaId IS NULL;');
    EXEC (N'IF EXISTS (SELECT 1 FROM dbo.TrayStock WHERE AreaId IS NULL) THROW 51704, ''A legacy Tray Stock row has no Area (its Polyhouse has no Area) -- cannot migrate.'', 1;');
    EXEC (N'ALTER TABLE dbo.TrayStock ALTER COLUMN AreaId INT NOT NULL;');
    IF OBJECT_ID('dbo.FK_TrayStock_Area', 'F') IS NULL
        EXEC (N'ALTER TABLE dbo.TrayStock ADD CONSTRAINT FK_TrayStock_Area FOREIGN KEY (AreaId) REFERENCES dbo.Area(Id);');

    ----------------------------------------------------------------------
    -- 2) Uniqueness: replace the NULL-colliding UNIQUE constraint
    ----------------------------------------------------------------------
    IF OBJECT_ID('dbo.UQ_TrayStock_Polyhouse_Size', 'UQ') IS NOT NULL
        ALTER TABLE dbo.TrayStock DROP CONSTRAINT UQ_TrayStock_Polyhouse_Size;
    ALTER TABLE dbo.TrayStock ALTER COLUMN PolyhouseId INT NULL;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.TrayStock') AND name = 'UX_TrayStock_Polyhouse_Size')
        EXEC (N'CREATE UNIQUE INDEX UX_TrayStock_Polyhouse_Size ON dbo.TrayStock (PolyhouseId, TraySize) WHERE PolyhouseId IS NOT NULL;');
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.TrayStock') AND name = 'UX_TrayStock_Area_Size')
        EXEC (N'CREATE UNIQUE INDEX UX_TrayStock_Area_Size ON dbo.TrayStock (AreaId, TraySize) WHERE PolyhouseId IS NULL;');

    ----------------------------------------------------------------------
    -- 3) Carry legacy balances into Area + Cavity pools through the ledger
    ----------------------------------------------------------------------
    EXEC (N'
DECLARE @Remark NVARCHAR(500) = N''Workflow change 2026-10-04: Tray Stock now held per Area + Cavity'';
DECLARE @LegacyId INT, @AreaId INT, @TraySize NVARCHAR(20), @Qty DECIMAL(18,2), @AreaPoolId INT, @AreaBefore DECIMAL(18,2);
DECLARE c CURSOR LOCAL FAST_FORWARD FOR
    SELECT Id, AreaId, TraySize, PhysicalQuantity FROM dbo.TrayStock
    WHERE PolyhouseId IS NOT NULL AND PhysicalQuantity > 0 ORDER BY Id;
OPEN c;
FETCH NEXT FROM c INTO @LegacyId, @AreaId, @TraySize, @Qty;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @AreaPoolId = (SELECT Id FROM dbo.TrayStock WITH (UPDLOCK, HOLDLOCK) WHERE AreaId = @AreaId AND TraySize = @TraySize AND PolyhouseId IS NULL);
    IF @AreaPoolId IS NULL
    BEGIN
        INSERT INTO dbo.TrayStock (PolyhouseId, AreaId, TraySize, PhysicalQuantity, IsActive, CreatedDate, CreatedBy)
        VALUES (NULL, @AreaId, @TraySize, 0, 1, SYSUTCDATETIME(), N''Migration 2026-10-04'');
        SET @AreaPoolId = CAST(SCOPE_IDENTITY() AS INT);
    END
    SET @AreaBefore = (SELECT PhysicalQuantity FROM dbo.TrayStock WHERE Id = @AreaPoolId);

    -- OUT of the legacy Polyhouse pool
    INSERT INTO dbo.TrayStockTransactions (TrayStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)
    VALUES (@LegacyId, SYSUTCDATETIME(), N''Adjustment'', N''TrayStockMigration'', @AreaPoolId, -@Qty, @Qty, NULL, @Remark + N'' -- moved to Area pool'', SYSUTCDATETIME());
    UPDATE dbo.TrayStock SET PhysicalQuantity = 0, ModifiedDate = SYSUTCDATETIME(), ModifiedBy = N''Migration 2026-10-04'' WHERE Id = @LegacyId;

    -- IN to the Area + Cavity pool
    INSERT INTO dbo.TrayStockTransactions (TrayStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)
    VALUES (@AreaPoolId, SYSUTCDATETIME(), N''Adjustment'', N''TrayStockMigration'', @LegacyId, @Qty, @AreaBefore, NULL, @Remark + N'' -- carried over from Polyhouse pool'', SYSUTCDATETIME());
    UPDATE dbo.TrayStock SET PhysicalQuantity = @AreaBefore + @Qty, ModifiedDate = SYSUTCDATETIME(), ModifiedBy = N''Migration 2026-10-04'' WHERE Id = @AreaPoolId;

    FETCH NEXT FROM c INTO @LegacyId, @AreaId, @TraySize, @Qty;
END
CLOSE c; DEALLOCATE c;

UPDATE dbo.TrayStock SET IsActive = 0, ModifiedDate = SYSUTCDATETIME(), ModifiedBy = N''Migration 2026-10-04''
WHERE PolyhouseId IS NOT NULL AND IsActive = 1;');

    ----------------------------------------------------------------------
    -- 4) Permission TrayStock.Enter -> Sowing Supervisor only
    ----------------------------------------------------------------------
    IF NOT EXISTS (SELECT 1 FROM dbo.Permissions WHERE Code = N'TrayStock.Enter')
        INSERT INTO dbo.Permissions (Code, Description) VALUES (N'TrayStock.Enter', N'Add Tray Stock (Area + Cavity) for an authorized Area');
    DECLARE @PermId INT = (SELECT Id FROM dbo.Permissions WHERE Code = N'TrayStock.Enter');
    DECLARE @RoleId INT = (SELECT Id FROM dbo.Roles WHERE COALESCE(NULLIF(LTRIM(RTRIM(Name)), N''), RoleName) = N'Sowing Supervisor');
    IF @RoleId IS NULL THROW 51705, 'Role ''Sowing Supervisor'' not found.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.RolePermissions WHERE RoleId = @RoleId AND PermissionId = @PermId)
        INSERT INTO dbo.RolePermissions (RoleId, PermissionId) VALUES (@RoleId, @PermId);

    ----------------------------------------------------------------------
    -- VERIFICATION (any failure throws -> whole transaction rolls back)
    ----------------------------------------------------------------------
    EXEC (N'
PRINT ''=== AFTER: Tray Stock pools ==='';
SELECT t.Id, t.PolyhouseId, p.Name AS Polyhouse, t.AreaId, a.Name AS Area, t.TraySize, t.PhysicalQuantity, t.IsActive,
       CASE WHEN t.PolyhouseId IS NULL THEN ''Area pool (new)'' ELSE ''Polyhouse pool (legacy, retired)'' END AS Kind
FROM dbo.TrayStock t LEFT JOIN dbo.Polyhouses p ON p.Id = t.PolyhouseId JOIN dbo.Area a ON a.Id = t.AreaId ORDER BY t.PolyhouseId, t.Id;

PRINT ''=== Area + Cavity totals: legacy BEFORE (sum of legacy ledger excl. migration) vs new Area pool ==='';
;WITH pre AS (
    SELECT t.Id, t.AreaId, t.TraySize, t.PolyhouseId, t.PhysicalQuantity,
           ISNULL((SELECT SUM(x.Quantity) FROM dbo.TrayStockTransactions x WHERE x.TrayStockId = t.Id AND ISNULL(x.ReferenceType, N'''') <> N''TrayStockMigration''), 0) AS BalanceExclMigration
    FROM dbo.TrayStock t)
SELECT a.Name AS Area, pre.TraySize,
       SUM(CASE WHEN pre.PolyhouseId IS NOT NULL THEN pre.BalanceExclMigration ELSE 0 END) AS LegacyBalanceBefore,
       SUM(CASE WHEN pre.PolyhouseId IS NULL THEN pre.PhysicalQuantity ELSE 0 END) AS AreaPoolNow
FROM pre JOIN dbo.Area a ON a.Id = pre.AreaId GROUP BY a.Name, pre.TraySize ORDER BY a.Name, pre.TraySize;

PRINT ''=== New ledger rows written by this migration ==='';
SELECT Id, TrayStockId, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, Quantity + BeforeQuantity AS AfterQuantity FROM dbo.TrayStockTransactions WHERE ReferenceType = N''TrayStockMigration'' ORDER BY Id;

IF EXISTS (SELECT 1 FROM dbo.TrayStock t WHERE t.PhysicalQuantity <> ISNULL((SELECT SUM(x.Quantity) FROM dbo.TrayStockTransactions x WHERE x.TrayStockId = t.Id), 0))
    THROW 51706, ''Post-check failed: ledger does not reconcile.'', 1;
IF EXISTS (SELECT 1 FROM dbo.TrayStock WHERE PhysicalQuantity < 0)
    THROW 51707, ''Post-check failed: negative stock.'', 1;
IF EXISTS (SELECT 1 FROM dbo.TrayStock WHERE PolyhouseId IS NOT NULL AND (IsActive = 1 OR PhysicalQuantity <> 0))
    THROW 51708, ''Post-check failed: a legacy pool is still active or holds stock.'', 1;
IF EXISTS (SELECT 1 FROM dbo.TrayStock WHERE PolyhouseId IS NULL GROUP BY AreaId, TraySize HAVING COUNT(*) > 1)
    THROW 51709, ''Post-check failed: duplicate Area + Cavity pool.'', 1;
IF (SELECT SUM(PhysicalQuantity) FROM dbo.TrayStock) <> (SELECT SUM(Quantity) FROM dbo.TrayStockTransactions WHERE ISNULL(ReferenceType, N'''') <> N''TrayStockMigration'')
    THROW 51710, ''Post-check failed: total trays changed.'', 1;
IF EXISTS (SELECT 1 FROM dbo.TrayStockTransactions WHERE ReferenceType = N''TrayStockMigration'' GROUP BY ReferenceType HAVING SUM(Quantity) <> 0)
    THROW 51711, ''Post-check failed: migration OUT/IN rows do not net to zero.'', 1;');

    IF (SELECT COUNT(*) FROM dbo.TrayStockTransactions WHERE Id <= @MaxTxIdBefore) <> @TxCountBefore
       OR (SELECT CHECKSUM_AGG(CHECKSUM(Id, TrayStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt))
           FROM dbo.TrayStockTransactions WHERE Id <= @MaxTxIdBefore) <> @TxChecksumBefore
        THROW 51712, 'Post-check failed: an existing ledger row was changed or removed.', 1;
    PRINT '=== Existing history preserved: ' + CAST(@TxCountBefore AS VARCHAR(10)) + ' pre-migration ledger rows unchanged (count + checksum) ===';

    PRINT '=== Indexes / constraints on dbo.TrayStock ===';
    SELECT name, is_unique, filter_definition FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.TrayStock') AND name IS NOT NULL;
    SELECT name FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID('dbo.TrayStock');

    PRINT '=== Roles holding TrayStock.Enter (expect: Sowing Supervisor only) ===';
    SELECT r.RoleName FROM dbo.RolePermissions rp JOIN dbo.Roles r ON r.Id = rp.RoleId JOIN dbo.Permissions p ON p.Id = rp.PermissionId WHERE p.Code = N'TrayStock.Enter';

END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION TrayStockAreaCavity2026_10_04;
    THROW;
END CATCH;

----------------------------------------------------------------------------
-- Approved 2026-10-04: user reviewed the dry run (Area pools 2935 / 498 /
-- 1484, total 4917 unchanged, 14 migration ledger rows netting to 0, 23
-- historical rows unchanged, TrayStock.Enter -> Sowing Supervisor only) and
-- explicitly approved committing. Committed against PlantsIMS2_Test.
-- Verified backups: PlantsIMS2_Test_PreTrayStockAreaCavity_20261004_103050.bak
--                   PlantsIMS2_Test_PostTrayStockAreaCavity_20261004_103356.bak
----------------------------------------------------------------------------
IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION TrayStockAreaCavity2026_10_04;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION TrayStockAreaCavity2026_10_04;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
