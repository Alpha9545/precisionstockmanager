using System.Security.Claims;

namespace PlantStockManager.Authorization
{
    // Read-only helpers over the claims stamped by UserClaimsFactory. They
    // implement the SAME rule as MinimumAuthorizationLevelHandler (the one
    // central permission check), for code that needs a yes/no answer inside
    // a handler (e.g. "may this user grant an administrative role?").
    public static class ClaimsPrincipalSecurityExtensions
    {
        public const string AdminPermissionPrefix = "Admin.";

        // Stamped at login for holders of a full-access role
        // (SecurityOptions.FullAccessRoleNames, default "System Administrator").
        public const string FullAccessClaimType = "FullAccess";

        public static bool IsFullAccess(this ClaimsPrincipal user)
            => user.Identity?.IsAuthenticated == true
               && user.HasClaim(c => c.Type == FullAccessClaimType && c.Value == "true");

        // True when the user holds ANY of the given codes ("A|B|C" allowed),
        // or has full access.
        public static bool HasPermission(this ClaimsPrincipal user, string permissionCodes)
        {
            if (user.Identity?.IsAuthenticated != true)
                return false;
            if (user.IsFullAccess())
                return true;
            foreach (var code in PermissionPolicy.Split(permissionCodes))
            {
                if (user.HasClaim(MinimumAuthorizationLevelHandler.PermissionClaimType, code))
                    return true;
            }
            return false;
        }

        // The signed-in user's dbo.IMSUsers.Id, or null.
        public static int? GetUserId(this ClaimsPrincipal user)
            => int.TryParse(user.FindFirst("UserId")?.Value, out var id) ? id : null;

        public static bool IsAdminPermissionCode(string? code)
            => code != null && code.StartsWith(AdminPermissionPrefix, StringComparison.Ordinal);
    }

    // Policy-name syntax shared by the provider, the handler and the page map:
    // a policy name is one permission code ("Booking.View") or several codes
    // separated by '|' meaning ANY of them ("Dispatch.Enter|Outlet.Sell").
    public static class PermissionPolicy
    {
        public const char AnyOfSeparator = '|';

        // A code nobody is ever granted: only full-access users pass it.
        // Used as the fail-closed default for pages missing from the map.
        public const string FullAccessOnly = "System.FullAccessOnly";

        public static IReadOnlyList<string> Split(string policyName)
            => policyName.Split(AnyOfSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
