-- ============================================================================
-- ZZTEST-CHANGE additional login-capable test accounts for PlantsIMS2_Test
-- ONLY, needed to exercise the full Mother Plant -> Cutting Production ->
-- Cutting Confirmation -> Main Office -> Sowing -> Ready Stock workflow
-- end-to-end as genuinely separate role-holders (segregation of duties is
-- enforced by role/permission, not just by page access):
--   MotherPlant.Enter   -> "Mother Plant Supervisor" (zztest.multirole already has this)
--   MainOffice.Confirm  -> "Main Office Store Keeper" (NEW: zztest.mainofficekeeper)
--   Sowing.Enter        -> "Sowing Operator" (NEW: zztest.sowingoperator)
--   ReadyStock.Confirm  -> "Sowing Supervisor" (zztest.multirole already has this)
-- Companion to ZZTEST_SeedData.sql / ZZTEST_Cleanup.sql (same cleanup script
-- already removes these too -- it matches Username LIKE 'zztest%').
-- Idempotent: every INSERT is guarded by IF NOT EXISTS.
-- ============================================================================
IF DB_NAME() <> N'PlantsIMS2_Test' THROW 50299, 'ZZTEST_Phase2Workflow_SeedData.sql may only be run against PlantsIMS2_Test.', 1;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;

-- Password for both accounts is "ZZTest@123" (the same real BCrypt hash
-- already used for zztest.multirole in ZZTEST_SeedData.sql) so each can
-- actually be signed into for manual verification, not just read from the DB.
IF NOT EXISTS (SELECT 1 FROM dbo.IMSUsers WHERE Username = N'zztest.mainofficekeeper')
    INSERT INTO dbo.IMSUsers (Username, Password, Name, IsActive) VALUES (N'zztest.mainofficekeeper', N'$2a$11$IggwpAYvVfuSQp36AXYELewlJ5TFW6Nv/Ee.Qn/.n2BnjXP6bhBfW', N'ZZTEST-CHANGE-MAINOFFICEKEEPER', 1);
IF NOT EXISTS (SELECT 1 FROM dbo.IMSUsers WHERE Username = N'zztest.sowingoperator')
    INSERT INTO dbo.IMSUsers (Username, Password, Name, IsActive) VALUES (N'zztest.sowingoperator', N'$2a$11$IggwpAYvVfuSQp36AXYELewlJ5TFW6Nv/Ee.Qn/.n2BnjXP6bhBfW', N'ZZTEST-CHANGE-SOWINGOPERATOR', 1);
GO

DECLARE @MainOfficeKeeper INT = (SELECT Id FROM dbo.IMSUsers WHERE Username = N'zztest.mainofficekeeper');
DECLARE @SowingOperator INT = (SELECT Id FROM dbo.IMSUsers WHERE Username = N'zztest.sowingoperator');
DECLARE @RoleMainOfficeKeeper INT = (SELECT Id FROM dbo.Roles WHERE COALESCE(NULLIF(LTRIM(RTRIM(Name)), N''), RoleName) = N'Main Office Store Keeper');
DECLARE @RoleSowingOperator INT = (SELECT Id FROM dbo.Roles WHERE COALESCE(NULLIF(LTRIM(RTRIM(Name)), N''), RoleName) = N'Sowing Operator');
-- The one real, active Main Office Area (MainOffice.Confirm is scoped to it;
-- Main Office cutting/seed stock architecturally requires a real Main Office
-- Area -- never a fake ZZTEST one, same choice ZZTEST_SeedData.sql already made).
DECLARE @AreaMainOffice INT = (SELECT TOP 1 Id FROM dbo.Area WHERE AreaType = N'MainOffice' AND IsActive = 1 ORDER BY Id);

-- ZZTEST-CHANGE-MAINOFFICEKEEPER: Main Office Store Keeper of the real Main Office Area.
IF NOT EXISTS (SELECT 1 FROM dbo.UserRoles WHERE UserId = @MainOfficeKeeper AND RoleId = @RoleMainOfficeKeeper AND AreaId = @AreaMainOffice)
    INSERT INTO dbo.UserRoles (UserId, RoleId, AreaId) VALUES (@MainOfficeKeeper, @RoleMainOfficeKeeper, @AreaMainOffice);

-- ZZTEST-CHANGE-SOWINGOPERATOR: Sowing Operator, not Area-scoped (matches the
-- existing convention -- every current Sowing Operator assignment has a NULL AreaId).
IF NOT EXISTS (SELECT 1 FROM dbo.UserRoles WHERE UserId = @SowingOperator AND RoleId = @RoleSowingOperator AND AreaId IS NULL)
    INSERT INTO dbo.UserRoles (UserId, RoleId, AreaId) VALUES (@SowingOperator, @RoleSowingOperator, NULL);

-- CORRECTION found while running the end-to-end test: AreaAccessService only
-- stamps an "AreaAccess" claim for a UserRoles row that HAS a non-null
-- AreaId (Authorization/UserClaimsFactory.cs) -- the NULL-Area row above
-- grants the Sowing.Enter PERMISSION but no Area to actually sow into. A
-- second Sowing Operator row, scoped to ZZTEST-CHANGE-AREA-MP (125, the same
-- Mother Plant/Polyhouse test Area), is required so this account can select
-- that Area on Cutting Tray Sowing and exercise the Area -> Polyhouse cascade.
DECLARE @AreaZZTestMP INT = (SELECT Id FROM dbo.Area WHERE Name = N'ZZTEST-CHANGE-AREA-MP');
IF NOT EXISTS (SELECT 1 FROM dbo.UserRoles WHERE UserId = @SowingOperator AND RoleId = @RoleSowingOperator AND AreaId = @AreaZZTestMP)
    INSERT INTO dbo.UserRoles (UserId, RoleId, AreaId) VALUES (@SowingOperator, @RoleSowingOperator, @AreaZZTestMP);

-- Also ZZTEST-CHANGE-OUTLET (126, no Polyhouse assigned): needed to test the
-- "Polyhouse belongs to a different Area" rejection in isolation from the
-- separate "not authorized for this Area" check (submitting an Area this
-- account can genuinely use, paired with a Polyhouse that belongs to a
-- DIFFERENT Area, must still be rejected server-side).
DECLARE @AreaZZTestOutlet INT = (SELECT Id FROM dbo.Area WHERE Name = N'ZZTEST-CHANGE-OUTLET');
IF NOT EXISTS (SELECT 1 FROM dbo.UserRoles WHERE UserId = @SowingOperator AND RoleId = @RoleSowingOperator AND AreaId = @AreaZZTestOutlet)
    INSERT INTO dbo.UserRoles (UserId, RoleId, AreaId) VALUES (@SowingOperator, @RoleSowingOperator, @AreaZZTestOutlet);
GO

SELECT N'User' AS What, Id, Name FROM dbo.IMSUsers WHERE Username IN (N'zztest.mainofficekeeper', N'zztest.sowingoperator')
UNION ALL SELECT N'UserRole', ur.Id, u.Username + ' -> ' + COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) + ISNULL(' @ ' + a.Name, ' (all Areas)')
FROM dbo.UserRoles ur
JOIN dbo.IMSUsers u ON u.Id = ur.UserId
JOIN dbo.Roles r ON r.Id = ur.RoleId
LEFT JOIN dbo.Area a ON a.Id = ur.AreaId
WHERE u.Username IN (N'zztest.mainofficekeeper', N'zztest.sowingoperator')
ORDER BY What, Id;
GO
