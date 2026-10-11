-- ============================================================================
-- ZZTEST_BedSowingUsers.sql -- SCRATCH-ONLY browser-test logins for Bed Sowing (2026-10-12).
-- Refuses any database not named PlantsIMS2_Scratch_*. Never run against PlantsIMS2_Test or
-- production. Requires 2026-10-12_BedSowing.sql. Password: the same test hash (and password) as
-- ZZTEST_StoreUsers.sql.
--
--   zztest.bed.maya    Sowing Operator + Bed Sowing Transplanter   Area 2  (stands in for Maya:
--                      the real transplanter's password is never used. The role belongs to ONE
--                      person (TR_UserRoles_BedTransplanter), so on this SCRATCH copy it is MOVED
--                      from the migration's holder to this test login -- logged in dbo.BedSowingAuditLog)
--   zztest.bed.op      Sowing Operator                             Area 2  (records bed sowings, cannot transplant)
--   zztest.bed.sup     Sowing Supervisor                           Area 2  (approves the tray batches)
--   zztest.bed.admin   System Administrator                        (all Areas)
-- Idempotent. Remove with the DELETE block at the bottom (commented out).
-- ============================================================================
:on error exit
IF DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'
    THROW 52190, 'ZZTEST_BedSowingUsers.sql may only be run against a PlantsIMS2_Scratch_* copy.', 1;
IF OBJECT_ID('dbo.BedSowings', 'U') IS NULL
    THROW 52191, 'Run 2026-10-12_BedSowing.sql on this scratch copy first.', 1;
GO
SET NOCOUNT ON;
DECLARE @hash NVARCHAR(100) = N'$2a$11$uyHITMZfN1Cit91MLQsY6umSRpOtcOJUyN9uHPNU.ZhebigEOdxUS';
DECLARE @users TABLE (Username NVARCHAR(100), Name NVARCHAR(100), RoleName NVARCHAR(100), AreaId INT NULL);
INSERT INTO @users VALUES
    (N'zztest.bed.maya',  N'ZZTEST-BED-TRANSPLANTER', N'Sowing Operator', 2),
    (N'zztest.bed.maya',  N'ZZTEST-BED-TRANSPLANTER', N'Bed Sowing Transplanter', 2),
    (N'zztest.bed.op',    N'ZZTEST-BED-OPERATOR',     N'Sowing Operator', 2),
    (N'zztest.bed.sup',   N'ZZTEST-BED-SUPERVISOR',   N'Sowing Supervisor', 2),
    (N'zztest.bed.admin', N'ZZTEST-BED-ADMIN',        N'System Administrator', NULL);
-- Scratch only: free the one Transplanter seat for the test login.
DELETE ur FROM dbo.UserRoles ur
JOIN dbo.Roles r ON r.Id = ur.RoleId
JOIN dbo.IMSUsers u ON u.Id = ur.UserId
WHERE COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = N'Bed Sowing Transplanter' AND u.Username <> N'zztest.bed.maya';
INSERT INTO dbo.IMSUsers (Username, Password, Name, IsActive)
SELECT DISTINCT u.Username, @hash, u.Name, 1 FROM @users u WHERE NOT EXISTS (SELECT 1 FROM dbo.IMSUsers x WHERE x.Username = u.Username);
INSERT INTO dbo.UserRoles (UserId, RoleId, AreaId)
SELECT x.Id, r.Id, u.AreaId FROM @users u JOIN dbo.IMSUsers x ON x.Username = u.Username
JOIN dbo.Roles r ON COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = u.RoleName
WHERE NOT EXISTS (SELECT 1 FROM dbo.UserRoles y WHERE y.UserId = x.Id AND y.RoleId = r.Id);
SELECT x.Id, x.Username, COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) AS RoleName, ur.AreaId
FROM dbo.IMSUsers x JOIN dbo.UserRoles ur ON ur.UserId = x.Id JOIN dbo.Roles r ON r.Id = ur.RoleId
WHERE x.Username LIKE N'zztest.bed.%' ORDER BY x.Id;
GO
-- DELETE dbo.UserLoginHistory WHERE UserId IN (SELECT Id FROM dbo.IMSUsers WHERE Username LIKE N'zztest.bed.%');
-- DELETE dbo.UserRoles WHERE UserId IN (SELECT Id FROM dbo.IMSUsers WHERE Username LIKE N'zztest.bed.%');
-- DELETE dbo.IMSUsers WHERE Username LIKE N'zztest.bed.%';   -- only after their bed / sowing rows are gone
