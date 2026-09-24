using Microsoft.AspNetCore.Authorization;

namespace PlantStockManager.Authorization
{
    // The requirement behind every feature-permission policy (see
    // PermissionAuthorizationPolicyProvider). PermissionCode is the policy
    // name as written (e.g. "Booking.View" or "Dispatch.Enter|Outlet.Sell");
    // PermissionCodes is the parsed any-of list.
    public class MinimumAuthorizationLevelRequirement : IAuthorizationRequirement
    {
        public string PermissionCode { get; }
        public IReadOnlyList<string> PermissionCodes { get; }

        public MinimumAuthorizationLevelRequirement(string permissionCode)
        {
            PermissionCode = permissionCode;
            PermissionCodes = PermissionPolicy.Split(permissionCode);
        }
    }
}
