-- ============================================================================
-- Phase B: Seed Stock -> Direct Sowing -> Batch -> Supervisor Approval -> Ready Stock
-- ============================================================================
-- Run AFTER PhaseA_RoleBasedAccess.sql (and Phases 22-25). Take a full backup
-- first. Idempotent and additive: safe to re-run.
--
-- WHAT IT DOES
--   1. Area -> Polyhouse business hierarchy (Area = site, Polyhouse = unit
--      inside it): adds nullable dbo.Polyhouses.AreaId -> dbo.Area(Id).
--      The EXISTING dbo.Area.PolyhouseId foreign key (Area located inside a
--      Polyhouse -- used by Mother Plant / Pot Production / Polyhouse admin)
--      is NOT reversed, altered or dropped, and no existing row is changed.
--      Administrators assign each Polyhouse to its Area in Admin > Polyhouses.
--   2. dbo.SeedSowings: + PolyhouseId (the Polyhouse the batch is sown in),
--      + WastageQuantity (running total recorded at Supervisor Approval),
--      Status widened to 'Sown' | 'Completed' | 'Cancelled', and
--      ConfirmedReadyQuantity + WastageQuantity <= QuantitySown.
--      The sowing batch number (YYYY-MM-DD-L-NNN) is stored in the EXISTING
--      unique SowingCode column (NVARCHAR(20), UQ_SeedSowings_Code); its
--      per-day sequence uses the EXISTING dbo.BatchNumberSequences counter.
--      No new batch-number table/column is created.
--   3. dbo.ReadyConfirmations (the Supervisor Approval record):
--      + WastageQuantity, + WastageReason (closed list), + ApprovedById;
--      quantity check widened so a fully-wasted batch (Ready = 0) can be
--      approved; a reason is required whenever wastage > 0.
--   4. dbo.ReadyStock: + PolyhouseId (location-aware Ready Stock).
--   5. Role 'Sowing Supervisor' (approves Ready Stock). When -- and only when --
--      this script creates that role, 'ReadyStock.Confirm' is moved from
--      'Sowing Operator' to it (operators sow, supervisors approve). Re-running
--      the script never repeats that change. Reversible in Admin > Roles.
--   6. Read-only report of existing Seed Issue records (they are preserved).
--
-- WHAT IT NEVER DOES
--   No DROP TABLE, no DROP/rename of any column, no DELETE/UPDATE of stock,
--   ledgers, sowings, seed issues, legacy Seed Bank / SeedEntries / Inventory /
--   Bookings or users. The only row removed is the single RolePermissions
--   grant described in step 5 (configuration, not history).
-- ============================================================================

SET XACT_ABORT ON;
GO

-- ----------------------------------------------------------------------------
-- STEP 1: Polyhouses.AreaId  (Area -> Polyhouse)
-- ----------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Polyhouses') AND name = 'AreaId')
BEGIN
    ALTER TABLE dbo.Polyhouses ADD AreaId INT NULL;
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Polyhouses_Area')
BEGIN
    ALTER TABLE dbo.Polyhouses ADD CONSTRAINT FK_Polyhouses_Area FOREIGN KEY (AreaId) REFERENCES dbo.Area(Id);
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Polyhouses_AreaId' AND object_id = OBJECT_ID('dbo.Polyhouses'))
BEGIN
    CREATE INDEX IX_Polyhouses_AreaId ON dbo.Polyhouses(AreaId);
END
GO

-- ----------------------------------------------------------------------------
-- STEP 2: SeedSowings
-- ----------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SeedSowings') AND name = 'PolyhouseId')
BEGIN
    ALTER TABLE dbo.SeedSowings ADD PolyhouseId INT NULL;
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_SeedSowings_Polyhouse')
BEGIN
    ALTER TABLE dbo.SeedSowings ADD CONSTRAINT FK_SeedSowings_Polyhouse FOREIGN KEY (PolyhouseId) REFERENCES dbo.Polyhouses(Id);
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SeedSowings_PolyhouseId' AND object_id = OBJECT_ID('dbo.SeedSowings'))
BEGIN
    CREATE INDEX IX_SeedSowings_PolyhouseId ON dbo.SeedSowings(PolyhouseId);
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SeedSowings') AND name = 'WastageQuantity')
BEGIN
    ALTER TABLE dbo.SeedSowings ADD WastageQuantity DECIMAL(18,2) NOT NULL
        CONSTRAINT DF_SeedSowings_WastageQuantity DEFAULT (0);
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_SeedSowings_WastageQuantity')
BEGIN
    ALTER TABLE dbo.SeedSowings ADD CONSTRAINT CK_SeedSowings_WastageQuantity
        CHECK (WastageQuantity >= 0 AND ConfirmedReadyQuantity + WastageQuantity <= QuantitySown);
END
GO
-- Widen the status list (existing values 'Sown'/'Cancelled' stay valid).
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_SeedSowings_Status'
           AND [definition] NOT LIKE '%Completed%')
BEGIN
    ALTER TABLE dbo.SeedSowings DROP CONSTRAINT CK_SeedSowings_Status;
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_SeedSowings_Status')
BEGIN
    ALTER TABLE dbo.SeedSowings ADD CONSTRAINT CK_SeedSowings_Status
        CHECK (Status IN ('Sown', 'Completed', 'Cancelled'));
END
GO
-- A Completed sowing is fully accounted for: Ready + Wastage = Sown.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_SeedSowings_CompletedAccounted')
BEGIN
    ALTER TABLE dbo.SeedSowings ADD CONSTRAINT CK_SeedSowings_CompletedAccounted
        CHECK (Status <> 'Completed' OR ConfirmedReadyQuantity + WastageQuantity = QuantitySown);
END
GO
-- (CK_SeedSowings_CavityType -- 9/24/42/102/150 Cavity -- is kept exactly as is.)

-- ----------------------------------------------------------------------------
-- STEP 3: ReadyConfirmations = Supervisor Approval
-- ----------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.ReadyConfirmations') AND name = 'WastageQuantity')
BEGIN
    ALTER TABLE dbo.ReadyConfirmations ADD WastageQuantity DECIMAL(18,2) NOT NULL
        CONSTRAINT DF_ReadyConfirmations_WastageQuantity DEFAULT (0);
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.ReadyConfirmations') AND name = 'WastageReason')
BEGIN
    ALTER TABLE dbo.ReadyConfirmations ADD WastageReason NVARCHAR(50) NULL;
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.ReadyConfirmations') AND name = 'ApprovedById')
BEGIN
    ALTER TABLE dbo.ReadyConfirmations ADD ApprovedById INT NULL;
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_ReadyConfirmations_ApprovedBy')
BEGIN
    ALTER TABLE dbo.ReadyConfirmations ADD CONSTRAINT FK_ReadyConfirmations_ApprovedBy FOREIGN KEY (ApprovedById) REFERENCES dbo.IMSUsers(Id);
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_ReadyConfirmations_WastageReason')
BEGIN
    ALTER TABLE dbo.ReadyConfirmations ADD CONSTRAINT CK_ReadyConfirmations_WastageReason
        CHECK (WastageReason IS NULL OR WastageReason IN ('Germination failure', 'Disease', 'Damaged plants', 'Poor growth', 'Other'));
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_ReadyConfirmations_WastageReasonRequired')
BEGIN
    ALTER TABLE dbo.ReadyConfirmations ADD CONSTRAINT CK_ReadyConfirmations_WastageReasonRequired
        CHECK (WastageQuantity = 0 OR WastageReason IS NOT NULL);
END
GO
-- Widen: Ready may be 0 when the whole remainder is wastage.
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_ReadyConfirmations_Quantity'
           AND [definition] NOT LIKE '%WastageQuantity%')
BEGIN
    ALTER TABLE dbo.ReadyConfirmations DROP CONSTRAINT CK_ReadyConfirmations_Quantity;
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_ReadyConfirmations_Quantity')
BEGIN
    ALTER TABLE dbo.ReadyConfirmations ADD CONSTRAINT CK_ReadyConfirmations_Quantity
        CHECK (ConfirmedQuantity >= 0 AND WastageQuantity >= 0 AND ConfirmedQuantity + WastageQuantity > 0);
END
GO

-- ----------------------------------------------------------------------------
-- STEP 4: ReadyStock.PolyhouseId
-- ----------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.ReadyStock') AND name = 'PolyhouseId')
BEGIN
    ALTER TABLE dbo.ReadyStock ADD PolyhouseId INT NULL;
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_ReadyStock_Polyhouse')
BEGIN
    ALTER TABLE dbo.ReadyStock ADD CONSTRAINT FK_ReadyStock_Polyhouse FOREIGN KEY (PolyhouseId) REFERENCES dbo.Polyhouses(Id);
END
GO

-- ----------------------------------------------------------------------------
-- STEP 5: 'Sowing Supervisor' role (Phase A schema: Name + RoleName in sync)
-- ----------------------------------------------------------------------------
DECLARE @CreatedSupervisorRole BIT = 0;

IF NOT EXISTS (SELECT 1 FROM dbo.Roles r
               WHERE COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), ''), r.RoleName) = N'Sowing Supervisor'
                  OR r.RoleName = N'Sowing Supervisor')
BEGIN
    INSERT INTO dbo.Roles (Name, RoleName, Description, IsSystemRole)
    VALUES (N'Sowing Supervisor', N'Sowing Supervisor', N'Approves Ready Stock (actual ready quantity / wastage) for sowing batches', 0);
    SET @CreatedSupervisorRole = 1;
END

DECLARE @SupervisorRoleId INT =
    (SELECT TOP 1 r.Id FROM dbo.Roles r WHERE COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), ''), r.RoleName) = N'Sowing Supervisor');

-- Default permissions only while the role has none.
IF @SupervisorRoleId IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions WHERE RoleId = @SupervisorRoleId)
BEGIN
    INSERT INTO dbo.RolePermissions (RoleId, PermissionId)
    SELECT @SupervisorRoleId, p.Id
    FROM dbo.Permissions p
    WHERE p.Code IN (N'Dashboard.View', N'Sowing.View', N'SeedStock.View', N'ReadyStock.View', N'ReadyStock.Confirm');
END

-- Move ReadyStock.Confirm away from Sowing Operator ONLY in the run that
-- created the Sowing Supervisor role (never repeated on re-run).
IF @CreatedSupervisorRole = 1
BEGIN
    DELETE rp
    FROM dbo.RolePermissions rp
    JOIN dbo.Roles r ON r.Id = rp.RoleId
    JOIN dbo.Permissions p ON p.Id = rp.PermissionId
    WHERE COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), ''), r.RoleName) = N'Sowing Operator'
      AND p.Code = N'ReadyStock.Confirm';
    PRINT 'ReadyStock.Confirm moved from Sowing Operator to Sowing Supervisor (' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' grant removed).';
END
GO

-- ----------------------------------------------------------------------------
-- STEP 6: VERIFY (read-only)
-- ----------------------------------------------------------------------------
-- Seed Issue records are PRESERVED. Any 'PendingConfirmation' issue still
-- holds seed in SeedStock.InTransitQuantity: a System Administrator should
-- confirm or reject it on /Production/SeedIssue/PendingReceipts.
SELECT Status, COUNT(*) AS SeedIssueCount, SUM(IssuedQuantity) AS IssuedQuantity
FROM dbo.SeedIssues GROUP BY Status;

-- Seed that earlier Seed Issues moved OUT of Main Office to growing Areas.
-- Direct Sowing only consumes Main Office Seed Stock, so any balance listed
-- here is not sowable through the new workflow (decide how to handle it in
-- the historical cutover phase; nothing is changed here).
SELECT ss.Id AS SeedStockId, a.Name AS AreaName, a.AreaType, ps.Name AS Variety, ss.BatchNo,
       ss.PhysicalQuantity, ss.InTransitQuantity, ss.AvailableQuantity
FROM dbo.SeedStock ss
JOIN dbo.Area a ON a.Id = ss.AreaId
JOIN dbo.PlantSpecies ps ON ps.Id = ss.SpeciesId
WHERE ISNULL(a.AreaType, '') <> 'MainOffice' AND ss.PhysicalQuantity > 0;

SELECT COUNT(*) AS PolyhousesWithoutArea FROM dbo.Polyhouses WHERE AreaId IS NULL;
SELECT COUNT(*) AS VarietiesWithoutGrowingDays FROM dbo.PlantSpecies WHERE ReadyStockDays IS NULL;

SELECT r.Id, COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), ''), r.RoleName) AS RoleName,
       STUFF((SELECT N', ' + p.Code FROM dbo.RolePermissions rp JOIN dbo.Permissions p ON p.Id = rp.PermissionId
              WHERE rp.RoleId = r.Id ORDER BY p.Code FOR XML PATH('')), 1, 2, N'') AS Permissions
FROM dbo.Roles r
WHERE COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), ''), r.RoleName) IN (N'Sowing Operator', N'Sowing Supervisor');
GO
