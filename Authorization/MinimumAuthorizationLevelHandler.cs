using Microsoft.AspNetCore.Authorization;

namespace PlantStockManager.Authorization
{
    // Was an empty, unused stub -- this is the handler the redesign plan's
    // Section D described as "replacing the currently-empty
    // MinimumAuthorizationLevelHandler stub." Succeeds when the current
    // user carries a "Permission" claim matching the requirement's code.
    // Every claim is granted at login from UserRoles -> RolePermissions ->
    // Permissions (see Login.cshtml.cs), so this handler never touches the
    // database itself -- it is a cheap in-memory claim check per request.
    public class MinimumAuthorizationLevelHandler : AuthorizationHandler<MinimumAuthorizationLevelRequirement>
    {
        public const string PermissionClaimType = "Permission";

        protected override Task HandleRequirementAsync(
            AuthorizationHandlerContext context,
            MinimumAuthorizationLevelRequirement requirement)
        {
            if (context.User.HasClaim(c => c.Type == PermissionClaimType && c.Value == requirement.PermissionCode))
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }
    }
}
