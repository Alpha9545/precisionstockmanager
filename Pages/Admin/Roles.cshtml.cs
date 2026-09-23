using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Admin
{
    // Phase 14: first real end-to-end use of the permission-code
    // authorization system (Authorization/MinimumAuthorizationLevelHandler
    // + PermissionAuthorizationPolicyProvider). The /Admin folder is still
    // protected by the pre-existing AuthorizeFolder("/Admin") (any
    // authenticated user), and this page ADDITIONALLY requires the
    // "Admin.ManageRoles" permission specifically -- proving the new
    // mechanism actually denies/allows correctly before it's relied on
    // anywhere else.
    [Authorize(Policy = "Admin.ManageRoles")]
    public class RolesModel : PageModel
    {
        private readonly RoleRepository _roleRepo;

        public RolesModel(RoleRepository roleRepo)
        {
            _roleRepo = roleRepo;
        }

        public List<Role> Roles { get; set; } = new();
        public List<Permission> PermissionsForSelectedRole { get; set; } = new();

        [BindProperty(SupportsGet = true)]
        public int? SelectedRoleId { get; set; }

        [BindProperty]
        public string NewRoleName { get; set; } = string.Empty;

        [BindProperty]
        public string? NewRoleDescription { get; set; }

        public string? ErrorMessage { get; set; }

        public async Task OnGetAsync()
        {
            await LoadAsync();
        }

        public async Task<IActionResult> OnPostAddRoleAsync()
        {
            if (string.IsNullOrWhiteSpace(NewRoleName))
            {
                ErrorMessage = "Role name is required.";
                await LoadAsync();
                return Page();
            }

            await _roleRepo.AddRoleAsync(NewRoleName.Trim(), string.IsNullOrWhiteSpace(NewRoleDescription) ? null : NewRoleDescription.Trim());
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostSavePermissionsAsync(int roleId, List<int>? permissionIds)
        {
            await _roleRepo.SetRolePermissionsAsync(roleId, permissionIds ?? new List<int>());
            return RedirectToPage(new { SelectedRoleId = roleId });
        }

        private async Task LoadAsync()
        {
            Roles = await _roleRepo.GetAllRolesAsync();
            if (SelectedRoleId.HasValue)
            {
                PermissionsForSelectedRole = await _roleRepo.GetPermissionsWithGrantFlagAsync(SelectedRoleId.Value);
            }
        }
    }
}
