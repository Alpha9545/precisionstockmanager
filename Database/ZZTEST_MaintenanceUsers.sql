-- ============================================================================
-- ZZTEST_MaintenanceUsers.sql -- SCRATCH-ONLY test logins for the Maintenance
-- Tickets module (2026-10-06), used by MaintenanceE2ETests and browser tests.
-- Refuses any database not named PlantsIMS2_Scratch_*. Never run against
-- PlantsIMS2_Test or production. Requires 2026-10-06_MaintenanceTickets.sql.
--
-- Test password for every user: ZzLabour-8f95d45d   (same hash as ZZTEST_LabourBrowserUsers.sql)
--   zztest.mt.electrician1   Electrician (no Area)
--   zztest.mt.electrician2   Electrician (no Area)
--   zztest.mt.plumber        Plumber     (no Area)
--   zztest.mt.sowing         Sowing Supervisor         Area 2   (Main-Office-Areas)
--   zztest.mt.mp             Mother Plant Supervisor   Area 127 (Green Bless Nursery)
--   zztest.mt.admin          System Administrator      (all Areas)
-- Idempotent. Remove with the DELETE block at the bottom (commented out).
-- ============================================================================
:on error exit
IF DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 51790, 'ZZTEST_MaintenanceUsers.sql may only be run against a PlantsIMS2_Scratch_* copy.', 1;
IF OBJECT_ID('dbo.MaintenanceCategories', 'U') IS NULL
    THROW 51791, 'Run 2026-10-06_MaintenanceTickets.sql on this scratch copy first.', 1;
GO
SET NOCOUNT ON;
DECLARE @hash NVARCHAR(100) = N'$2a$11$uyHITMZfN1Cit91MLQsY6umSRpOtcOJUyN9uHPNU.ZhebigEOdxUS';
DECLARE @users TABLE (Username NVARCHAR(100), Name NVARCHAR(100), RoleName NVARCHAR(100), AreaId INT NULL);
INSERT INTO @users VALUES
    (N'zztest.mt.electrician1', N'ZZTEST-MT-ELECTRICIAN-1', N'Electrician', NULL),
    (N'zztest.mt.electrician2', N'ZZTEST-MT-ELECTRICIAN-2', N'Electrician', NULL),
    (N'zztest.mt.plumber',      N'ZZTEST-MT-PLUMBER',       N'Plumber', NULL),
    (N'zztest.mt.sowing',       N'ZZTEST-MT-SOWING-SUP',    N'Sowing Supervisor', 2),
    (N'zztest.mt.mp',           N'ZZTEST-MT-MP-SUP',        N'Mother Plant Supervisor', 127),
    (N'zztest.mt.admin',        N'ZZTEST-MT-ADMIN',         N'System Administrator', NULL);

INSERT INTO dbo.IMSUsers (Username, Password, Name, IsActive)
SELECT u.Username, @hash, u.Name, 1 FROM @users u
WHERE NOT EXISTS (SELECT 1 FROM dbo.IMSUsers x WHERE x.Username = u.Username);

INSERT INTO dbo.UserRoles (UserId, RoleId, AreaId)
SELECT x.Id, r.Id, u.AreaId
FROM @users u
JOIN dbo.IMSUsers x ON x.Username = u.Username
JOIN dbo.Roles r ON COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = u.RoleName
WHERE NOT EXISTS (SELECT 1 FROM dbo.UserRoles y WHERE y.UserId = x.Id AND y.RoleId = r.Id);

SELECT x.Id, x.Username, COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) AS RoleName, ur.AreaId
FROM dbo.IMSUsers x JOIN dbo.UserRoles ur ON ur.UserId = x.Id JOIN dbo.Roles r ON r.Id = ur.RoleId
WHERE x.Username LIKE N'zztest.mt.%' ORDER BY x.Id;
GO
/* Cleanup (scratch only):
DELETE dbo.MaintenanceTickets WHERE RequestedById IN (SELECT Id FROM dbo.IMSUsers WHERE Username LIKE N'zztest.mt.%') OR AssignedWorkerId IN (SELECT Id FROM dbo.IMSUsers WHERE Username LIKE N'zztest.mt.%');
DELETE dbo.UserLoginHistory WHERE UserId IN (SELECT Id FROM dbo.IMSUsers WHERE Username LIKE N'zztest.mt.%');
DELETE dbo.UserRoles WHERE UserId IN (SELECT Id FROM dbo.IMSUsers WHERE Username LIKE N'zztest.mt.%');
DELETE dbo.IMSUsers WHERE Username LIKE N'zztest.mt.%';
*/
