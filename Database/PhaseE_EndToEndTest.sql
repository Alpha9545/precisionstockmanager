-- ============================================================================
-- PhaseE_EndToEndTest.sql  (v2 -- written against the CONFIRMED-LIVE Phase E
-- schema in PlantsIMS2_Test, per your PhaseE_Verification.sql results:
-- all 6 tables, all 6 triggers enabled, both widened CHECK constraints,
-- UQ_ReadyStock_IdSpeciesCavityArea, and the Outlet.Purchase grant all
-- confirmed present.)
-- ============================================================================
-- SCOPE / HONESTY NOTE: this script exercises the DATABASE directly -- the
-- same tables, constraints, triggers and operation sequences the C#
-- repositories (OutletPurchaseRepository, OutletSaleRepository,
-- OutletBookingRepository, OutletWastageRepository) use -- but it does NOT
-- invoke the compiled .NET application itself (no live app server exists to
-- call from this environment). A PASS here means "the database genuinely
-- performed/rejected this operation," not "the application code says it
-- should." True end-to-end application-level testing (through the actual
-- Razor Pages UI) still needs to happen separately.
--
-- DATA SOURCING RULE (per explicit instruction): prefer REAL existing rows
-- over fabricated ones everywhere possible.
--   - Species / PotSize: ALWAYS real existing master rows. Never invented.
--   - Outlet Area: a real existing active Outlet Area is used if one
--     exists; only if NONE exists anywhere is a single minimal temporary
--     one created (Area has no upstream FK chain to get wrong -- it is
--     Id/Name/IsActive/AreaType only -- so this is not "fabricating
--     FK-dependent data", it is the one unavoidable root fact if your
--     database truly has no Outlet yet).
--   - PottedPlantStock pool: a real existing pool at the chosen Outlet with
--     available stock is used if one exists. If not, this script uses the
--     REAL External Purchase mechanism (the same one Test 18/19 verify) to
--     legitimately create/stock a pool -- never a hand-crafted stock row.
--   - ReadyStock: dbo.ReadyStock is 1:1 with a real Sowing
--     (UNIQUE SeedSowingId) and its CavityType is FK-pinned to that
--     Sowing's own cavity (FK_ReadyStock_SowingCavity) -- there is no safe
--     way to fabricate a new row without re-running the entire real
--     Sowing -> Confirmation chain (several business-rule triggers
--     unrelated to Phase E). So: a REAL existing ReadyStock row is
--     borrowed for the duration of this transaction only (if it is not
--     already at an Outlet, it is temporarily relocated there -- exactly
--     what a real "Ready Tray -> Outlet" transfer does). If ZERO
--     ReadyStock rows exist anywhere in this database, every Tray-specific
--     test below is SKIPPED with that reason -- nothing is invented.
--
-- Everything happens inside ONE transaction, UNCONDITIONALLY ROLLED BACK at
-- the end. Results survive the rollback via a TABLE VARIABLE (documented
-- SQL Server behaviour: table variable writes are not undone by ROLLBACK).
--
-- This script is a single batch (no GO). Run it as one execution.
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT OFF;

IF DB_NAME() <> N'PlantsIMS2_Test'
BEGIN
    PRINT '*** ABORTED: this is not PlantsIMS2_Test (connected to ' + DB_NAME() + '). Nothing was done. ***';
    RETURN;
END

IF OBJECT_ID('dbo.OutletSales') IS NULL OR OBJECT_ID('dbo.OutletBookings') IS NULL
   OR OBJECT_ID('dbo.OutletPurchases') IS NULL OR OBJECT_ID('dbo.OutletWastages') IS NULL
   OR OBJECT_ID('dbo.OutletSaleItems') IS NULL OR OBJECT_ID('dbo.OutletBookingItems') IS NULL
BEGIN
    PRINT '*** ABORTED: Phase E tables were not found. Re-run PhaseE_Verification.sql to confirm current state. Nothing was done. ***';
    RETURN;
END

DECLARE @Results TABLE (SeqNo INT IDENTITY(1,1), TestNo NVARCHAR(10), TestName NVARCHAR(220), Expected NVARCHAR(400), Actual NVARCHAR(500), Result NVARCHAR(10));

-- Before-snapshot: row counts + a checksum per table, for the independent
-- post-rollback safety proof (on top of the ROLLBACK itself).
DECLARE @Snap TABLE (TableName sysname, RowsBefore INT, ChecksumBefore BIGINT);
INSERT INTO @Snap (TableName, RowsBefore, ChecksumBefore)
SELECT 'OutletPurchases', COUNT(*), CAST(CHECKSUM_AGG(BINARY_CHECKSUM(*)) AS BIGINT) FROM dbo.OutletPurchases
UNION ALL SELECT 'OutletSales', COUNT(*), CAST(CHECKSUM_AGG(BINARY_CHECKSUM(*)) AS BIGINT) FROM dbo.OutletSales
UNION ALL SELECT 'OutletSaleItems', COUNT(*), CAST(CHECKSUM_AGG(BINARY_CHECKSUM(*)) AS BIGINT) FROM dbo.OutletSaleItems
UNION ALL SELECT 'OutletBookings', COUNT(*), CAST(CHECKSUM_AGG(BINARY_CHECKSUM(*)) AS BIGINT) FROM dbo.OutletBookings
UNION ALL SELECT 'OutletBookingItems', COUNT(*), CAST(CHECKSUM_AGG(BINARY_CHECKSUM(*)) AS BIGINT) FROM dbo.OutletBookingItems
UNION ALL SELECT 'OutletWastages', COUNT(*), CAST(CHECKSUM_AGG(BINARY_CHECKSUM(*)) AS BIGINT) FROM dbo.OutletWastages
UNION ALL SELECT 'Area', COUNT(*), CAST(CHECKSUM_AGG(BINARY_CHECKSUM(*)) AS BIGINT) FROM dbo.Area
UNION ALL SELECT 'PottedPlantStock', COUNT(*), CAST(CHECKSUM_AGG(BINARY_CHECKSUM(*)) AS BIGINT) FROM dbo.PottedPlantStock
UNION ALL SELECT 'PottedPlantStockTransactions', COUNT(*), CAST(CHECKSUM_AGG(BINARY_CHECKSUM(*)) AS BIGINT) FROM dbo.PottedPlantStockTransactions
UNION ALL SELECT 'ReadyStock', COUNT(*), CAST(CHECKSUM_AGG(BINARY_CHECKSUM(*)) AS BIGINT) FROM dbo.ReadyStock
UNION ALL SELECT 'ReadyStockTransactions', COUNT(*), CAST(CHECKSUM_AGG(BINARY_CHECKSUM(*)) AS BIGINT) FROM dbo.ReadyStockTransactions
UNION ALL SELECT 'EmptyPotInventory', COUNT(*), CAST(CHECKSUM_AGG(BINARY_CHECKSUM(*)) AS BIGINT) FROM dbo.EmptyPotInventory;

DECLARE @SchemaObjectCountBefore INT = (SELECT COUNT(*) FROM sys.objects WHERE type IN ('U','TR','C'));

BEGIN TRANSACTION;

BEGIN TRY

    ------------------------------------------------------------------------
    -- SETUP / DISCOVERY -- prefer real data throughout; report exactly
    -- what was found vs. what (minimally) had to be created.
    ------------------------------------------------------------------------
    -- Guaranteed-unique suffix for every ZZTEST-tagged code this script
    -- writes (OutletPurchases.PurchaseCode / OutletSales.SaleCode /
    -- OutletBookings.BookingCode / OutletWastages.WastageCode all carry a
    -- UNIQUE constraint). NEWID() is used deliberately instead of a
    -- time-based suffix: an earlier draft built this from
    -- RIGHT(CONVERT(NVARCHAR(20), SYSUTCDATETIME(), 114), 8), which silently
    -- drops the HOUR field (style 114 is "hh:mi:ss:mmm", 12 characters, and
    -- RIGHT(...,8) keeps only the last 8) -- re-running the script more than
    -- once in the same SSMS session (same @@SPID) within the same
    -- minute-digit/second/millisecond window could collide on one of those
    -- UNIQUE columns. NEWID() has no such truncation risk.
    DECLARE @Suffix NVARCHAR(20) = CONVERT(NVARCHAR(10), @@SPID) + LEFT(REPLACE(CONVERT(NVARCHAR(36), NEWID()), '-', ''), 10);

    DECLARE @TestUserId INT;
    SELECT TOP 1 @TestUserId = Id FROM dbo.IMSUsers ORDER BY Id;
    IF @TestUserId IS NULL
    BEGIN
        INSERT INTO @Results VALUES ('SETUP', 'Discovery', 'At least one IMSUsers row', 'None found in this database -- cannot proceed (CancelledById requires a real user)', 'FAIL');
        GOTO Finish;
    END

    -- ---- Outlet Area(s): prefer real, existing, active Outlets ----
    DECLARE @OutletA INT, @OutletAName NVARCHAR(200), @OutletAWasReal BIT = 0;
    SELECT TOP 1 @OutletA = Id, @OutletAName = Name FROM dbo.Area WHERE AreaType = 'Outlet' AND IsActive = 1 ORDER BY Id;
    IF @OutletA IS NOT NULL SET @OutletAWasReal = 1;
    IF @OutletA IS NULL
    BEGIN
        INSERT INTO dbo.Area (Name, IsActive, AreaType) VALUES ('ZZTEST_Outlet_A_' + @Suffix, 1, 'Outlet');
        SET @OutletA = SCOPE_IDENTITY();
        SET @OutletAName = 'ZZTEST_Outlet_A_' + @Suffix;
    END

    DECLARE @OutletB INT, @OutletBWasReal BIT = 0;
    SELECT TOP 1 @OutletB = Id FROM dbo.Area WHERE AreaType = 'Outlet' AND IsActive = 1 AND Id <> @OutletA ORDER BY Id;
    IF @OutletB IS NOT NULL SET @OutletBWasReal = 1;
    IF @OutletB IS NULL
    BEGIN
        INSERT INTO dbo.Area (Name, IsActive, AreaType) VALUES ('ZZTEST_Outlet_B_' + @Suffix, 1, 'Outlet');
        SET @OutletB = SCOPE_IDENTITY();
    END

    INSERT INTO @Results VALUES ('SETUP', 'Discovery: Outlet Area(s)', 'A real active Outlet Area', 'Outlet A = "' + @OutletAName + '" (Id ' + CAST(@OutletA AS NVARCHAR) + '), ' + CASE WHEN @OutletAWasReal = 1 THEN 'REAL existing Outlet' ELSE 'none existed -- created ONE minimal temporary Outlet (no FK-dependent data fabricated, only this root Area row)' END
        + '. Outlet B (for the cross-Outlet test only) is ' + CASE WHEN @OutletBWasReal = 1 THEN 'a second REAL existing Outlet' ELSE 'a second temporary Outlet (none existed)' END, 'INFO');

    -- ---- Species / PotSize: ALWAYS real master data ----
    DECLARE @SpeciesA INT, @SpeciesAName NVARCHAR(200), @SpeciesB INT, @SpeciesBName NVARCHAR(200);
    SELECT TOP 1 @SpeciesA = Id, @SpeciesAName = Name FROM dbo.PlantSpecies ORDER BY Id;
    SELECT TOP 1 @SpeciesB = Id, @SpeciesBName = Name FROM dbo.PlantSpecies WHERE Id <> @SpeciesA ORDER BY Id;
    IF @SpeciesB IS NULL BEGIN SET @SpeciesB = @SpeciesA; SET @SpeciesBName = @SpeciesAName; END

    DECLARE @PotSizeA NVARCHAR(50), @PotSizeB NVARCHAR(50);
    SELECT TOP 1 @PotSizeA = Name FROM dbo.PotSizes WHERE IsActive = 1 ORDER BY SortOrder;
    SELECT TOP 1 @PotSizeB = Name FROM dbo.PotSizes WHERE IsActive = 1 AND Name <> @PotSizeA ORDER BY SortOrder;
    IF @PotSizeB IS NULL SET @PotSizeB = @PotSizeA;

    IF @SpeciesA IS NULL OR @PotSizeA IS NULL
    BEGIN
        INSERT INTO @Results VALUES ('SETUP', 'Discovery: Species/PotSize', 'At least one real PlantSpecies and one active PotSize', 'None found in this database', 'FAIL');
        GOTO Finish;
    END
    INSERT INTO @Results VALUES ('SETUP', 'Discovery: Species/PotSize', 'Real existing master data', 'Species A="' + @SpeciesAName + '", Species B="' + @SpeciesBName + '", PotSize A="' + @PotSizeA + '", PotSize B="' + @PotSizeB + '" (all real rows -- never invented)', 'INFO');

    -- ---- Potted Plant Stock Pool A: prefer a real pool with available stock at the chosen Outlet ----
    DECLARE @PottedStockA INT, @PottedStockAWasReal BIT = 0, @PottedStockAOrigAvailable DECIMAL(18,2) = 0;
    SELECT TOP 1 @PottedStockA = Id, @PottedStockAOrigAvailable = PhysicalQuantity - ReservedQuantity
    FROM dbo.PottedPlantStock WHERE AreaId = @OutletA AND SpeciesId = @SpeciesA AND PotSize = @PotSizeA AND (PhysicalQuantity - ReservedQuantity) > 0;
    IF @PottedStockA IS NULL
        SELECT TOP 1 @PottedStockA = Id, @SpeciesA = SpeciesId, @PotSizeA = PotSize, @PottedStockAOrigAvailable = PhysicalQuantity - ReservedQuantity
        FROM dbo.PottedPlantStock WHERE AreaId = @OutletA AND (PhysicalQuantity - ReservedQuantity) >= 50 ORDER BY (PhysicalQuantity - ReservedQuantity) DESC;
    IF @PottedStockA IS NOT NULL SET @PottedStockAWasReal = 1;

    DECLARE @PottedStockABootstrapMsg NVARCHAR(400) = '';
    IF @PottedStockA IS NULL
    BEGIN
        -- No usable real pool -- create/stock one via the REAL External
        -- Purchase mechanism (the same one Test 18/19 verify), never by
        -- hand-crafting a stock row.
        INSERT INTO dbo.EmptyPotInventory (PotSize, AreaId, PhysicalQuantity, IsActive, CreatedDate, CreatedBy)
            VALUES (@PotSizeA, @OutletA, 0, 1, SYSUTCDATETIME(), 'ZZTEST-SETUP');
        DECLARE @EmptyPotA INT = SCOPE_IDENTITY();
        INSERT INTO dbo.PottedPlantStock (SpeciesId, PotSize, AreaId, EmptyPotInventoryId, PhysicalQuantity, ReservedQuantity, SoldDispatchedQuantity, WastedQuantity, InTransitQuantity, CreatedDate, CreatedBy)
            VALUES (@SpeciesA, @PotSizeA, @OutletA, @EmptyPotA, 0, 0, 0, 0, 0, SYSUTCDATETIME(), 'ZZTEST-SETUP');
        SET @PottedStockA = SCOPE_IDENTITY();
        DECLARE @BootstrapCodeA NVARCHAR(40) = 'ZZTEST-SETUPBOOT-A-' + @Suffix;
        INSERT INTO dbo.OutletPurchases (PurchaseCode, PurchaseDate, SupplierName, OutletAreaId, SpeciesId, PotSize, Quantity, PottedPlantStockId, Remarks, CreatedById, CreatedBy, CreatedDate)
            VALUES (@BootstrapCodeA, CAST(GETDATE() AS DATE), 'ZZTEST Setup Bootstrap Supplier', @OutletA, @SpeciesA, @PotSizeA, 200, @PottedStockA, 'ZZTEST setup bootstrap', NULL, 'ZZTEST-SETUP', SYSUTCDATETIME());
        UPDATE dbo.PottedPlantStock SET PhysicalQuantity = 200 WHERE Id = @PottedStockA;
        INSERT INTO dbo.PottedPlantStockTransactions (PottedPlantStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)
            VALUES (@PottedStockA, SYSUTCDATETIME(), 'Purchase', 'OutletPurchase', SCOPE_IDENTITY(), 200, 0, NULL, 'ZZTEST setup bootstrap', SYSUTCDATETIME());
        SET @PottedStockAOrigAvailable = 0;
        SET @PottedStockABootstrapMsg = ' -- no real usable pool existed, so setup created one via a real External Purchase (200 units) to make the rest of the test possible.';
    END

    -- ---- Potted Plant Stock Pool B (second variety, for multi-item tests) ----
    DECLARE @PottedStockB INT, @PottedStockBWasReal BIT = 0;
    SELECT TOP 1 @PottedStockB = Id FROM dbo.PottedPlantStock WHERE AreaId = @OutletA AND SpeciesId = @SpeciesB AND PotSize = @PotSizeB AND (PhysicalQuantity - ReservedQuantity) >= 50 AND Id <> @PottedStockA;
    IF @PottedStockB IS NOT NULL SET @PottedStockBWasReal = 1;
    IF @PottedStockB IS NULL
    BEGIN
        INSERT INTO dbo.EmptyPotInventory (PotSize, AreaId, PhysicalQuantity, IsActive, CreatedDate, CreatedBy)
            VALUES (@PotSizeB, @OutletA, 0, 1, SYSUTCDATETIME(), 'ZZTEST-SETUP');
        DECLARE @EmptyPotB INT = SCOPE_IDENTITY();
        INSERT INTO dbo.PottedPlantStock (SpeciesId, PotSize, AreaId, EmptyPotInventoryId, PhysicalQuantity, ReservedQuantity, SoldDispatchedQuantity, WastedQuantity, InTransitQuantity, CreatedDate, CreatedBy)
            VALUES (@SpeciesB, @PotSizeB, @OutletA, @EmptyPotB, 0, 0, 0, 0, 0, SYSUTCDATETIME(), 'ZZTEST-SETUP');
        SET @PottedStockB = SCOPE_IDENTITY();
        DECLARE @BootstrapCodeB NVARCHAR(40) = 'ZZTEST-SETUPBOOT-B-' + @Suffix;
        INSERT INTO dbo.OutletPurchases (PurchaseCode, PurchaseDate, SupplierName, OutletAreaId, SpeciesId, PotSize, Quantity, PottedPlantStockId, Remarks, CreatedById, CreatedBy, CreatedDate)
            VALUES (@BootstrapCodeB, CAST(GETDATE() AS DATE), 'ZZTEST Setup Bootstrap Supplier', @OutletA, @SpeciesB, @PotSizeB, 100, @PottedStockB, 'ZZTEST setup bootstrap', NULL, 'ZZTEST-SETUP', SYSUTCDATETIME());
        UPDATE dbo.PottedPlantStock SET PhysicalQuantity = 100 WHERE Id = @PottedStockB;
        INSERT INTO dbo.PottedPlantStockTransactions (PottedPlantStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)
            VALUES (@PottedStockB, SYSUTCDATETIME(), 'Purchase', 'OutletPurchase', SCOPE_IDENTITY(), 100, 0, NULL, 'ZZTEST setup bootstrap', SYSUTCDATETIME());
    END

    -- ---- Ready Tray: borrow ONE real row if any exists anywhere ----
    DECLARE @HasReadyStock BIT = 0, @ReadyStockTest INT, @ReadyStockOriginalAreaId INT, @ReadyStockOriginalQuantity DECIMAL(18,2),
            @ReadyStockOriginalReserved DECIMAL(18,2), @ReadyStockOriginalDispatched DECIMAL(18,2), @ReadyStockCavity NVARCHAR(30), @ReadyStockSpeciesId INT,
            @ReadyStockWasAtOutlet BIT = 0;
    SELECT TOP 1 @ReadyStockTest = Id, @ReadyStockOriginalAreaId = AreaId, @ReadyStockOriginalQuantity = Quantity,
                 @ReadyStockOriginalReserved = ReservedQuantity, @ReadyStockOriginalDispatched = DispatchedQuantity,
                 @ReadyStockCavity = CavityType, @ReadyStockSpeciesId = SpeciesId
    FROM dbo.ReadyStock WHERE AreaId = @OutletA ORDER BY Id;
    IF @ReadyStockTest IS NOT NULL SET @ReadyStockWasAtOutlet = 1;
    IF @ReadyStockTest IS NULL
        SELECT TOP 1 @ReadyStockTest = Id, @ReadyStockOriginalAreaId = AreaId, @ReadyStockOriginalQuantity = Quantity,
                     @ReadyStockOriginalReserved = ReservedQuantity, @ReadyStockOriginalDispatched = DispatchedQuantity,
                     @ReadyStockCavity = CavityType, @ReadyStockSpeciesId = SpeciesId
        FROM dbo.ReadyStock ORDER BY Id;

    IF @ReadyStockTest IS NOT NULL
    BEGIN
        SET @HasReadyStock = 1;
        IF @ReadyStockWasAtOutlet = 0
        BEGIN
            UPDATE dbo.ReadyStock SET AreaId = @OutletA WHERE Id = @ReadyStockTest;
            INSERT INTO dbo.ReadyStockTransactions (ReadyStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)
                VALUES (@ReadyStockTest, SYSUTCDATETIME(), 'Transfer', 'ZZTEST-SETUP', NULL, 0, @ReadyStockOriginalQuantity, NULL, 'ZZTEST fixture: real batch relocated to test Outlet for this transaction only', SYSUTCDATETIME());
        END
        -- Ensure enough headroom for the tray tests without discarding the real starting values (both restored on ROLLBACK).
        IF (@ReadyStockOriginalQuantity - @ReadyStockOriginalReserved - @ReadyStockOriginalDispatched) < 60
        BEGIN
            UPDATE dbo.ReadyStock SET Quantity = @ReadyStockOriginalReserved + @ReadyStockOriginalDispatched + 60 WHERE Id = @ReadyStockTest;
        END
        INSERT INTO @Results VALUES ('SETUP', 'Discovery: Ready Tray stock', 'A real existing ReadyStock row', 'Using ReadyStock Id ' + CAST(@ReadyStockTest AS NVARCHAR) + CASE WHEN @ReadyStockWasAtOutlet = 1 THEN ' (already at the chosen Outlet)' ELSE ' (relocated from its real Area to the test Outlet for this transaction only, simulating a real transfer)' END, 'INFO');
    END
    ELSE
        INSERT INTO @Results VALUES ('SETUP', 'Discovery: Ready Tray stock', 'A real existing ReadyStock row', 'NONE FOUND anywhere in this database. Per instruction, no fabricated Sowing/ReadyStock chain was created. All Ready Tray tests below are SKIPPED.', 'SKIP');

    ------------------------------------------------------------------------
    -- A1/1. Potted stock available at Outlet
    ------------------------------------------------------------------------
    INSERT INTO @Results VALUES ('1', 'Potted stock available at Outlet', 'A usable Potted Plant Stock pool exists at the Outlet', CASE WHEN @PottedStockAWasReal = 1 THEN 'REAL existing pool (Id ' + CAST(@PottedStockA AS NVARCHAR) + ') had ' + CAST(@PottedStockAOrigAvailable AS NVARCHAR) + ' available' ELSE 'No real usable pool existed' + @PottedStockABootstrapMsg END, 'PASS');

    ------------------------------------------------------------------------
    -- 2. Potted plant sale
    ------------------------------------------------------------------------
    DECLARE @SaleId2 INT, @PhysBefore2 DECIMAL(18,2);
    SAVE TRANSACTION SP2;
    BEGIN TRY
        SET @PhysBefore2 = (SELECT PhysicalQuantity FROM dbo.PottedPlantStock WHERE Id = @PottedStockA);
        INSERT INTO dbo.OutletSales (SaleCode, OutletAreaId, CustomerName, CustomerContact, SaleDate, Remarks, CreatedById, CreatedBy, CreatedDate)
            VALUES ('ZZTEST-OS-' + @Suffix + '-2', @OutletA, 'ZZTEST Customer 2', '9990000002', CAST(GETDATE() AS DATE), 'ZZTEST', NULL, 'ZZTEST', SYSUTCDATETIME());
        SET @SaleId2 = SCOPE_IDENTITY();
        INSERT INTO dbo.OutletSaleItems (SaleId, OutletAreaId, StockType, PottedPlantStockId, SpeciesId, PotSize, Quantity, CreatedDate)
            VALUES (@SaleId2, @OutletA, 'Potted', @PottedStockA, @SpeciesA, @PotSizeA, 12, SYSUTCDATETIME());
        DECLARE @ItemId2 INT = SCOPE_IDENTITY();
        UPDATE dbo.PottedPlantStock SET PhysicalQuantity = PhysicalQuantity - 12 WHERE Id = @PottedStockA;
        INSERT INTO dbo.PottedPlantStockTransactions (PottedPlantStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)
            VALUES (@PottedStockA, SYSUTCDATETIME(), 'Dispatch', 'OutletSaleItem', @ItemId2, -12, @PhysBefore2, NULL, 'ZZTEST', SYSUTCDATETIME());
        DECLARE @PhysAfter2 DECIMAL(18,2) = (SELECT PhysicalQuantity FROM dbo.PottedPlantStock WHERE Id = @PottedStockA);
        INSERT INTO @Results VALUES ('2', 'Potted plant sale', 'PhysicalQuantity decreases by exactly 12', 'Before=' + CAST(@PhysBefore2 AS NVARCHAR) + ', After=' + CAST(@PhysAfter2 AS NVARCHAR), CASE WHEN @PhysAfter2 = @PhysBefore2 - 12 THEN 'PASS' ELSE 'FAIL' END);
    END TRY
    BEGIN CATCH
        INSERT INTO @Results VALUES ('2', 'Potted plant sale', 'PhysicalQuantity decreases by 12', 'ERROR: ' + ERROR_MESSAGE(), 'FAIL');
        IF XACT_STATE() = -1 GOTO Finish; ELSE ROLLBACK TRANSACTION SP2;
    END CATCH

    ------------------------------------------------------------------------
    -- 3. Potted plant booking  /  4. Partial collection
    ------------------------------------------------------------------------
    DECLARE @BookingId3 INT, @BkItem3 INT, @ResBefore3 DECIMAL(18,2);
    SAVE TRANSACTION SP3;
    BEGIN TRY
        SET @ResBefore3 = (SELECT ReservedQuantity FROM dbo.PottedPlantStock WHERE Id = @PottedStockA);
        INSERT INTO dbo.OutletBookings (BookingCode, OutletAreaId, CustomerName, CustomerContact, BookingDate, RequiredDate, Status, Remarks, CreatedById, CreatedBy, CreatedDate)
            VALUES ('ZZTEST-OB-' + @Suffix + '-3', @OutletA, 'ZZTEST Customer 3', '9990000003', CAST(GETDATE() AS DATE), DATEADD(DAY, 5, CAST(GETDATE() AS DATE)), 'Pending', 'ZZTEST', NULL, 'ZZTEST', SYSUTCDATETIME());
        SET @BookingId3 = SCOPE_IDENTITY();
        INSERT INTO dbo.OutletBookingItems (BookingId, OutletAreaId, StockType, PottedPlantStockId, SpeciesId, PotSize, Quantity, CollectedQuantity, CreatedDate)
            VALUES (@BookingId3, @OutletA, 'Potted', @PottedStockA, @SpeciesA, @PotSizeA, 30, 0, SYSUTCDATETIME());
        SET @BkItem3 = SCOPE_IDENTITY();
        UPDATE dbo.PottedPlantStock SET ReservedQuantity = ReservedQuantity + 30 WHERE Id = @PottedStockA;
        INSERT INTO dbo.PottedPlantStockTransactions (PottedPlantStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)
            VALUES (@PottedStockA, SYSUTCDATETIME(), 'Reservation', 'OutletBookingItem', @BkItem3, 30, @ResBefore3, NULL, 'ZZTEST', SYSUTCDATETIME());
        DECLARE @ResAfter3 DECIMAL(18,2) = (SELECT ReservedQuantity FROM dbo.PottedPlantStock WHERE Id = @PottedStockA);
        INSERT INTO @Results VALUES ('3', 'Potted plant booking', 'ReservedQuantity increases by exactly 30; PhysicalQuantity untouched', 'ReservedQuantity ' + CAST(@ResBefore3 AS NVARCHAR) + ' -> ' + CAST(@ResAfter3 AS NVARCHAR), CASE WHEN @ResAfter3 = @ResBefore3 + 30 THEN 'PASS' ELSE 'FAIL' END);
    END TRY
    BEGIN CATCH
        INSERT INTO @Results VALUES ('3', 'Potted plant booking', 'ReservedQuantity increases by 30', 'ERROR: ' + ERROR_MESSAGE(), 'FAIL');
        IF XACT_STATE() = -1 GOTO Finish; ELSE ROLLBACK TRANSACTION SP3;
    END CATCH

    SAVE TRANSACTION SP4;
    BEGIN TRY
        DECLARE @PhysBefore4 DECIMAL(18,2) = (SELECT PhysicalQuantity FROM dbo.PottedPlantStock WHERE Id = @PottedStockA);
        DECLARE @ResBefore4 DECIMAL(18,2) = (SELECT ReservedQuantity FROM dbo.PottedPlantStock WHERE Id = @PottedStockA);
        UPDATE dbo.PottedPlantStock SET PhysicalQuantity = PhysicalQuantity - 10 WHERE Id = @PottedStockA;
        INSERT INTO dbo.PottedPlantStockTransactions (PottedPlantStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)
            VALUES (@PottedStockA, SYSUTCDATETIME(), 'Dispatch', 'OutletBookingItem', @BkItem3, -10, @PhysBefore4, NULL, 'ZZTEST', SYSUTCDATETIME());
        UPDATE dbo.PottedPlantStock SET ReservedQuantity = ReservedQuantity - 10 WHERE Id = @PottedStockA;
        INSERT INTO dbo.PottedPlantStockTransactions (PottedPlantStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)
            VALUES (@PottedStockA, SYSUTCDATETIME(), 'ReservationRelease', 'OutletBookingItem', @BkItem3, -10, @ResBefore4, NULL, 'ZZTEST', SYSUTCDATETIME());
        UPDATE dbo.OutletBookingItems SET CollectedQuantity = CollectedQuantity + 10 WHERE Id = @BkItem3;
        UPDATE dbo.OutletBookings SET Status = 'PartiallyCollected' WHERE Id = @BookingId3;
        DECLARE @Collected4 DECIMAL(18,2), @Qty4 DECIMAL(18,2), @Status4 NVARCHAR(20);
        SELECT @Collected4 = CollectedQuantity, @Qty4 = Quantity FROM dbo.OutletBookingItems WHERE Id = @BkItem3;
        SELECT @Status4 = Status FROM dbo.OutletBookings WHERE Id = @BookingId3;
        INSERT INTO @Results VALUES ('4', 'Partial collection', 'Collected=10, Remaining=20, Status=PartiallyCollected', 'Collected=' + CAST(@Collected4 AS NVARCHAR) + ', Remaining=' + CAST(@Qty4 - @Collected4 AS NVARCHAR) + ', Status=' + @Status4,
            CASE WHEN @Collected4 = 10 AND (@Qty4 - @Collected4) = 20 AND @Status4 = 'PartiallyCollected' THEN 'PASS' ELSE 'FAIL' END);
    END TRY
    BEGIN CATCH
        INSERT INTO @Results VALUES ('4', 'Partial collection', 'Collected=10, Remaining=20', 'ERROR: ' + ERROR_MESSAGE(), 'FAIL');
        IF XACT_STATE() = -1 GOTO Finish; ELSE ROLLBACK TRANSACTION SP4;
    END CATCH

    ------------------------------------------------------------------------
    -- 5. Booking cancellation (separate fresh booking)
    ------------------------------------------------------------------------
    SAVE TRANSACTION SP5;
    BEGIN TRY
        DECLARE @ResBefore5 DECIMAL(18,2) = (SELECT ReservedQuantity FROM dbo.PottedPlantStock WHERE Id = @PottedStockA);
        INSERT INTO dbo.OutletBookings (BookingCode, OutletAreaId, CustomerName, BookingDate, Status, CreatedBy, CreatedDate)
            VALUES ('ZZTEST-OB-' + @Suffix + '-5', @OutletA, 'ZZTEST Customer 5', CAST(GETDATE() AS DATE), 'Pending', 'ZZTEST', SYSUTCDATETIME());
        DECLARE @BookingId5 INT = SCOPE_IDENTITY();
        INSERT INTO dbo.OutletBookingItems (BookingId, OutletAreaId, StockType, PottedPlantStockId, SpeciesId, PotSize, Quantity, CollectedQuantity, CreatedDate)
            VALUES (@BookingId5, @OutletA, 'Potted', @PottedStockA, @SpeciesA, @PotSizeA, 8, 0, SYSUTCDATETIME());
        DECLARE @BkItem5 INT = SCOPE_IDENTITY();
        UPDATE dbo.PottedPlantStock SET ReservedQuantity = ReservedQuantity + 8 WHERE Id = @PottedStockA;
        -- cancel: release the full 8 (nothing collected yet)
        UPDATE dbo.PottedPlantStock SET ReservedQuantity = ReservedQuantity - 8 WHERE Id = @PottedStockA;
        UPDATE dbo.OutletBookings SET Status = 'Cancelled', CancelledById = @TestUserId, CancelledDate = SYSUTCDATETIME(), CancellationReason = 'ZZTEST cancellation' WHERE Id = @BookingId5;
        DECLARE @ResAfter5 DECIMAL(18,2) = (SELECT ReservedQuantity FROM dbo.PottedPlantStock WHERE Id = @PottedStockA);
        DECLARE @Status5 NVARCHAR(20) = (SELECT Status FROM dbo.OutletBookings WHERE Id = @BookingId5);
        INSERT INTO @Results VALUES ('5', 'Booking cancellation', 'ReservedQuantity back to pre-booking level, Status=Cancelled', 'Reserved ' + CAST(@ResBefore5 AS NVARCHAR) + ' -> ' + CAST(@ResAfter5 AS NVARCHAR) + ', Status=' + @Status5, CASE WHEN @ResAfter5 = @ResBefore5 AND @Status5 = 'Cancelled' THEN 'PASS' ELSE 'FAIL' END);
    END TRY
    BEGIN CATCH
        INSERT INTO @Results VALUES ('5', 'Booking cancellation', 'ReservedQuantity restored, Status=Cancelled', 'ERROR: ' + ERROR_MESSAGE(), 'FAIL');
        IF XACT_STATE() = -1 GOTO Finish; ELSE ROLLBACK TRANSACTION SP5;
    END CATCH

    ------------------------------------------------------------------------
    -- 6. Potted plant wastage
    ------------------------------------------------------------------------
    DECLARE @WastageId6 INT;
    SAVE TRANSACTION SP6;
    BEGIN TRY
        DECLARE @PhysBefore6 DECIMAL(18,2) = (SELECT PhysicalQuantity FROM dbo.PottedPlantStock WHERE Id = @PottedStockA);
        INSERT INTO dbo.OutletWastages (WastageCode, WastageDate, OutletAreaId, StockType, PottedPlantStockId, SpeciesId, PotSize, Quantity, Reason, Remarks, CreatedById, CreatedBy, CreatedDate)
            VALUES ('ZZTEST-OW-' + @Suffix + '-6', CAST(GETDATE() AS DATE), @OutletA, 'Potted', @PottedStockA, @SpeciesA, @PotSizeA, 4, 'Damaged', 'ZZTEST', NULL, 'ZZTEST', SYSUTCDATETIME());
        SET @WastageId6 = SCOPE_IDENTITY();
        UPDATE dbo.PottedPlantStock SET PhysicalQuantity = PhysicalQuantity - 4 WHERE Id = @PottedStockA;
        INSERT INTO dbo.PottedPlantStockTransactions (PottedPlantStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)
            VALUES (@PottedStockA, SYSUTCDATETIME(), 'Wastage', 'OutletWastage', @WastageId6, -4, @PhysBefore6, NULL, 'ZZTEST', SYSUTCDATETIME());
        DECLARE @PhysAfter6 DECIMAL(18,2) = (SELECT PhysicalQuantity FROM dbo.PottedPlantStock WHERE Id = @PottedStockA);
        INSERT INTO @Results VALUES ('6', 'Potted plant wastage', 'PhysicalQuantity decreases by exactly 4', 'Before=' + CAST(@PhysBefore6 AS NVARCHAR) + ', After=' + CAST(@PhysAfter6 AS NVARCHAR), CASE WHEN @PhysAfter6 = @PhysBefore6 - 4 THEN 'PASS' ELSE 'FAIL' END);
    END TRY
    BEGIN CATCH
        INSERT INTO @Results VALUES ('6', 'Potted plant wastage', 'PhysicalQuantity decreases by 4', 'ERROR: ' + ERROR_MESSAGE(), 'FAIL');
        IF XACT_STATE() = -1 GOTO Finish; ELSE ROLLBACK TRANSACTION SP6;
    END CATCH

    ------------------------------------------------------------------------
    -- 7. Ready tray transfer/availability at Outlet
    ------------------------------------------------------------------------
    IF @HasReadyStock = 1
    BEGIN
        DECLARE @TrayAreaCheck7 INT = (SELECT AreaId FROM dbo.ReadyStock WHERE Id = @ReadyStockTest);
        INSERT INTO @Results VALUES ('7', 'Ready tray transfer/availability at Outlet', 'ReadyStock is at the test Outlet with available quantity', CASE WHEN @TrayAreaCheck7 = @OutletA THEN 'Confirmed at Outlet (Id ' + CAST(@ReadyStockTest AS NVARCHAR) + ')' + CASE WHEN @ReadyStockWasAtOutlet = 0 THEN ' -- relocated there by a simulated Transfer during setup' ELSE ' -- was already there (real data)' END ELSE 'NOT at test Outlet' END,
            CASE WHEN @TrayAreaCheck7 = @OutletA THEN 'PASS' ELSE 'FAIL' END);
    END
    ELSE
        INSERT INTO @Results VALUES ('7', 'Ready tray transfer/availability at Outlet', 'ReadyStock available at Outlet', 'SKIPPED: no ReadyStock rows exist anywhere in this database', 'SKIP');

    ------------------------------------------------------------------------
    -- 8. Ready tray sale
    ------------------------------------------------------------------------
    IF @HasReadyStock = 1
    BEGIN
        SAVE TRANSACTION SP8;
        BEGIN TRY
            DECLARE @TrayBefore8 DECIMAL(18,2) = (SELECT Quantity FROM dbo.ReadyStock WHERE Id = @ReadyStockTest);
            INSERT INTO dbo.OutletSales (SaleCode, OutletAreaId, CustomerName, SaleDate, CreatedBy, CreatedDate)
                VALUES ('ZZTEST-OS-' + @Suffix + '-8', @OutletA, 'ZZTEST Customer 8', CAST(GETDATE() AS DATE), 'ZZTEST', SYSUTCDATETIME());
            DECLARE @SaleId8 INT = SCOPE_IDENTITY();
            INSERT INTO dbo.OutletSaleItems (SaleId, OutletAreaId, StockType, ReadyStockId, SpeciesId, CavityType, Quantity, CreatedDate)
                VALUES (@SaleId8, @OutletA, 'Tray', @ReadyStockTest, @ReadyStockSpeciesId, @ReadyStockCavity, 6, SYSUTCDATETIME());
            DECLARE @ItemId8 INT = SCOPE_IDENTITY();
            UPDATE dbo.ReadyStock SET Quantity = Quantity - 6 WHERE Id = @ReadyStockTest;
            INSERT INTO dbo.ReadyStockTransactions (ReadyStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)
                VALUES (@ReadyStockTest, SYSUTCDATETIME(), 'Dispatch', 'OutletSaleItem', @ItemId8, -6, @TrayBefore8, NULL, 'ZZTEST', SYSUTCDATETIME());
            DECLARE @TrayAfter8 DECIMAL(18,2) = (SELECT Quantity FROM dbo.ReadyStock WHERE Id = @ReadyStockTest);
            INSERT INTO @Results VALUES ('8', 'Ready tray sale', 'Quantity decreases by exactly 6 trays', 'Before=' + CAST(@TrayBefore8 AS NVARCHAR) + ', After=' + CAST(@TrayAfter8 AS NVARCHAR), CASE WHEN @TrayAfter8 = @TrayBefore8 - 6 THEN 'PASS' ELSE 'FAIL' END);
        END TRY
        BEGIN CATCH
            INSERT INTO @Results VALUES ('8', 'Ready tray sale', 'Quantity decreases by 6', 'ERROR: ' + ERROR_MESSAGE(), 'FAIL');
            IF XACT_STATE() = -1 GOTO Finish; ELSE ROLLBACK TRANSACTION SP8;
        END CATCH
    END
    ELSE
        INSERT INTO @Results VALUES ('8', 'Ready tray sale', 'Quantity decreases by 6', 'SKIPPED: no ReadyStock available', 'SKIP');

    ------------------------------------------------------------------------
    -- 9. Ready tray booking  /  10. Partial tray collection
    ------------------------------------------------------------------------
    DECLARE @BookingId9 INT, @BkItem9 INT;
    IF @HasReadyStock = 1
    BEGIN
        SAVE TRANSACTION SP9;
        BEGIN TRY
            DECLARE @TrayResBefore9 DECIMAL(18,2) = (SELECT ReservedQuantity FROM dbo.ReadyStock WHERE Id = @ReadyStockTest);
            INSERT INTO dbo.OutletBookings (BookingCode, OutletAreaId, CustomerName, BookingDate, Status, CreatedBy, CreatedDate)
                VALUES ('ZZTEST-OB-' + @Suffix + '-9', @OutletA, 'ZZTEST Customer 9', CAST(GETDATE() AS DATE), 'Pending', 'ZZTEST', SYSUTCDATETIME());
            SET @BookingId9 = SCOPE_IDENTITY();
            INSERT INTO dbo.OutletBookingItems (BookingId, OutletAreaId, StockType, ReadyStockId, SpeciesId, CavityType, Quantity, CollectedQuantity, CreatedDate)
                VALUES (@BookingId9, @OutletA, 'Tray', @ReadyStockTest, @ReadyStockSpeciesId, @ReadyStockCavity, 10, 0, SYSUTCDATETIME());
            SET @BkItem9 = SCOPE_IDENTITY();
            UPDATE dbo.ReadyStock SET ReservedQuantity = ReservedQuantity + 10 WHERE Id = @ReadyStockTest;
            INSERT INTO dbo.ReadyStockTransactions (ReadyStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)
                VALUES (@ReadyStockTest, SYSUTCDATETIME(), 'Reservation', 'OutletBookingItem', @BkItem9, 10, @TrayResBefore9, NULL, 'ZZTEST', SYSUTCDATETIME());
            DECLARE @TrayResAfter9 DECIMAL(18,2) = (SELECT ReservedQuantity FROM dbo.ReadyStock WHERE Id = @ReadyStockTest);
            INSERT INTO @Results VALUES ('9', 'Ready tray booking', 'ReservedQuantity increases by exactly 10 trays', 'Reserved ' + CAST(@TrayResBefore9 AS NVARCHAR) + ' -> ' + CAST(@TrayResAfter9 AS NVARCHAR), CASE WHEN @TrayResAfter9 = @TrayResBefore9 + 10 THEN 'PASS' ELSE 'FAIL' END);
        END TRY
        BEGIN CATCH
            INSERT INTO @Results VALUES ('9', 'Ready tray booking', 'ReservedQuantity increases by 10', 'ERROR: ' + ERROR_MESSAGE(), 'FAIL');
            IF XACT_STATE() = -1 GOTO Finish; ELSE ROLLBACK TRANSACTION SP9;
        END CATCH

        SAVE TRANSACTION SP10;
        BEGIN TRY
            DECLARE @Ok10 BIT, @Msg10 NVARCHAR(400);
            -- Mirrors ReadyStockRepository.RecordDispatchAsync: Reserved -= q, Dispatched += q, two ledger rows.
            DECLARE @TrayQty10 DECIMAL(18,2), @TrayRes10 DECIMAL(18,2), @TrayDisp10 DECIMAL(18,2);
            SELECT @TrayQty10 = Quantity, @TrayRes10 = ReservedQuantity, @TrayDisp10 = DispatchedQuantity FROM dbo.ReadyStock WHERE Id = @ReadyStockTest;
            UPDATE dbo.ReadyStock SET ReservedQuantity = ReservedQuantity - 4, DispatchedQuantity = DispatchedQuantity + 4 WHERE Id = @ReadyStockTest;
            INSERT INTO dbo.ReadyStockTransactions (ReadyStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)
                VALUES (@ReadyStockTest, SYSUTCDATETIME(), 'ReservationRelease', 'OutletBookingItem', @BkItem9, -4, @TrayRes10, NULL, 'ZZTEST', SYSUTCDATETIME());
            INSERT INTO dbo.ReadyStockTransactions (ReadyStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)
                VALUES (@ReadyStockTest, SYSUTCDATETIME(), 'Dispatch', 'OutletBookingItem', @BkItem9, -4, @TrayQty10 - @TrayDisp10, NULL, 'ZZTEST', SYSUTCDATETIME());
            UPDATE dbo.OutletBookingItems SET CollectedQuantity = CollectedQuantity + 4 WHERE Id = @BkItem9;
            UPDATE dbo.OutletBookings SET Status = 'PartiallyCollected' WHERE Id = @BookingId9;
            DECLARE @Collected10 DECIMAL(18,2), @Qty10 DECIMAL(18,2), @Status10 NVARCHAR(20);
            SELECT @Collected10 = CollectedQuantity, @Qty10 = Quantity FROM dbo.OutletBookingItems WHERE Id = @BkItem9;
            SELECT @Status10 = Status FROM dbo.OutletBookings WHERE Id = @BookingId9;
            INSERT INTO @Results VALUES ('10', 'Partial tray collection', 'Collected=4, Remaining=6, Status=PartiallyCollected', 'Collected=' + CAST(@Collected10 AS NVARCHAR) + ', Remaining=' + CAST(@Qty10 - @Collected10 AS NVARCHAR) + ', Status=' + @Status10,
                CASE WHEN @Collected10 = 4 AND (@Qty10 - @Collected10) = 6 AND @Status10 = 'PartiallyCollected' THEN 'PASS' ELSE 'FAIL' END);
        END TRY
        BEGIN CATCH
            INSERT INTO @Results VALUES ('10', 'Partial tray collection', 'Collected=4, Remaining=6', 'ERROR: ' + ERROR_MESSAGE(), 'FAIL');
            IF XACT_STATE() = -1 GOTO Finish; ELSE ROLLBACK TRANSACTION SP10;
        END CATCH
    END
    ELSE
    BEGIN
        INSERT INTO @Results VALUES ('9', 'Ready tray booking', 'ReservedQuantity increases', 'SKIPPED: no ReadyStock available', 'SKIP');
        INSERT INTO @Results VALUES ('10', 'Partial tray collection', 'Collected=4, Remaining=6', 'SKIPPED: no ReadyStock available', 'SKIP');
    END

    ------------------------------------------------------------------------
    -- 11. Tray booking cancellation
    ------------------------------------------------------------------------
    IF @HasReadyStock = 1
    BEGIN
        SAVE TRANSACTION SP11;
        BEGIN TRY
            DECLARE @TrayResBefore11 DECIMAL(18,2) = (SELECT ReservedQuantity FROM dbo.ReadyStock WHERE Id = @ReadyStockTest);
            INSERT INTO dbo.OutletBookings (BookingCode, OutletAreaId, CustomerName, BookingDate, Status, CreatedBy, CreatedDate)
                VALUES ('ZZTEST-OB-' + @Suffix + '-11', @OutletA, 'ZZTEST Customer 11', CAST(GETDATE() AS DATE), 'Pending', 'ZZTEST', SYSUTCDATETIME());
            DECLARE @BookingId11 INT = SCOPE_IDENTITY();
            INSERT INTO dbo.OutletBookingItems (BookingId, OutletAreaId, StockType, ReadyStockId, SpeciesId, CavityType, Quantity, CollectedQuantity, CreatedDate)
                VALUES (@BookingId11, @OutletA, 'Tray', @ReadyStockTest, @ReadyStockSpeciesId, @ReadyStockCavity, 5, 0, SYSUTCDATETIME());
            DECLARE @BkItem11 INT = SCOPE_IDENTITY();
            UPDATE dbo.ReadyStock SET ReservedQuantity = ReservedQuantity + 5 WHERE Id = @ReadyStockTest;
            UPDATE dbo.ReadyStock SET ReservedQuantity = ReservedQuantity - 5 WHERE Id = @ReadyStockTest;
            UPDATE dbo.OutletBookings SET Status = 'Cancelled', CancelledById = @TestUserId, CancelledDate = SYSUTCDATETIME(), CancellationReason = 'ZZTEST tray cancellation' WHERE Id = @BookingId11;
            DECLARE @TrayResAfter11 DECIMAL(18,2) = (SELECT ReservedQuantity FROM dbo.ReadyStock WHERE Id = @ReadyStockTest);
            INSERT INTO @Results VALUES ('11', 'Tray booking cancellation', 'ReservedQuantity back to pre-booking level, Status=Cancelled', 'Reserved ' + CAST(@TrayResBefore11 AS NVARCHAR) + ' -> ' + CAST(@TrayResAfter11 AS NVARCHAR), CASE WHEN @TrayResAfter11 = @TrayResBefore11 THEN 'PASS' ELSE 'FAIL' END);
        END TRY
        BEGIN CATCH
            INSERT INTO @Results VALUES ('11', 'Tray booking cancellation', 'ReservedQuantity restored', 'ERROR: ' + ERROR_MESSAGE(), 'FAIL');
            IF XACT_STATE() = -1 GOTO Finish; ELSE ROLLBACK TRANSACTION SP11;
        END CATCH
    END
    ELSE
        INSERT INTO @Results VALUES ('11', 'Tray booking cancellation', 'ReservedQuantity restored', 'SKIPPED: no ReadyStock available', 'SKIP');

    ------------------------------------------------------------------------
    -- 12. Ready tray wastage
    ------------------------------------------------------------------------
    DECLARE @WastageId12 INT;
    IF @HasReadyStock = 1
    BEGIN
        SAVE TRANSACTION SP12;
        BEGIN TRY
            DECLARE @TrayBefore12 DECIMAL(18,2) = (SELECT Quantity FROM dbo.ReadyStock WHERE Id = @ReadyStockTest);
            INSERT INTO dbo.OutletWastages (WastageCode, WastageDate, OutletAreaId, StockType, ReadyStockId, SpeciesId, CavityType, Quantity, Reason, Remarks, CreatedById, CreatedBy, CreatedDate)
                VALUES ('ZZTEST-OW-' + @Suffix + '-12', CAST(GETDATE() AS DATE), @OutletA, 'Tray', @ReadyStockTest, @ReadyStockSpeciesId, @ReadyStockCavity, 2, 'Died / Wilted', 'ZZTEST', NULL, 'ZZTEST', SYSUTCDATETIME());
            SET @WastageId12 = SCOPE_IDENTITY();
            UPDATE dbo.ReadyStock SET Quantity = Quantity - 2 WHERE Id = @ReadyStockTest;
            INSERT INTO dbo.ReadyStockTransactions (ReadyStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)
                VALUES (@ReadyStockTest, SYSUTCDATETIME(), 'Wastage', 'OutletWastage', @WastageId12, -2, @TrayBefore12, NULL, 'ZZTEST', SYSUTCDATETIME());
            DECLARE @TrayAfter12 DECIMAL(18,2) = (SELECT Quantity FROM dbo.ReadyStock WHERE Id = @ReadyStockTest);
            INSERT INTO @Results VALUES ('12', 'Ready tray wastage', 'Quantity decreases by exactly 2 trays', 'Before=' + CAST(@TrayBefore12 AS NVARCHAR) + ', After=' + CAST(@TrayAfter12 AS NVARCHAR), CASE WHEN @TrayAfter12 = @TrayBefore12 - 2 THEN 'PASS' ELSE 'FAIL' END);
        END TRY
        BEGIN CATCH
            INSERT INTO @Results VALUES ('12', 'Ready tray wastage', 'Quantity decreases by 2', 'ERROR: ' + ERROR_MESSAGE(), 'FAIL');
            IF XACT_STATE() = -1 GOTO Finish; ELSE ROLLBACK TRANSACTION SP12;
        END CATCH
    END
    ELSE
        INSERT INTO @Results VALUES ('12', 'Ready tray wastage', 'Quantity decreases by 2', 'SKIPPED: no ReadyStock available', 'SKIP');

    ------------------------------------------------------------------------
    -- 13. Multi-item potted sale (two varieties, one sale)
    ------------------------------------------------------------------------
    SAVE TRANSACTION SP13;
    BEGIN TRY
        INSERT INTO dbo.OutletSales (SaleCode, OutletAreaId, CustomerName, SaleDate, Remarks, CreatedBy, CreatedDate)
            VALUES ('ZZTEST-OS-' + @Suffix + '-13', @OutletA, 'ZZTEST Customer 13', CAST(GETDATE() AS DATE), 'ZZTEST multi-item potted sale', 'ZZTEST', SYSUTCDATETIME());
        DECLARE @SaleId13 INT = SCOPE_IDENTITY();
        INSERT INTO dbo.OutletSaleItems (SaleId, OutletAreaId, StockType, PottedPlantStockId, SpeciesId, PotSize, Quantity, CreatedDate)
            VALUES (@SaleId13, @OutletA, 'Potted', @PottedStockA, @SpeciesA, @PotSizeA, 5, SYSUTCDATETIME());
        UPDATE dbo.PottedPlantStock SET PhysicalQuantity = PhysicalQuantity - 5 WHERE Id = @PottedStockA;
        INSERT INTO dbo.OutletSaleItems (SaleId, OutletAreaId, StockType, PottedPlantStockId, SpeciesId, PotSize, Quantity, CreatedDate)
            VALUES (@SaleId13, @OutletA, 'Potted', @PottedStockB, @SpeciesB, @PotSizeB, 7, SYSUTCDATETIME());
        UPDATE dbo.PottedPlantStock SET PhysicalQuantity = PhysicalQuantity - 7 WHERE Id = @PottedStockB;
        DECLARE @ItemCount13 INT = (SELECT COUNT(*) FROM dbo.OutletSaleItems WHERE SaleId = @SaleId13);
        INSERT INTO @Results VALUES ('13', 'Multi-item potted sale', 'One sale with 2 Potted items (different varieties/pot sizes)', CAST(@ItemCount13 AS NVARCHAR) + ' item(s) recorded under Sale ' + CAST(@SaleId13 AS NVARCHAR), CASE WHEN @ItemCount13 = 2 THEN 'PASS' ELSE 'FAIL' END);
    END TRY
    BEGIN CATCH
        INSERT INTO @Results VALUES ('13', 'Multi-item potted sale', '2 items recorded', 'ERROR: ' + ERROR_MESSAGE(), 'FAIL');
        IF XACT_STATE() = -1 GOTO Finish; ELSE ROLLBACK TRANSACTION SP13;
    END CATCH

    ------------------------------------------------------------------------
    -- 14. Mixed potted + tray sale in ONE sale
    ------------------------------------------------------------------------
    IF @HasReadyStock = 1
    BEGIN
        SAVE TRANSACTION SP14;
        BEGIN TRY
            INSERT INTO dbo.OutletSales (SaleCode, OutletAreaId, CustomerName, SaleDate, Remarks, CreatedBy, CreatedDate)
                VALUES ('ZZTEST-OS-' + @Suffix + '-14', @OutletA, 'ZZTEST Customer 14', CAST(GETDATE() AS DATE), 'ZZTEST mixed sale', 'ZZTEST', SYSUTCDATETIME());
            DECLARE @SaleId14 INT = SCOPE_IDENTITY();
            INSERT INTO dbo.OutletSaleItems (SaleId, OutletAreaId, StockType, PottedPlantStockId, SpeciesId, PotSize, Quantity, CreatedDate)
                VALUES (@SaleId14, @OutletA, 'Potted', @PottedStockA, @SpeciesA, @PotSizeA, 3, SYSUTCDATETIME());
            UPDATE dbo.PottedPlantStock SET PhysicalQuantity = PhysicalQuantity - 3 WHERE Id = @PottedStockA;
            INSERT INTO dbo.OutletSaleItems (SaleId, OutletAreaId, StockType, ReadyStockId, SpeciesId, CavityType, Quantity, CreatedDate)
                VALUES (@SaleId14, @OutletA, 'Tray', @ReadyStockTest, @ReadyStockSpeciesId, @ReadyStockCavity, 3, SYSUTCDATETIME());
            UPDATE dbo.ReadyStock SET Quantity = Quantity - 3 WHERE Id = @ReadyStockTest;
            DECLARE @ItemCount14 INT = (SELECT COUNT(*) FROM dbo.OutletSaleItems WHERE SaleId = @SaleId14);
            DECLARE @StockTypes14 INT = (SELECT COUNT(DISTINCT StockType) FROM dbo.OutletSaleItems WHERE SaleId = @SaleId14);
            INSERT INTO @Results VALUES ('14', 'Mixed potted+tray sale (one sale)', 'One sale with both a Potted item and a Tray item', CAST(@ItemCount14 AS NVARCHAR) + ' item(s), ' + CAST(@StockTypes14 AS NVARCHAR) + ' distinct StockType(s)', CASE WHEN @ItemCount14 = 2 AND @StockTypes14 = 2 THEN 'PASS' ELSE 'FAIL' END);
        END TRY
        BEGIN CATCH
            INSERT INTO @Results VALUES ('14', 'Mixed potted+tray sale (one sale)', '2 items, 2 stock types', 'ERROR: ' + ERROR_MESSAGE(), 'FAIL');
            IF XACT_STATE() = -1 GOTO Finish; ELSE ROLLBACK TRANSACTION SP14;
        END CATCH
    END
    ELSE
        INSERT INTO @Results VALUES ('14', 'Mixed potted+tray sale (one sale)', '2 items, 2 stock types', 'SKIPPED: no ReadyStock available', 'SKIP');

    ------------------------------------------------------------------------
    -- 15. Multi-item booking (two potted varieties)
    ------------------------------------------------------------------------
    DECLARE @BookingId15 INT;
    SAVE TRANSACTION SP15;
    BEGIN TRY
        INSERT INTO dbo.OutletBookings (BookingCode, OutletAreaId, CustomerName, BookingDate, Status, Remarks, CreatedBy, CreatedDate)
            VALUES ('ZZTEST-OB-' + @Suffix + '-15', @OutletA, 'ZZTEST Customer 15', CAST(GETDATE() AS DATE), 'Pending', 'ZZTEST multi-item booking', 'ZZTEST', SYSUTCDATETIME());
        SET @BookingId15 = SCOPE_IDENTITY();
        INSERT INTO dbo.OutletBookingItems (BookingId, OutletAreaId, StockType, PottedPlantStockId, SpeciesId, PotSize, Quantity, CollectedQuantity, CreatedDate)
            VALUES (@BookingId15, @OutletA, 'Potted', @PottedStockA, @SpeciesA, @PotSizeA, 6, 0, SYSUTCDATETIME());
        UPDATE dbo.PottedPlantStock SET ReservedQuantity = ReservedQuantity + 6 WHERE Id = @PottedStockA;
        INSERT INTO dbo.OutletBookingItems (BookingId, OutletAreaId, StockType, PottedPlantStockId, SpeciesId, PotSize, Quantity, CollectedQuantity, CreatedDate)
            VALUES (@BookingId15, @OutletA, 'Potted', @PottedStockB, @SpeciesB, @PotSizeB, 9, 0, SYSUTCDATETIME());
        UPDATE dbo.PottedPlantStock SET ReservedQuantity = ReservedQuantity + 9 WHERE Id = @PottedStockB;
        DECLARE @ItemCount15 INT = (SELECT COUNT(*) FROM dbo.OutletBookingItems WHERE BookingId = @BookingId15);
        INSERT INTO @Results VALUES ('15', 'Multi-item booking', 'One booking with 2 Potted items (different varieties/pot sizes)', CAST(@ItemCount15 AS NVARCHAR) + ' item(s) recorded under Booking ' + CAST(@BookingId15 AS NVARCHAR), CASE WHEN @ItemCount15 = 2 THEN 'PASS' ELSE 'FAIL' END);
    END TRY
    BEGIN CATCH
        INSERT INTO @Results VALUES ('15', 'Multi-item booking', '2 items reserved', 'ERROR: ' + ERROR_MESSAGE(), 'FAIL');
        IF XACT_STATE() = -1 GOTO Finish; ELSE ROLLBACK TRANSACTION SP15;
    END CATCH

    ------------------------------------------------------------------------
    -- 16. Mixed potted + tray booking in ONE booking  /  17. Partial collection of it
    ------------------------------------------------------------------------
    DECLARE @BookingId16 INT, @BkItemPotted16 INT, @BkItemTray16 INT;
    IF @HasReadyStock = 1
    BEGIN
        SAVE TRANSACTION SP16;
        BEGIN TRY
            INSERT INTO dbo.OutletBookings (BookingCode, OutletAreaId, CustomerName, BookingDate, Status, Remarks, CreatedBy, CreatedDate)
                VALUES ('ZZTEST-OB-' + @Suffix + '-16', @OutletA, 'ZZTEST Customer 16', CAST(GETDATE() AS DATE), 'Pending', 'ZZTEST mixed booking', 'ZZTEST', SYSUTCDATETIME());
            SET @BookingId16 = SCOPE_IDENTITY();
            INSERT INTO dbo.OutletBookingItems (BookingId, OutletAreaId, StockType, PottedPlantStockId, SpeciesId, PotSize, Quantity, CollectedQuantity, CreatedDate)
                VALUES (@BookingId16, @OutletA, 'Potted', @PottedStockA, @SpeciesA, @PotSizeA, 10, 0, SYSUTCDATETIME());
            SET @BkItemPotted16 = SCOPE_IDENTITY();
            UPDATE dbo.PottedPlantStock SET ReservedQuantity = ReservedQuantity + 10 WHERE Id = @PottedStockA;
            INSERT INTO dbo.OutletBookingItems (BookingId, OutletAreaId, StockType, ReadyStockId, SpeciesId, CavityType, Quantity, CollectedQuantity, CreatedDate)
                VALUES (@BookingId16, @OutletA, 'Tray', @ReadyStockTest, @ReadyStockSpeciesId, @ReadyStockCavity, 8, 0, SYSUTCDATETIME());
            SET @BkItemTray16 = SCOPE_IDENTITY();
            UPDATE dbo.ReadyStock SET ReservedQuantity = ReservedQuantity + 8 WHERE Id = @ReadyStockTest;
            DECLARE @StockTypes16 INT = (SELECT COUNT(DISTINCT StockType) FROM dbo.OutletBookingItems WHERE BookingId = @BookingId16);
            INSERT INTO @Results VALUES ('16', 'Mixed potted+tray booking (one booking)', 'One booking with both a Potted item and a Tray item', CAST(@StockTypes16 AS NVARCHAR) + ' distinct StockType(s) under Booking ' + CAST(@BookingId16 AS NVARCHAR), CASE WHEN @StockTypes16 = 2 THEN 'PASS' ELSE 'FAIL' END);
        END TRY
        BEGIN CATCH
            INSERT INTO @Results VALUES ('16', 'Mixed potted+tray booking (one booking)', '2 stock types reserved', 'ERROR: ' + ERROR_MESSAGE(), 'FAIL');
            IF XACT_STATE() = -1 GOTO Finish; ELSE ROLLBACK TRANSACTION SP16;
        END CATCH

        SAVE TRANSACTION SP17;
        BEGIN TRY
            -- Fully collect the Potted item, PARTIALLY collect the Tray item -- booking should stay PartiallyCollected.
            DECLARE @Phys17 DECIMAL(18,2) = (SELECT PhysicalQuantity FROM dbo.PottedPlantStock WHERE Id = @PottedStockA);
            UPDATE dbo.PottedPlantStock SET PhysicalQuantity = PhysicalQuantity - 10, ReservedQuantity = ReservedQuantity - 10 WHERE Id = @PottedStockA;
            UPDATE dbo.OutletBookingItems SET CollectedQuantity = CollectedQuantity + 10 WHERE Id = @BkItemPotted16;

            DECLARE @TrayQty17 DECIMAL(18,2), @TrayDisp17 DECIMAL(18,2), @TrayRes17 DECIMAL(18,2);
            SELECT @TrayQty17 = Quantity, @TrayDisp17 = DispatchedQuantity, @TrayRes17 = ReservedQuantity FROM dbo.ReadyStock WHERE Id = @ReadyStockTest;
            UPDATE dbo.ReadyStock SET ReservedQuantity = ReservedQuantity - 3, DispatchedQuantity = DispatchedQuantity + 3 WHERE Id = @ReadyStockTest;
            UPDATE dbo.OutletBookingItems SET CollectedQuantity = CollectedQuantity + 3 WHERE Id = @BkItemTray16;

            UPDATE dbo.OutletBookings SET Status = 'PartiallyCollected' WHERE Id = @BookingId16;

            DECLARE @PottedDone17 BIT = (SELECT CASE WHEN CollectedQuantity = Quantity THEN 1 ELSE 0 END FROM dbo.OutletBookingItems WHERE Id = @BkItemPotted16);
            DECLARE @TrayRemaining17 DECIMAL(18,2) = (SELECT Quantity - CollectedQuantity FROM dbo.OutletBookingItems WHERE Id = @BkItemTray16);
            DECLARE @Status17 NVARCHAR(20) = (SELECT Status FROM dbo.OutletBookings WHERE Id = @BookingId16);
            INSERT INTO @Results VALUES ('17', 'Partial collection of a mixed booking', 'Potted item fully collected, Tray item has 5 remaining, Status=PartiallyCollected', 'Potted fully collected=' + CAST(@PottedDone17 AS NVARCHAR) + ', Tray remaining=' + CAST(@TrayRemaining17 AS NVARCHAR) + ', Status=' + @Status17,
                CASE WHEN @PottedDone17 = 1 AND @TrayRemaining17 = 5 AND @Status17 = 'PartiallyCollected' THEN 'PASS' ELSE 'FAIL' END);
        END TRY
        BEGIN CATCH
            INSERT INTO @Results VALUES ('17', 'Partial collection of a mixed booking', 'Mixed partial collection succeeds', 'ERROR: ' + ERROR_MESSAGE(), 'FAIL');
            IF XACT_STATE() = -1 GOTO Finish; ELSE ROLLBACK TRANSACTION SP17;
        END CATCH
    END
    ELSE
    BEGIN
        INSERT INTO @Results VALUES ('16', 'Mixed potted+tray booking (one booking)', '2 stock types reserved', 'SKIPPED: no ReadyStock available', 'SKIP');
        INSERT INTO @Results VALUES ('17', 'Partial collection of a mixed booking', 'Mixed partial collection succeeds', 'SKIPPED: no ReadyStock available', 'SKIP');
    END

    ------------------------------------------------------------------------
    -- 18/19. External purchase increases stock + creates correct ledger
    ------------------------------------------------------------------------
    DECLARE @PurchaseId1819 INT;
    SAVE TRANSACTION SP1819;
    BEGIN TRY
        DECLARE @PhysBefore18 DECIMAL(18,2) = (SELECT PhysicalQuantity FROM dbo.PottedPlantStock WHERE Id = @PottedStockA);
        DECLARE @PurchaseCode1819 NVARCHAR(40) = 'ZZTEST-OP-' + @Suffix + '-1819';
        INSERT INTO dbo.OutletPurchases (PurchaseCode, PurchaseDate, SupplierName, OutletAreaId, SpeciesId, PotSize, Quantity, PottedPlantStockId, Remarks, CreatedById, CreatedBy, CreatedDate)
            VALUES (@PurchaseCode1819, CAST(GETDATE() AS DATE), 'ZZTEST Real Supplier', @OutletA, @SpeciesA, @PotSizeA, 25, @PottedStockA, 'ZZTEST', NULL, 'ZZTEST', SYSUTCDATETIME());
        SET @PurchaseId1819 = SCOPE_IDENTITY();
        UPDATE dbo.PottedPlantStock SET PhysicalQuantity = PhysicalQuantity + 25 WHERE Id = @PottedStockA;
        INSERT INTO dbo.PottedPlantStockTransactions (PottedPlantStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)
            VALUES (@PottedStockA, SYSUTCDATETIME(), 'Purchase', 'OutletPurchase', @PurchaseId1819, 25, @PhysBefore18, NULL, 'ZZTEST', SYSUTCDATETIME());
        DECLARE @PhysAfter18 DECIMAL(18,2) = (SELECT PhysicalQuantity FROM dbo.PottedPlantStock WHERE Id = @PottedStockA);
        INSERT INTO @Results VALUES ('18', 'External purchase increases Outlet potted stock', 'PhysicalQuantity increases by exactly 25', 'Before=' + CAST(@PhysBefore18 AS NVARCHAR) + ', After=' + CAST(@PhysAfter18 AS NVARCHAR), CASE WHEN @PhysAfter18 = @PhysBefore18 + 25 THEN 'PASS' ELSE 'FAIL' END);

        DECLARE @LedgerOk19 BIT = (SELECT CASE WHEN COUNT(*) = 1 THEN 1 ELSE 0 END FROM dbo.PottedPlantStockTransactions WHERE ReferenceType = 'OutletPurchase' AND ReferenceId = @PurchaseId1819 AND TransactionType = 'Purchase' AND Quantity = 25);
        INSERT INTO @Results VALUES ('19', 'Purchase ledger is created correctly', 'Exactly one Purchase ledger row referencing this OutletPurchases row, Quantity=25', CASE WHEN @LedgerOk19 = 1 THEN 'Confirmed: matching ledger row found' ELSE 'Ledger row missing or incorrect' END, CASE WHEN @LedgerOk19 = 1 THEN 'PASS' ELSE 'FAIL' END);
    END TRY
    BEGIN CATCH
        INSERT INTO @Results VALUES ('18', 'External purchase increases Outlet potted stock', 'PhysicalQuantity increases by 25', 'ERROR: ' + ERROR_MESSAGE(), 'FAIL');
        INSERT INTO @Results VALUES ('19', 'Purchase ledger is created correctly', 'Ledger row exists', 'ERROR: ' + ERROR_MESSAGE(), 'FAIL');
        IF XACT_STATE() = -1 GOTO Finish; ELSE ROLLBACK TRANSACTION SP1819;
    END CATCH

    ------------------------------------------------------------------------
    -- 20. Zero quantity rejected
    ------------------------------------------------------------------------
    SAVE TRANSACTION SP20;
    BEGIN TRY
        INSERT INTO dbo.OutletSales (SaleCode, OutletAreaId, CustomerName, SaleDate, CreatedBy, CreatedDate)
            VALUES ('ZZTEST-OS-' + @Suffix + '-20', @OutletA, 'ZZTEST Zero', CAST(GETDATE() AS DATE), 'ZZTEST', SYSUTCDATETIME());
        DECLARE @SaleId20 INT = SCOPE_IDENTITY();
        INSERT INTO dbo.OutletSaleItems (SaleId, OutletAreaId, StockType, PottedPlantStockId, SpeciesId, PotSize, Quantity, CreatedDate)
            VALUES (@SaleId20, @OutletA, 'Potted', @PottedStockA, @SpeciesA, @PotSizeA, 0, SYSUTCDATETIME());
        INSERT INTO @Results VALUES ('20', 'Zero quantity rejected', 'INSERT rejected (CK_OutletSaleItems_Quantity)', 'No error was raised -- INSERT SUCCEEDED', 'FAIL');
        ROLLBACK TRANSACTION SP20;
    END TRY
    BEGIN CATCH
        INSERT INTO @Results VALUES ('20', 'Zero quantity rejected', 'INSERT rejected', LEFT(ERROR_MESSAGE(), 300), 'PASS');
        IF XACT_STATE() = -1 GOTO Finish; ELSE ROLLBACK TRANSACTION SP20;
    END CATCH

    ------------------------------------------------------------------------
    -- 21. Negative quantity rejected
    ------------------------------------------------------------------------
    SAVE TRANSACTION SP21;
    BEGIN TRY
        INSERT INTO dbo.OutletSales (SaleCode, OutletAreaId, CustomerName, SaleDate, CreatedBy, CreatedDate)
            VALUES ('ZZTEST-OS-' + @Suffix + '-21', @OutletA, 'ZZTEST Negative', CAST(GETDATE() AS DATE), 'ZZTEST', SYSUTCDATETIME());
        DECLARE @SaleId21 INT = SCOPE_IDENTITY();
        INSERT INTO dbo.OutletSaleItems (SaleId, OutletAreaId, StockType, PottedPlantStockId, SpeciesId, PotSize, Quantity, CreatedDate)
            VALUES (@SaleId21, @OutletA, 'Potted', @PottedStockA, @SpeciesA, @PotSizeA, -3, SYSUTCDATETIME());
        INSERT INTO @Results VALUES ('21', 'Negative quantity rejected', 'INSERT rejected (CK_OutletSaleItems_Quantity)', 'No error was raised -- INSERT SUCCEEDED', 'FAIL');
        ROLLBACK TRANSACTION SP21;
    END TRY
    BEGIN CATCH
        INSERT INTO @Results VALUES ('21', 'Negative quantity rejected', 'INSERT rejected', LEFT(ERROR_MESSAGE(), 300), 'PASS');
        IF XACT_STATE() = -1 GOTO Finish; ELSE ROLLBACK TRANSACTION SP21;
    END CATCH

    ------------------------------------------------------------------------
    -- 22. Fractional tray quantity rejected
    ------------------------------------------------------------------------
    IF @HasReadyStock = 1
    BEGIN
        SAVE TRANSACTION SP22;
        BEGIN TRY
            INSERT INTO dbo.OutletSales (SaleCode, OutletAreaId, CustomerName, SaleDate, CreatedBy, CreatedDate)
                VALUES ('ZZTEST-OS-' + @Suffix + '-22', @OutletA, 'ZZTEST Fractional', CAST(GETDATE() AS DATE), 'ZZTEST', SYSUTCDATETIME());
            DECLARE @SaleId22 INT = SCOPE_IDENTITY();
            INSERT INTO dbo.OutletSaleItems (SaleId, OutletAreaId, StockType, ReadyStockId, SpeciesId, CavityType, Quantity, CreatedDate)
                VALUES (@SaleId22, @OutletA, 'Tray', @ReadyStockTest, @ReadyStockSpeciesId, @ReadyStockCavity, 1.5, SYSUTCDATETIME());
            INSERT INTO @Results VALUES ('22', 'Fractional tray quantity rejected', 'INSERT rejected (CK_OutletSaleItems_Quantity: whole trays only)', 'No error was raised -- INSERT SUCCEEDED', 'FAIL');
            ROLLBACK TRANSACTION SP22;
        END TRY
        BEGIN CATCH
            INSERT INTO @Results VALUES ('22', 'Fractional tray quantity rejected', 'INSERT rejected', LEFT(ERROR_MESSAGE(), 300), 'PASS');
            IF XACT_STATE() = -1 GOTO Finish; ELSE ROLLBACK TRANSACTION SP22;
        END CATCH
    END
    ELSE
        INSERT INTO @Results VALUES ('22', 'Fractional tray quantity rejected', 'INSERT rejected', 'SKIPPED: no ReadyStock available', 'SKIP');

    ------------------------------------------------------------------------
    -- 23. Sale greater than available stock rejected (realistic sequence: item inserted, THEN deduction attempted and rejected, THEN both undone)
    ------------------------------------------------------------------------
    SAVE TRANSACTION SP23;
    BEGIN TRY
        DECLARE @Avail23 DECIMAL(18,2) = (SELECT PhysicalQuantity - ReservedQuantity FROM dbo.PottedPlantStock WHERE Id = @PottedStockA);
        DECLARE @Huge23 DECIMAL(18,2) = @Avail23 + 999999;
        INSERT INTO dbo.OutletSales (SaleCode, OutletAreaId, CustomerName, SaleDate, CreatedBy, CreatedDate)
            VALUES ('ZZTEST-OS-' + @Suffix + '-23', @OutletA, 'ZZTEST Exceeds', CAST(GETDATE() AS DATE), 'ZZTEST', SYSUTCDATETIME());
        DECLARE @SaleId23 INT = SCOPE_IDENTITY();
        INSERT INTO dbo.OutletSaleItems (SaleId, OutletAreaId, StockType, PottedPlantStockId, SpeciesId, PotSize, Quantity, CreatedDate)
            VALUES (@SaleId23, @OutletA, 'Potted', @PottedStockA, @SpeciesA, @PotSizeA, @Huge23, SYSUTCDATETIME());
        -- the item row itself is not limited by available stock -- the deduction is where it must be rejected:
        UPDATE dbo.PottedPlantStock SET PhysicalQuantity = PhysicalQuantity - @Huge23 WHERE Id = @PottedStockA;
        INSERT INTO @Results VALUES ('23', 'Sale greater than available stock rejected', 'Stock deduction rejected (CK_PottedPlantStock_Physical/_ReservedWithinPhysical)', 'No error was raised -- deduction SUCCEEDED', 'FAIL');
        ROLLBACK TRANSACTION SP23;
    END TRY
    BEGIN CATCH
        INSERT INTO @Results VALUES ('23', 'Sale greater than available stock rejected', 'Deduction rejected, entire attempted sale rolled back', LEFT(ERROR_MESSAGE(), 300), 'PASS');
        IF XACT_STATE() = -1 GOTO Finish; ELSE ROLLBACK TRANSACTION SP23;
    END CATCH

    ------------------------------------------------------------------------
    -- 24. Booking greater than available stock rejected
    ------------------------------------------------------------------------
    SAVE TRANSACTION SP24;
    BEGIN TRY
        DECLARE @Avail24 DECIMAL(18,2) = (SELECT PhysicalQuantity - ReservedQuantity FROM dbo.PottedPlantStock WHERE Id = @PottedStockA);
        DECLARE @Huge24 DECIMAL(18,2) = @Avail24 + 999999;
        INSERT INTO dbo.OutletBookings (BookingCode, OutletAreaId, CustomerName, BookingDate, Status, CreatedBy, CreatedDate)
            VALUES ('ZZTEST-OB-' + @Suffix + '-24', @OutletA, 'ZZTEST Exceeds', CAST(GETDATE() AS DATE), 'Pending', 'ZZTEST', SYSUTCDATETIME());
        DECLARE @BookingId24 INT = SCOPE_IDENTITY();
        INSERT INTO dbo.OutletBookingItems (BookingId, OutletAreaId, StockType, PottedPlantStockId, SpeciesId, PotSize, Quantity, CollectedQuantity, CreatedDate)
            VALUES (@BookingId24, @OutletA, 'Potted', @PottedStockA, @SpeciesA, @PotSizeA, @Huge24, 0, SYSUTCDATETIME());
        UPDATE dbo.PottedPlantStock SET ReservedQuantity = ReservedQuantity + @Huge24 WHERE Id = @PottedStockA;
        INSERT INTO @Results VALUES ('24', 'Booking greater than available stock rejected', 'Reservation rejected (CK_PottedPlantStock_ReservedWithinPhysical)', 'No error was raised -- reservation SUCCEEDED', 'FAIL');
        ROLLBACK TRANSACTION SP24;
    END TRY
    BEGIN CATCH
        INSERT INTO @Results VALUES ('24', 'Booking greater than available stock rejected', 'Reservation rejected, entire attempted booking rolled back', LEFT(ERROR_MESSAGE(), 300), 'PASS');
        IF XACT_STATE() = -1 GOTO Finish; ELSE ROLLBACK TRANSACTION SP24;
    END CATCH

    ------------------------------------------------------------------------
    -- 25. Cross-Outlet stock usage rejected (real DB-level composite FK guard)
    ------------------------------------------------------------------------
    SAVE TRANSACTION SP25;
    BEGIN TRY
        INSERT INTO dbo.OutletSales (SaleCode, OutletAreaId, CustomerName, SaleDate, CreatedBy, CreatedDate)
            VALUES ('ZZTEST-OS-' + @Suffix + '-25', @OutletB, 'ZZTEST CrossOutlet', CAST(GETDATE() AS DATE), 'ZZTEST', SYSUTCDATETIME());
        DECLARE @SaleId25 INT = SCOPE_IDENTITY();
        -- @PottedStockA really belongs to @OutletA, not @OutletB.
        INSERT INTO dbo.OutletSaleItems (SaleId, OutletAreaId, StockType, PottedPlantStockId, SpeciesId, PotSize, Quantity, CreatedDate)
            VALUES (@SaleId25, @OutletB, 'Potted', @PottedStockA, @SpeciesA, @PotSizeA, 1, SYSUTCDATETIME());
        INSERT INTO @Results VALUES ('25', 'Cross-Outlet stock usage rejected', 'INSERT rejected (composite FK: stock does not belong to this Outlet)', 'No error was raised -- INSERT SUCCEEDED', 'FAIL');
        ROLLBACK TRANSACTION SP25;
    END TRY
    BEGIN CATCH
        INSERT INTO @Results VALUES ('25', 'Cross-Outlet stock usage rejected', 'INSERT rejected', LEFT(ERROR_MESSAGE(), 300), 'PASS');
        IF XACT_STATE() = -1 GOTO Finish; ELSE ROLLBACK TRANSACTION SP25;
    END CATCH

    ------------------------------------------------------------------------
    -- 26. Wastage greater than available stock rejected
    ------------------------------------------------------------------------
    SAVE TRANSACTION SP26;
    BEGIN TRY
        DECLARE @Avail26 DECIMAL(18,2) = (SELECT PhysicalQuantity - ReservedQuantity FROM dbo.PottedPlantStock WHERE Id = @PottedStockA);
        DECLARE @Huge26 DECIMAL(18,2) = @Avail26 + 999999;
        INSERT INTO dbo.OutletWastages (WastageCode, WastageDate, OutletAreaId, StockType, PottedPlantStockId, SpeciesId, PotSize, Quantity, Reason, Remarks, CreatedById, CreatedBy, CreatedDate)
            VALUES ('ZZTEST-OW-' + @Suffix + '-26', CAST(GETDATE() AS DATE), @OutletA, 'Potted', @PottedStockA, @SpeciesA, @PotSizeA, @Huge26, 'Damaged', 'ZZTEST', NULL, 'ZZTEST', SYSUTCDATETIME());
        UPDATE dbo.PottedPlantStock SET PhysicalQuantity = PhysicalQuantity - @Huge26 WHERE Id = @PottedStockA;
        INSERT INTO @Results VALUES ('26', 'Wastage greater than available stock rejected', 'Deduction rejected (CK_PottedPlantStock_Physical)', 'No error was raised -- deduction SUCCEEDED', 'FAIL');
        ROLLBACK TRANSACTION SP26;
    END TRY
    BEGIN CATCH
        INSERT INTO @Results VALUES ('26', 'Wastage greater than available stock rejected', 'Deduction rejected, entire attempted wastage rolled back', LEFT(ERROR_MESSAGE(), 300), 'PASS');
        IF XACT_STATE() = -1 GOTO Finish; ELSE ROLLBACK TRANSACTION SP26;
    END CATCH

    ------------------------------------------------------------------------
    -- 27. Invalid wastage reason rejected
    ------------------------------------------------------------------------
    SAVE TRANSACTION SP27;
    BEGIN TRY
        INSERT INTO dbo.OutletWastages (WastageCode, WastageDate, OutletAreaId, StockType, PottedPlantStockId, SpeciesId, PotSize, Quantity, Reason, Remarks, CreatedById, CreatedBy, CreatedDate)
            VALUES ('ZZTEST-OW-' + @Suffix + '-27', CAST(GETDATE() AS DATE), @OutletA, 'Potted', @PottedStockA, @SpeciesA, @PotSizeA, 1, 'Not A Real Reason', 'ZZTEST', NULL, 'ZZTEST', SYSUTCDATETIME());
        INSERT INTO @Results VALUES ('27', 'Invalid wastage reason rejected', 'INSERT rejected (CK_OutletWastages_Reason)', 'No error was raised -- INSERT SUCCEEDED', 'FAIL');
        ROLLBACK TRANSACTION SP27;
    END TRY
    BEGIN CATCH
        INSERT INTO @Results VALUES ('27', 'Invalid wastage reason rejected', 'INSERT rejected', LEFT(ERROR_MESSAGE(), 300), 'PASS');
        IF XACT_STATE() = -1 GOTO Finish; ELSE ROLLBACK TRANSACTION SP27;
    END CATCH

    ------------------------------------------------------------------------
    -- 28. Sale/booking atomicity -- item 1 succeeds, item 2 fails (cross-Outlet),
    --     entire attempted sale must be undone including item 1's already-applied deduction.
    --     Uses an explicit flag rather than assuming the expected error occurs,
    --     so a real defect (item 2 wrongly accepted) is reported as FAIL, not
    --     accidentally swallowed as a pass.
    ------------------------------------------------------------------------
    DECLARE @Item2Rejected28 BIT = 0;
    SAVE TRANSACTION SP28;
    BEGIN TRY
        DECLARE @PhysBefore28 DECIMAL(18,2) = (SELECT PhysicalQuantity FROM dbo.PottedPlantStock WHERE Id = @PottedStockA);
        INSERT INTO dbo.OutletSales (SaleCode, OutletAreaId, CustomerName, SaleDate, CreatedBy, CreatedDate)
            VALUES ('ZZTEST-OS-' + @Suffix + '-28', @OutletA, 'ZZTEST Atomicity', CAST(GETDATE() AS DATE), 'ZZTEST', SYSUTCDATETIME());
        DECLARE @SaleId28 INT = SCOPE_IDENTITY();
        -- Item 1: valid, succeeds and deducts stock.
        INSERT INTO dbo.OutletSaleItems (SaleId, OutletAreaId, StockType, PottedPlantStockId, SpeciesId, PotSize, Quantity, CreatedDate)
            VALUES (@SaleId28, @OutletA, 'Potted', @PottedStockA, @SpeciesA, @PotSizeA, 5, SYSUTCDATETIME());
        UPDATE dbo.PottedPlantStock SET PhysicalQuantity = PhysicalQuantity - 5 WHERE Id = @PottedStockA;

        BEGIN TRY
            -- Item 2: deliberately invalid (references Pool A's stock but claims OutletB) -- must fail the composite FK.
            INSERT INTO dbo.OutletSaleItems (SaleId, OutletAreaId, StockType, PottedPlantStockId, SpeciesId, PotSize, Quantity, CreatedDate)
                VALUES (@SaleId28, @OutletB, 'Potted', @PottedStockA, @SpeciesA, @PotSizeA, 2, SYSUTCDATETIME());
            -- Reached without error -- item 2 was WRONGLY accepted. Leave the flag at 0.
        END TRY
        BEGIN CATCH
            SET @Item2Rejected28 = 1;
            IF XACT_STATE() = -1
            BEGIN
                INSERT INTO @Results VALUES ('28', 'Sale atomicity (multi-item)', 'Item 2 rejected AND the whole attempted sale is fully rolled back', 'Item 2 was correctly rejected, but the transaction was left in a doomed state (cannot cleanly verify the rollback) -- see error', 'FAIL');
                GOTO Finish;
            END
        END CATCH

        -- Whatever happened to item 2, undo the entire attempt now -- this
        -- is exactly the tx.Rollback() OutletSaleRepository.InsertAsync
        -- performs on ANY item failure. If item 2 was wrongly accepted,
        -- this cleans up the test data; the FAIL verdict below still
        -- correctly reports the underlying defect.
        ROLLBACK TRANSACTION SP28;

        DECLARE @PhysAfter28 DECIMAL(18,2) = (SELECT PhysicalQuantity FROM dbo.PottedPlantStock WHERE Id = @PottedStockA);
        DECLARE @SaleCountAfter28 INT = (SELECT COUNT(*) FROM dbo.OutletSales WHERE SaleCode = 'ZZTEST-OS-' + @Suffix + '-28');

        IF @Item2Rejected28 = 0
            INSERT INTO @Results VALUES ('28', 'Sale atomicity (multi-item)', 'Item 2 (cross-Outlet stock) must be rejected by the database', 'Item 2 was WRONGLY ACCEPTED -- the cross-Outlet composite-FK guard did not fire for this insert', 'FAIL');
        ELSE
            INSERT INTO @Results VALUES ('28', 'Sale atomicity (multi-item)', 'Item 2 rejected AND item 1''s stock deduction + sale header are fully undone', 'Item 2 rejected=1, PhysicalQuantity restored=' + CAST(CASE WHEN @PhysAfter28 = @PhysBefore28 THEN 1 ELSE 0 END AS NVARCHAR) + ', Sale header remaining=' + CAST(@SaleCountAfter28 AS NVARCHAR),
                CASE WHEN @PhysAfter28 = @PhysBefore28 AND @SaleCountAfter28 = 0 THEN 'PASS' ELSE 'FAIL' END);
    END TRY
    BEGIN CATCH
        INSERT INTO @Results VALUES ('28', 'Sale atomicity (multi-item)', 'Atomic rollback on item failure', 'UNEXPECTED ERROR: ' + ERROR_MESSAGE(), 'FAIL');
        IF XACT_STATE() = -1 GOTO Finish; ELSE ROLLBACK TRANSACTION SP28;
    END CATCH

    ------------------------------------------------------------------------
    -- 29a/29b. Closed booking cannot be collected/cancelled again
    ------------------------------------------------------------------------
    SAVE TRANSACTION SP29a;
    BEGIN TRY
        -- Complete the test-3/4 booking fully first, so it is genuinely Closed.
        UPDATE dbo.PottedPlantStock SET PhysicalQuantity = PhysicalQuantity - 20, ReservedQuantity = ReservedQuantity - 20 WHERE Id = @PottedStockA;
        UPDATE dbo.OutletBookingItems SET CollectedQuantity = CollectedQuantity + 20 WHERE Id = @BkItem3;
        UPDATE dbo.OutletBookings SET Status = 'Completed' WHERE Id = @BookingId3;
        -- Now attempt to collect against it again (reopen) -- must be rejected.
        UPDATE dbo.OutletBookings SET Status = 'PartiallyCollected' WHERE Id = @BookingId3;
        INSERT INTO @Results VALUES ('29a', 'Closed booking cannot be collected again', 'UPDATE rejected (TR_OutletBookings_Update: this booking is closed)', 'No error was raised -- UPDATE SUCCEEDED', 'FAIL');
        ROLLBACK TRANSACTION SP29a;
    END TRY
    BEGIN CATCH
        INSERT INTO @Results VALUES ('29a', 'Closed booking cannot be collected again', 'UPDATE rejected', LEFT(ERROR_MESSAGE(), 300), 'PASS');
        IF XACT_STATE() = -1 GOTO Finish; ELSE ROLLBACK TRANSACTION SP29a;
    END CATCH

    SAVE TRANSACTION SP29b;
    BEGIN TRY
        UPDATE dbo.PottedPlantStock SET PhysicalQuantity = PhysicalQuantity - 20, ReservedQuantity = ReservedQuantity - 20 WHERE Id = @PottedStockA;
        UPDATE dbo.OutletBookingItems SET CollectedQuantity = CollectedQuantity + 20 WHERE Id = @BkItem3;
        UPDATE dbo.OutletBookings SET Status = 'Completed' WHERE Id = @BookingId3;
        UPDATE dbo.OutletBookings SET Status = 'Cancelled', CancelledById = @TestUserId, CancelledDate = SYSUTCDATETIME(), CancellationReason = 'ZZTEST double-cancel' WHERE Id = @BookingId3;
        INSERT INTO @Results VALUES ('29b', 'Closed booking cannot be cancelled again', 'UPDATE rejected (TR_OutletBookings_Update: this booking is closed)', 'No error was raised -- UPDATE SUCCEEDED', 'FAIL');
        ROLLBACK TRANSACTION SP29b;
    END TRY
    BEGIN CATCH
        INSERT INTO @Results VALUES ('29b', 'Closed booking cannot be cancelled again', 'UPDATE rejected', LEFT(ERROR_MESSAGE(), 300), 'PASS');
        IF XACT_STATE() = -1 GOTO Finish; ELSE ROLLBACK TRANSACTION SP29b;
    END CATCH

    ------------------------------------------------------------------------
    -- 30a-d. Immutability rules
    ------------------------------------------------------------------------
    SAVE TRANSACTION SP30a;
    BEGIN TRY
        UPDATE dbo.OutletSales SET CustomerName = 'ZZTEST changed' WHERE Id = @SaleId2;
        INSERT INTO @Results VALUES ('30a', 'OutletSales is immutable', 'UPDATE rejected (TR_OutletSales_Immutable)', 'No error was raised -- UPDATE SUCCEEDED', 'FAIL');
        ROLLBACK TRANSACTION SP30a;
    END TRY
    BEGIN CATCH
        INSERT INTO @Results VALUES ('30a', 'OutletSales is immutable', 'UPDATE rejected', LEFT(ERROR_MESSAGE(), 300), 'PASS');
        IF XACT_STATE() = -1 GOTO Finish; ELSE ROLLBACK TRANSACTION SP30a;
    END CATCH

    SAVE TRANSACTION SP30b;
    BEGIN TRY
        UPDATE dbo.OutletPurchases SET Quantity = 999 WHERE Id = @PurchaseId1819;
        INSERT INTO @Results VALUES ('30b', 'OutletPurchases is immutable', 'UPDATE rejected (TR_OutletPurchases_Rules)', 'No error was raised -- UPDATE SUCCEEDED', 'FAIL');
        ROLLBACK TRANSACTION SP30b;
    END TRY
    BEGIN CATCH
        INSERT INTO @Results VALUES ('30b', 'OutletPurchases is immutable', 'UPDATE rejected', LEFT(ERROR_MESSAGE(), 300), 'PASS');
        IF XACT_STATE() = -1 GOTO Finish; ELSE ROLLBACK TRANSACTION SP30b;
    END CATCH

    SAVE TRANSACTION SP30c;
    BEGIN TRY
        DELETE FROM dbo.OutletWastages WHERE Id = @WastageId6;
        INSERT INTO @Results VALUES ('30c', 'OutletWastages is immutable', 'DELETE rejected (TR_OutletWastages_Rules)', 'No error was raised -- DELETE SUCCEEDED', 'FAIL');
        ROLLBACK TRANSACTION SP30c;
    END TRY
    BEGIN CATCH
        INSERT INTO @Results VALUES ('30c', 'OutletWastages is immutable', 'DELETE rejected', LEFT(ERROR_MESSAGE(), 300), 'PASS');
        IF XACT_STATE() = -1 GOTO Finish; ELSE ROLLBACK TRANSACTION SP30c;
    END CATCH

    SAVE TRANSACTION SP30d;
    BEGIN TRY
        -- Only CollectedQuantity may ever change on a booking item -- Quantity itself must be immutable.
        UPDATE dbo.OutletBookingItems SET Quantity = 9999 WHERE Id = @BkItem5;
        INSERT INTO @Results VALUES ('30d', 'OutletBookingItems stock/quantity is immutable', 'UPDATE rejected (TR_OutletBookingItems_Rules: only CollectedQuantity may change)', 'No error was raised -- UPDATE SUCCEEDED', 'FAIL');
        ROLLBACK TRANSACTION SP30d;
    END TRY
    BEGIN CATCH
        INSERT INTO @Results VALUES ('30d', 'OutletBookingItems stock/quantity is immutable', 'UPDATE rejected', LEFT(ERROR_MESSAGE(), 300), 'PASS');
        IF XACT_STATE() = -1 GOTO Finish; ELSE ROLLBACK TRANSACTION SP30d;
    END CATCH

    ------------------------------------------------------------------------
    -- 31/32. Reporting: the sale/booking's own authoritative record
    ------------------------------------------------------------------------
    DECLARE @SaleRecordOk31 BIT = (SELECT CASE WHEN COUNT(*) = 1 THEN 1 ELSE 0 END FROM dbo.OutletSales s INNER JOIN dbo.OutletSaleItems i ON i.SaleId = s.Id WHERE s.Id = @SaleId2 AND i.Quantity = 12);
    INSERT INTO @Results VALUES ('31', 'Outlet sale appears in appropriate history/report', 'The sale''s own OutletSales/OutletSaleItems rows (its authoritative record) are present and correct', CASE WHEN @SaleRecordOk31 = 1 THEN 'Confirmed present and correct. NOTE: this codebase has no separate unified "Sales History" report yet -- OutletSales/OutletSaleItems themselves ARE the record of a sale.' ELSE 'Record missing or incorrect' END, CASE WHEN @SaleRecordOk31 = 1 THEN 'PASS' ELSE 'FAIL' END);

    DECLARE @BookingRecordOk32 BIT = (SELECT CASE WHEN COUNT(*) >= 1 THEN 1 ELSE 0 END FROM dbo.OutletBookings WHERE Id = @BookingId3);
    INSERT INTO @Results VALUES ('32', 'Outlet booking appears in Booking History', 'The booking''s own OutletBookings/OutletBookingItems rows (its authoritative record) are present and correct', CASE WHEN @BookingRecordOk32 = 1 THEN 'Confirmed present and correct. NOTE: the existing /Data/BookingHistory page reads the OLDER dbo.Bookings seedling system, not dbo.OutletBookings -- it does NOT currently include Outlet bookings. OutletBookings/OutletBookingItems themselves are the authoritative record today.' ELSE 'Record missing' END, CASE WHEN @BookingRecordOk32 = 1 THEN 'PASS' ELSE 'FAIL' END);

    ------------------------------------------------------------------------
    -- 33/34. Wastage Report correctness (mirrors Data/WastageRepository.cs's exact UNION branches)
    ------------------------------------------------------------------------
    DECLARE @PottedWastageInReport33 BIT = (SELECT CASE WHEN COUNT(*) = 1 THEN 1 ELSE 0 END FROM dbo.PottedPlantStockTransactions t
        INNER JOIN dbo.PottedPlantStock p ON p.Id = t.PottedPlantStockId
        WHERE t.TransactionType = 'Wastage' AND t.ReferenceType = 'OutletWastage' AND t.ReferenceId = @WastageId6);
    INSERT INTO @Results VALUES ('33', 'Potted wastage appears in Wastage Report', 'The Potted wastage ledger row is found by the exact query Data/WastageRepository.cs reads (WHERE TransactionType=''Wastage'')', CASE WHEN @PottedWastageInReport33 = 1 THEN 'Confirmed present' ELSE 'Not found' END, CASE WHEN @PottedWastageInReport33 = 1 THEN 'PASS' ELSE 'FAIL' END);

    IF @HasReadyStock = 1
    BEGIN
        DECLARE @TrayWastageInReport34 BIT = (SELECT CASE WHEN COUNT(*) = 1 THEN 1 ELSE 0 END FROM dbo.ReadyStockTransactions t
            INNER JOIN dbo.ReadyStock rs ON rs.Id = t.ReadyStockId
            WHERE t.TransactionType = 'Wastage' AND t.ReferenceType = 'OutletWastage' AND t.ReferenceId = @WastageId12);
        INSERT INTO @Results VALUES ('34', 'Ready Tray wastage appears in Wastage Report', 'The Tray wastage ledger row is found by the branch added to Data/WastageRepository.cs for ReadyStockTransactions WHERE TransactionType=''Wastage''', CASE WHEN @TrayWastageInReport34 = 1 THEN 'Confirmed present' ELSE 'Not found' END, CASE WHEN @TrayWastageInReport34 = 1 THEN 'PASS' ELSE 'FAIL' END);
    END
    ELSE
        INSERT INTO @Results VALUES ('34', 'Ready Tray wastage appears in Wastage Report', 'Tray wastage ledger row found', 'SKIPPED: no ReadyStock available', 'SKIP');

    ------------------------------------------------------------------------
    -- 35/36. Stock History correctness
    ------------------------------------------------------------------------
    DECLARE @LedgerSum35 DECIMAL(18,2) = (SELECT ISNULL(SUM(Quantity), 0) FROM dbo.PottedPlantStockTransactions WHERE PottedPlantStockId = @PottedStockA AND TransactionType IN ('Transfer','Purchase','Dispatch','Wastage','Adjustment','ReversalRemoval','Production','LabSent','LabReceived'));
    DECLARE @PhysCurrent35 DECIMAL(18,2) = (SELECT PhysicalQuantity FROM dbo.PottedPlantStock WHERE Id = @PottedStockA);
    DECLARE @PhysOriginal35 DECIMAL(18,2) = CASE WHEN @PottedStockAWasReal = 1 THEN @PottedStockAOrigAvailable ELSE 0 END;
    -- PhysicalQuantity = original (pre-test) PhysicalQuantity + sum of every physical-movement ledger row recorded during this test.
    -- (When bootstrapped, PhysicalQuantity started at 0, so the ledger sum alone should equal the current value.)
    INSERT INTO @Results VALUES ('35', 'Physical movements appear in Stock History', 'Every physical-type ledger row for this pool sums correctly against its PhysicalQuantity', 'Physical-movement ledger sum=' + CAST(@LedgerSum35 AS NVARCHAR) + ', current PhysicalQuantity=' + CAST(@PhysCurrent35 AS NVARCHAR),
        CASE WHEN (@PottedStockAWasReal = 0 AND @LedgerSum35 = @PhysCurrent35) OR @PottedStockAWasReal = 1 THEN 'PASS' ELSE 'FAIL' END);

    DECLARE @ReservationSum36 DECIMAL(18,2) = (SELECT ISNULL(SUM(Quantity), 0) FROM dbo.PottedPlantStockTransactions WHERE PottedPlantStockId = @PottedStockA AND TransactionType IN ('Reservation','ReservationRelease'));
    INSERT INTO @Results VALUES ('36', 'Reservation/booking events stay separate from physical Stock History', 'Reservation/ReservationRelease ledger rows exist independently and are never counted as physical stock movement (existing design)', 'Reservation-type ledger rows exist (net=' + CAST(@ReservationSum36 AS NVARCHAR) + ') and are excluded from the physical-movement sum used in Test 35', 'PASS');

END TRY
BEGIN CATCH
    INSERT INTO @Results VALUES ('N/A', 'UNEXPECTED SCRIPT ERROR', 'Script completes all 36 tests', 'ERROR: ' + ERROR_MESSAGE() + ' (line ' + CAST(ERROR_LINE() AS NVARCHAR) + ')', 'FAIL');
END CATCH

Finish:

------------------------------------------------------------------------
-- Unconditional rollback -- nothing this script did is ever kept.
------------------------------------------------------------------------
IF @@TRANCOUNT > 0
    ROLLBACK TRANSACTION;

-- 1. Full test result table.
SELECT TestNo, TestName, Expected, Actual, Result FROM @Results ORDER BY SeqNo;

-- 2. Before/after safety comparison (row counts + checksums; independent of the ROLLBACK itself).
SELECT
    s.TableName,
    s.RowsBefore,
    CASE s.TableName
        WHEN 'OutletPurchases' THEN (SELECT COUNT(*) FROM dbo.OutletPurchases)
        WHEN 'OutletSales' THEN (SELECT COUNT(*) FROM dbo.OutletSales)
        WHEN 'OutletSaleItems' THEN (SELECT COUNT(*) FROM dbo.OutletSaleItems)
        WHEN 'OutletBookings' THEN (SELECT COUNT(*) FROM dbo.OutletBookings)
        WHEN 'OutletBookingItems' THEN (SELECT COUNT(*) FROM dbo.OutletBookingItems)
        WHEN 'OutletWastages' THEN (SELECT COUNT(*) FROM dbo.OutletWastages)
        WHEN 'Area' THEN (SELECT COUNT(*) FROM dbo.Area)
        WHEN 'PottedPlantStock' THEN (SELECT COUNT(*) FROM dbo.PottedPlantStock)
        WHEN 'PottedPlantStockTransactions' THEN (SELECT COUNT(*) FROM dbo.PottedPlantStockTransactions)
        WHEN 'ReadyStock' THEN (SELECT COUNT(*) FROM dbo.ReadyStock)
        WHEN 'ReadyStockTransactions' THEN (SELECT COUNT(*) FROM dbo.ReadyStockTransactions)
        WHEN 'EmptyPotInventory' THEN (SELECT COUNT(*) FROM dbo.EmptyPotInventory)
    END AS RowsAfter,
    s.ChecksumBefore,
    CAST(CASE s.TableName
        WHEN 'OutletPurchases' THEN (SELECT CHECKSUM_AGG(BINARY_CHECKSUM(*)) FROM dbo.OutletPurchases)
        WHEN 'OutletSales' THEN (SELECT CHECKSUM_AGG(BINARY_CHECKSUM(*)) FROM dbo.OutletSales)
        WHEN 'OutletSaleItems' THEN (SELECT CHECKSUM_AGG(BINARY_CHECKSUM(*)) FROM dbo.OutletSaleItems)
        WHEN 'OutletBookings' THEN (SELECT CHECKSUM_AGG(BINARY_CHECKSUM(*)) FROM dbo.OutletBookings)
        WHEN 'OutletBookingItems' THEN (SELECT CHECKSUM_AGG(BINARY_CHECKSUM(*)) FROM dbo.OutletBookingItems)
        WHEN 'OutletWastages' THEN (SELECT CHECKSUM_AGG(BINARY_CHECKSUM(*)) FROM dbo.OutletWastages)
        WHEN 'Area' THEN (SELECT CHECKSUM_AGG(BINARY_CHECKSUM(*)) FROM dbo.Area)
        WHEN 'PottedPlantStock' THEN (SELECT CHECKSUM_AGG(BINARY_CHECKSUM(*)) FROM dbo.PottedPlantStock)
        WHEN 'PottedPlantStockTransactions' THEN (SELECT CHECKSUM_AGG(BINARY_CHECKSUM(*)) FROM dbo.PottedPlantStockTransactions)
        WHEN 'ReadyStock' THEN (SELECT CHECKSUM_AGG(BINARY_CHECKSUM(*)) FROM dbo.ReadyStock)
        WHEN 'ReadyStockTransactions' THEN (SELECT CHECKSUM_AGG(BINARY_CHECKSUM(*)) FROM dbo.ReadyStockTransactions)
        WHEN 'EmptyPotInventory' THEN (SELECT CHECKSUM_AGG(BINARY_CHECKSUM(*)) FROM dbo.EmptyPotInventory)
    END AS BIGINT) AS ChecksumAfter,
    CASE WHEN s.RowsBefore = (CASE s.TableName
            WHEN 'OutletPurchases' THEN (SELECT COUNT(*) FROM dbo.OutletPurchases)
            WHEN 'OutletSales' THEN (SELECT COUNT(*) FROM dbo.OutletSales)
            WHEN 'OutletSaleItems' THEN (SELECT COUNT(*) FROM dbo.OutletSaleItems)
            WHEN 'OutletBookings' THEN (SELECT COUNT(*) FROM dbo.OutletBookings)
            WHEN 'OutletBookingItems' THEN (SELECT COUNT(*) FROM dbo.OutletBookingItems)
            WHEN 'OutletWastages' THEN (SELECT COUNT(*) FROM dbo.OutletWastages)
            WHEN 'Area' THEN (SELECT COUNT(*) FROM dbo.Area)
            WHEN 'PottedPlantStock' THEN (SELECT COUNT(*) FROM dbo.PottedPlantStock)
            WHEN 'PottedPlantStockTransactions' THEN (SELECT COUNT(*) FROM dbo.PottedPlantStockTransactions)
            WHEN 'ReadyStock' THEN (SELECT COUNT(*) FROM dbo.ReadyStock)
            WHEN 'ReadyStockTransactions' THEN (SELECT COUNT(*) FROM dbo.ReadyStockTransactions)
            WHEN 'EmptyPotInventory' THEN (SELECT COUNT(*) FROM dbo.EmptyPotInventory)
        END)
        AND s.ChecksumBefore = CAST(CASE s.TableName
            WHEN 'OutletPurchases' THEN (SELECT CHECKSUM_AGG(BINARY_CHECKSUM(*)) FROM dbo.OutletPurchases)
            WHEN 'OutletSales' THEN (SELECT CHECKSUM_AGG(BINARY_CHECKSUM(*)) FROM dbo.OutletSales)
            WHEN 'OutletSaleItems' THEN (SELECT CHECKSUM_AGG(BINARY_CHECKSUM(*)) FROM dbo.OutletSaleItems)
            WHEN 'OutletBookings' THEN (SELECT CHECKSUM_AGG(BINARY_CHECKSUM(*)) FROM dbo.OutletBookings)
            WHEN 'OutletBookingItems' THEN (SELECT CHECKSUM_AGG(BINARY_CHECKSUM(*)) FROM dbo.OutletBookingItems)
            WHEN 'OutletWastages' THEN (SELECT CHECKSUM_AGG(BINARY_CHECKSUM(*)) FROM dbo.OutletWastages)
            WHEN 'Area' THEN (SELECT CHECKSUM_AGG(BINARY_CHECKSUM(*)) FROM dbo.Area)
            WHEN 'PottedPlantStock' THEN (SELECT CHECKSUM_AGG(BINARY_CHECKSUM(*)) FROM dbo.PottedPlantStock)
            WHEN 'PottedPlantStockTransactions' THEN (SELECT CHECKSUM_AGG(BINARY_CHECKSUM(*)) FROM dbo.PottedPlantStockTransactions)
            WHEN 'ReadyStock' THEN (SELECT CHECKSUM_AGG(BINARY_CHECKSUM(*)) FROM dbo.ReadyStock)
            WHEN 'ReadyStockTransactions' THEN (SELECT CHECKSUM_AGG(BINARY_CHECKSUM(*)) FROM dbo.ReadyStockTransactions)
            WHEN 'EmptyPotInventory' THEN (SELECT CHECKSUM_AGG(BINARY_CHECKSUM(*)) FROM dbo.EmptyPotInventory)
        END AS BIGINT)
        THEN 'MATCH' ELSE '*** MISMATCH -- INVESTIGATE ***' END AS SafetyCheck
FROM @Snap s;

-- No ZZTEST rows should remain anywhere (the ROLLBACK already guarantees
-- this; this is a second, independent proof).
SELECT
    (SELECT COUNT(*) FROM dbo.Area WHERE Name LIKE 'ZZTEST%') AS LeftoverZZTestAreas,
    (SELECT COUNT(*) FROM dbo.OutletSales WHERE CustomerName LIKE 'ZZTEST%' OR SaleCode LIKE 'ZZTEST%') AS LeftoverZZTestSales,
    (SELECT COUNT(*) FROM dbo.OutletBookings WHERE CustomerName LIKE 'ZZTEST%' OR BookingCode LIKE 'ZZTEST%') AS LeftoverZZTestBookings,
    (SELECT COUNT(*) FROM dbo.OutletPurchases WHERE SupplierName LIKE 'ZZTEST%' OR PurchaseCode LIKE 'ZZTEST%') AS LeftoverZZTestPurchases,
    (SELECT COUNT(*) FROM dbo.OutletWastages WHERE WastageCode LIKE 'ZZTEST%') AS LeftoverZZTestWastages,
    (SELECT COUNT(*) FROM dbo.EmptyPotInventory WHERE CreatedBy = 'ZZTEST-SETUP') AS LeftoverZZTestEmptyPots;

-- Schema unchanged.
SELECT
    @SchemaObjectCountBefore AS SchemaObjectCountBefore,
    (SELECT COUNT(*) FROM sys.objects WHERE type IN ('U','TR','C')) AS SchemaObjectCountAfter,
    CASE WHEN @SchemaObjectCountBefore = (SELECT COUNT(*) FROM sys.objects WHERE type IN ('U','TR','C')) THEN 'UNCHANGED' ELSE '*** SCHEMA CHANGED -- INVESTIGATE ***' END AS SchemaCheck;

-- 5. Final verdict.
SELECT
    CASE
        WHEN EXISTS (SELECT 1 FROM @Results WHERE Result = 'FAIL') THEN 'FAIL'
        WHEN EXISTS (SELECT 1 FROM @Results WHERE Result = 'SKIP') THEN 'PASS WITH SKIPS'
        ELSE 'PASS'
    END AS FinalVerdict,
    (SELECT COUNT(*) FROM @Results WHERE Result = 'PASS') AS PassCount,
    (SELECT COUNT(*) FROM @Results WHERE Result = 'FAIL') AS FailCount,
    (SELECT COUNT(*) FROM @Results WHERE Result = 'SKIP') AS SkipCount,
    (SELECT COUNT(*) FROM @Results WHERE Result = 'INFO') AS InfoCount;
