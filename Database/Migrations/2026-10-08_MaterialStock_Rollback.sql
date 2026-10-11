-- ============================================================================
-- 2026-10-08_MaterialStock_Rollback.sql
--
-- Undoes 2026-10-08_MaterialStock.sql -- ONLY what it added: its triggers, the
-- SeedSowings.GrowingMaterialTypeId / SubmissionToken columns (and their index /
-- foreign key), the MaterialTransactions / MaterialStock / MaterialConsumptionRules /
-- MaterialTypes tables, the MaterialStock.* grants and the MaterialStock.* permissions.
-- REFUSES to run while any material stock balance or transaction exists, or while any
-- sowing records a growing material, so material data can never be lost by a rollback
-- (restore the pre-migration backup instead if that is really intended). The
-- application code must be rolled back first.
-- NOT touched: dbo.Inventory / InventoryTransactions, the Inventory Store, Tray Stock.
--
-- SAFETY: same guards as the migration. One transaction. Idempotent.
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 51941, 'This rollback may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 51942, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 51943, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION MaterialStockRollback;
BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';

    DECLARE @Rows INT = 0, @n INT;
    IF OBJECT_ID('dbo.MaterialStock', 'U') IS NOT NULL
    BEGIN EXEC sp_executesql N'SELECT @n = COUNT(*) FROM dbo.MaterialStock', N'@n INT OUTPUT', @n = @n OUTPUT; SET @Rows += @n; END
    IF OBJECT_ID('dbo.MaterialTransactions', 'U') IS NOT NULL
    BEGIN EXEC sp_executesql N'SELECT @n = COUNT(*) FROM dbo.MaterialTransactions', N'@n INT OUTPUT', @n = @n OUTPUT; SET @Rows += @n; END
    IF COL_LENGTH('dbo.SeedSowings', 'GrowingMaterialTypeId') IS NOT NULL
    BEGIN EXEC sp_executesql N'SELECT @n = COUNT(*) FROM dbo.SeedSowings WHERE GrowingMaterialTypeId IS NOT NULL', N'@n INT OUTPUT', @n = @n OUTPUT; SET @Rows += @n; END
    IF @Rows > 0
        THROW 51944, 'Material stock / transactions / sowings with a growing material exist -- refusing to drop them. Restore the pre-migration backup if that is really intended.', 1;

    IF OBJECT_ID('dbo.TR_SeedSowings_GrowingMaterial', 'TR') IS NOT NULL DROP TRIGGER dbo.TR_SeedSowings_GrowingMaterial;
    IF OBJECT_ID('dbo.TR_MaterialTransactions_Ledger', 'TR') IS NOT NULL DROP TRIGGER dbo.TR_MaterialTransactions_Ledger;
    IF OBJECT_ID('dbo.TR_MaterialStock_Rules', 'TR') IS NOT NULL DROP TRIGGER dbo.TR_MaterialStock_Rules;

    IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.SeedSowings') AND name = 'UX_SeedSowings_SubmissionToken')
        DROP INDEX UX_SeedSowings_SubmissionToken ON dbo.SeedSowings;
    IF OBJECT_ID('dbo.FK_SeedSowings_GrowingMaterial', 'F') IS NOT NULL
        ALTER TABLE dbo.SeedSowings DROP CONSTRAINT FK_SeedSowings_GrowingMaterial;
    IF COL_LENGTH('dbo.SeedSowings', 'GrowingMaterialTypeId') IS NOT NULL
        EXEC (N'ALTER TABLE dbo.SeedSowings DROP COLUMN GrowingMaterialTypeId;');
    IF COL_LENGTH('dbo.SeedSowings', 'SubmissionToken') IS NOT NULL
        EXEC (N'ALTER TABLE dbo.SeedSowings DROP COLUMN SubmissionToken;');

    IF OBJECT_ID('dbo.MaterialTransactions', 'U') IS NOT NULL DROP TABLE dbo.MaterialTransactions;
    IF OBJECT_ID('dbo.MaterialStock', 'U') IS NOT NULL DROP TABLE dbo.MaterialStock;
    IF OBJECT_ID('dbo.MaterialConsumptionRules', 'U') IS NOT NULL DROP TABLE dbo.MaterialConsumptionRules;
    IF OBJECT_ID('dbo.MaterialTypes', 'U') IS NOT NULL DROP TABLE dbo.MaterialTypes;

    DELETE rp FROM dbo.RolePermissions rp JOIN dbo.Permissions p ON p.Id = rp.PermissionId WHERE p.Code LIKE N'MaterialStock.%';
    PRINT CONCAT('Grants removed: ', @@ROWCOUNT);
    DELETE FROM dbo.Permissions WHERE Code LIKE N'MaterialStock.%';
    PRINT CONCAT('Permissions removed: ', @@ROWCOUNT);
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION MaterialStockRollback;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION MaterialStockRollback;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION MaterialStockRollback;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
