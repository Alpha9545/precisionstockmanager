-- ============================================================================
-- 2026-10-06_StoreInventory_Rollback.sql
--
-- Undoes 2026-10-06_StoreInventory.sql -- ONLY what it added: its triggers, the
-- StoreTransactions / StoreRequestLines / StoreRequests / StoreItems /
-- StoreItemCategories tables, the Store.* grants and the Store.* permissions.
-- REFUSES to run while any store item, request or transaction exists, so store
-- data can never be lost by a rollback (restore the pre-migration backup
-- instead if that is really intended). The application code must be rolled back first.
-- NOT touched: dbo.Inventory / dbo.InventoryTransactions (legacy), UnitMaster, Vendors.
--
-- SAFETY: same guards as the migration. One transaction. Idempotent.
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 51821, 'This rollback may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 51822, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 51823, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION StoreInventoryRollback;
BEGIN TRY
    PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';

    DECLARE @Rows INT = 0, @n INT;
    IF OBJECT_ID('dbo.StoreItems', 'U') IS NOT NULL
    BEGIN EXEC sp_executesql N'SELECT @n = COUNT(*) FROM dbo.StoreItems', N'@n INT OUTPUT', @n = @n OUTPUT; SET @Rows += @n; END
    IF OBJECT_ID('dbo.StoreRequests', 'U') IS NOT NULL
    BEGIN EXEC sp_executesql N'SELECT @n = COUNT(*) FROM dbo.StoreRequests', N'@n INT OUTPUT', @n = @n OUTPUT; SET @Rows += @n; END
    IF OBJECT_ID('dbo.StoreTransactions', 'U') IS NOT NULL
    BEGIN EXEC sp_executesql N'SELECT @n = COUNT(*) FROM dbo.StoreTransactions', N'@n INT OUTPUT', @n = @n OUTPUT; SET @Rows += @n; END
    IF @Rows > 0
        THROW 51824, 'Store items / requests / transactions exist -- refusing to drop them. Restore the pre-migration backup if that is really intended.', 1;

    IF OBJECT_ID('dbo.TR_StoreRequests_Location', 'TR') IS NOT NULL   DROP TRIGGER dbo.TR_StoreRequests_Location;
    IF OBJECT_ID('dbo.TR_StoreItems_Balance', 'TR') IS NOT NULL       DROP TRIGGER dbo.TR_StoreItems_Balance;
    IF OBJECT_ID('dbo.TR_StoreTransactions_Ledger', 'TR') IS NOT NULL DROP TRIGGER dbo.TR_StoreTransactions_Ledger;
    IF OBJECT_ID('dbo.StoreTransactions', 'U') IS NOT NULL            DROP TABLE dbo.StoreTransactions;
    IF OBJECT_ID('dbo.StoreRequestLines', 'U') IS NOT NULL            DROP TABLE dbo.StoreRequestLines;
    IF OBJECT_ID('dbo.StoreRequests', 'U') IS NOT NULL                DROP TABLE dbo.StoreRequests;
    IF OBJECT_ID('dbo.StoreItems', 'U') IS NOT NULL                   DROP TABLE dbo.StoreItems;
    IF OBJECT_ID('dbo.StoreItemCategories', 'U') IS NOT NULL          DROP TABLE dbo.StoreItemCategories;

    DELETE rp FROM dbo.RolePermissions rp JOIN dbo.Permissions p ON p.Id = rp.PermissionId WHERE p.Code IN (N'Store.Request', N'Store.View', N'Store.Manage');
    PRINT CONCAT('Store grants removed: ', @@ROWCOUNT);
    DELETE FROM dbo.Permissions WHERE Code IN (N'Store.Request', N'Store.View', N'Store.Manage');
    PRINT CONCAT('Store permissions removed: ', @@ROWCOUNT);

    SELECT OBJECT_ID('dbo.StoreItems') AS StoreItemsTable, (SELECT COUNT(*) FROM dbo.Inventory) AS LegacyInventoryRows,
           (SELECT COUNT(*) FROM dbo.InventoryTransactions) AS LegacyInventoryTransactionRows;

    IF N'$(MODE)' = N'COMMIT'
    BEGIN
        COMMIT TRANSACTION StoreInventoryRollback;
        PRINT '=== COMMITTED ===';
    END
    ELSE
    BEGIN
        ROLLBACK TRANSACTION StoreInventoryRollback;
        PRINT '=== DRY RUN -- rolled back, nothing changed ===';
    END
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH
GO
