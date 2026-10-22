-- ============================================================================
-- ZZTEST_Wf1019Users.sql -- SCRATCH-ONLY browser-test logins for the 2026-10-19 workflow corrections
-- (Mother Plant Sowing Approvals, Sowing Approvals visibility, Mother Plant Ready Stock actions, 70 Cavity).
-- Refuses any database not named PlantsIMS2_Scratch_*. Never run against PlantsIMS2_Test or production.
-- Password: the same test hash (and password) as ZZTEST_StoreUsers.sql.
--
--   zztest.wf19.mp1     Mother Plant Supervisor   Area 1   (Shree Swami Samarth Agro -- an Area Supervisor)
--   zztest.wf19.mp2     Mother Plant Supervisor   Area 1   (a SECOND Area Supervisor of the same Area)
--   zztest.wf19.mpother Mother Plant Supervisor   Area 127 (Green Bless Nursery -- another Mother Plant Area)
--   zztest.wf19.viewer  Booking Executive         Area 2   (may VIEW Sowing Approvals, may not approve)
--   zztest.wf19.sowsup  Sowing Supervisor         Area 2   (Main Office approver, not the Bed Sowing approver)
--   zztest.wf19.admin   System Administrator      (all Areas)
-- Idempotent. Remove with the DELETE block at the bottom (commented out).
-- ============================================================================
:on error exit
IF DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52596, 'ZZTEST_Wf1019Users.sql may only be run against a PlantsIMS2_Scratch_* copy.', 1;
GO
SET NOCOUNT ON;
DECLARE @hash NVARCHAR(100) = N'$2a$11$uyHITMZfN1Cit91MLQsY6umSRpOtcOJUyN9uHPNU.ZhebigEOdxUS';
DECLARE @users TABLE (Username NVARCHAR(100), Name NVARCHAR(100), RoleName NVARCHAR(100), AreaId INT NULL);
INSERT INTO @users VALUES
    (N'zztest.wf19.mp1',     N'ZZTEST-WF19-MP-SUP-1',     N'Mother Plant Supervisor', 1),
    (N'zztest.wf19.mp2',     N'ZZTEST-WF19-MP-SUP-2',     N'Mother Plant Supervisor', 1),
    (N'zztest.wf19.mpother', N'ZZTEST-WF19-MP-OTHER',     N'Mother Plant Supervisor', 127),
    (N'zztest.wf19.viewer',  N'ZZTEST-WF19-VIEWER',       N'Booking Executive', 2),
    (N'zztest.wf19.sowsup',  N'ZZTEST-WF19-SOWING-SUP',   N'Sowing Supervisor', 2),
    (N'zztest.wf19.admin',   N'ZZTEST-WF19-ADMIN',        N'System Administrator', NULL);
INSERT INTO dbo.IMSUsers (Username, Password, Name, IsActive)
SELECT DISTINCT u.Username, @hash, u.Name, 1 FROM @users u WHERE NOT EXISTS (SELECT 1 FROM dbo.IMSUsers x WHERE x.Username = u.Username);
INSERT INTO dbo.UserRoles (UserId, RoleId, AreaId)
SELECT x.Id, r.Id, u.AreaId FROM @users u JOIN dbo.IMSUsers x ON x.Username = u.Username
JOIN dbo.Roles r ON COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = u.RoleName
WHERE NOT EXISTS (SELECT 1 FROM dbo.UserRoles y WHERE y.UserId = x.Id AND y.RoleId = r.Id);
SELECT x.Id, x.Username, COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) AS RoleName, ur.AreaId
FROM dbo.IMSUsers x JOIN dbo.UserRoles ur ON ur.UserId = x.Id JOIN dbo.Roles r ON r.Id = ur.RoleId
WHERE x.Username LIKE N'zztest.wf19.%' ORDER BY x.Id;
GO
-- DELETE dbo.UserLoginHistory WHERE UserId IN (SELECT Id FROM dbo.IMSUsers WHERE Username LIKE N'zztest.wf19.%');
-- DELETE dbo.UserRoles WHERE UserId IN (SELECT Id FROM dbo.IMSUsers WHERE Username LIKE N'zztest.wf19.%');
-- DELETE dbo.IMSUsers WHERE Username LIKE N'zztest.wf19.%';   -- only after their approval / sale / transfer rows are gone
