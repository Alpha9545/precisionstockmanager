-- ============================================================================
-- ZZTEST-CHANGE manual test dataset for PlantsIMS2_Test ONLY.
-- ============================================================================
-- Idempotent: every INSERT is guarded by IF NOT EXISTS, so re-running this
-- script is a no-op wherever the row already exists. PERSISTENT (not rolled
-- back) -- this is deliberately left in place for manual UI testing.
--
-- Reuses REAL existing master data (PlantSpecies, MainOffice Area/SeedStock
-- lot) where a foreign key requires it; creates only clearly-prefixed new
-- ZZTEST-CHANGE-* rows for everything else (Area, Polyhouse, Employees,
-- Mother Plant, Sowing, Ready Stock transfer, Outlet). Nothing here touches
-- or renames any existing row.
--
-- To remove everything this script creates, see the companion
-- ZZTEST_Cleanup.sql (deletes only rows whose own code/name starts with
-- 'ZZTEST-CHANGE', in FK-safe order).
-- ============================================================================
IF DB_NAME() <> N'PlantsIMS2_Test' THROW 50299, 'ZZTEST_SeedData.sql may only be run against PlantsIMS2_Test.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;

-- ---------------------------------------------------------------- 1) Areas
IF NOT EXISTS (SELECT 1 FROM dbo.Area WHERE Name = N'ZZTEST-CHANGE-AREA-MP')
    INSERT INTO dbo.Area (Name, IsActive, AreaType, CreatedAt) VALUES (N'ZZTEST-CHANGE-AREA-MP', 1, N'MotherPlant', SYSUTCDATETIME());
IF NOT EXISTS (SELECT 1 FROM dbo.Area WHERE Name = N'ZZTEST-CHANGE-OUTLET')
    INSERT INTO dbo.Area (Name, IsActive, AreaType, CreatedAt) VALUES (N'ZZTEST-CHANGE-OUTLET', 1, N'Outlet', SYSUTCDATETIME());
GO

-- ---------------------------------------------------------------- 2) Polyhouse (of the ZZTEST Mother Plant Area)
IF NOT EXISTS (SELECT 1 FROM dbo.Polyhouses WHERE Name = N'ZZTEST-CHANGE-POLYHOUSE-A')
    INSERT INTO dbo.Polyhouses (Name, AreaId) VALUES (N'ZZTEST-CHANGE-POLYHOUSE-A', (SELECT Id FROM dbo.Area WHERE Name = N'ZZTEST-CHANGE-AREA-MP'));
GO

-- ---------------------------------------------------------------- 3) Employees (IMSUsers)
-- Password for ZZTEST-CHANGE-MULTIROLE (the only one meant to be logged
-- into) is "ZZTest@123" -- a real BCrypt hash, so the multi-role permission
-- test can be done by actually signing in as this account, not just reading
-- the database.
IF NOT EXISTS (SELECT 1 FROM dbo.IMSUsers WHERE Username = N'zztest.supervisor')
    INSERT INTO dbo.IMSUsers (Username, Password, Name, IsActive) VALUES (N'zztest.supervisor', N'$2a$11$0000000000000000000000000000000000000000000000000', N'ZZTEST-CHANGE-SUPERVISOR', 1);
IF NOT EXISTS (SELECT 1 FROM dbo.IMSUsers WHERE Username = N'zztest.employeea')
    INSERT INTO dbo.IMSUsers (Username, Password, Name, IsActive) VALUES (N'zztest.employeea', N'$2a$11$0000000000000000000000000000000000000000000000000', N'ZZTEST-CHANGE-EMPLOYEE-A', 1);
IF NOT EXISTS (SELECT 1 FROM dbo.IMSUsers WHERE Username = N'zztest.employeeb')
    INSERT INTO dbo.IMSUsers (Username, Password, Name, IsActive) VALUES (N'zztest.employeeb', N'$2a$11$0000000000000000000000000000000000000000000000000', N'ZZTEST-CHANGE-EMPLOYEE-B', 1);
IF NOT EXISTS (SELECT 1 FROM dbo.IMSUsers WHERE Username = N'zztest.multirole')
    INSERT INTO dbo.IMSUsers (Username, Password, Name, IsActive) VALUES (N'zztest.multirole', N'$2a$11$IggwpAYvVfuSQp36AXYELewlJ5TFW6Nv/Ee.Qn/.n2BnjXP6bhBfW', N'ZZTEST-CHANGE-MULTIROLE', 1);
GO

-- ---------------------------------------------------------------- 4) Role assignments
DECLARE @AreaMP INT = (SELECT Id FROM dbo.Area WHERE Name = N'ZZTEST-CHANGE-AREA-MP');
DECLARE @AreaOutlet INT = (SELECT Id FROM dbo.Area WHERE Name = N'ZZTEST-CHANGE-OUTLET');
DECLARE @AreaMainOffice INT = (SELECT TOP 1 Id FROM dbo.Area WHERE AreaType = N'MainOffice' ORDER BY Id);
DECLARE @Supervisor INT = (SELECT Id FROM dbo.IMSUsers WHERE Username = N'zztest.supervisor');
DECLARE @MultiRole INT = (SELECT Id FROM dbo.IMSUsers WHERE Username = N'zztest.multirole');
DECLARE @RoleMPSupervisor INT = (SELECT Id FROM dbo.Roles WHERE COALESCE(NULLIF(LTRIM(RTRIM(Name)), N''), RoleName) = N'Mother Plant Supervisor');
DECLARE @RoleSowingSupervisor INT = (SELECT Id FROM dbo.Roles WHERE COALESCE(NULLIF(LTRIM(RTRIM(Name)), N''), RoleName) = N'Sowing Supervisor');
DECLARE @RoleOutletSales INT = (SELECT Id FROM dbo.Roles WHERE COALESCE(NULLIF(LTRIM(RTRIM(Name)), N''), RoleName) = N'Outlet Sales');

-- ZZTEST-CHANGE-SUPERVISOR: Mother Plant Supervisor of the ZZTEST MP Area
-- (so it can be assigned on the ZZTEST Mother Plant, TR_MotherPlants_AreaAndSupervisor),
-- and Sowing Supervisor of Main Office (so it can approve the ZZTEST sowing below).
IF NOT EXISTS (SELECT 1 FROM dbo.UserRoles WHERE UserId = @Supervisor AND RoleId = @RoleMPSupervisor AND AreaId = @AreaMP)
    INSERT INTO dbo.UserRoles (UserId, RoleId, AreaId) VALUES (@Supervisor, @RoleMPSupervisor, @AreaMP);
IF NOT EXISTS (SELECT 1 FROM dbo.UserRoles WHERE UserId = @Supervisor AND RoleId = @RoleSowingSupervisor AND AreaId = @AreaMainOffice)
    INSERT INTO dbo.UserRoles (UserId, RoleId, AreaId) VALUES (@Supervisor, @RoleSowingSupervisor, @AreaMainOffice);

-- ZZTEST-CHANGE-MULTIROLE: THREE roles across TWO different Areas -- log in
-- as this account (Username zztest.multirole / Password ZZTest@123) to
-- manually confirm the union of all three roles' permissions is granted.
IF NOT EXISTS (SELECT 1 FROM dbo.UserRoles WHERE UserId = @MultiRole AND RoleId = @RoleMPSupervisor AND AreaId = @AreaMP)
    INSERT INTO dbo.UserRoles (UserId, RoleId, AreaId) VALUES (@MultiRole, @RoleMPSupervisor, @AreaMP);
IF NOT EXISTS (SELECT 1 FROM dbo.UserRoles WHERE UserId = @MultiRole AND RoleId = @RoleSowingSupervisor AND AreaId = @AreaMainOffice)
    INSERT INTO dbo.UserRoles (UserId, RoleId, AreaId) VALUES (@MultiRole, @RoleSowingSupervisor, @AreaMainOffice);
IF NOT EXISTS (SELECT 1 FROM dbo.UserRoles WHERE UserId = @MultiRole AND RoleId = @RoleOutletSales AND AreaId = @AreaOutlet)
    INSERT INTO dbo.UserRoles (UserId, RoleId, AreaId) VALUES (@MultiRole, @RoleOutletSales, @AreaOutlet);
GO

-- ---------------------------------------------------------------- 5) Mother Plant (TEST 2)
-- Reuses a real existing Species (Antirrhinum Snappy Mix, Id 3106 -- already
-- has ReadyStockDays configured, and is the same variety the real historical
-- Seed Sowing rows already use, so nothing new needs configuring in Admin >
-- Plant Master for this test to work end to end).
IF NOT EXISTS (SELECT 1 FROM dbo.MotherPlants WHERE MotherPlantCode = N'ZZTEST-MP-001')
BEGIN
    DECLARE @AreaMP2 INT = (SELECT Id FROM dbo.Area WHERE Name = N'ZZTEST-CHANGE-AREA-MP');
    DECLARE @PolyhouseA INT = (SELECT Id FROM dbo.Polyhouses WHERE Name = N'ZZTEST-CHANGE-POLYHOUSE-A');
    DECLARE @Supervisor2 INT = (SELECT Id FROM dbo.IMSUsers WHERE Username = N'zztest.supervisor');
    DECLARE @EmployeeA INT = (SELECT Id FROM dbo.IMSUsers WHERE Username = N'zztest.employeea');
    INSERT INTO dbo.MotherPlants
        (MotherPlantCode, PolyhouseId, SpeciesId, AreaId, ResponsiblePersonId, SupervisorId, PlantingDate,
         MotherPlantQuantity, CuttingPeriodDays, CuttingRate, ExpectedCuttingQuantity, ExpectedMonthlyCuttingQuantity,
         Status, Remarks, CreatedDate, CreatedBy)
    VALUES
        (N'ZZTEST-MP-001', @PolyhouseA, 3106, @AreaMP2, @EmployeeA, @Supervisor2, CAST(GETDATE() AS DATE),
         50, 30, 4, 50 * 4, (50 * 4) * 30.0 / 30, N'Active', N'ZZTEST-CHANGE manual test record', SYSUTCDATETIME(), N'ZZTEST-seed-script');
END
GO

-- ---------------------------------------------------------------- 6) Seed Stock lot for Sowing (TEST 3, 4, 5)
-- A DEDICATED ZZTEST lot (own BatchNo) at the real Main Office Area, so
-- Sowing tests never touch a real production seed lot's quantity.
IF NOT EXISTS (SELECT 1 FROM dbo.SeedStock WHERE BatchNo = N'ZZTEST-LOT-001')
BEGIN
    DECLARE @AreaMainOffice2 INT = (SELECT TOP 1 Id FROM dbo.Area WHERE AreaType = N'MainOffice' ORDER BY Id);
    INSERT INTO dbo.SeedStock (SpeciesId, AreaId, BatchNo, Unit, PhysicalQuantity, InTransitQuantity, CreatedDate, CreatedBy)
    VALUES (3106, @AreaMainOffice2, N'ZZTEST-LOT-001', N'pcs', 1000, 0, SYSUTCDATETIME(), N'ZZTEST-seed-script');
END
GO

-- ---------------------------------------------------------------- 7) A ZZTEST Sowing already 'Sown', ready to approve (TEST 3 -- larger tray approval)
-- 24-cavity, 20 trays = 480 seeds used; deliberately sown from the ZZTEST
-- lot above so approving MORE than 20 trays (e.g. 22) is a safe, isolated
-- demonstration of the new "overage allowed" rule (Approved Change 3).
IF NOT EXISTS (SELECT 1 FROM dbo.SeedSowings WHERE SowingCode = N'ZZTEST-SOW-001')
BEGIN
    DECLARE @Lot INT = (SELECT Id FROM dbo.SeedStock WHERE BatchNo = N'ZZTEST-LOT-001');
    DECLARE @AreaMainOffice3 INT = (SELECT TOP 1 Id FROM dbo.Area WHERE AreaType = N'MainOffice' ORDER BY Id);
    DECLARE @Supervisor3 INT = (SELECT Id FROM dbo.IMSUsers WHERE Username = N'zztest.supervisor');
    INSERT INTO dbo.SeedSowings
        (SowingCode, SourceType, SourceSeedStockId, SpeciesId, AreaId, BatchNo, CavityType, NumberOfTrays, QuantitySown, SeedQuantity,
         SowingDate, ReadyStockDays, ExpectedReadyDate, Status, SupervisorId, Remarks, CreatedDate, CreatedBy)
    VALUES
        (N'ZZTEST-SOW-001', N'Seed', @Lot, 3106, @AreaMainOffice3, N'ZZTEST-LOT-001', N'24 Cavity', 20, 480, 480,
         CAST(GETDATE() AS DATE), 90, DATEADD(day, 90, CAST(GETDATE() AS DATE)), N'Sown', @Supervisor3, N'ZZTEST-CHANGE manual test sowing -- approve MORE than 20 trays to test Approved Change 3', SYSUTCDATETIME(), N'ZZTEST-seed-script');
    UPDATE dbo.SeedStock SET PhysicalQuantity = PhysicalQuantity - 480 WHERE Id = @Lot;
    INSERT INTO dbo.SeedStockTransactions (SeedStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, Remarks, CreatedAt)
    VALUES (@Lot, SYSUTCDATETIME(), N'Sown', N'SeedSowing', (SELECT Id FROM dbo.SeedSowings WHERE SowingCode = N'ZZTEST-SOW-001'), -480, 1000, N'ZZTEST-CHANGE manual test sowing', SYSUTCDATETIME());
END
GO

-- ---------------------------------------------------------------- 8) A second, already-Completed ZZTEST sowing with Ready Tray
-- stock physically at the ZZTEST Outlet (TEST 6/7 -- Outlet booking/sale/collection).
-- 42-cavity, 10 trays = 420 seedlings, fully accounted (Completed).
IF NOT EXISTS (SELECT 1 FROM dbo.SeedSowings WHERE SowingCode = N'ZZTEST-SOW-002')
BEGIN
    DECLARE @Lot2 INT = (SELECT Id FROM dbo.SeedStock WHERE BatchNo = N'ZZTEST-LOT-001');
    DECLARE @AreaMainOffice4 INT = (SELECT TOP 1 Id FROM dbo.Area WHERE AreaType = N'MainOffice' ORDER BY Id);
    DECLARE @AreaOutlet2 INT = (SELECT Id FROM dbo.Area WHERE Name = N'ZZTEST-CHANGE-OUTLET');
    DECLARE @Supervisor4 INT = (SELECT Id FROM dbo.IMSUsers WHERE Username = N'zztest.supervisor');
    INSERT INTO dbo.SeedSowings
        (SowingCode, SourceType, SourceSeedStockId, SpeciesId, AreaId, BatchNo, CavityType, NumberOfTrays, QuantitySown, SeedQuantity,
         SowingDate, ReadyStockDays, ExpectedReadyDate, Status, SupervisorId, ConfirmedReadyQuantity, WastageQuantity, Remarks, CreatedDate, CreatedBy)
    VALUES
        (N'ZZTEST-SOW-002', N'Seed', @Lot2, 3106, @AreaMainOffice4, N'ZZTEST-LOT-001', N'42 Cavity', 10, 420, 420,
         DATEADD(day, -95, CAST(GETDATE() AS DATE)), 90, DATEADD(day, -5, CAST(GETDATE() AS DATE)), N'Completed', @Supervisor4, 420, 0,
         N'ZZTEST-CHANGE manual test sowing -- already approved, ready trays transferred to ZZTEST Outlet below', SYSUTCDATETIME(), N'ZZTEST-seed-script');
    UPDATE dbo.SeedStock SET PhysicalQuantity = PhysicalQuantity - 420 WHERE Id = @Lot2;
    INSERT INTO dbo.SeedStockTransactions (SeedStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, Remarks, CreatedAt)
    VALUES (@Lot2, SYSUTCDATETIME(), N'Sown', N'SeedSowing', (SELECT Id FROM dbo.SeedSowings WHERE SowingCode = N'ZZTEST-SOW-002'), -420, 520, N'ZZTEST-CHANGE manual test sowing', SYSUTCDATETIME());

    DECLARE @Sowing2 INT = (SELECT Id FROM dbo.SeedSowings WHERE SowingCode = N'ZZTEST-SOW-002');
    INSERT INTO dbo.ReadyStock (SeedSowingId, SpeciesId, AreaId, BatchNo, CavityType, SowingDate, Quantity, FirstConfirmationDate, CreatedDate, CreatedBy, ReservedQuantity, DispatchedQuantity)
    VALUES (@Sowing2, 3106, @AreaOutlet2, N'ZZTEST-LOT-001', N'42 Cavity', DATEADD(day, -95, CAST(GETDATE() AS DATE)), 420, SYSUTCDATETIME(), SYSUTCDATETIME(), N'ZZTEST-seed-script', 0, 0);
    INSERT INTO dbo.ReadyStockTransactions (ReadyStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, Remarks, CreatedAt)
    VALUES ((SELECT Id FROM dbo.ReadyStock WHERE SeedSowingId = @Sowing2), SYSUTCDATETIME(), N'Confirmed', N'SeedSowing', @Sowing2, 420, 0, N'ZZTEST-CHANGE: approved directly at the ZZTEST Outlet for manual booking/sale testing', SYSUTCDATETIME());
END
GO

-- ---------------------------------------------------------------- 9) ZZTEST Potted Plant stock at the ZZTEST Outlet (potted Sale/Booking/Wastage)
IF NOT EXISTS (SELECT 1 FROM dbo.PottedPlantStock WHERE AreaId = (SELECT Id FROM dbo.Area WHERE Name = N'ZZTEST-CHANGE-OUTLET') AND SpeciesId = 3106)
BEGIN
    DECLARE @AreaOutlet3 INT = (SELECT Id FROM dbo.Area WHERE Name = N'ZZTEST-CHANGE-OUTLET');
    INSERT INTO dbo.PottedPlantStock (SpeciesId, PotSize, AreaId, PhysicalQuantity, ReservedQuantity, SoldDispatchedQuantity, WastedQuantity, InTransitQuantity, CreatedDate)
    VALUES (3106, N'4 inch', @AreaOutlet3, 40, 0, 0, 0, 0, SYSUTCDATETIME());
    INSERT INTO dbo.PottedPlantStockTransactions (PottedPlantStockId, TransactionDate, TransactionType, ReferenceType, Quantity, BeforeQuantity, Remarks, CreatedAt)
    VALUES ((SELECT Id FROM dbo.PottedPlantStock WHERE AreaId = @AreaOutlet3 AND SpeciesId = 3106), SYSUTCDATETIME(), N'Transfer', N'ZZTEST', 40, 0, N'ZZTEST-CHANGE manual test potted stock at Outlet', SYSUTCDATETIME());
END
GO

-- ---------------------------------------------------------------- Summary
SELECT N'Area' AS What, Id, Name FROM dbo.Area WHERE Name LIKE N'ZZTEST%'
UNION ALL SELECT N'Polyhouse', Id, Name FROM dbo.Polyhouses WHERE Name LIKE N'ZZTEST%'
UNION ALL SELECT N'User', Id, Name FROM dbo.IMSUsers WHERE Username LIKE N'zztest%'
UNION ALL SELECT N'MotherPlant', Id, MotherPlantCode FROM dbo.MotherPlants WHERE MotherPlantCode LIKE N'ZZTEST%'
UNION ALL SELECT N'SeedStock', Id, BatchNo FROM dbo.SeedStock WHERE BatchNo LIKE N'ZZTEST%'
UNION ALL SELECT N'SeedSowing', Id, SowingCode FROM dbo.SeedSowings WHERE SowingCode LIKE N'ZZTEST%'
UNION ALL SELECT N'ReadyStock', rs.Id, sw.SowingCode FROM dbo.ReadyStock rs JOIN dbo.SeedSowings sw ON sw.Id = rs.SeedSowingId WHERE sw.SowingCode LIKE N'ZZTEST%'
UNION ALL SELECT N'PottedPlantStock', Id, CAST(AreaId AS NVARCHAR(20)) FROM dbo.PottedPlantStock WHERE AreaId = (SELECT Id FROM dbo.Area WHERE Name = N'ZZTEST-CHANGE-OUTLET')
ORDER BY What, Id;
GO
