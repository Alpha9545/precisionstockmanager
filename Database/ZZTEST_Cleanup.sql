-- ============================================================================
-- ZZTEST_Cleanup.sql
--
-- Removes ALL confirmed ZZTEST/PERMTEST/CHANGE- test data from PlantsIMS2_Test,
-- and nothing else. Built from a full, manual, database-wide investigation on
-- 2026-09-27 (every varchar/nvarchar column in every table was scanned for
-- ZZTEST / PERMTEST / CHANGE- / zztest. markers, then the FK graph was walked
-- outward from every match, plus every FK column pointing at Area 125/126,
-- MotherPlants 88 and IMSUsers 104-109 was independently re-checked so that
-- "silent" test children with no marker of their own -- e.g. PottedPlantStock
-- 186, created with CreatedBy = NULL but sitting in test Area 126 -- are not
-- missed). See claude_reports/ZZTEST_Cleanup_Report_2026-09-27.md for the full
-- inventory, reasoning per row, and the dependency tree this script encodes.
--
-- All IDs below are HARD-CODED to the rows actually verified as test data.
-- This is intentional: pattern-based deletes (LIKE 'ZZTEST%') risk matching
-- future legitimate data and were the reason the previous draft of this file
-- was replaced. Every ID here was individually inspected before being listed.
--
-- Deletion order is children-before-parents per the real SQL Server FK graph
-- (sys.foreign_keys), not a guess. Running this against any row set that has
-- since changed will simply delete 0 rows for the stale IDs and the
-- verification block will say so -- it will not delete anything unintended.
--
-- SAFETY:
--   * Guarded to PlantsIMS2_Test only.
--   * Runs inside BEGIN TRANSACTION and ends with ROLLBACK TRANSACTION.
--   * Prints a verification report before the rollback.
--   * Change ROLLBACK TRANSACTION -> COMMIT TRANSACTION at the bottom only
--     after reviewing that report, and only with explicit approval.
-- ============================================================================
IF DB_NAME() <> N'PlantsIMS2_Test'
    THROW 50299, 'ZZTEST_Cleanup.sql may only be run against PlantsIMS2_Test.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;  -- any error rolls the whole batch back automatically

BEGIN TRANSACTION ZZTestCleanup;

BEGIN TRY

    ----------------------------------------------------------------------
    -- Confirmed test root IDs (see report for how each was identified)
    ----------------------------------------------------------------------
    -- Area:          125 (ZZTEST-CHANGE-AREA-MP), 126 (ZZTEST-CHANGE-OUTLET)
    -- IMSUsers:      104-109 (zztest.supervisor/employeea/employeeb/multirole/
    --                mainofficekeeper/sowingoperator) -- already IsActive=0
    -- MotherPlants:  88 (ZZTEST-MP-001)
    -- SeedStock:     111 (BatchNo ZZTEST-LOT-001)
    -- SeedSowings:   646, 647 (ZZTEST-SOW-001/002), 652 (e2e cutting-tray
    --                sowing; SourceCuttingStockId=159)
    -- ReadyStock:    195 (child of sowing 647), 202 (child of sowing 652)
    -- CuttingStock:  158 (Area 125), 159 (Area 2 / Main-Office-Areas -- a
    --                REAL area, but this specific row was created by
    --                zztest.mainofficekeeper as a byproduct of the internal-
    --                transfer test and is now at 0 quantity; the row itself
    --                is test data even though it landed in a real area)
    -- CuttingProductions: 168, 169, 170
    -- InternalTransfers:  248 (125 -> Area 2, confirmed/discrepancy by test
    --                users; note InternalTransfers.Id 249 was created and
    --                then hard-deleted by the test workflow itself before
    --                this cleanup ever ran -- PottedPlantStockTransactions
    --                398/401 reference it as a dangling ReferenceId, which
    --                is pre-existing, not something this script creates, and
    --                is resolved as a side effect of deleting 398/401 below)
    -- OutletSales:        1 (100% of the table)
    -- OutletBookings:     1 (100% of the table)
    -- ReadyConfirmations: 355
    -- PotProductionBatches: 209, 210, 211, 212, 213, 214
    -- EmptyPotInventory:  257 (Area 125, 4 inch)
    -- PottedPlantStock:   186 (Area 126, CreatedBy NULL -- silent test child,
    --                identified only via AreaId membership, not a marker),
    --                187 (Area 125, zztest.mainofficekeeper)
    ----------------------------------------------------------------------

    DECLARE @BeforeCounts TABLE (TableName SYSNAME, RowsFound INT);
    INSERT INTO @BeforeCounts
    SELECT 'CuttingStockTransactions', COUNT(*) FROM dbo.CuttingStockTransactions WHERE CuttingStockId IN (158,159)
    UNION ALL SELECT 'PotProductionEntries', COUNT(*) FROM dbo.PotProductionEntries WHERE BatchId IN (209,210,211,212,213,214)
    UNION ALL SELECT 'EmptyPotInventoryTransactions', COUNT(*) FROM dbo.EmptyPotInventoryTransactions WHERE EmptyPotInventoryId = 257
    UNION ALL SELECT 'PottedPlantStockTransactions', COUNT(*) FROM dbo.PottedPlantStockTransactions WHERE PottedPlantStockId IN (186,187)
    UNION ALL SELECT 'ReadyStockTransactions', COUNT(*) FROM dbo.ReadyStockTransactions WHERE ReadyStockId IN (195,202)
    UNION ALL SELECT 'OutletBookingItems', COUNT(*) FROM dbo.OutletBookingItems WHERE Id IN (1,2)
    UNION ALL SELECT 'OutletSaleItems', COUNT(*) FROM dbo.OutletSaleItems WHERE Id IN (1,2)
    UNION ALL SELECT 'ReadyConfirmations', COUNT(*) FROM dbo.ReadyConfirmations WHERE Id = 355
    UNION ALL SELECT 'InternalTransfers', COUNT(*) FROM dbo.InternalTransfers WHERE Id = 248
    UNION ALL SELECT 'OutletBookings', COUNT(*) FROM dbo.OutletBookings WHERE Id = 1
    UNION ALL SELECT 'OutletSales', COUNT(*) FROM dbo.OutletSales WHERE Id = 1
    UNION ALL SELECT 'CuttingProductions', COUNT(*) FROM dbo.CuttingProductions WHERE Id IN (168,169,170)
    UNION ALL SELECT 'PotProductionBatches', COUNT(*) FROM dbo.PotProductionBatches WHERE Id IN (209,210,211,212,213,214)
    UNION ALL SELECT 'ReadyStock', COUNT(*) FROM dbo.ReadyStock WHERE Id IN (195,202)
    UNION ALL SELECT 'PottedPlantStock', COUNT(*) FROM dbo.PottedPlantStock WHERE Id IN (186,187)
    UNION ALL SELECT 'SeedSowings', COUNT(*) FROM dbo.SeedSowings WHERE Id IN (646,647,652)
    UNION ALL SELECT 'CuttingStock', COUNT(*) FROM dbo.CuttingStock WHERE Id IN (158,159)
    UNION ALL SELECT 'SeedStock', COUNT(*) FROM dbo.SeedStock WHERE Id = 111
    UNION ALL SELECT 'EmptyPotInventory', COUNT(*) FROM dbo.EmptyPotInventory WHERE Id = 257
    UNION ALL SELECT 'MotherPlants', COUNT(*) FROM dbo.MotherPlants WHERE Id = 88
    UNION ALL SELECT 'IMSUsers', COUNT(*) FROM dbo.IMSUsers WHERE Id IN (104,105,106,107,108,109)
    UNION ALL SELECT 'Area', COUNT(*) FROM dbo.Area WHERE Id IN (125,126);

    ----------------------------------------------------------------------
    -- LEVEL 1 -- deepest leaf/transaction/detail rows
    ----------------------------------------------------------------------
    DELETE FROM dbo.CuttingStockTransactions WHERE CuttingStockId IN (158,159);

    -- TR_PotEntries_Rules unconditionally blocks ANY delete on
    -- PotProductionEntries ("A daily production entry cannot be changed or
    -- deleted.") -- by design, with no test-data exception. User approved
    -- (2026-09-27) temporarily disabling it for just this transaction so the
    -- 5 test entries can be removed; it is re-enabled immediately after and
    -- again defensively at the end of the script. DISABLE/ENABLE TRIGGER is
    -- itself transactional in SQL Server, so a ROLLBACK restores it too.
    DISABLE TRIGGER dbo.TR_PotEntries_Rules ON dbo.PotProductionEntries;
    DELETE FROM dbo.PotProductionEntries WHERE BatchId IN (209,210,211,212,213,214);
    ENABLE TRIGGER dbo.TR_PotEntries_Rules ON dbo.PotProductionEntries;

    DELETE FROM dbo.EmptyPotInventoryTransactions WHERE EmptyPotInventoryId = 257;
    DELETE FROM dbo.PottedPlantStockTransactions WHERE PottedPlantStockId IN (186,187);
    DELETE FROM dbo.ReadyStockTransactions WHERE ReadyStockId IN (195,202);

    -- Same immutability pattern as TR_PotEntries_Rules above, same approval:
    -- TR_OutletBookingItems_Rules / TR_OutletSaleItems_Rules unconditionally
    -- block DELETE ("A booking item cannot be deleted." / "A recorded Outlet
    -- sale item cannot be changed or deleted."). Discovered only once the
    -- dry run reached these tables -- a full sys.triggers sweep confirmed no
    -- other tables in this script are affected besides these two plus
    -- TR_PotEntries_Rules and TR_OutletSales_Immutable (handled below).
    DISABLE TRIGGER dbo.TR_OutletBookingItems_Rules ON dbo.OutletBookingItems;
    DELETE FROM dbo.OutletBookingItems WHERE Id IN (1,2);
    ENABLE TRIGGER dbo.TR_OutletBookingItems_Rules ON dbo.OutletBookingItems;

    DISABLE TRIGGER dbo.TR_OutletSaleItems_Rules ON dbo.OutletSaleItems;
    DELETE FROM dbo.OutletSaleItems WHERE Id IN (1,2);
    ENABLE TRIGGER dbo.TR_OutletSaleItems_Rules ON dbo.OutletSaleItems;

    DELETE FROM dbo.SeedStockTransactions WHERE SeedStockId = 111;            -- defensive; 0 rows found live
    DELETE FROM dbo.CuttingTransplants WHERE InternalTransferId IN (248,249); -- defensive; 0 rows found live

    ----------------------------------------------------------------------
    -- LEVEL 2 -- entities that reference Level-3 stock but have no children
    -- of their own left after Level 1
    ----------------------------------------------------------------------
    DELETE FROM dbo.ReadyConfirmations WHERE Id = 355;
    DELETE FROM dbo.InternalTransfers WHERE Id = 248;
    DELETE FROM dbo.OutletBookings WHERE Id = 1; -- TR_OutletBookings_Update only fires on UPDATE, not DELETE

    -- TR_OutletSales_Immutable unconditionally blocks UPDATE and DELETE.
    DISABLE TRIGGER dbo.TR_OutletSales_Immutable ON dbo.OutletSales;
    DELETE FROM dbo.OutletSales WHERE Id = 1;
    ENABLE TRIGGER dbo.TR_OutletSales_Immutable ON dbo.OutletSales;

    DELETE FROM dbo.CuttingProductions WHERE Id IN (168,169,170); -- TR_CuttingProductions_Rules only fires on INSERT/UPDATE
    DELETE FROM dbo.PotProductionBatches WHERE Id IN (209,210,211,212,213,214);

    ----------------------------------------------------------------------
    -- LEVEL 3 -- stock/record entities now free of children
    ----------------------------------------------------------------------
    DELETE FROM dbo.ReadyStock WHERE Id IN (195,202);
    DELETE FROM dbo.PottedPlantStock WHERE Id IN (186,187);
    DELETE FROM dbo.SeedSowings WHERE Id IN (646,647,652);

    ----------------------------------------------------------------------
    -- LEVEL 4 -- source stock entities now free of children
    ----------------------------------------------------------------------
    DELETE FROM dbo.CuttingStock WHERE Id IN (158,159);
    DELETE FROM dbo.SeedStock WHERE Id = 111;
    DELETE FROM dbo.EmptyPotInventory WHERE Id = 257;

    ----------------------------------------------------------------------
    -- LEVEL 5 -- top-level entities now free of children
    ----------------------------------------------------------------------
    DELETE FROM dbo.MotherPlants WHERE Id = 88;
    DELETE FROM dbo.UserRoles WHERE UserId IN (104,105,106,107,108,109); -- defensive; 0 rows found live
    DELETE FROM dbo.IMSUsers WHERE Id IN (104,105,106,107,108,109);

    ----------------------------------------------------------------------
    -- LEVEL 6 -- root
    ----------------------------------------------------------------------
    DELETE FROM dbo.Polyhouses WHERE AreaId IN (125,126); -- defensive; 0 rows found live
    DELETE FROM dbo.Area WHERE Id IN (125,126);

    ----------------------------------------------------------------------
    -- VERIFICATION -- run before the ROLLBACK so you can see exactly what
    -- WOULD have happened
    ----------------------------------------------------------------------
    PRINT '=== Rows deleted per table (before -> after, within this transaction) ===';
    SELECT
        b.TableName,
        b.RowsFound AS RowsFoundBefore,
        CASE b.TableName
            WHEN 'CuttingStockTransactions'      THEN (SELECT COUNT(*) FROM dbo.CuttingStockTransactions WHERE CuttingStockId IN (158,159))
            WHEN 'PotProductionEntries'          THEN (SELECT COUNT(*) FROM dbo.PotProductionEntries WHERE BatchId IN (209,210,211,212,213,214))
            WHEN 'EmptyPotInventoryTransactions' THEN (SELECT COUNT(*) FROM dbo.EmptyPotInventoryTransactions WHERE EmptyPotInventoryId = 257)
            WHEN 'PottedPlantStockTransactions'  THEN (SELECT COUNT(*) FROM dbo.PottedPlantStockTransactions WHERE PottedPlantStockId IN (186,187))
            WHEN 'ReadyStockTransactions'        THEN (SELECT COUNT(*) FROM dbo.ReadyStockTransactions WHERE ReadyStockId IN (195,202))
            WHEN 'OutletBookingItems'            THEN (SELECT COUNT(*) FROM dbo.OutletBookingItems WHERE Id IN (1,2))
            WHEN 'OutletSaleItems'               THEN (SELECT COUNT(*) FROM dbo.OutletSaleItems WHERE Id IN (1,2))
            WHEN 'ReadyConfirmations'            THEN (SELECT COUNT(*) FROM dbo.ReadyConfirmations WHERE Id = 355)
            WHEN 'InternalTransfers'             THEN (SELECT COUNT(*) FROM dbo.InternalTransfers WHERE Id = 248)
            WHEN 'OutletBookings'                THEN (SELECT COUNT(*) FROM dbo.OutletBookings WHERE Id = 1)
            WHEN 'OutletSales'                   THEN (SELECT COUNT(*) FROM dbo.OutletSales WHERE Id = 1)
            WHEN 'CuttingProductions'            THEN (SELECT COUNT(*) FROM dbo.CuttingProductions WHERE Id IN (168,169,170))
            WHEN 'PotProductionBatches'          THEN (SELECT COUNT(*) FROM dbo.PotProductionBatches WHERE Id IN (209,210,211,212,213,214))
            WHEN 'ReadyStock'                    THEN (SELECT COUNT(*) FROM dbo.ReadyStock WHERE Id IN (195,202))
            WHEN 'PottedPlantStock'              THEN (SELECT COUNT(*) FROM dbo.PottedPlantStock WHERE Id IN (186,187))
            WHEN 'SeedSowings'                   THEN (SELECT COUNT(*) FROM dbo.SeedSowings WHERE Id IN (646,647,652))
            WHEN 'CuttingStock'                  THEN (SELECT COUNT(*) FROM dbo.CuttingStock WHERE Id IN (158,159))
            WHEN 'SeedStock'                     THEN (SELECT COUNT(*) FROM dbo.SeedStock WHERE Id = 111)
            WHEN 'EmptyPotInventory'             THEN (SELECT COUNT(*) FROM dbo.EmptyPotInventory WHERE Id = 257)
            WHEN 'MotherPlants'                  THEN (SELECT COUNT(*) FROM dbo.MotherPlants WHERE Id = 88)
            WHEN 'IMSUsers'                      THEN (SELECT COUNT(*) FROM dbo.IMSUsers WHERE Id IN (104,105,106,107,108,109))
            WHEN 'Area'                          THEN (SELECT COUNT(*) FROM dbo.Area WHERE Id IN (125,126))
        END AS RowsRemainingAfterDelete
    FROM @BeforeCounts b
    ORDER BY b.TableName;

    PRINT '=== Database-wide re-scan for ZZTEST / PERMTEST / CHANGE- / zztest. markers (should return NOTHING) ===';
    DECLARE @sql NVARCHAR(MAX) = N'DECLARE @c INT;' + CHAR(10);
    SELECT @sql = @sql +
        N'SET @c = (SELECT COUNT(*) FROM ' + QUOTENAME(t.name) +
        N' WHERE ' + QUOTENAME(c.name) + N' LIKE ''%ZZTEST%'' OR ' + QUOTENAME(c.name) + N' LIKE ''%PERMTEST%'' OR ' +
        QUOTENAME(c.name) + N' LIKE ''%CHANGE-%'' OR ' + QUOTENAME(c.name) + N' LIKE ''zztest.%'');' +
        N' IF @c > 0 PRINT ''REMAINING: ' + t.name + '.' + c.name + ''' + '': '' + CAST(@c AS VARCHAR(20));' + CHAR(10)
    FROM sys.tables t
    JOIN sys.columns c ON c.object_id = t.object_id
    JOIN sys.types ty ON ty.user_type_id = c.user_type_id
    WHERE ty.name IN ('varchar','nvarchar','char','nchar','text','ntext');
    EXEC sp_executesql @sql;
    PRINT '(no REMAINING: lines above means the re-scan is clean)';

    PRINT '=== Real-data sanity spot-check (must be unchanged) ===';
    SELECT 'ReadyStock#3' AS Row_, BatchNo, AreaId FROM dbo.ReadyStock WHERE Id = 3
    UNION ALL SELECT 'ReadyStock#30', BatchNo, AreaId FROM dbo.ReadyStock WHERE Id = 30
    UNION ALL SELECT 'SeedStock#2', BatchNo, AreaId FROM dbo.SeedStock WHERE Id = 2;
    SELECT COUNT(*) AS RemainingArea_ShouldBe6 FROM dbo.Area WHERE Id NOT IN (125,126);
    SELECT COUNT(*) AS RemainingIMSUsers_ShouldBe20 FROM dbo.IMSUsers;

END TRY
BEGIN CATCH
    PRINT '=== ERROR -- rolling back ===';
    PRINT ERROR_MESSAGE();
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION ZZTestCleanup;
    THROW;
END CATCH;

----------------------------------------------------------------------------
-- Approved 2026-09-27: user reviewed the dry-run verification output and
-- explicitly approved committing this exact script. Fresh COPY_ONLY backup
-- taken and verified immediately before this run:
--   PlantsIMS2_Test_PreZZTESTCleanup_20260927_204811.bak
----------------------------------------------------------------------------
IF XACT_STATE() = 1 COMMIT TRANSACTION ZZTestCleanup;
ELSE IF XACT_STATE() <> 0 ROLLBACK TRANSACTION ZZTestCleanup;
GO

-- Final unconditional safety net, outside any transaction: if for any reason
-- one of the four immutability triggers this script disables was left
-- disabled (should never happen -- ROLLBACK undoes the DISABLE TRIGGER DDL,
-- and the TRY block re-enables each one right after its delete), re-enable
-- it now rather than leave production unprotected.
IF EXISTS (SELECT 1 FROM sys.triggers WHERE name = 'TR_PotEntries_Rules' AND is_disabled = 1)
BEGIN
    ENABLE TRIGGER dbo.TR_PotEntries_Rules ON dbo.PotProductionEntries;
    PRINT 'Safety net: TR_PotEntries_Rules was found disabled after the script ended and has been re-enabled.';
END
IF EXISTS (SELECT 1 FROM sys.triggers WHERE name = 'TR_OutletBookingItems_Rules' AND is_disabled = 1)
BEGIN
    ENABLE TRIGGER dbo.TR_OutletBookingItems_Rules ON dbo.OutletBookingItems;
    PRINT 'Safety net: TR_OutletBookingItems_Rules was found disabled after the script ended and has been re-enabled.';
END
IF EXISTS (SELECT 1 FROM sys.triggers WHERE name = 'TR_OutletSaleItems_Rules' AND is_disabled = 1)
BEGIN
    ENABLE TRIGGER dbo.TR_OutletSaleItems_Rules ON dbo.OutletSaleItems;
    PRINT 'Safety net: TR_OutletSaleItems_Rules was found disabled after the script ended and has been re-enabled.';
END
IF EXISTS (SELECT 1 FROM sys.triggers WHERE name = 'TR_OutletSales_Immutable' AND is_disabled = 1)
BEGIN
    ENABLE TRIGGER dbo.TR_OutletSales_Immutable ON dbo.OutletSales;
    PRINT 'Safety net: TR_OutletSales_Immutable was found disabled after the script ended and has been re-enabled.';
END
GO
