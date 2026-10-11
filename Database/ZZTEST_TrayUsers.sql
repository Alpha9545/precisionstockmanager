-- ============================================================================
-- ZZTEST_TrayUsers.sql -- SCRATCH-ONLY browser-test logins for Tray Adjustment / Tray
-- Transfers (2026-10-10). Refuses any database not named PlantsIMS2_Scratch_*.
-- Never run against PlantsIMS2_Test or production. Requires 2026-10-10_TrayTransfers.sql.
-- Password: the same test hash (and password) as ZZTEST_StoreUsers.sql.
--
--   zztest.tt.admin    System Administrator       (all Areas)
--   zztest.tt.sowing   Sowing Supervisor          Area 2   (adjusts, sends)
--   zztest.tt.oc       Office Coordinator         Area 2   (sends from Mother Plant Areas)
--   zztest.tt.mp       Mother Plant Supervisor    Area 127 (receives)
--   zztest.tt.booking  Booking Executive          Area 2   (role-access smoke test)
-- Idempotent. Remove with the DELETE block at the bottom (commented out).
-- ============================================================================
:on error exit
IF DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52090, 'ZZTEST_TrayUsers.sql may only be run against a PlantsIMS2_Scratch_* copy.', 1;
IF OBJECT_ID('dbo.TrayTransfers', 'U') IS NULL
    THROW 52091, 'Run 2026-10-10_TrayTransfers.sql on this scratch copy first.', 1;
GO
SET NOCOUNT ON;
DECLARE @hash NVARCHAR(100) = N'$2a$11$uyHITMZfN1Cit91MLQsY6umSRpOtcOJUyN9uHPNU.ZhebigEOdxUS';
DECLARE @users TABLE (Username NVARCHAR(100), Name NVARCHAR(100), RoleName NVARCHAR(100), AreaId INT NULL);
INSERT INTO @users VALUES
    (N'zztest.tt.admin',  N'ZZTEST-TT-ADMIN',      N'System Administrator', NULL),
    (N'zztest.tt.sowing', N'ZZTEST-TT-SOWING-SUP', N'Sowing Supervisor', 2),
    (N'zztest.tt.oc',     N'ZZTEST-TT-OFFICE',     N'Office Coordinator', 2),
    (N'zztest.tt.mp',     N'ZZTEST-TT-MP-SUP',     N'Mother Plant Supervisor', 127),
    (N'zztest.tt.booking', N'ZZTEST-TT-BOOKING',   N'Booking Executive', 2);
INSERT INTO dbo.IMSUsers (Username, Password, Name, IsActive)
SELECT u.Username, @hash, u.Name, 1 FROM @users u WHERE NOT EXISTS (SELECT 1 FROM dbo.IMSUsers x WHERE x.Username = u.Username);
INSERT INTO dbo.UserRoles (UserId, RoleId, AreaId)
SELECT x.Id, r.Id, u.AreaId FROM @users u JOIN dbo.IMSUsers x ON x.Username = u.Username
JOIN dbo.Roles r ON COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = u.RoleName
WHERE NOT EXISTS (SELECT 1 FROM dbo.UserRoles y WHERE y.UserId = x.Id AND y.RoleId = r.Id);
SELECT x.Id, x.Username, COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) AS RoleName, ur.AreaId
FROM dbo.IMSUsers x JOIN dbo.UserRoles ur ON ur.UserId = x.Id JOIN dbo.Roles r ON r.Id = ur.RoleId
WHERE x.Username LIKE N'zztest.tt.%' ORDER BY x.Id;
GO
-- DELETE dbo.UserLoginHistory WHERE UserId IN (SELECT Id FROM dbo.IMSUsers WHERE Username LIKE N'zztest.tt.%');
-- DELETE dbo.UserRoles WHERE UserId IN (SELECT Id FROM dbo.IMSUsers WHERE Username LIKE N'zztest.tt.%');
-- DELETE dbo.IMSUsers WHERE Username LIKE N'zztest.tt.%';   -- only after their tray rows are gone
