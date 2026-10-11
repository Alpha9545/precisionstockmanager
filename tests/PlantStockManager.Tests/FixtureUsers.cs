namespace PlantStockManager.Tests
{
    // Real users the E2E classes act as, LOOKED UP in the copy instead of hard-coded. 2026-10-10: user 6
    // (Akshay) was moved from Sowing Supervisor to Sowing Operator in the local data after 2026-10-09,
    // which broke every class that assumed "user 6 = the Main Office Sowing Supervisor". The rule the
    // tests need is "an ACTIVE user holding the Sowing Supervisor role for the Main Office Area";
    // a user with only that role is preferred (then user 6, then the lowest Id). 0 = none (tests skip).
    internal static class FixtureUsers
    {
        public const string MainOfficeSowingSupervisorSql = @"
SELECT TOP 1 u.Id
FROM dbo.UserRoles ur
JOIN dbo.Roles r ON r.Id = ur.RoleId
JOIN dbo.IMSUsers u ON u.Id = ur.UserId AND u.IsActive = 1
WHERE COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = N'Sowing Supervisor' AND ur.AreaId = 2
ORDER BY (SELECT COUNT(*) FROM dbo.UserRoles x WHERE x.UserId = u.Id), CASE WHEN u.Id = 6 THEN 0 ELSE 1 END, u.Id";
    }
}
