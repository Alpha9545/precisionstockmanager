-- ============================================================================
-- Phase 14: Role/Permission Foundation + Area Extension
-- ============================================================================
-- Purpose: lay the database foundation for the workflow/role redesign
-- (see claude/plantstockmanager-phase2-redesign-plan.md), Migration Plan
-- steps 1-2. This script ONLY adds columns/tables/seed data; it does not
-- touch any Phase 2-13 table's existing columns, rows, or behavior, and it
-- does not wire up any enforcement (nothing is denied access by this script
-- alone -- that happens in application code, reading these new tables).
--
-- Additive, idempotent, safe to re-run: every ALTER/CREATE/seed is guarded
-- with an existence check, exactly like every prior phase script. No DROP,
-- no DELETE, no data loss possible from running this script.
--
-- Decisions this script implements (confirmed with the business owner):
--   1) dbo.Area.PolyhouseId is loosened back to NULL-able. Main Office is a
--      central stock/verification location with no Polyhouse of its own;
--      forcing a fake Polyhouse relationship was explicitly rejected.
--      (The FK_Area_Polyhouse constraint added in Phase 2 already tolerates
--      NULL by design -- see that script's own comment -- so nothing about
--      the FK needs to change here.)
--   2) Kunjir/Kiran/Outlet/Main Office are modelled as dbo.Area rows
--      distinguished by a new AreaType column, NOT a parallel Locations
--      table -- Area is already the FK target of PottedPlantStock.AreaId
--      and EmptyPotInventory.AreaId (Phase 8), so this reuses that link.
--   3) Roles/Permissions are new, generic, admin-configurable tables
--      (Roles -> RolePermissions -> Permissions, and UserRoles which scopes
--      a role to a specific Area for a specific user). dbo.Designation
--      (job title) is untouched and is NOT repurposed as the permission
--      system.
-- ============================================================================


-- ----------------------------------------------------------------------------
-- PART 1: dbo.Area extension
-- ----------------------------------------------------------------------------

-- 1a) Loosen PolyhouseId back to nullable. Phase 2 tightened it to NOT NULL
--     once no NULL rows existed; Main Office rows now need to legitimately
--     be NULL, so relax it. This is safe: the FK stays exactly as-is (a
--     NULL FK column value never violates a foreign key), and both of
--     Phase 2's uniqueness indexes on (PolyhouseId, Name) / (PolyhouseId,
--     AreaCode) are already filtered to WHERE PolyhouseId IS NOT NULL, so
--     they are unaffected by rows that are NULL.
IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.Area') AND name = 'PolyhouseId' AND is_nullable = 0
)
BEGIN
    ALTER TABLE dbo.Area ALTER COLUMN PolyhouseId INT NULL;
END
GO

-- 1b) AreaType: distinguishes what kind of location this Area row is.
--     NULL = a plain/legacy Area (today's behavior, unchanged).
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Area') AND name = 'AreaType')
BEGIN
    ALTER TABLE dbo.Area ADD AreaType NVARCHAR(30) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_Area_AreaType')
BEGIN
    ALTER TABLE dbo.Area ADD CONSTRAINT CK_Area_AreaType
        CHECK (AreaType IS NULL OR AreaType IN ('MotherPlant', 'Kunjir', 'Kiran', 'Outlet', 'MainOffice'));
END
GO

-- 1c) SupervisorId: the person responsible for this Area (used by the new
--     role model to scope a Supervisor role to their own Area(s)).
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Area') AND name = 'SupervisorId')
BEGIN
    ALTER TABLE dbo.Area ADD SupervisorId INT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Area_Supervisor')
BEGIN
    ALTER TABLE dbo.Area
        ADD CONSTRAINT FK_Area_Supervisor FOREIGN KEY (SupervisorId) REFERENCES dbo.IMSUsers(Id);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Area_SupervisorId' AND object_id = OBJECT_ID('dbo.Area'))
BEGIN
    CREATE INDEX IX_Area_SupervisorId ON dbo.Area(SupervisorId);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Area_AreaType' AND object_id = OBJECT_ID('dbo.Area'))
BEGIN
    CREATE INDEX IX_Area_AreaType ON dbo.Area(AreaType);
END
GO

-- 1d) Location / Remarks: free-text fields requested for the Area admin form.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Area') AND name = 'Location')
BEGIN
    ALTER TABLE dbo.Area ADD Location NVARCHAR(200) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Area') AND name = 'Remarks')
BEGIN
    ALTER TABLE dbo.Area ADD Remarks NVARCHAR(500) NULL;
END
GO


-- ----------------------------------------------------------------------------
-- PART 2: Role / Permission tables
-- ----------------------------------------------------------------------------

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Roles' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.Roles
    (
        Id           INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Roles PRIMARY KEY,
        Name         NVARCHAR(50)      NOT NULL,
        Description  NVARCHAR(200)     NULL,
        IsSystemRole BIT               NOT NULL CONSTRAINT DF_Roles_IsSystemRole DEFAULT (0),
        CreatedDate  DATETIME2         NOT NULL CONSTRAINT DF_Roles_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT UQ_Roles_Name UNIQUE (Name)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Permissions' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.Permissions
    (
        Id          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Permissions PRIMARY KEY,
        Code        NVARCHAR(100)     NOT NULL,
        Description NVARCHAR(200)     NULL,
        CONSTRAINT UQ_Permissions_Code UNIQUE (Code)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'RolePermissions' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.RolePermissions
    (
        RoleId       INT NOT NULL,
        PermissionId INT NOT NULL,
        CONSTRAINT PK_RolePermissions PRIMARY KEY (RoleId, PermissionId),
        CONSTRAINT FK_RolePermissions_Role       FOREIGN KEY (RoleId)       REFERENCES dbo.Roles(Id)       ON DELETE CASCADE,
        CONSTRAINT FK_RolePermissions_Permission  FOREIGN KEY (PermissionId) REFERENCES dbo.Permissions(Id) ON DELETE CASCADE
    );
END
GO

-- UserRoles: assigns a Role to a User, optionally scoped to one Area (e.g.
-- "this user is a KunjirSupervisor for Area #7 specifically"). AreaId is
-- NULL for roles that are not area-scoped (Admin, Management, LabWorker).
-- The UNIQUE constraint below prevents the same (User, Role, Area)
-- combination being granted twice; SQL Server treats a repeated NULL in a
-- UNIQUE constraint as a duplicate (unlike a filtered unique INDEX), which
-- is exactly what we want here -- it stops the same non-area-scoped role
-- being assigned to the same user more than once.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'UserRoles' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.UserRoles
    (
        Id          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_UserRoles PRIMARY KEY,
        UserId      INT NOT NULL,
        RoleId      INT NOT NULL,
        AreaId      INT NULL,
        CreatedDate DATETIME2 NOT NULL CONSTRAINT DF_UserRoles_CreatedDate DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_UserRoles_User FOREIGN KEY (UserId) REFERENCES dbo.IMSUsers(Id),
        CONSTRAINT FK_UserRoles_Role FOREIGN KEY (RoleId) REFERENCES dbo.Roles(Id),
        CONSTRAINT FK_UserRoles_Area FOREIGN KEY (AreaId) REFERENCES dbo.Area(Id),
        CONSTRAINT UQ_UserRoles_User_Role_Area UNIQUE (UserId, RoleId, AreaId)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_UserRoles_UserId' AND object_id = OBJECT_ID('dbo.UserRoles'))
BEGIN
    CREATE INDEX IX_UserRoles_UserId ON dbo.UserRoles(UserId);
END
GO


-- ----------------------------------------------------------------------------
-- PART 3: Seed roles, permissions, and sensible default grants
-- ----------------------------------------------------------------------------
-- All admin-adjustable afterwards through Pages/Admin/Roles and
-- Pages/Admin/UserRoles -- this seed only ensures the app is usable
-- immediately after this script runs, it is not meant to be the final word.

-- 3a) Roles
INSERT INTO dbo.Roles (Name, Description, IsSystemRole)
SELECT v.Name, v.Description, 1
FROM (VALUES
    ('Admin',                  'Full system access and administration'),
    ('MotherPlantSupervisor',  'Manages assigned Mother Plant area(s)'),
    ('MainOfficeOfficer',      'Confirms incoming deliveries and routes stock from Main Office'),
    ('KunjirSupervisor',       'Manages assigned Kunjir area(s)'),
    ('KiranSupervisor',        'Manages assigned Kiran area(s); direct pot/tray production'),
    ('OutletSupervisor',       'Manages assigned Outlet area(s): receipts, sales'),
    ('LabWorker',              'Lab request workflow'),
    ('Management',             'Cross-location read-only visibility and reports')
) AS v(Name, Description)
WHERE NOT EXISTS (SELECT 1 FROM dbo.Roles r WHERE r.Name = v.Name);
GO

-- 3b) Permission codes covering the redesign's planned menu sections.
INSERT INTO dbo.Permissions (Code, Description)
SELECT v.Code, v.Description
FROM (VALUES
    ('Dashboard.View',        'View the dashboard'),
    ('MotherPlant.View',      'View Mother Plant records'),
    ('MotherPlant.Enter',     'Enter/edit Mother Plant records and cuttings'),
    ('MainOffice.View',       'View Main Office stock'),
    ('MainOffice.Confirm',    'Confirm incoming deliveries and route to a destination'),
    ('Kunjir.View',           'View Kunjir area records'),
    ('Kunjir.Enter',          'Enter Kunjir take/give transactions'),
    ('Kiran.View',            'View Kiran area records'),
    ('Kiran.Enter',           'Enter Kiran direct pot/tray production'),
    ('CuttingPlan.View',      'View Cutting Plan (hidden from the normal menu)'),
    ('CuttingDelivery.View',  'View cutting delivery records'),
    ('CuttingDelivery.Enter', 'Enter cutting delivery records'),
    ('PotProduction.View',    'View pot/tray production records'),
    ('PotProduction.Enter',   'Enter pot/tray production records'),
    ('InternalTransfer.View', 'View internal transfers'),
    ('InternalTransfer.Enter','Enter/confirm internal transfers'),
    ('Outlet.View',           'View Outlet stock and receipts'),
    ('Outlet.Confirm',        'Confirm pending receipts at Outlet'),
    ('Outlet.Sell',           'Record Outlet sales/dispatch'),
    ('Booking.View',          'View potted plant bookings'),
    ('Booking.Enter',         'Enter potted plant bookings'),
    ('Dispatch.View',         'View dispatches'),
    ('Dispatch.Enter',        'Enter dispatches'),
    ('Lab.View',              'View lab requests'),
    ('Lab.Enter',             'Enter lab requests'),
    ('Purchase.View',         'View vendor purchases/purchase orders'),
    ('Purchase.Enter',        'Enter purchase orders/receipts'),
    ('Reports.View',          'View cross-location reports'),
    ('Admin.ManageAreas',     'Manage Areas'),
    ('Admin.ManageUsers',     'Manage user role/area assignments'),
    ('Admin.ManageRoles',     'Manage roles and their permissions')
) AS v(Code, Description)
WHERE NOT EXISTS (SELECT 1 FROM dbo.Permissions p WHERE p.Code = v.Code);
GO

-- 3c) Admin gets every permission that exists (including any added later).
INSERT INTO dbo.RolePermissions (RoleId, PermissionId)
SELECT r.Id, p.Id
FROM dbo.Roles r
CROSS JOIN dbo.Permissions p
WHERE r.Name = 'Admin'
  AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = r.Id AND rp.PermissionId = p.Id);
GO

-- 3d) Management gets every read-only ("*.View") permission -- cross-location
--     visibility, no data-entry actions, per the approved role table.
INSERT INTO dbo.RolePermissions (RoleId, PermissionId)
SELECT r.Id, p.Id
FROM dbo.Roles r
CROSS JOIN dbo.Permissions p
WHERE r.Name = 'Management'
  AND p.Code LIKE '%.View'
  AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = r.Id AND rp.PermissionId = p.Id);
GO

-- 3e) Sensible default grants for the remaining roles, matching Section B
--     of the redesign plan. Admin can freely add/remove any of these later
--     through Pages/Admin/Roles.
INSERT INTO dbo.RolePermissions (RoleId, PermissionId)
SELECT r.Id, p.Id FROM dbo.Roles r CROSS JOIN dbo.Permissions p
WHERE r.Name = 'MotherPlantSupervisor'
  AND p.Code IN ('Dashboard.View','MotherPlant.View','MotherPlant.Enter','InternalTransfer.View','InternalTransfer.Enter')
  AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = r.Id AND rp.PermissionId = p.Id);
GO

INSERT INTO dbo.RolePermissions (RoleId, PermissionId)
SELECT r.Id, p.Id FROM dbo.Roles r CROSS JOIN dbo.Permissions p
WHERE r.Name = 'MainOfficeOfficer'
  AND p.Code IN ('Dashboard.View','MainOffice.View','MainOffice.Confirm','InternalTransfer.View','InternalTransfer.Enter','CuttingDelivery.View')
  AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = r.Id AND rp.PermissionId = p.Id);
GO

INSERT INTO dbo.RolePermissions (RoleId, PermissionId)
SELECT r.Id, p.Id FROM dbo.Roles r CROSS JOIN dbo.Permissions p
WHERE r.Name = 'KunjirSupervisor'
  AND p.Code IN ('Dashboard.View','Kunjir.View','Kunjir.Enter','InternalTransfer.View','InternalTransfer.Enter')
  AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = r.Id AND rp.PermissionId = p.Id);
GO

INSERT INTO dbo.RolePermissions (RoleId, PermissionId)
SELECT r.Id, p.Id FROM dbo.Roles r CROSS JOIN dbo.Permissions p
WHERE r.Name = 'KiranSupervisor'
  AND p.Code IN ('Dashboard.View','Kiran.View','Kiran.Enter','PotProduction.View','PotProduction.Enter')
  AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = r.Id AND rp.PermissionId = p.Id);
GO

INSERT INTO dbo.RolePermissions (RoleId, PermissionId)
SELECT r.Id, p.Id FROM dbo.Roles r CROSS JOIN dbo.Permissions p
WHERE r.Name = 'OutletSupervisor'
  AND p.Code IN ('Dashboard.View','Outlet.View','Outlet.Confirm','Outlet.Sell','Booking.View','Booking.Enter','Dispatch.View','Dispatch.Enter')
  AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = r.Id AND rp.PermissionId = p.Id);
GO

INSERT INTO dbo.RolePermissions (RoleId, PermissionId)
SELECT r.Id, p.Id FROM dbo.Roles r CROSS JOIN dbo.Permissions p
WHERE r.Name = 'LabWorker'
  AND p.Code IN ('Dashboard.View','Lab.View','Lab.Enter')
  AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = r.Id AND rp.PermissionId = p.Id);
GO

-- 3f) Safety net (Migration Plan step 4): grant every EXISTING active
--     IMSUsers row the Admin role, AreaId NULL, so nobody is locked out the
--     moment enforcement is wired up in application code. This is meant to
--     be temporary -- the business owner narrows real users down to their
--     real role(s) via Pages/Admin/UserRoles at their own pace afterwards.
--     Safe to re-run: the NOT EXISTS guard means a user who has already
--     been narrowed down (their blanket Admin grant removed) does NOT get
--     it re-added by re-running this script.
INSERT INTO dbo.UserRoles (UserId, RoleId, AreaId)
SELECT u.Id, r.Id, NULL
FROM dbo.IMSUsers u
CROSS JOIN dbo.Roles r
WHERE r.Name = 'Admin'
  AND u.IsActive = 1
  AND NOT EXISTS (
      SELECT 1 FROM dbo.UserRoles ur WHERE ur.UserId = u.Id
      -- "NOT EXISTS ... UserId = u.Id" (not just this Role) so a user who
      -- already has ANY role assignment (including a real, narrowed-down
      -- one applied before this script last ran) is left alone.
  );
GO

-- ============================================================================
-- End of Phase 14. Nothing above denies any current user access to
-- anything -- enforcement is added in application code (Program.cs /
-- Authorization/*), which as a safety net assigns every EXISTING IMSUsers
-- row the Admin role (see Migration step 4) so nobody is locked out. Real
-- role/area assignments are then made through Pages/Admin/UserRoles.
-- ============================================================================
