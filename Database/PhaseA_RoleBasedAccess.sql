-- ============================================================================
-- Phase A: Role-Based Access Control (repair + completion of Phase 14)
-- ============================================================================
-- WHY THIS SCRIPT EXISTS
--   Inspection of the production backup (PlantsIMS2) showed that Phase 14 only
--   partially applied, because dbo.Roles ALREADY existed as a legacy table
--   (Id, RoleName) before Phase 14 ran:
--     * Phase 14's CREATE TABLE dbo.Roles was skipped (table existed), so
--       Description / IsSystemRole / CreatedDate were never added.
--     * A "Name" column was later added by hand (NOT NULL, a copy of RoleName).
--     * Phase 14's role seed (INSERT ... (Name, Description, IsSystemRole))
--       failed, so none of its roles exist, dbo.RolePermissions has 0 rows and
--       dbo.UserRoles has 0 rows. Only dbo.Permissions (31 codes) was seeded.
--   Current dbo.Roles rows: 1 Office Coordinator, 2 Sowing Operator,
--   3 Booking Executive, 4 Dispatch Executive, 5 System Administrator
--   (identical names to dbo.Designation 1-5).
--
-- WHAT IT DOES (all steps additive and idempotent -- safe to re-run)
--   1. dbo.Roles: guarantees BOTH "Name" and "RoleName" exist and are in sync
--      (the application reads COALESCE(Name, RoleName) and writes both), and
--      adds the missing nullable/defaulted Description / IsSystemRole /
--      CreatedDate columns that RoleRepository already selects.
--   2. Adds the two roles approved by the business owner for designations
--      that had no role: 'Fertilizer Supervisor', 'Mother Plant Supervisor'.
--      Marks 'System Administrator' IsSystemRole = 1 (full access is granted
--      by the application to this role -- it needs NO RolePermissions rows).
--   3. Adds the permission codes needed so every page maps to a permission.
--   4. Backfills dbo.UserRoles for users that have NO role yet, from their
--      legacy dbo.IMSUsers.DesignationID -> dbo.Roles row of the SAME NAME,
--      plus one Area-scoped row per dbo.Area whose SupervisorId is that user.
--      Users that already have any UserRoles row are never touched.
--   5. Seeds default RolePermissions ONLY for roles that currently have none.
--      Administrators adjust them afterwards in Admin > Roles & Permissions.
--
-- WHAT IT NEVER DOES
--   No DROP, no DELETE, no UPDATE of users, passwords, designations, stock or
--   transactions. Existing RolePermissions / UserRoles rows are never changed.
--
-- PREREQUISITE: Phase14_RoleFoundation_AreaExtension.sql (creates
-- Permissions / RolePermissions / UserRoles when absent). Take a full backup
-- before running.
-- ============================================================================

SET XACT_ABORT ON;
GO

-- ----------------------------------------------------------------------------
-- STEP 1: dbo.Roles columns
-- ----------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Roles') AND name = 'Name')
BEGIN
    ALTER TABLE dbo.Roles ADD Name NVARCHAR(50) NULL;
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Roles') AND name = 'RoleName')
BEGIN
    -- Only on a database created from scratch by Phase 14 (Name only).
    ALTER TABLE dbo.Roles ADD RoleName NVARCHAR(50) NULL;
END
GO
-- Keep the two name columns identical (fill whichever one is empty).
UPDATE dbo.Roles SET Name = RoleName WHERE (Name IS NULL OR LTRIM(RTRIM(Name)) = '') AND RoleName IS NOT NULL;
UPDATE dbo.Roles SET RoleName = Name WHERE (RoleName IS NULL OR LTRIM(RTRIM(RoleName)) = '') AND Name IS NOT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Roles') AND name = 'Description')
BEGIN
    ALTER TABLE dbo.Roles ADD Description NVARCHAR(200) NULL;
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Roles') AND name = 'IsSystemRole')
BEGIN
    ALTER TABLE dbo.Roles ADD IsSystemRole BIT NOT NULL CONSTRAINT DF_Roles_IsSystemRole DEFAULT (0);
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Roles') AND name = 'CreatedDate')
BEGIN
    ALTER TABLE dbo.Roles ADD CreatedDate DATETIME2 NOT NULL CONSTRAINT DF_Roles_CreatedDate DEFAULT (SYSUTCDATETIME());
END
GO

-- ----------------------------------------------------------------------------
-- STEP 2: Roles approved for existing designations 6 and 7; flag the
--         System Administrator role.
-- ----------------------------------------------------------------------------
INSERT INTO dbo.Roles (Name, RoleName, Description, IsSystemRole)
SELECT v.Name, v.Name, v.Description, 0
FROM (VALUES
    (N'Fertilizer Supervisor',   N'Fertilizer stock, issue to Polyhouse and usage'),
    (N'Mother Plant Supervisor', N'Mother Plant, cutting and cutting stock')
) AS v(Name, Description)
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.Roles r
    WHERE COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), ''), r.RoleName) = v.Name
       OR r.RoleName = v.Name
);
GO
UPDATE dbo.Roles
SET IsSystemRole = 1,
    Description = COALESCE(Description, N'Full access to every feature and Area (granted automatically by the application)')
WHERE COALESCE(NULLIF(LTRIM(RTRIM(Name)), ''), RoleName) = N'System Administrator'
  AND (IsSystemRole = 0 OR Description IS NULL);
GO
-- Descriptions for the pre-existing roles (only where still empty).
UPDATE r SET Description = v.Description
FROM dbo.Roles r
JOIN (VALUES
    (N'Office Coordinator', N'Office, customer and order coordination (mostly read-only)'),
    (N'Sowing Operator',    N'Seed sowing, ready stock confirmation'),
    (N'Booking Executive',  N'Customer bookings'),
    (N'Dispatch Executive', N'Order allocation, dispatch and delivery')
) AS v(Name, Description) ON COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), ''), r.RoleName) = v.Name
WHERE r.Description IS NULL;
GO

-- ----------------------------------------------------------------------------
-- STEP 3: Permission codes (existing 31 codes are left exactly as they are)
-- ----------------------------------------------------------------------------
INSERT INTO dbo.Permissions (Code, Description)
SELECT v.Code, v.Description
FROM (VALUES
    (N'Sowing.View',          N'View sowing records and sowing reports'),
    (N'Sowing.Enter',         N'Enter / edit sowing records'),
    (N'ReadyStock.View',      N'View ready stock and ready alerts'),
    (N'ReadyStock.Confirm',   N'Confirm ready stock (actual ready quantity / wastage)'),
    (N'SeedStock.View',       N'View Main Office seed stock'),
    (N'SeedStock.Enter',      N'Receive / adjust Main Office seed stock'),
    (N'Fertilizer.View',      N'View fertilizer stock and usage'),
    (N'Fertilizer.Enter',     N'Receive, issue and record fertilizer usage'),
    (N'Fertilizer.Manage',    N'Maintain fertilizer masters (name, type, unit, source)'),
    (N'Labour.View',          N'View labour logs'),
    (N'Labour.Enter',         N'Enter / edit labour logs'),
    (N'Booking.Direct',       N'Legacy direct booking screen'),
    (N'Admin.ManageMasters',  N'Maintain master data (plants/varieties, seed sources)')
) AS v(Code, Description)
WHERE NOT EXISTS (SELECT 1 FROM dbo.Permissions p WHERE p.Code = v.Code);
GO

-- ----------------------------------------------------------------------------
-- STEP 4: UserRoles backfill from legacy designation (users with NO role only)
-- ----------------------------------------------------------------------------
IF OBJECT_ID('tempdb..#UsersWithoutRole') IS NOT NULL DROP TABLE #UsersWithoutRole;

SELECT u.Id AS UserId, r.Id AS RoleId, COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), ''), r.RoleName) AS RoleName
INTO #UsersWithoutRole
FROM dbo.IMSUsers u
JOIN dbo.Designation d ON d.DesignationID = u.DesignationID
JOIN dbo.Roles r ON COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), ''), r.RoleName) = d.DesignationName
WHERE NOT EXISTS (SELECT 1 FROM dbo.UserRoles ur WHERE ur.UserId = u.Id);

-- 4a) The role itself (AreaId NULL).
INSERT INTO dbo.UserRoles (UserId, RoleId, AreaId)
SELECT b.UserId, b.RoleId, NULL
FROM #UsersWithoutRole b
WHERE NOT EXISTS (SELECT 1 FROM dbo.UserRoles ur WHERE ur.UserId = b.UserId AND ur.RoleId = b.RoleId AND ur.AreaId IS NULL);

-- 4b) Area scope: every Area whose SupervisorId is that user.
INSERT INTO dbo.UserRoles (UserId, RoleId, AreaId)
SELECT b.UserId, b.RoleId, a.Id
FROM #UsersWithoutRole b
JOIN dbo.Area a ON a.SupervisorId = b.UserId
WHERE b.RoleName <> N'System Administrator'
  AND NOT EXISTS (SELECT 1 FROM dbo.UserRoles ur WHERE ur.UserId = b.UserId AND ur.RoleId = b.RoleId AND ur.AreaId = a.Id);

DROP TABLE #UsersWithoutRole;
GO

-- ----------------------------------------------------------------------------
-- STEP 5: Default RolePermissions -- ONLY for roles that currently have none.
--   Derived from the legacy designation menu gates in Pages/Shared/_Layout
--   (designation 2 = stock sorting + direct booking, 3 = booking entry,
--   4 = booking fulfilment, 5 = everything) plus the approved workflow.
--   'System Administrator' is intentionally absent: it has full access.
-- ----------------------------------------------------------------------------
IF OBJECT_ID('tempdb..#DefaultGrants') IS NOT NULL DROP TABLE #DefaultGrants;
CREATE TABLE #DefaultGrants (RoleName NVARCHAR(50) NOT NULL, Code NVARCHAR(100) NOT NULL);
INSERT INTO #DefaultGrants (RoleName, Code) VALUES
    (N'Office Coordinator', N'Dashboard.View'),
    (N'Office Coordinator', N'Reports.View'),
    (N'Office Coordinator', N'Booking.View'),
    (N'Office Coordinator', N'Dispatch.View'),
    (N'Office Coordinator', N'ReadyStock.View'),
    (N'Office Coordinator', N'SeedStock.View'),
    (N'Office Coordinator', N'Sowing.View'),

    (N'Sowing Operator', N'Dashboard.View'),
    (N'Sowing Operator', N'Sowing.View'),
    (N'Sowing Operator', N'Sowing.Enter'),
    (N'Sowing Operator', N'ReadyStock.View'),
    (N'Sowing Operator', N'ReadyStock.Confirm'),
    (N'Sowing Operator', N'SeedStock.View'),
    (N'Sowing Operator', N'Booking.Direct'),

    (N'Booking Executive', N'Dashboard.View'),
    (N'Booking Executive', N'Booking.View'),
    (N'Booking Executive', N'Booking.Enter'),
    (N'Booking Executive', N'ReadyStock.View'),

    (N'Dispatch Executive', N'Dashboard.View'),
    (N'Dispatch Executive', N'Dispatch.View'),
    (N'Dispatch Executive', N'Dispatch.Enter'),
    (N'Dispatch Executive', N'Booking.View'),
    (N'Dispatch Executive', N'ReadyStock.View'),

    (N'Fertilizer Supervisor', N'Dashboard.View'),
    (N'Fertilizer Supervisor', N'Fertilizer.View'),
    (N'Fertilizer Supervisor', N'Fertilizer.Enter'),

    (N'Mother Plant Supervisor', N'Dashboard.View'),
    (N'Mother Plant Supervisor', N'MotherPlant.View'),
    (N'Mother Plant Supervisor', N'MotherPlant.Enter'),
    (N'Mother Plant Supervisor', N'CuttingPlan.View'),
    (N'Mother Plant Supervisor', N'CuttingDelivery.View'),
    (N'Mother Plant Supervisor', N'CuttingDelivery.Enter'),
    (N'Mother Plant Supervisor', N'InternalTransfer.View'),
    (N'Mother Plant Supervisor', N'PotProduction.View');

INSERT INTO dbo.RolePermissions (RoleId, PermissionId)
SELECT r.Id, p.Id
FROM #DefaultGrants g
JOIN dbo.Roles r ON COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), ''), r.RoleName) = g.RoleName
JOIN dbo.Permissions p ON p.Code = g.Code
WHERE NOT EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = r.Id)   -- role untouched so far
  AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = r.Id AND rp.PermissionId = p.Id);

DROP TABLE #DefaultGrants;
GO

-- ----------------------------------------------------------------------------
-- VERIFY (read-only)
-- ----------------------------------------------------------------------------
SELECT r.Id, COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), ''), r.RoleName) AS RoleName, r.IsSystemRole,
       (SELECT COUNT(*) FROM dbo.RolePermissions rp WHERE rp.RoleId = r.Id) AS PermissionCount,
       (SELECT COUNT(DISTINCT ur.UserId) FROM dbo.UserRoles ur WHERE ur.RoleId = r.Id) AS UserCount
FROM dbo.Roles r ORDER BY r.Id;

SELECT u.Id, u.Username, u.IsActive, d.DesignationName,
       STUFF((SELECT N', ' + COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), ''), r.RoleName)
                     + CASE WHEN ur.AreaId IS NULL THEN N'' ELSE N' @Area ' + CAST(ur.AreaId AS NVARCHAR(10)) END
              FROM dbo.UserRoles ur JOIN dbo.Roles r ON r.Id = ur.RoleId
              WHERE ur.UserId = u.Id FOR XML PATH('')), 1, 2, N'') AS Roles
FROM dbo.IMSUsers u
LEFT JOIN dbo.Designation d ON d.DesignationID = u.DesignationID
ORDER BY u.Id;
GO
