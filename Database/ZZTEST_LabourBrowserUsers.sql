-- ============================================================================
-- ZZTEST_LabourBrowserUsers.sql -- SCRATCH-ONLY browser-test logins for the
-- Daily Labour module (2026-10-04). Refuses any database not named
-- PlantsIMS2_Scratch_*. Never run against PlantsIMS2_Test or production.
--
-- Test password for all four users: ZzLabour-8f95d45d
--   zztest.labour.sowing   Sowing Supervisor        Area 2   (Main-Office-Areas)
--   zztest.labour.mp       Mother Plant Supervisor  Area 127 (Green Bless Nursery)
--   zztest.labour.booking  Booking Executive        Area 2   (no Labour permission)
--   zztest.labour.admin    System Administrator     (all Areas)
-- Idempotent. Remove with the DELETE block at the bottom (commented out).
-- ============================================================================
:on error exit
IF DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 51790, 'ZZTEST_LabourBrowserUsers.sql may only be run against a PlantsIMS2_Scratch_* copy.', 1;
GO
SET NOCOUNT ON;
DECLARE @hash NVARCHAR(100) = N'$2a$11$uyHITMZfN1Cit91MLQsY6umSRpOtcOJUyN9uHPNU.ZhebigEOdxUS';
IF NOT EXISTS (SELECT 1 FROM dbo.IMSUsers WHERE Username = N'zztest.labour.sowing')
    INSERT INTO dbo.IMSUsers (Username, Password, Name, IsActive) VALUES (N'zztest.labour.sowing', @hash, N'ZZTEST-LABOUR-SOWING-SUP', 1);
INSERT INTO dbo.UserRoles (UserId, RoleId, AreaId)
SELECT u.Id, r.Id, 2 FROM dbo.IMSUsers u CROSS JOIN dbo.Roles r
WHERE u.Username = N'zztest.labour.sowing' AND r.Name = N'Sowing Supervisor'
  AND NOT EXISTS (SELECT 1 FROM dbo.UserRoles x WHERE x.UserId = u.Id AND x.RoleId = r.Id);
IF NOT EXISTS (SELECT 1 FROM dbo.IMSUsers WHERE Username = N'zztest.labour.mp')
    INSERT INTO dbo.IMSUsers (Username, Password, Name, IsActive) VALUES (N'zztest.labour.mp', @hash, N'ZZTEST-LABOUR-MP-SUP', 1);
INSERT INTO dbo.UserRoles (UserId, RoleId, AreaId)
SELECT u.Id, r.Id, 127 FROM dbo.IMSUsers u CROSS JOIN dbo.Roles r
WHERE u.Username = N'zztest.labour.mp' AND r.Name = N'Mother Plant Supervisor'
  AND NOT EXISTS (SELECT 1 FROM dbo.UserRoles x WHERE x.UserId = u.Id AND x.RoleId = r.Id);
IF NOT EXISTS (SELECT 1 FROM dbo.IMSUsers WHERE Username = N'zztest.labour.booking')
    INSERT INTO dbo.IMSUsers (Username, Password, Name, IsActive) VALUES (N'zztest.labour.booking', @hash, N'ZZTEST-LABOUR-BOOKING', 1);
INSERT INTO dbo.UserRoles (UserId, RoleId, AreaId)
SELECT u.Id, r.Id, 2 FROM dbo.IMSUsers u CROSS JOIN dbo.Roles r
WHERE u.Username = N'zztest.labour.booking' AND r.Name = N'Booking Executive'
  AND NOT EXISTS (SELECT 1 FROM dbo.UserRoles x WHERE x.UserId = u.Id AND x.RoleId = r.Id);
IF NOT EXISTS (SELECT 1 FROM dbo.IMSUsers WHERE Username = N'zztest.labour.admin')
    INSERT INTO dbo.IMSUsers (Username, Password, Name, IsActive) VALUES (N'zztest.labour.admin', @hash, N'ZZTEST-LABOUR-ADMIN', 1);
INSERT INTO dbo.UserRoles (UserId, RoleId, AreaId)
SELECT u.Id, r.Id, NULL FROM dbo.IMSUsers u CROSS JOIN dbo.Roles r
WHERE u.Username = N'zztest.labour.admin' AND r.Name = N'System Administrator'
  AND NOT EXISTS (SELECT 1 FROM dbo.UserRoles x WHERE x.UserId = u.Id AND x.RoleId = r.Id);
SELECT u.Id, u.Username, r.Name AS RoleName, ur.AreaId FROM dbo.IMSUsers u JOIN dbo.UserRoles ur ON ur.UserId = u.Id JOIN dbo.Roles r ON r.Id = ur.RoleId WHERE u.Username LIKE N'zztest.labour.%';
GO
/* Cleanup (scratch only):
DELETE dbo.DailyLabourCounts WHERE CreatedById IN (SELECT Id FROM dbo.IMSUsers WHERE Username LIKE N'zztest.labour.%') OR ModifiedById IN (SELECT Id FROM dbo.IMSUsers WHERE Username LIKE N'zztest.labour.%');
DELETE dbo.UserLoginHistory WHERE UserId IN (SELECT Id FROM dbo.IMSUsers WHERE Username LIKE N'zztest.labour.%');
DELETE dbo.UserRoles WHERE UserId IN (SELECT Id FROM dbo.IMSUsers WHERE Username LIKE N'zztest.labour.%');
DELETE dbo.IMSUsers WHERE Username LIKE N'zztest.labour.%';
*/
