-- ============================================================================
-- 2026-10-04_TrayStock_LiveFinal.sql   (LIVE database only)
--
-- WHY: the live DB (shop.precisionagritech.in) still has the legacy TrayStock
-- schema: Polyhouse + TraySize, no AreaId. Locally, two scripts ran today:
--   2026-10-04_TrayStockAreaCavity.sql  (polyhouse pools -> area pools)
--   2026-10-04_TrayStockPolyhouse.sql   (area pools -> back to polyhouse pools)
-- Their stock movements cancel out, and the Polyhouse script only runs on the
-- local DB's exact audited state. Live pools are already polyhouse-wise, so
-- live only needs the FINAL SCHEMA, with NO ledger movements.
--
-- WHAT THIS DOES (one transaction):
--   1. dbo.TrayStock.AreaId INT NOT NULL, back-filled from Polyhouses.AreaId,
--      FK_TrayStock_Area -> dbo.Area(Id)
--   2. Drops UQ_TrayStock_Polyhouse_Size; PolyhouseId becomes NULLable
--   3. UX_TrayStock_Area_Polyhouse_Size (AreaId, PolyhouseId, TraySize) WHERE PolyhouseId IS NOT NULL
--      UX_TrayStock_Area_Size           (AreaId, TraySize)              WHERE PolyhouseId IS NULL
--      CK_TrayStock_ActivePoolHasPolyhouse CHECK (PolyhouseId IS NOT NULL OR IsActive = 0)
--   4. Permission 'TrayStock.Enter' (if missing), granted to 'Sowing Supervisor' only
--
-- NOT changed: no TrayStock balance, no IsActive flag, no TrayStockTransactions
-- row (count + checksum verified), no other table.
--
-- SAFETY: guarded to PlantsIMS2_Test. One transaction. Re-run safe.
-- Run:  sqlcmd ... -d PlantsIMS2_Test -I -b -v MODE=DRYRUN -i <this file>
--       then MODE=COMMIT
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test'
    THROW 51901, 'This script may only be run against PlantsIMS2_Test.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 51902, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
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

BEGIN TRANSACTION TrayStockLiveFinal;

BEGIN TRY

    ------------------------------------------------------------------
    -- BEFORE snapshot
    ------------------------------------------------------------------
    DECLARE @PoolRowsBefore INT           = (SELECT COUNT(*) FROM dbo.TrayStock);
    DECLARE @TotalBefore    DECIMAL(18,2) = (SELECT ISNULL(SUM(PhysicalQuantity), 0) FROM dbo.TrayStock);
    DECLARE @ActiveBefore   INT           = (SELECT COUNT(*) FROM dbo.TrayStock WHERE IsActive = 1);
    DECLARE @TxCountBefore  INT           = (SELECT COUNT(*) FROM dbo.TrayStockTransactions);
    DECLARE @TxSumBefore    INT           = (SELECT CHECKSUM_AGG(CHECKSUM(Id, TrayStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)) FROM dbo.TrayStockTransactions);

    PRINT '=== BEFORE: Tray Stock pools ===';
    SELECT t.Id, t.PolyhouseId, p.Name AS Polyhouse, p.AreaId, t.TraySize, t.PhysicalQuantity, t.IsActive
    FROM dbo.TrayStock t LEFT JOIN dbo.Polyhouses p ON p.Id = t.PolyhouseId
    ORDER BY t.Id;

    PRINT '=== Live CK_TrayStock_TraySize definition (compare with local) ===';
    SELECT name, definition FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID('dbo.TrayStock') AND name = 'CK_TrayStock_TraySize';

    ------------------------------------------------------------------
    -- PRE-CHECKS
    ------------------------------------------------------------------
    IF COL_LENGTH('dbo.TrayStock', 'AreaId') IS NULL
       AND EXISTS (SELECT 1 FROM dbo.TrayStock t
                   LEFT JOIN dbo.Polyhouses p ON p.Id = t.PolyhouseId
                   WHERE t.PolyhouseId IS NULL OR p.AreaId IS NULL)
        THROW 51903, 'A TrayStock row has no Polyhouse, or its Polyhouse has no AreaId. Nothing was changed.', 1;

    ------------------------------------------------------------------
    -- 1. AreaId
    ------------------------------------------------------------------
    IF COL_LENGTH('dbo.TrayStock', 'AreaId') IS NULL
    BEGIN
        ALTER TABLE dbo.TrayStock ADD AreaId INT NULL;
        PRINT '=== AreaId column added ===';
    END
    ELSE
        PRINT '=== AreaId already exists -- no-op ===';

    EXEC (N'UPDATE t SET AreaId = p.AreaId
            FROM dbo.TrayStock t JOIN dbo.Polyhouses p ON p.Id = t.PolyhouseId
            WHERE t.AreaId IS NULL;');
    PRINT CONCAT('AreaId back-filled rows: ', @@ROWCOUNT);

    EXEC (N'ALTER TABLE dbo.TrayStock ALTER COLUMN AreaId INT NOT NULL;');

    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_TrayStock_Area' AND parent_object_id = OBJECT_ID('dbo.TrayStock'))
    BEGIN
        EXEC (N'ALTER TABLE dbo.TrayStock WITH CHECK ADD CONSTRAINT FK_TrayStock_Area FOREIGN KEY (AreaId) REFERENCES dbo.Area(Id);');
        PRINT '=== FK_TrayStock_Area added ===';
    END

    ------------------------------------------------------------------
    -- 2. Old uniqueness out, PolyhouseId NULLable
    ------------------------------------------------------------------
    IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'UQ_TrayStock_Polyhouse_Size' AND parent_object_id = OBJECT_ID('dbo.TrayStock'))
    BEGIN
        ALTER TABLE dbo.TrayStock DROP CONSTRAINT UQ_TrayStock_Polyhouse_Size;
        PRINT '=== UQ_TrayStock_Polyhouse_Size (constraint) dropped ===';
    END
    ELSE IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UQ_TrayStock_Polyhouse_Size' AND object_id = OBJECT_ID('dbo.TrayStock'))
    BEGIN
        DROP INDEX UQ_TrayStock_Polyhouse_Size ON dbo.TrayStock;
        PRINT '=== UQ_TrayStock_Polyhouse_Size (index) dropped ===';
    END

    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_TrayStock_Polyhouse_Size' AND object_id = OBJECT_ID('dbo.TrayStock'))
        DROP INDEX UX_TrayStock_Polyhouse_Size ON dbo.TrayStock;

    ALTER TABLE dbo.TrayStock ALTER COLUMN PolyhouseId INT NULL;

    ------------------------------------------------------------------
    -- 3. Final uniqueness + check
    ------------------------------------------------------------------
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_TrayStock_Area_Polyhouse_Size' AND object_id = OBJECT_ID('dbo.TrayStock'))
        EXEC (N'CREATE UNIQUE INDEX UX_TrayStock_Area_Polyhouse_Size ON dbo.TrayStock (AreaId, PolyhouseId, TraySize) WHERE PolyhouseId IS NOT NULL;');

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_TrayStock_Area_Size' AND object_id = OBJECT_ID('dbo.TrayStock'))
        EXEC (N'CREATE UNIQUE INDEX UX_TrayStock_Area_Size ON dbo.TrayStock (AreaId, TraySize) WHERE PolyhouseId IS NULL;');

    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_TrayStock_ActivePoolHasPolyhouse' AND parent_object_id = OBJECT_ID('dbo.TrayStock'))
        EXEC (N'ALTER TABLE dbo.TrayStock WITH CHECK ADD CONSTRAINT CK_TrayStock_ActivePoolHasPolyhouse CHECK (PolyhouseId IS NOT NULL OR IsActive = 0);');

    ------------------------------------------------------------------
    -- 4. Permission TrayStock.Enter -> Sowing Supervisor only
    ------------------------------------------------------------------
    IF NOT EXISTS (SELECT 1 FROM dbo.Permissions WHERE Code = N'TrayStock.Enter')
    BEGIN
        IF COLUMNPROPERTY(OBJECT_ID('dbo.Permissions'), 'Id', 'IsIdentity') = 1
            INSERT INTO dbo.Permissions (Code, Description)
            VALUES (N'TrayStock.Enter', N'Add Tray Stock (Area + Polyhouse + Cavity) for an authorized Area');
        ELSE
            INSERT INTO dbo.Permissions (Id, Code, Description)
            SELECT ISNULL(MAX(Id), 0) + 1, N'TrayStock.Enter', N'Add Tray Stock (Area + Polyhouse + Cavity) for an authorized Area'
            FROM dbo.Permissions;
        PRINT '=== Permission TrayStock.Enter added ===';
    END
    ELSE
        PRINT '=== Permission TrayStock.Enter already exists -- no-op ===';

    INSERT INTO dbo.RolePermissions (RoleId, PermissionId)
    SELECT r.Id, p.Id
    FROM dbo.Roles r
    CROSS JOIN dbo.Permissions p
    WHERE COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = N'Sowing Supervisor'
      AND p.Code = N'TrayStock.Enter'
      AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = r.Id AND rp.PermissionId = p.Id);
    PRINT CONCAT('RolePermissions rows inserted: ', @@ROWCOUNT, ' (expect 1 on first run, 0 on re-run)');

    ------------------------------------------------------------------
    -- AFTER: verify nothing moved
    ------------------------------------------------------------------
    IF (SELECT COUNT(*) FROM dbo.TrayStock) <> @PoolRowsBefore
       OR (SELECT ISNULL(SUM(PhysicalQuantity), 0) FROM dbo.TrayStock) <> @TotalBefore
       OR (SELECT COUNT(*) FROM dbo.TrayStock WHERE IsActive = 1) <> @ActiveBefore
        THROW 51904, 'TrayStock rows / balances / active flags changed -- rolling back.', 1;

    IF (SELECT COUNT(*) FROM dbo.TrayStockTransactions) <> @TxCountBefore
       OR ISNULL((SELECT CHECKSUM_AGG(CHECKSUM(Id, TrayStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)) FROM dbo.TrayStockTransactions), 0) <> ISNULL(@TxSumBefore, 0)
        THROW 51905, 'TrayStockTransactions changed -- rolling back.', 1;

    PRINT '=== CHECK PASSED: pools, balances, active flags and ledger unchanged ===';

    PRINT '=== AFTER: columns ===';
    SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'TrayStock' ORDER BY ORDINAL_POSITION;

    PRINT '=== AFTER: indexes ===';
    SELECT name, is_unique, filter_definition FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.TrayStock') AND name IS NOT NULL;

    PRINT '=== AFTER: check constraints / foreign keys ===';
    SELECT name FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID('dbo.TrayStock');
    SELECT name FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID('dbo.TrayStock');

    PRINT '=== AFTER: TrayStock grants ===';
    SELECT COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) AS RoleName, p.Code
    FROM dbo.RolePermissions rp
    JOIN dbo.Roles r ON r.Id = rp.RoleId
    JOIN dbo.Permissions p ON p.Id = rp.PermissionId
    WHERE p.Code LIKE N'TrayStock.%';

    PRINT '=== AFTER: pools ===';
    EXEC (N'SELECT Id, AreaId, PolyhouseId, TraySize, PhysicalQuantity, IsActive FROM dbo.TrayStock ORDER BY Id;');

END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION TrayStockLiveFinal;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION TrayStockLiveFinal;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION TrayStockLiveFinal;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
