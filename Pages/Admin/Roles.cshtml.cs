using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Admin
{
    // Role Management (Phase 14 page, completed in Phase A).
    //
    // A Role's permissions are the ONLY source of feature access for every
    // user holding that Role (UserRoles -> Roles -> RolePermissions ->
    // Permissions); changing them here changes all those users' access at
    // their next claims refresh (SecurityOptions.PrincipalRevalidationMinutes)
    // or login. There are no per-user permissions.
    //
    // Full-access roles (System Administrator) need no permissions: the
    // central handler passes every policy for them, so their permission grid
    // is shown read-only.
    //
    // Page access: "Admin.ManageRoles" (Authorization/FeatureAuthorizationConventions.cs).
    // Anti-escalation (AdministrativeAccessGuard):
    //   * granting or revoking any Admin.* permission requires a full-access user;
    //   * a user who is not full-access cannot edit a role they hold themselves.
    public class RolesModel : PageModel
    {
        private readonly RoleRepository _roleRepo;
        private readonly UserRoleRepository _userRoleRepo;
        private readonly AdministrativeAccessGuard _guard;

        public RolesModel(RoleRepository roleRepo, UserRoleRepository userRoleRepo, AdministrativeAccessGuard guard)
        {
            _roleRepo = roleRepo;
            _userRoleRepo = userRoleRepo;
            _guard = guard;
        }

        public List<Role> Roles { get; set; } = new();
        public List<Permission> PermissionsForSelectedRole { get; set; } = new();
        public Role? SelectedRole { get; set; }
        public bool SelectedRoleIsFullAccess { get; set; }
        public bool CanEditAdminPermissions { get; set; }

        // Permissions grouped by module prefix ("Booking.View" -> "Booking").
        public IEnumerable<IGrouping<string, Permission>> PermissionGroups =>
            PermissionsForSelectedRole
                .GroupBy(p => p.Code.Contains('.') ? p.Code[..p.Code.IndexOf('.')] : p.Code)
                .OrderBy(g => g.Key == "Admin" ? "zzz" : g.Key);

        [BindProperty(SupportsGet = true)]
        public int? SelectedRoleId { get; set; }

        [BindProperty]
        public string NewRoleName { get; set; } = string.Empty;

        [BindProperty]
        public string? NewRoleDescription { get; set; }

        public string? ErrorMessage { get; set; }

        [TempData]
        public string? SuccessMessage { get; set; }

        public bool IsFullAccessRole(Role role) => _guard.IsFullAccessRoleName(role.Name);

        public async Task OnGetAsync()
        {
            await LoadAsync();
        }

        public async Task<IActionResult> OnPostAddRoleAsync()
        {
            var name = NewRoleName?.Trim() ?? string.Empty;
            if (name.Length == 0 || name.Length > 50)
            {
                ErrorMessage = "Role name is required (max 50 characters).";
                await LoadAsync();
                return Page();
            }

            var added = await _roleRepo.AddRoleAsync(name, string.IsNullOrWhiteSpace(NewRoleDescription) ? null : NewRoleDescription.Trim());
            if (!added)
            {
                ErrorMessage = $"A role named '{name}' already exists.";
                await LoadAsync();
                return Page();
            }

            SuccessMessage = $"Role '{name}' added. Select it to assign permissions.";
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostSavePermissionsAsync(int roleId, List<int>? permissionIds)
        {
            SelectedRoleId = roleId;
            var role = await _roleRepo.GetRoleByIdAsync(roleId);
            if (role == null)
            {
                ErrorMessage = "Role not found.";
                await LoadAsync();
                return Page();
            }

            if (_guard.IsFullAccessRoleName(role.Name))
            {
                ErrorMessage = $"'{role.Name}' has full access automatically; its permissions cannot be edited.";
                await LoadAsync();
                return Page();
            }

            var actorId = User.GetUserId();
            if (actorId == null)
            {
                ErrorMessage = "Your session is missing a user id. Please sign in again.";
                await LoadAsync();
                return Page();
            }

            var fullAccess = _guard.CanManageAdministrators(User);
            if (!fullAccess)
            {
                // Read the actor's roles from the DATABASE, not the cookie.
                var ownRoleIds = (await _userRoleRepo.GetAssignmentsForUserAsync(actorId.Value)).Select(a => a.RoleId).ToHashSet();
                if (ownRoleIds.Contains(roleId))
                {
                    ErrorMessage = "You cannot change the permissions of a role that is assigned to you. Ask a System Administrator.";
                    await LoadAsync();
                    return Page();
                }
            }

            // Only codes that really exist; compute which Admin.* grants change.
            var current = await _roleRepo.GetPermissionsWithGrantFlagAsync(roleId);
            var requested = (permissionIds ?? new List<int>()).Distinct().ToHashSet();
            var validRequested = current.Where(p => requested.Contains(p.Id)).ToList();
            var adminChanged = current.Any(p =>
                ClaimsPrincipalSecurityExtensions.IsAdminPermissionCode(p.Code)
                && p.IsGrantedToRole != requested.Contains(p.Id));
            if (adminChanged && !fullAccess)
            {
                ErrorMessage = "Only a System Administrator can grant or revoke Admin.* permissions.";
                await LoadAsync();
                return Page();
            }

            await _roleRepo.SetRolePermissionsAsync(roleId, validRequested.Select(p => p.Id).ToList());
            SuccessMessage = $"Permissions saved for '{role.Name}'. Users holding this role receive them within a few minutes (or at next login).";
            return RedirectToPage(new { SelectedRoleId = roleId });
        }

        private async Task LoadAsync()
        {
            Roles = await _roleRepo.GetAllRolesAsync();
            CanEditAdminPermissions = _guard.CanManageAdministrators(User);
            if (SelectedRoleId.HasValue)
            {
                SelectedRole = Roles.FirstOrDefault(r => r.Id == SelectedRoleId.Value);
                if (SelectedRole != null)
                {
                    SelectedRoleIsFullAccess = _guard.IsFullAccessRoleName(SelectedRole.Name);
                    PermissionsForSelectedRole = await _roleRepo.GetPermissionsWithGrantFlagAsync(SelectedRoleId.Value);
                }
            }
        }
    }
}
