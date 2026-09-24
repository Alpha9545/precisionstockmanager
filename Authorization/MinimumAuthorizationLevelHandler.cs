using Microsoft.AspNetCore.Authorization;

namespace PlantStockManager.Authorization
{
    // THE central feature-permission check. Succeeds when the current user
    //   * has full access (a full-access role such as "System Administrator"
    //     -- stamped as the "FullAccess" claim by UserClaimsFactory), or
    //   * carries a "Permission" claim for ANY of the requirement's codes.
    // Permission claims come ONLY from the user's roles
    // (UserRoles -> Roles -> RolePermissions -> Permissions); there are no
    // per-user permission rows. The handler never touches the database -- the
    // claims are computed at login and refreshed by the cookie revalidation.
    public class MinimumAuthorizationLevelHandler : AuthorizationHandler<MinimumAuthorizationLevelRequirement>
    {
        public const string PermissionClaimType = "Permission";

        protected override Task HandleRequirementAsync(
            AuthorizationHandlerContext context,
            MinimumAuthorizationLevelRequirement requirement)
        {
            var user = context.User;
            if (user.IsFullAccess()
                || requirement.PermissionCodes.Any(code => user.HasClaim(PermissionClaimType, code)))
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }
    }
}
