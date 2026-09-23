using Microsoft.AspNetCore.Authorization;

namespace PlantStockManager.Authorization
{
    // Was an empty, unused stub. Now the real requirement type behind the
    // permission-code authorization system described in the redesign plan
    // (Section D): one requirement per permission code (e.g.
    // "MainOffice.Confirm"), checked against the "Permission" claims a
    // user was granted at login (see Login.cshtml.cs and
    // UserRoleRepository.GetPermissionCodesForUserAsync). The class name
    // is kept as-is (it was never referenced anywhere, so nothing needed
    // updating) -- only its content changes from an empty stub to a real
    // requirement.
    public class MinimumAuthorizationLevelRequirement : IAuthorizationRequirement
    {
        public string PermissionCode { get; }

        public MinimumAuthorizationLevelRequirement(string permissionCode)
        {
            PermissionCode = permissionCode;
        }
    }
}
