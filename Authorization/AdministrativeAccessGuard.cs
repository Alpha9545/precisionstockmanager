using System.Security.Claims;
using Microsoft.Extensions.Options;
using PlantStockManager.Data;

namespace PlantStockManager.Authorization
{
    // Phase A: the anti-escalation rules shared by the user / role
    // administration pages (Admin/Users, Admin/UserRoles, Admin/Roles).
    // Page access itself is decided by FeatureAuthorizationConventions; these
    // rules decide WHICH records a permitted user may change:
    //
    //   * An "administrative role" is a full-access role (System
    //     Administrator) or any role granting an Admin.* permission.
    //   * An "administrator account" is a user holding an administrative role.
    //   * Only a full-access user may grant/remove an administrative role,
    //     grant/revoke Admin.* permissions, or change / reset the password
    //     of / deactivate ANOTHER administrator account.
    //   * Nobody changes their own role assignments or deactivates themselves
    //     (enforced by the pages).
    //
    // Everything is read from the database at request time, never from the
    // posted form or the (possibly stale) cookie.
    public class AdministrativeAccessGuard
    {
        private readonly RoleRepository _roleRepo;
        private readonly UserRoleRepository _userRoleRepo;
        private readonly SecurityOptions _options;

        public AdministrativeAccessGuard(RoleRepository roleRepo, UserRoleRepository userRoleRepo, IOptions<SecurityOptions> options)
        {
            _roleRepo = roleRepo;
            _userRoleRepo = userRoleRepo;
            _options = options.Value;
        }

        public bool CanManageAdministrators(ClaimsPrincipal actor) => actor.IsFullAccess();

        public bool IsFullAccessRoleName(string? roleName) => _options.IsFullAccessRole(roleName);

        public async Task<bool> IsAdministrativeRoleAsync(int roleId)
        {
            var role = await _roleRepo.GetRoleByIdAsync(roleId);
            if (role == null)
                return false;
            if (_options.IsFullAccessRole(role.Name))
                return true;
            return (await _roleRepo.GetPermissionCodesForRoleAsync(roleId))
                .Any(ClaimsPrincipalSecurityExtensions.IsAdminPermissionCode);
        }

        public async Task<bool> IsAdministratorAccountAsync(int userId)
        {
            var assignments = await _userRoleRepo.GetAssignmentsForUserAsync(userId);
            if (assignments.Any(a => _options.IsFullAccessRole(a.RoleName)))
                return true;
            return (await _userRoleRepo.GetPermissionCodesForUserAsync(userId))
                .Any(ClaimsPrincipalSecurityExtensions.IsAdminPermissionCode);
        }
    }
}
