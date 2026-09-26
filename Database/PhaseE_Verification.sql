-- ============================================================================
-- PhaseE_Verification.sql
-- ============================================================================
-- READ-ONLY. Does not run PhaseE_OutletModule.sql. Does not CREATE, ALTER,
-- INSERT, UPDATE or DELETE anything. Safe to run on PlantsIMS2_Test at any
-- time, any number of times.
--
-- Purpose: tell you exactly which Phase E objects already exist in THIS
-- database, whether they match what Database/PhaseE_OutletModule.sql and the
-- C# repositories (OutletPurchaseRepository, OutletSaleRepository,
-- OutletBookingRepository, OutletWastageRepository) both expect, and whether
-- it is safe to run the migration script (only ever safe when objects are
-- either ALL present or ALL absent -- a partial state means something else
-- already touched these names and must be looked at by hand, NOT migrated
-- over blindly).
--
-- Run this in SSMS / sqlcmd against PlantsIMS2_Test and send back the full
-- result set(s) it produces.
-- ============================================================================

SET NOCOUNT ON;

PRINT '============================================================';
PRINT 'Connected to database: ' + DB_NAME() + '  on server: ' + @@SERVERNAME;
PRINT '============================================================';
IF DB_NAME() <> N'PlantsIMS2_Test'
    PRINT '*** WARNING: this is NOT PlantsIMS2_Test. Stop and re-check your connection before doing anything else. ***';
GO

-- ============================================================================
-- A. Phase E tables
-- ============================================================================
SELECT 'A. Table' AS Category, v.ObjectName,
       CASE WHEN t.object_id IS NULL THEN 'MISSING' ELSE 'EXISTS' END AS Status,
       CASE WHEN t.object_id IS NULL THEN NULL ELSE CAST(p.rows AS NVARCHAR(20)) END AS RowCountApprox
FROM (VALUES ('OutletPurchases'), ('OutletSales'), ('OutletSaleItems'),
             ('OutletBookings'), ('OutletBookingItems'), ('OutletWastages')) v(ObjectName)
LEFT JOIN sys.tables t ON t.name = v.ObjectName AND t.schema_id = SCHEMA_ID('dbo')
LEFT JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
ORDER BY v.ObjectName;
GO

-- ============================================================================
-- B. Phase E triggers (and which table each is actually on, and whether it
--    is enabled -- a disabled trigger gives a false sense of safety)
-- ============================================================================
SELECT 'B. Trigger' AS Category, v.ObjectName,
       CASE WHEN tr.object_id IS NULL THEN 'MISSING' ELSE 'EXISTS' END AS Status,
       CASE WHEN tr.object_id IS NULL THEN NULL ELSE OBJECT_NAME(tr.parent_id) END AS OnTable,
       CASE WHEN tr.object_id IS NULL THEN NULL WHEN tr.is_disabled = 1 THEN 'DISABLED -- INVESTIGATE' ELSE 'Enabled' END AS EnabledStatus
FROM (VALUES ('TR_OutletPurchases_Rules'), ('TR_OutletSales_Immutable'), ('TR_OutletSaleItems_Rules'),
             ('TR_OutletBookings_Update'), ('TR_OutletBookingItems_Rules'), ('TR_OutletWastages_Rules')) v(ObjectName)
LEFT JOIN sys.triggers tr ON tr.name = v.ObjectName
ORDER BY v.ObjectName;
GO

-- ============================================================================
-- C. Phase E constraints / indexes
-- ============================================================================
SELECT 'C. Constraint/Index' AS Category, 'UQ_ReadyStock_IdSpeciesCavityArea' AS ObjectName,
       CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UQ_ReadyStock_IdSpeciesCavityArea')
            THEN 'EXISTS' ELSE 'MISSING' END AS Status,
       NULL AS Details
UNION ALL
SELECT 'C. Constraint/Index', 'CK_PottedStockTx_Type',
       CASE WHEN EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_PottedStockTx_Type') THEN 'EXISTS' ELSE 'MISSING' END,
       CASE WHEN EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_PottedStockTx_Type' AND [definition] LIKE '%Purchase%')
            THEN 'Includes ''Purchase'' -- Phase E applied'
            WHEN EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_PottedStockTx_Type')
            THEN '*** Does NOT include ''Purchase'' -- Phase E NOT applied to this constraint ***'
            ELSE NULL END
UNION ALL
SELECT 'C. Constraint/Index', 'CK_ReadyStockTx_Type',
       CASE WHEN EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_ReadyStockTx_Type') THEN 'EXISTS' ELSE 'MISSING' END,
       CASE WHEN EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_ReadyStockTx_Type' AND [definition] LIKE '%Wastage%')
            THEN 'Includes ''Wastage'' -- Phase E applied'
            WHEN EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_ReadyStockTx_Type')
            THEN '*** Does NOT include ''Wastage'' -- Phase E NOT applied to this constraint ***'
            ELSE NULL END;
GO

-- Full current definitions, for your own eyes (does not modify anything):
SELECT name, [definition] FROM sys.check_constraints WHERE name IN ('CK_PottedStockTx_Type', 'CK_ReadyStockTx_Type');
GO

-- ============================================================================
-- D. Permission + role grant
-- ============================================================================
SELECT 'D. Permission' AS Category, 'Outlet.Purchase' AS ObjectName,
       CASE WHEN EXISTS (SELECT 1 FROM dbo.Permissions WHERE Code = N'Outlet.Purchase') THEN 'EXISTS' ELSE 'MISSING' END AS Status,
       NULL AS Details
UNION ALL
SELECT 'D. Permission', 'Outlet.Purchase granted to ''Outlet Sales''',
       CASE WHEN EXISTS (
                SELECT 1 FROM dbo.RolePermissions rp
                INNER JOIN dbo.Roles r ON r.Id = rp.RoleId
                INNER JOIN dbo.Permissions p ON p.Id = rp.PermissionId
                WHERE COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = N'Outlet Sales' AND p.Code = N'Outlet.Purchase')
            THEN 'EXISTS' ELSE 'MISSING' END,
       NULL;
GO

-- What the "Outlet Sales" role currently holds (sanity check -- should
-- already include Dashboard.View, Outlet.View, Outlet.Sell, Outlet.Confirm
-- from Phase D, plus Outlet.Purchase once Phase E is applied):
SELECT r.Name AS RoleName, p.Code AS PermissionCode
FROM dbo.Roles r
INNER JOIN dbo.RolePermissions rp ON rp.RoleId = r.Id
INNER JOIN dbo.Permissions p ON p.Id = rp.PermissionId
WHERE COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = N'Outlet Sales'
ORDER BY p.Code;
GO

-- ============================================================================
-- E. Existing foundation this all depends on
-- ============================================================================
SELECT 'E. Foundation table' AS Category, v.ObjectName,
       CASE WHEN t.object_id IS NULL THEN '*** MISSING -- STOP, something is badly wrong ***' ELSE 'EXISTS' END AS Status
FROM (VALUES ('PottedPlantStock'), ('ReadyStock'), ('PottedPlantStockTransactions'), ('ReadyStockTransactions'),
             ('InternalTransfers'), ('PotSizes'), ('PlantSpecies'), ('Area'), ('Roles'), ('Permissions'),
             ('RolePermissions'), ('IMSUsers')) v(ObjectName)
LEFT JOIN sys.tables t ON t.name = v.ObjectName AND t.schema_id = SCHEMA_ID('dbo')
ORDER BY v.ObjectName;
GO

-- The two foundation constraints PhaseE_OutletModule.sql's own composite FKs
-- depend on -- if either is missing, the Phase E script CANNOT run at all:
SELECT 'E. Foundation constraint' AS Category, v.ObjectName,
       CASE WHEN EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = v.ObjectName)
            THEN 'EXISTS' ELSE '*** MISSING -- Phase E script will fail on its composite FKs ***' END AS Status
FROM (VALUES ('UQ_PottedPlantStock_IdSpeciesPotSizeArea')) v(ObjectName);
GO

-- ============================================================================
-- F. Application/code compatibility -- every column each Outlet repository
--    actually reads or writes, checked against what really exists. Any
--    "MISSING COLUMN" row here means the C# code and this database have
--    drifted apart and WILL throw at runtime.
-- ============================================================================
DECLARE @Cols TABLE (RepositorySource NVARCHAR(60), TableName NVARCHAR(60), ColumnName NVARCHAR(60));
INSERT INTO @Cols (RepositorySource, TableName, ColumnName) VALUES
-- OutletPurchaseRepository
('OutletPurchaseRepository', 'OutletPurchases', 'Id'), ('OutletPurchaseRepository', 'OutletPurchases', 'PurchaseCode'),
('OutletPurchaseRepository', 'OutletPurchases', 'PurchaseDate'), ('OutletPurchaseRepository', 'OutletPurchases', 'SupplierName'),
('OutletPurchaseRepository', 'OutletPurchases', 'OutletAreaId'), ('OutletPurchaseRepository', 'OutletPurchases', 'SpeciesId'),
('OutletPurchaseRepository', 'OutletPurchases', 'PotSize'), ('OutletPurchaseRepository', 'OutletPurchases', 'Quantity'),
('OutletPurchaseRepository', 'OutletPurchases', 'PottedPlantStockId'), ('OutletPurchaseRepository', 'OutletPurchases', 'Remarks'),
('OutletPurchaseRepository', 'OutletPurchases', 'CreatedById'), ('OutletPurchaseRepository', 'OutletPurchases', 'CreatedBy'),
('OutletPurchaseRepository', 'OutletPurchases', 'CreatedDate'),
-- OutletSaleRepository
('OutletSaleRepository', 'OutletSales', 'Id'), ('OutletSaleRepository', 'OutletSales', 'SaleCode'),
('OutletSaleRepository', 'OutletSales', 'OutletAreaId'), ('OutletSaleRepository', 'OutletSales', 'CustomerName'),
('OutletSaleRepository', 'OutletSales', 'CustomerContact'), ('OutletSaleRepository', 'OutletSales', 'SaleDate'),
('OutletSaleRepository', 'OutletSales', 'Remarks'), ('OutletSaleRepository', 'OutletSales', 'CreatedById'),
('OutletSaleRepository', 'OutletSales', 'CreatedBy'), ('OutletSaleRepository', 'OutletSales', 'CreatedDate'),
('OutletSaleRepository', 'OutletSaleItems', 'Id'), ('OutletSaleRepository', 'OutletSaleItems', 'SaleId'),
('OutletSaleRepository', 'OutletSaleItems', 'OutletAreaId'), ('OutletSaleRepository', 'OutletSaleItems', 'StockType'),
('OutletSaleRepository', 'OutletSaleItems', 'PottedPlantStockId'), ('OutletSaleRepository', 'OutletSaleItems', 'ReadyStockId'),
('OutletSaleRepository', 'OutletSaleItems', 'SpeciesId'), ('OutletSaleRepository', 'OutletSaleItems', 'PotSize'),
('OutletSaleRepository', 'OutletSaleItems', 'CavityType'), ('OutletSaleRepository', 'OutletSaleItems', 'Quantity'),
-- OutletBookingRepository
('OutletBookingRepository', 'OutletBookings', 'Id'), ('OutletBookingRepository', 'OutletBookings', 'BookingCode'),
('OutletBookingRepository', 'OutletBookings', 'OutletAreaId'), ('OutletBookingRepository', 'OutletBookings', 'CustomerName'),
('OutletBookingRepository', 'OutletBookings', 'CustomerContact'), ('OutletBookingRepository', 'OutletBookings', 'BookingDate'),
('OutletBookingRepository', 'OutletBookings', 'RequiredDate'), ('OutletBookingRepository', 'OutletBookings', 'Status'),
('OutletBookingRepository', 'OutletBookings', 'Remarks'), ('OutletBookingRepository', 'OutletBookings', 'CreatedById'),
('OutletBookingRepository', 'OutletBookings', 'CreatedBy'), ('OutletBookingRepository', 'OutletBookings', 'CreatedDate'),
('OutletBookingRepository', 'OutletBookings', 'CancelledById'), ('OutletBookingRepository', 'OutletBookings', 'CancelledDate'),
('OutletBookingRepository', 'OutletBookings', 'CancellationReason'), ('OutletBookingRepository', 'OutletBookings', 'ModifiedBy'),
('OutletBookingRepository', 'OutletBookings', 'ModifiedDate'),
('OutletBookingRepository', 'OutletBookingItems', 'Id'), ('OutletBookingRepository', 'OutletBookingItems', 'BookingId'),
('OutletBookingRepository', 'OutletBookingItems', 'OutletAreaId'), ('OutletBookingRepository', 'OutletBookingItems', 'StockType'),
('OutletBookingRepository', 'OutletBookingItems', 'PottedPlantStockId'), ('OutletBookingRepository', 'OutletBookingItems', 'ReadyStockId'),
('OutletBookingRepository', 'OutletBookingItems', 'SpeciesId'), ('OutletBookingRepository', 'OutletBookingItems', 'PotSize'),
('OutletBookingRepository', 'OutletBookingItems', 'CavityType'), ('OutletBookingRepository', 'OutletBookingItems', 'Quantity'),
('OutletBookingRepository', 'OutletBookingItems', 'CollectedQuantity'),
-- OutletWastageRepository
('OutletWastageRepository', 'OutletWastages', 'Id'), ('OutletWastageRepository', 'OutletWastages', 'WastageCode'),
('OutletWastageRepository', 'OutletWastages', 'WastageDate'), ('OutletWastageRepository', 'OutletWastages', 'OutletAreaId'),
('OutletWastageRepository', 'OutletWastages', 'StockType'), ('OutletWastageRepository', 'OutletWastages', 'PottedPlantStockId'),
('OutletWastageRepository', 'OutletWastages', 'ReadyStockId'), ('OutletWastageRepository', 'OutletWastages', 'SpeciesId'),
('OutletWastageRepository', 'OutletWastages', 'PotSize'), ('OutletWastageRepository', 'OutletWastages', 'CavityType'),
('OutletWastageRepository', 'OutletWastages', 'Quantity'), ('OutletWastageRepository', 'OutletWastages', 'Reason'),
('OutletWastageRepository', 'OutletWastages', 'Remarks'), ('OutletWastageRepository', 'OutletWastages', 'CreatedById'),
('OutletWastageRepository', 'OutletWastages', 'CreatedBy'), ('OutletWastageRepository', 'OutletWastages', 'CreatedDate'),
-- Foundation columns every repository also reads/writes on the pre-existing
-- stock tables (PottedPlantStock/ReadyStock/their ledgers)
('Foundation (all repos)', 'PottedPlantStock', 'Id'), ('Foundation (all repos)', 'PottedPlantStock', 'SpeciesId'),
('Foundation (all repos)', 'PottedPlantStock', 'PotSize'), ('Foundation (all repos)', 'PottedPlantStock', 'AreaId'),
('Foundation (all repos)', 'PottedPlantStock', 'PhysicalQuantity'), ('Foundation (all repos)', 'PottedPlantStock', 'ReservedQuantity'),
('Foundation (all repos)', 'PottedPlantStockTransactions', 'PottedPlantStockId'), ('Foundation (all repos)', 'PottedPlantStockTransactions', 'TransactionType'),
('Foundation (all repos)', 'PottedPlantStockTransactions', 'ReferenceType'), ('Foundation (all repos)', 'PottedPlantStockTransactions', 'ReferenceId'),
('Foundation (all repos)', 'PottedPlantStockTransactions', 'Quantity'), ('Foundation (all repos)', 'PottedPlantStockTransactions', 'BeforeQuantity'),
('Foundation (all repos)', 'ReadyStock', 'Id'), ('Foundation (all repos)', 'ReadyStock', 'SpeciesId'),
('Foundation (all repos)', 'ReadyStock', 'CavityType'), ('Foundation (all repos)', 'ReadyStock', 'AreaId'),
('Foundation (all repos)', 'ReadyStock', 'Quantity'), ('Foundation (all repos)', 'ReadyStock', 'ReservedQuantity'),
('Foundation (all repos)', 'ReadyStock', 'DispatchedQuantity'),
('Foundation (all repos)', 'ReadyStockTransactions', 'ReadyStockId'), ('Foundation (all repos)', 'ReadyStockTransactions', 'TransactionType'),
('Foundation (all repos)', 'Area', 'Id'), ('Foundation (all repos)', 'Area', 'Name'),
('Foundation (all repos)', 'Area', 'IsActive'), ('Foundation (all repos)', 'Area', 'AreaType'),
('Foundation (all repos)', 'PotSizes', 'Name'), ('Foundation (all repos)', 'PlantSpecies', 'Id'), ('Foundation (all repos)', 'PlantSpecies', 'Name');

SELECT 'F. Code compatibility' AS Category,
       c.RepositorySource + ' -> ' + c.TableName + '.' + c.ColumnName AS ObjectName,
       CASE WHEN COL_LENGTH('dbo.' + c.TableName, c.ColumnName) IS NULL
            THEN '*** MISSING COLUMN ***'
            ELSE 'OK' END AS Status
FROM @Cols c
WHERE OBJECT_ID('dbo.' + c.TableName) IS NOT NULL   -- skip columns on tables that don't exist yet (already flagged in A/E above)
ORDER BY CASE WHEN COL_LENGTH('dbo.' + c.TableName, c.ColumnName) IS NULL THEN 0 ELSE 1 END, c.RepositorySource, c.TableName, c.ColumnName;
GO

-- ============================================================================
-- SUMMARY: is it safe to run PhaseE_OutletModule.sql?
-- ============================================================================
DECLARE @TablesPresent INT = (
    SELECT COUNT(*) FROM sys.tables
    WHERE schema_id = SCHEMA_ID('dbo')
      AND name IN ('OutletPurchases','OutletSales','OutletSaleItems','OutletBookings','OutletBookingItems','OutletWastages'));
DECLARE @TriggersPresent INT = (
    SELECT COUNT(*) FROM sys.triggers
    WHERE name IN ('TR_OutletPurchases_Rules','TR_OutletSales_Immutable','TR_OutletSaleItems_Rules',
                   'TR_OutletBookings_Update','TR_OutletBookingItems_Rules','TR_OutletWastages_Rules'));
DECLARE @PurchaseTypeAdded BIT = CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_PottedStockTx_Type' AND [definition] LIKE '%Purchase%') THEN 1 ELSE 0 END AS BIT);
DECLARE @WastageTypeAdded BIT = CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_ReadyStockTx_Type' AND [definition] LIKE '%Wastage%') THEN 1 ELSE 0 END AS BIT);
DECLARE @UqPresent BIT = CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UQ_ReadyStock_IdSpeciesCavityArea') THEN 1 ELSE 0 END AS BIT);
DECLARE @PermPresent BIT = CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.Permissions WHERE Code = N'Outlet.Purchase') THEN 1 ELSE 0 END AS BIT);

DECLARE @FullyApplied BIT = CAST(CASE WHEN @TablesPresent = 6 AND @TriggersPresent = 6 AND @PurchaseTypeAdded = 1 AND @WastageTypeAdded = 1 AND @UqPresent = 1 AND @PermPresent = 1 THEN 1 ELSE 0 END AS BIT);
DECLARE @FullyAbsent BIT = CAST(CASE WHEN @TablesPresent = 0 AND @TriggersPresent = 0 AND @PurchaseTypeAdded = 0 AND @WastageTypeAdded = 0 AND @UqPresent = 0 AND @PermPresent = 0 THEN 1 ELSE 0 END AS BIT);

SELECT
    @TablesPresent AS TablesPresent_Of_6,
    @TriggersPresent AS TriggersPresent_Of_6,
    @PurchaseTypeAdded AS PottedPurchaseTypeAdded,
    @WastageTypeAdded AS TrayWastageTypeAdded,
    @UqPresent AS ReadyStockCompositeKeyAdded,
    @PermPresent AS OutletPurchasePermissionAdded,
    CASE
        WHEN @FullyApplied = 1 THEN 'PHASE E ALREADY FULLY APPLIED -- running the script again is a safe no-op (every statement is idempotent), but there is nothing left to gain by running it.'
        WHEN @FullyAbsent = 1 THEN 'PHASE E NOT APPLIED AT ALL -- safe to run PhaseE_OutletModule.sql once you have taken and verified a backup.'
        ELSE '*** PARTIAL STATE -- DO NOT RUN THE MIGRATION SCRIPT YET. Some Phase E objects exist and some do not. Send me this entire result set before running anything -- this needs to be understood by hand first, since it means either the migration was interrupted partway, or something else in this database already uses one of these names. ***'
    END AS SafeToMigrate;
GO