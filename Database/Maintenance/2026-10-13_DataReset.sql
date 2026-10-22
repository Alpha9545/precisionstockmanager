-- ============================================================================
-- 2026-10-13_DataReset.sql
--
-- !!! DESTRUCTIVE: DELETES *EVERY ROW* (no WHERE clause) FROM 76 TRANSACTIONAL / OPERATIONAL TABLES !!!
--
-- WHAT: the exact transaction-data reset run on LOCAL LAPTOP-R87BKQGH\MSSQLSERVER02 / PlantsIMS2_Test on 2026-10-13
--   (~15:25 IST, owner-approved manifest; 22,819 rows deleted). Same 76 DELETE statements, same order (children before
--   parents, from the foreign keys; ON DELETE CASCADE parents first), same targeted DISABLE/ENABLE of the 8 append-only /
--   ledger / immutable triggers -- each only around its own table's DELETE and inside the one transaction.
--   Added for this saved copy: the migrations' guard header, and a before / after row-count report for all 76 tables.
-- KEEPS (never touched): the 37 master / security / configuration tables listed in @keep below -- Areas, Polyhouses,
--   varieties, users, roles, permissions, Area assignments, material rules, MotherPlants, UserLoginHistory, batch counters.
--   No identity reseed, no counter reset, no schema change. All triggers are enabled again before COMMIT (checked).
-- NOT A CLEANUP OF TEST RECORDS: it empties all bookings, sales, sowings, approvals, stock, ledgers, transfers, purchases,
--   fertilizer, legacy seed/inventory and labour / maintenance / suggestion records.
-- SAFETY: guarded to PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy, to -v TARGET_SERVER=<@@SERVERNAME>, and to
--   -v TARGET_DB=<the connected database>; refuses if the table set differs from the 113-table inventory or any trigger is
--   already disabled. One transaction; MODE=DRYRUN does everything and rolls back. Take and verify a backup first.
--   Before the 2026-10-13 run: PlantsIMS2_Test_PreDataReset_20261013_152300.bak (after: PlantsIMS2_Test_PostDataReset_20261013_152651.bak).
-- Run:  sqlcmd -S <server> -d <db> -E -C -I -b -v MODE=DRYRUN TARGET_SERVER=<@@SERVERNAME> TARGET_DB=<db> -i <this file>
-- ============================================================================
:on error exit
IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 53002, '2026-10-13_DataReset.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;
IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')
    THROW 53004, 'Run with sqlcmd -v MODE=DRYRUN or MODE=COMMIT.', 1;
IF @@SERVERNAME <> N'$(TARGET_SERVER)'
    THROW 53003, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;
IF DB_NAME() <> N'$(TARGET_DB)'
    THROW 53001, 'TARGET_DB does not match the connected database -- refusing to run on an unexpected database.', 1;
GO
SET NOCOUNT ON; SET XACT_ABORT ON; SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON;
DECLARE @d TABLE (TableName SYSNAME, RowsDeleted BIGINT);
DECLARE @clear TABLE (n SYSNAME PRIMARY KEY); INSERT @clear VALUES (N'BedSowingAuditLog'), (N'BedSowingOperations'), (N'BookingRevisions'), (N'CuttingStockTransactions'), (N'CuttingTransplants'), (N'DailyLabourAttendance'), (N'DailyLabourCounts'), (N'Dispatches'), (N'EmptyPotInventoryTransactions'), (N'EmptyPotPurchases'), (N'FertilizerUsage'), (N'InventoryTransactions'), (N'LabRequests'), (N'LabourLogs'), (N'LabourOvertime'), (N'MaintenanceTickets'), (N'MaterialTransactions'), (N'OutletBookingItems'), (N'OutletPurchases'), (N'OutletSaleItems'), (N'OutletWastages'), (N'PotProduction'), (N'PotProductionEntries'), (N'PottedPlantStockTransactions'), (N'PurchaseReceiptItems'), (N'ReadyConfirmations'), (N'ReadyStockTransactions'), (N'ReadyStockTransplants'), (N'SeedCuttingTx'), (N'SeedIssues'), (N'SeedStockTransactions'), (N'SeedlingDispatchLines'), (N'StoreTransactions'), (N'SuggestionPosts'), (N'Transactions'), (N'TrayStockTransactions'), (N'TrayTransfers'), (N'BookingBatchAllocations'), (N'DailyLabourAttendanceHistory'), (N'FertilizerStock'), (N'InternalTransfers'), (N'Inventory'), (N'LabourOvertimeHistory'), (N'MaintenanceTicketEvents'), (N'MaintenanceTicketPhotos'), (N'MaterialStock'), (N'OutletBookings'), (N'OutletSales'), (N'PotProductionBatches'), (N'PottedPlantBookings'), (N'PropagationBatches'), (N'PurchaseOrderItems'), (N'PurchaseReceipts'), (N'SeedCuttingBank'), (N'SeedlingDispatches'), (N'StoreRequestLines'), (N'SuggestionComments'), (N'SuggestionPostEvents'), (N'SuggestionPostPhotos'), (N'TrayStock'), (N'VendorPurchases'), (N'Bookings'), (N'CuttingDeliveries'), (N'CuttingProductions'), (N'PottedPlantStock'), (N'PurchaseOrders'), (N'ReadyStock'), (N'SeedEntries'), (N'StoreRequests'), (N'ActualCuttings'), (N'EmptyPotInventory'), (N'SeedSowings'), (N'BedSowings'), (N'CuttingPlans'), (N'CuttingStock'), (N'SeedStock');
DECLARE @keep TABLE (n SYSNAME PRIMARY KEY); INSERT @keep VALUES (N'Area'), (N'Areas'), (N'Polyhouses'), (N'GrowingPartners'), (N'BedSowingLocations'), (N'BedSowingApprover'), (N'PlantSpecies'), (N'PlantTypes'), (N'PotSizes'), (N'SeedSources'), (N'Vendors'), (N'UnitMaster'), (N'States'), (N'Districts'), (N'TempStateDistrict'), (N'Designation'), (N'Employee'), (N'IMSUsers'), (N'Users'), (N'Roles'), (N'Permissions'), (N'RolePermissions'), (N'UserRoles'), (N'RolePermissionChangeLog'), (N'MaterialTypes'), (N'MaterialConsumptionRules'), (N'FertilizerMaster'), (N'FertilizerType'), (N'FertilizerSource'), (N'MaintenanceCategories'), (N'StoreItemCategories'), (N'StoreItems'), (N'SuggestionPostTypes'), (N'MotherPlants'), (N'UserLoginHistory'), (N'BatchNumberSequences'), (N'BatchNumberCounters');
-- drift guard: exactly the 113 inventoried tables, every one classified
IF (SELECT COUNT(*) FROM sys.tables) <> 113 OR EXISTS (SELECT 1 FROM sys.tables t WHERE t.name NOT IN (SELECT n FROM @clear UNION SELECT n FROM @keep))
   OR (SELECT COUNT(*) FROM sys.tables t WHERE t.name IN (SELECT n FROM @clear)) <> 76
    THROW 53005, 'Table set differs from the approved inventory -- nothing changed.', 1;
IF EXISTS (SELECT 1 FROM sys.triggers WHERE is_disabled = 1) THROW 53006, 'A trigger is already disabled -- nothing changed.', 1;
PRINT '=== ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';

-- Row counts BEFORE (all 76 tables).
DECLARE @before TABLE (n SYSNAME PRIMARY KEY, c BIGINT);
DECLARE @after TABLE (n SYSNAME PRIMARY KEY, c BIGINT);
DECLARE @ct SYSNAME, @cn BIGINT, @cq NVARCHAR(400), @line NVARCHAR(400);
    DECLARE cbefore CURSOR LOCAL FAST_FORWARD FOR SELECT n FROM @clear;
    OPEN cbefore; FETCH NEXT FROM cbefore INTO @ct;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @cq = N'SELECT @cn = COUNT_BIG(*) FROM dbo.' + QUOTENAME(@ct);
        EXEC sp_executesql @cq, N'@cn BIGINT OUTPUT', @cn = @cn OUTPUT;
        INSERT @before VALUES (@ct, @cn);
        FETCH NEXT FROM cbefore INTO @ct;
    END
    CLOSE cbefore; DEALLOCATE cbefore;

BEGIN TRANSACTION TxDataReset;
BEGIN TRY
    DISABLE TRIGGER dbo.TR_BedSowingAuditLog_AppendOnly ON dbo.BedSowingAuditLog;
    DELETE FROM dbo.BedSowingAuditLog; INSERT @d VALUES (N'BedSowingAuditLog', @@ROWCOUNT);
    ENABLE TRIGGER dbo.TR_BedSowingAuditLog_AppendOnly ON dbo.BedSowingAuditLog;
    DISABLE TRIGGER dbo.TR_BedSowingOperations_Immutable ON dbo.BedSowingOperations;
    DELETE FROM dbo.BedSowingOperations; INSERT @d VALUES (N'BedSowingOperations', @@ROWCOUNT);
    ENABLE TRIGGER dbo.TR_BedSowingOperations_Immutable ON dbo.BedSowingOperations;
    DELETE FROM dbo.BookingRevisions; INSERT @d VALUES (N'BookingRevisions', @@ROWCOUNT);
    DELETE FROM dbo.CuttingStockTransactions; INSERT @d VALUES (N'CuttingStockTransactions', @@ROWCOUNT);
    DELETE FROM dbo.CuttingTransplants; INSERT @d VALUES (N'CuttingTransplants', @@ROWCOUNT);
    DELETE FROM dbo.DailyLabourAttendance; INSERT @d VALUES (N'DailyLabourAttendance', @@ROWCOUNT);
    DELETE FROM dbo.DailyLabourCounts; INSERT @d VALUES (N'DailyLabourCounts', @@ROWCOUNT);
    DELETE FROM dbo.Dispatches; INSERT @d VALUES (N'Dispatches', @@ROWCOUNT);
    DELETE FROM dbo.EmptyPotInventoryTransactions; INSERT @d VALUES (N'EmptyPotInventoryTransactions', @@ROWCOUNT);
    DELETE FROM dbo.EmptyPotPurchases; INSERT @d VALUES (N'EmptyPotPurchases', @@ROWCOUNT);
    DELETE FROM dbo.FertilizerUsage; INSERT @d VALUES (N'FertilizerUsage', @@ROWCOUNT);
    DELETE FROM dbo.InventoryTransactions; INSERT @d VALUES (N'InventoryTransactions', @@ROWCOUNT);
    DELETE FROM dbo.LabRequests; INSERT @d VALUES (N'LabRequests', @@ROWCOUNT);
    DELETE FROM dbo.LabourLogs; INSERT @d VALUES (N'LabourLogs', @@ROWCOUNT);
    DELETE FROM dbo.LabourOvertime; INSERT @d VALUES (N'LabourOvertime', @@ROWCOUNT);
    DELETE FROM dbo.MaintenanceTickets; INSERT @d VALUES (N'MaintenanceTickets', @@ROWCOUNT);
    DISABLE TRIGGER dbo.TR_MaterialTransactions_Ledger ON dbo.MaterialTransactions;
    DELETE FROM dbo.MaterialTransactions; INSERT @d VALUES (N'MaterialTransactions', @@ROWCOUNT);
    ENABLE TRIGGER dbo.TR_MaterialTransactions_Ledger ON dbo.MaterialTransactions;
    DELETE FROM dbo.OutletBookingItems; INSERT @d VALUES (N'OutletBookingItems', @@ROWCOUNT);
    DISABLE TRIGGER dbo.TR_OutletPurchases_Rules ON dbo.OutletPurchases;
    DELETE FROM dbo.OutletPurchases; INSERT @d VALUES (N'OutletPurchases', @@ROWCOUNT);
    ENABLE TRIGGER dbo.TR_OutletPurchases_Rules ON dbo.OutletPurchases;
    DISABLE TRIGGER dbo.TR_OutletSaleItems_Rules ON dbo.OutletSaleItems;
    DELETE FROM dbo.OutletSaleItems; INSERT @d VALUES (N'OutletSaleItems', @@ROWCOUNT);
    ENABLE TRIGGER dbo.TR_OutletSaleItems_Rules ON dbo.OutletSaleItems;
    DISABLE TRIGGER dbo.TR_OutletWastages_Rules ON dbo.OutletWastages;
    DELETE FROM dbo.OutletWastages; INSERT @d VALUES (N'OutletWastages', @@ROWCOUNT);
    ENABLE TRIGGER dbo.TR_OutletWastages_Rules ON dbo.OutletWastages;
    DELETE FROM dbo.PotProduction; INSERT @d VALUES (N'PotProduction', @@ROWCOUNT);
    DISABLE TRIGGER dbo.TR_PotEntries_Rules ON dbo.PotProductionEntries;
    DELETE FROM dbo.PotProductionEntries; INSERT @d VALUES (N'PotProductionEntries', @@ROWCOUNT);
    ENABLE TRIGGER dbo.TR_PotEntries_Rules ON dbo.PotProductionEntries;
    DELETE FROM dbo.PottedPlantStockTransactions; INSERT @d VALUES (N'PottedPlantStockTransactions', @@ROWCOUNT);
    DELETE FROM dbo.PurchaseReceiptItems; INSERT @d VALUES (N'PurchaseReceiptItems', @@ROWCOUNT);
    DELETE FROM dbo.ReadyConfirmations; INSERT @d VALUES (N'ReadyConfirmations', @@ROWCOUNT);
    DELETE FROM dbo.ReadyStockTransactions; INSERT @d VALUES (N'ReadyStockTransactions', @@ROWCOUNT);
    DELETE FROM dbo.ReadyStockTransplants; INSERT @d VALUES (N'ReadyStockTransplants', @@ROWCOUNT);
    DELETE FROM dbo.SeedCuttingTx; INSERT @d VALUES (N'SeedCuttingTx', @@ROWCOUNT);
    DELETE FROM dbo.SeedIssues; INSERT @d VALUES (N'SeedIssues', @@ROWCOUNT);
    DELETE FROM dbo.SeedStockTransactions; INSERT @d VALUES (N'SeedStockTransactions', @@ROWCOUNT);
    DELETE FROM dbo.SeedlingDispatchLines; INSERT @d VALUES (N'SeedlingDispatchLines', @@ROWCOUNT);
    DELETE FROM dbo.StoreTransactions; INSERT @d VALUES (N'StoreTransactions', @@ROWCOUNT);
    DELETE FROM dbo.SuggestionPosts; INSERT @d VALUES (N'SuggestionPosts', @@ROWCOUNT);
    DELETE FROM dbo.Transactions; INSERT @d VALUES (N'Transactions', @@ROWCOUNT);
    DELETE FROM dbo.TrayStockTransactions; INSERT @d VALUES (N'TrayStockTransactions', @@ROWCOUNT);
    DELETE FROM dbo.TrayTransfers; INSERT @d VALUES (N'TrayTransfers', @@ROWCOUNT);
    DELETE FROM dbo.BookingBatchAllocations; INSERT @d VALUES (N'BookingBatchAllocations', @@ROWCOUNT);
    DELETE FROM dbo.DailyLabourAttendanceHistory; INSERT @d VALUES (N'DailyLabourAttendanceHistory', @@ROWCOUNT);
    DELETE FROM dbo.FertilizerStock; INSERT @d VALUES (N'FertilizerStock', @@ROWCOUNT);
    DELETE FROM dbo.InternalTransfers; INSERT @d VALUES (N'InternalTransfers', @@ROWCOUNT);
    DELETE FROM dbo.Inventory; INSERT @d VALUES (N'Inventory', @@ROWCOUNT);
    DELETE FROM dbo.LabourOvertimeHistory; INSERT @d VALUES (N'LabourOvertimeHistory', @@ROWCOUNT);
    DELETE FROM dbo.MaintenanceTicketEvents; INSERT @d VALUES (N'MaintenanceTicketEvents', @@ROWCOUNT);
    DELETE FROM dbo.MaintenanceTicketPhotos; INSERT @d VALUES (N'MaintenanceTicketPhotos', @@ROWCOUNT);
    DELETE FROM dbo.MaterialStock; INSERT @d VALUES (N'MaterialStock', @@ROWCOUNT);
    DELETE FROM dbo.OutletBookings; INSERT @d VALUES (N'OutletBookings', @@ROWCOUNT);
    DISABLE TRIGGER dbo.TR_OutletSales_Immutable ON dbo.OutletSales;
    DELETE FROM dbo.OutletSales; INSERT @d VALUES (N'OutletSales', @@ROWCOUNT);
    ENABLE TRIGGER dbo.TR_OutletSales_Immutable ON dbo.OutletSales;
    DELETE FROM dbo.PotProductionBatches; INSERT @d VALUES (N'PotProductionBatches', @@ROWCOUNT);
    DELETE FROM dbo.PottedPlantBookings; INSERT @d VALUES (N'PottedPlantBookings', @@ROWCOUNT);
    DELETE FROM dbo.PropagationBatches; INSERT @d VALUES (N'PropagationBatches', @@ROWCOUNT);
    DELETE FROM dbo.PurchaseOrderItems; INSERT @d VALUES (N'PurchaseOrderItems', @@ROWCOUNT);
    DELETE FROM dbo.PurchaseReceipts; INSERT @d VALUES (N'PurchaseReceipts', @@ROWCOUNT);
    DELETE FROM dbo.SeedCuttingBank; INSERT @d VALUES (N'SeedCuttingBank', @@ROWCOUNT);
    DELETE FROM dbo.SeedlingDispatches; INSERT @d VALUES (N'SeedlingDispatches', @@ROWCOUNT);
    DELETE FROM dbo.StoreRequestLines; INSERT @d VALUES (N'StoreRequestLines', @@ROWCOUNT);
    DELETE FROM dbo.SuggestionComments; INSERT @d VALUES (N'SuggestionComments', @@ROWCOUNT);
    DELETE FROM dbo.SuggestionPostEvents; INSERT @d VALUES (N'SuggestionPostEvents', @@ROWCOUNT);
    DELETE FROM dbo.SuggestionPostPhotos; INSERT @d VALUES (N'SuggestionPostPhotos', @@ROWCOUNT);
    DELETE FROM dbo.TrayStock; INSERT @d VALUES (N'TrayStock', @@ROWCOUNT);
    DELETE FROM dbo.VendorPurchases; INSERT @d VALUES (N'VendorPurchases', @@ROWCOUNT);
    DELETE FROM dbo.Bookings; INSERT @d VALUES (N'Bookings', @@ROWCOUNT);
    DELETE FROM dbo.CuttingDeliveries; INSERT @d VALUES (N'CuttingDeliveries', @@ROWCOUNT);
    DELETE FROM dbo.CuttingProductions; INSERT @d VALUES (N'CuttingProductions', @@ROWCOUNT);
    DELETE FROM dbo.PottedPlantStock; INSERT @d VALUES (N'PottedPlantStock', @@ROWCOUNT);
    DELETE FROM dbo.PurchaseOrders; INSERT @d VALUES (N'PurchaseOrders', @@ROWCOUNT);
    DELETE FROM dbo.ReadyStock; INSERT @d VALUES (N'ReadyStock', @@ROWCOUNT);
    DELETE FROM dbo.SeedEntries; INSERT @d VALUES (N'SeedEntries', @@ROWCOUNT);
    DELETE FROM dbo.StoreRequests; INSERT @d VALUES (N'StoreRequests', @@ROWCOUNT);
    DELETE FROM dbo.ActualCuttings; INSERT @d VALUES (N'ActualCuttings', @@ROWCOUNT);
    DELETE FROM dbo.EmptyPotInventory; INSERT @d VALUES (N'EmptyPotInventory', @@ROWCOUNT);
    DELETE FROM dbo.SeedSowings; INSERT @d VALUES (N'SeedSowings', @@ROWCOUNT);
    DELETE FROM dbo.BedSowings; INSERT @d VALUES (N'BedSowings', @@ROWCOUNT);
    DELETE FROM dbo.CuttingPlans; INSERT @d VALUES (N'CuttingPlans', @@ROWCOUNT);
    DELETE FROM dbo.CuttingStock; INSERT @d VALUES (N'CuttingStock', @@ROWCOUNT);
    DELETE FROM dbo.SeedStock; INSERT @d VALUES (N'SeedStock', @@ROWCOUNT);

    IF EXISTS (SELECT 1 FROM sys.triggers WHERE is_disabled = 1) THROW 53007, 'A trigger is still disabled -- rolling back.', 1;
    DECLARE @left NVARCHAR(MAX) = N'', @t SYSNAME, @n BIGINT;
    DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT n FROM @clear;
    OPEN c; FETCH NEXT FROM c INTO @t;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        DECLARE @q NVARCHAR(400) = N'SELECT @n = COUNT_BIG(*) FROM dbo.' + QUOTENAME(@t);
        EXEC sp_executesql @q, N'@n BIGINT OUTPUT', @n = @n OUTPUT;
        IF @n <> 0 SET @left += @t + N'=' + CAST(@n AS NVARCHAR(20)) + N' ';
        FETCH NEXT FROM c INTO @t;
    END
    CLOSE c; DEALLOCATE c;
    IF @left <> N'' THROW 53008, 'Rows remain after the reset -- rolling back.', 1;
    SELECT TableName + '|' + CAST(RowsDeleted AS VARCHAR(20)) AS Deleted FROM @d ORDER BY TableName;
    SELECT 'TOTAL|' + CAST(SUM(RowsDeleted) AS VARCHAR(20)) FROM @d;

    -- Row counts AFTER the deletes (inside the transaction, before COMMIT / ROLLBACK).
    DECLARE cafter CURSOR LOCAL FAST_FORWARD FOR SELECT n FROM @clear;
    OPEN cafter; FETCH NEXT FROM cafter INTO @ct;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @cq = N'SELECT @cn = COUNT_BIG(*) FROM dbo.' + QUOTENAME(@ct);
        EXEC sp_executesql @cq, N'@cn BIGINT OUTPUT', @cn = @cn OUTPUT;
        INSERT @after VALUES (@ct, @cn);
        FETCH NEXT FROM cafter INTO @ct;
    END
    CLOSE cafter; DEALLOCATE cafter;
    PRINT '=== Row counts: Table | Before | Deleted by its own statement | After ===';
    PRINT '    (ON DELETE CASCADE children show 0 deleted by their own statement -- their parent removed them;';
    PRINT '     DailyLabourAttendanceHistory also counts the rows its audit trigger adds when DailyLabourAttendance is cleared)';
    DECLARE cr CURSOR LOCAL FAST_FORWARD FOR
        SELECT b.n + ' | ' + CAST(b.c AS VARCHAR(20)) + ' | ' + CAST(ISNULL(d.RowsDeleted, 0) AS VARCHAR(20)) + ' | ' + CAST(a.c AS VARCHAR(20))
        FROM @before b JOIN @after a ON a.n = b.n LEFT JOIN @d d ON d.TableName = b.n ORDER BY b.n;
    OPEN cr; FETCH NEXT FROM cr INTO @line;
    WHILE @@FETCH_STATUS = 0 BEGIN PRINT '    ' + @line; FETCH NEXT FROM cr INTO @line; END
    CLOSE cr; DEALLOCATE cr;
    SELECT @line = 'TOTALS | before ' + CAST(SUM(b.c) AS VARCHAR(20)) + ' | after ' + CAST(SUM(a.c) AS VARCHAR(20))
    FROM @before b JOIN @after a ON a.n = b.n;
    PRINT '    ' + @line;
END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back: ' + ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION TxDataReset;
    THROW;
END CATCH;
IF N'$(MODE)' = N'COMMIT' BEGIN COMMIT TRANSACTION TxDataReset; PRINT '=== COMMITTED ==='; END
ELSE BEGIN ROLLBACK TRANSACTION TxDataReset; PRINT '=== DRY RUN -- rolled back, nothing changed ==='; END

-- Rows now in the 76 tables (COMMIT: 0; DRYRUN: the BEFORE total -- proof nothing changed).
DECLARE @after2 TABLE (n SYSNAME PRIMARY KEY, c BIGINT);
    DECLARE cafter2 CURSOR LOCAL FAST_FORWARD FOR SELECT n FROM @clear;
    OPEN cafter2; FETCH NEXT FROM cafter2 INTO @ct;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @cq = N'SELECT @cn = COUNT_BIG(*) FROM dbo.' + QUOTENAME(@ct);
        EXEC sp_executesql @cq, N'@cn BIGINT OUTPUT', @cn = @cn OUTPUT;
        INSERT @after2 VALUES (@ct, @cn);
        FETCH NEXT FROM cafter2 INTO @ct;
    END
    CLOSE cafter2; DEALLOCATE cafter2;
SELECT @line = 'Rows now in the 76 tables: ' + CAST(SUM(c) AS VARCHAR(20)) FROM @after2;
PRINT @line;
GO
