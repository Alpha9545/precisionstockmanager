-- ============================================================================
-- 2026-10-13_DataReset_Live.sql      (LIVE: shop.precisionagritech.in)
--
-- WHY: clean start on live. Mirrors the local transaction-data reset of
-- 2026-10-13 15:25 IST (TxDataReset): every TRANSACTIONAL table is emptied,
-- in the same child -> parent order, with the same triggers disabled around
-- their DELETE. Master data is NOT touched (users, roles, permissions, areas,
-- polyhouses, species, plant types, mother plants, employees, vendors,
-- growing partners, store items, vehicles, configuration, login history,
-- batch counters).
--
-- APPROVED by the owner on 2026-10-13 ("yes i want new live project, so you
-- can delete the live project data").
--
-- LIVE-SPECIFIC SAFETY
--   * A table that does not exist on live is skipped (and listed).
--   * Every ENABLED trigger on a table being emptied is disabled around that
--     table's DELETE and re-enabled right after (live has rows in tables that
--     were already empty locally, e.g. OutletBookingItems).
--   * Refuses to run if dbo.StoreTransactions has rows (StoreItems balances are
--     tied to that ledger; the local reset never had to handle that case).
--   * After the deletes: every reset table must be empty, every trigger must be
--     enabled again, and every master table must have the same row count --
--     otherwise everything is rolled back.
--   * Guarded to PlantsIMS2_Test on server $(TARGET_SERVER). One transaction.
--
-- Run:  sqlcmd -S 127.0.0.1,1433 -U sa -C -d PlantsIMS2_Test -I -b
--              -v MODE=DRYRUN TARGET_SERVER=srv1202245 -i <this file>
--       then MODE=COMMIT.  Undo = restore the backup taken just before.
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test'
    THROW 53001, 'This script may only be run against PlantsIMS2_Test.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 53002, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 53003, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run.', 1;
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

PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';

BEGIN TRANSACTION TxDataResetLive;

BEGIN TRY

    ------------------------------------------------------------------
    -- Reset list: same order and triggers as the local reset
    ------------------------------------------------------------------
    DECLARE @r TABLE (Seq INT PRIMARY KEY, TableName SYSNAME, TriggerName SYSNAME NULL,
                      Existed BIT NULL, RowsBefore INT NULL, RowsDeleted INT NULL);
    INSERT @r (Seq, TableName, TriggerName) VALUES
     ( 1,'BedSowingAuditLog','TR_BedSowingAuditLog_AppendOnly'),
     ( 2,'BedSowingOperations','TR_BedSowingOperations_Immutable'),
     ( 3,'BookingRevisions',NULL),
     ( 4,'CuttingStockTransactions',NULL),
     ( 5,'CuttingTransplants',NULL),
     ( 6,'DailyLabourAttendance',NULL),
     ( 7,'DailyLabourCounts',NULL),
     ( 8,'Dispatches',NULL),
     ( 9,'EmptyPotInventoryTransactions',NULL),
     (10,'EmptyPotPurchases',NULL),
     (11,'FertilizerUsage',NULL),
     (12,'InventoryTransactions',NULL),
     (13,'LabRequests',NULL),
     (14,'LabourLogs',NULL),
     (15,'LabourOvertime',NULL),
     (16,'MaintenanceTickets',NULL),
     (17,'MaterialTransactions','TR_MaterialTransactions_Ledger'),
     (18,'OutletBookingItems',NULL),
     (19,'OutletPurchases','TR_OutletPurchases_Rules'),
     (20,'OutletSaleItems','TR_OutletSaleItems_Rules'),
     (21,'OutletWastages','TR_OutletWastages_Rules'),
     (22,'PotProduction',NULL),
     (23,'PotProductionEntries','TR_PotEntries_Rules'),
     (24,'PottedPlantStockTransactions',NULL),
     (25,'PurchaseReceiptItems',NULL),
     (26,'ReadyConfirmations',NULL),
     (27,'ReadyStockTransactions',NULL),
     (28,'ReadyStockTransplants',NULL),
     (29,'SeedCuttingTx',NULL),
     (30,'SeedIssues',NULL),
     (31,'SeedStockTransactions',NULL),
     (32,'SeedlingDispatchLines',NULL),
     (33,'StoreTransactions',NULL),
     (34,'SuggestionPosts',NULL),
     (35,'Transactions',NULL),
     (36,'TrayStockTransactions',NULL),
     (37,'TrayTransfers',NULL),
     (38,'BookingBatchAllocations',NULL),
     (39,'DailyLabourAttendanceHistory',NULL),
     (40,'FertilizerStock',NULL),
     (41,'InternalTransfers',NULL),
     (42,'Inventory',NULL),
     (43,'LabourOvertimeHistory',NULL),
     (44,'MaintenanceTicketEvents',NULL),
     (45,'MaintenanceTicketPhotos',NULL),
     (46,'MaterialStock',NULL),
     (47,'OutletBookings',NULL),
     (48,'OutletSales','TR_OutletSales_Immutable'),
     (49,'PotProductionBatches',NULL),
     (50,'PottedPlantBookings',NULL),
     (51,'PropagationBatches',NULL),
     (52,'PurchaseOrderItems',NULL),
     (53,'PurchaseReceipts',NULL),
     (54,'SeedCuttingBank',NULL),
     (55,'SeedlingDispatches',NULL),
     (56,'StoreRequestLines',NULL),
     (57,'SuggestionComments',NULL),
     (58,'SuggestionPostEvents',NULL),
     (59,'SuggestionPostPhotos',NULL),
     (60,'TrayStock',NULL),
     (61,'VendorPurchases',NULL),
     (62,'Bookings',NULL),
     (63,'CuttingDeliveries',NULL),
     (64,'CuttingProductions',NULL),
     (65,'PottedPlantStock',NULL),
     (66,'PurchaseOrders',NULL),
     (67,'ReadyStock',NULL),
     (68,'SeedEntries',NULL),
     (69,'StoreRequests',NULL),
     (70,'ActualCuttings',NULL),
     (71,'EmptyPotInventory',NULL),
     (72,'SeedSowings',NULL),
     (73,'BedSowings',NULL),
     (74,'CuttingPlans',NULL),
     (75,'CuttingStock',NULL),
     (76,'SeedStock',NULL);

    UPDATE @r SET Existed = CASE WHEN OBJECT_ID(N'dbo.' + QUOTENAME(TableName), 'U') IS NULL THEN 0 ELSE 1 END;

    ------------------------------------------------------------------
    -- Master tables that must NOT change (only those that exist)
    ------------------------------------------------------------------
    DECLARE @m TABLE (TableName SYSNAME PRIMARY KEY, RowsBefore INT NULL, RowsAfter INT NULL);
    INSERT @m (TableName)
    SELECT v.n FROM (VALUES
      ('IMSUsers'),('Users'),('Roles'),('UserRoles'),('Permissions'),('RolePermissions'),
      ('Area'),('Areas'),('Polyhouses'),('PlantTypes'),('PlantSpecies'),('MotherPlants'),
      ('Employee'),('Designation'),('Vendors'),('GrowingPartners'),('PotSizes'),('UnitMaster'),
      ('FertilizerMaster'),('FertilizerType'),('FertilizerSource'),('SeedSources'),
      ('States'),('Districts'),('StoreItems'),('MaterialTypes'),('MaterialConsumptionRules'),
      ('MaintenanceCategories'),('SuggestionPostTypes'),('Vehicles'),('Drivers'),('CompanyLocations'),
      ('UserLoginHistory'),('BatchNumberCounters'),('BatchNumberSequences')
    ) v(n)
    WHERE OBJECT_ID(N'dbo.' + QUOTENAME(v.n), 'U') IS NOT NULL;

    DECLARE @t SYSNAME, @trg SYSNAME, @sql NVARCHAR(MAX), @n INT, @seq INT;

    DECLARE cm CURSOR LOCAL FAST_FORWARD FOR SELECT TableName FROM @m;
    OPEN cm; FETCH NEXT FROM cm INTO @t;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @sql = N'SELECT @n = COUNT(*) FROM dbo.' + QUOTENAME(@t) + N';';
        EXEC sp_executesql @sql, N'@n INT OUTPUT', @n OUTPUT;
        UPDATE @m SET RowsBefore = @n WHERE TableName = @t;
        FETCH NEXT FROM cm INTO @t;
    END
    CLOSE cm; DEALLOCATE cm;

    DECLARE cb CURSOR LOCAL FAST_FORWARD FOR SELECT Seq, TableName FROM @r WHERE Existed = 1;
    OPEN cb; FETCH NEXT FROM cb INTO @seq, @t;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @sql = N'SELECT @n = COUNT(*) FROM dbo.' + QUOTENAME(@t) + N';';
        EXEC sp_executesql @sql, N'@n INT OUTPUT', @n OUTPUT;
        UPDATE @r SET RowsBefore = @n WHERE Seq = @seq;
        FETCH NEXT FROM cb INTO @seq, @t;
    END
    CLOSE cb; DEALLOCATE cb;

    PRINT '=== Tables in the reset list that do not exist on live (skipped) ===';
    SELECT Seq, TableName FROM @r WHERE Existed = 0 ORDER BY Seq;

    ------------------------------------------------------------------
    -- Pre-check
    ------------------------------------------------------------------
    IF EXISTS (SELECT 1 FROM @r WHERE TableName = 'StoreTransactions' AND Existed = 1 AND RowsBefore > 0)
        THROW 53004, 'dbo.StoreTransactions has rows on live -- StoreItems balances depend on it. Nothing was changed; ask before resetting.', 1;

    ------------------------------------------------------------------
    -- Reset
    ------------------------------------------------------------------
    -- Every ENABLED trigger on a table being emptied is disabled around its DELETE
    -- (live has rows in tables that were empty locally, e.g. OutletBookingItems),
    -- and exactly those triggers are enabled again right after. Transactional.
    DECLARE @trgs TABLE (ObjId INT PRIMARY KEY, TableName SYSNAME, TriggerName SYSNAME);
    DECLARE @dis NVARCHAR(MAX), @en NVARCHAR(MAX);

    DECLARE cd CURSOR LOCAL FAST_FORWARD FOR SELECT Seq, TableName FROM @r WHERE Existed = 1 ORDER BY Seq;
    OPEN cd; FETCH NEXT FROM cd INTO @seq, @t;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SELECT @dis = NULL, @en = NULL;
        SELECT @dis = STRING_AGG(CAST(N'DISABLE TRIGGER ' + QUOTENAME(OBJECT_SCHEMA_NAME(tr.object_id)) + N'.' + QUOTENAME(tr.name) + N' ON dbo.' + QUOTENAME(@t) + N';' AS NVARCHAR(MAX)), N' '),
               @en  = STRING_AGG(CAST(N'ENABLE TRIGGER '  + QUOTENAME(OBJECT_SCHEMA_NAME(tr.object_id)) + N'.' + QUOTENAME(tr.name) + N' ON dbo.' + QUOTENAME(@t) + N';' AS NVARCHAR(MAX)), N' ')
        FROM sys.triggers tr
        WHERE tr.parent_id = OBJECT_ID(N'dbo.' + QUOTENAME(@t)) AND tr.is_disabled = 0 AND tr.is_ms_shipped = 0;

        INSERT @trgs (ObjId, TableName, TriggerName)
        SELECT tr.object_id, @t, tr.name
        FROM sys.triggers tr
        WHERE tr.parent_id = OBJECT_ID(N'dbo.' + QUOTENAME(@t)) AND tr.is_disabled = 0 AND tr.is_ms_shipped = 0;

        IF @dis IS NOT NULL EXEC (@dis);

        SET @sql = N'DELETE FROM dbo.' + QUOTENAME(@t) + N'; SET @n = @@ROWCOUNT;';
        EXEC sp_executesql @sql, N'@n INT OUTPUT', @n OUTPUT;
        UPDATE @r SET RowsDeleted = @n WHERE Seq = @seq;

        IF @en IS NOT NULL EXEC (@en);

        FETCH NEXT FROM cd INTO @seq, @t;
    END
    CLOSE cd; DEALLOCATE cd;

    ------------------------------------------------------------------
    -- Verify
    ------------------------------------------------------------------
    DECLARE @notEmpty NVARCHAR(MAX) = N'';
    DECLARE cv CURSOR LOCAL FAST_FORWARD FOR SELECT TableName FROM @r WHERE Existed = 1;
    OPEN cv; FETCH NEXT FROM cv INTO @t;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @sql = N'SELECT @n = COUNT(*) FROM dbo.' + QUOTENAME(@t) + N';';
        EXEC sp_executesql @sql, N'@n INT OUTPUT', @n OUTPUT;
        IF @n <> 0 SET @notEmpty += @t + N'(' + CAST(@n AS NVARCHAR(20)) + N') ';
        FETCH NEXT FROM cv INTO @t;
    END
    CLOSE cv; DEALLOCATE cv;
    IF @notEmpty <> N''
    BEGIN
        DECLARE @msg1 NVARCHAR(2048) = N'Tables not empty after reset: ' + @notEmpty;
        THROW 53005, @msg1, 1;
    END

    IF EXISTS (SELECT 1 FROM sys.triggers tr JOIN @trgs x ON x.ObjId = tr.object_id WHERE tr.is_disabled = 1)
        THROW 53006, 'A trigger is still disabled after the reset -- rolling back.', 1;

    DECLARE cm2 CURSOR LOCAL FAST_FORWARD FOR SELECT TableName FROM @m;
    OPEN cm2; FETCH NEXT FROM cm2 INTO @t;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @sql = N'SELECT @n = COUNT(*) FROM dbo.' + QUOTENAME(@t) + N';';
        EXEC sp_executesql @sql, N'@n INT OUTPUT', @n OUTPUT;
        UPDATE @m SET RowsAfter = @n WHERE TableName = @t;
        FETCH NEXT FROM cm2 INTO @t;
    END
    CLOSE cm2; DEALLOCATE cm2;

    IF EXISTS (SELECT 1 FROM @m WHERE RowsBefore <> RowsAfter)
    BEGIN
        SELECT TableName, RowsBefore, RowsAfter FROM @m WHERE RowsBefore <> RowsAfter;
        THROW 53007, 'A master table row count changed -- rolling back.', 1;
    END

    ------------------------------------------------------------------
    -- Report
    ------------------------------------------------------------------
    PRINT '=== Reset: rows before / deleted (tables that had rows) ===';
    SELECT Seq, TableName, RowsBefore, RowsDeleted FROM @r WHERE Existed = 1 AND RowsBefore > 0 ORDER BY Seq;

    PRINT '=== Triggers disabled during their table''s DELETE (all enabled again) ===';
    SELECT x.TableName, x.TriggerName, CASE tr.is_disabled WHEN 0 THEN 'enabled' ELSE 'DISABLED' END AS NowState
    FROM @trgs x JOIN sys.triggers tr ON tr.object_id = x.ObjId ORDER BY x.TableName, x.TriggerName;

    PRINT '=== Totals ===';
    SELECT COUNT(*) AS TablesReset, SUM(RowsBefore) AS RowsBefore, SUM(RowsDeleted) AS RowsDeleted FROM @r WHERE Existed = 1;

    PRINT '=== Master tables (must be unchanged) ===';
    SELECT TableName, RowsBefore, RowsAfter FROM @m ORDER BY TableName;

    PRINT '=== CHECK PASSED: reset tables empty, triggers enabled, master data unchanged ===';

END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION TxDataResetLive;
    THROW;
END CATCH;

IF N'$(MODE)' = N'COMMIT'
BEGIN
    COMMIT TRANSACTION TxDataResetLive;
    PRINT '=== COMMITTED ===';
END
ELSE
BEGIN
    ROLLBACK TRANSACTION TxDataResetLive;
    PRINT '=== DRY RUN -- rolled back, nothing changed ===';
END
GO
