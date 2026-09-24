using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Admin
{
    // Phase 14: assigns Roles (optionally scoped to an Area) to real
    // dbo.IMSUsers rows. This is the page an Admin uses to narrow everyone
    // down from the temporary blanket "every existing user gets Admin"
    // safety-net grant that Phase14_RoleFoundation_AreaExtension.sql
    // applies (Migration Plan step 4) to their real, permission-scoped role(s).
    //
    // Phase A: "Admin.ManageUsers" alone used to let its holder grant ANY
    // role -- including System Administrator -- to anyone, themselves
    // included. Both handlers now enforce (AdministrativeAccessGuard):
    //   * nobody may add or remove their OWN role assignments;
    //   * an administrative role (System Administrator, or any role carrying
    //     an Admin.* permission) may only be granted or removed by a
    //     full-access user.
    // The role's permissions are read from dbo.RolePermissions at request
    // time, never from the posted form.
    // Phase A: the server-side rule for this page ("Admin.ManageUsers") is declared
    // centrally in Authorization/FeatureAuthorizationConventions.cs.
    public class UserRolesModel : PageModel
    {
        private readonly UserRoleRepository _userRoleRepo;
        private readonly AdministrativeAccessGuard _guard;
        private readonly EmployeeRepository _employeeRepo;
        private readonly RoleRepository _roleRepo;
        private readonly AreaRepository _areaRepo;

        public UserRolesModel(
            UserRoleRepository userRoleRepo,
            EmployeeRepository employeeRepo,
            RoleRepository roleRepo,
            AreaRepository areaRepo,
            AdministrativeAccessGuard guard)
        {
            _guard = guard;
            _userRoleRepo = userRoleRepo;
            _employeeRepo = employeeRepo;
            _roleRepo = roleRepo;
            _areaRepo = areaRepo;
        }

        public List<UserRoleAssignment> Assignments { get; set; } = new();
        public List<Employee> Users { get; set; } = new();
        public List<Role> Roles { get; set; } = new();
        public List<Area> Areas { get; set; } = new();

        [BindProperty] public int NewUserId { get; set; }
        [BindProperty] public int NewRoleId { get; set; }
        [BindProperty] public int? NewAreaId { get; set; }

        public string? ErrorMessage { get; set; }

        public async Task OnGetAsync()
        {
            await LoadAsync();
        }

        public async Task<IActionResult> OnPostAddAsync()
        {
            if (NewUserId <= 0 || NewRoleId <= 0)
            {
                ErrorMessage = "User and Role are required.";
                await LoadAsync();
                return Page();
            }

            var denial = await CheckCanChangeAssignmentAsync(NewUserId, NewRoleId);
            if (denial != null)
            {
                ErrorMessage = denial;
                await LoadAsync();
                return Page();
            }

            var (success, message) = await _userRoleRepo.AddAssignmentAsync(NewUserId, NewRoleId, NewAreaId);
            if (!success)
            {
                ErrorMessage = message;
                await LoadAsync();
                return Page();
            }

            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostRemoveAsync(int id)
        {
            var assignment = (await _userRoleRepo.GetAllAssignmentsAsync()).FirstOrDefault(a => a.Id == id);
            if (assignment == null)
            {
                ErrorMessage = "Role assignment not found.";
                await LoadAsync();
                return Page();
            }

            var denial = await CheckCanChangeAssignmentAsync(assignment.UserId, assignment.RoleId);
            if (denial != null)
            {
                ErrorMessage = denial;
                await LoadAsync();
                return Page();
            }

            await _userRoleRepo.RemoveAssignmentAsync(id);
            return RedirectToPage();
        }

        // Returns an error message when the current user may NOT add or
        // remove an assignment of roleId for targetUserId, otherwise null.
        private async Task<string?> CheckCanChangeAssignmentAsync(int targetUserId, int roleId)
        {
            var actorId = User.GetUserId();
            if (actorId == null)
                return "Your session is missing a user id. Please sign in again.";

            if (actorId == targetUserId)
                return "You cannot change your own role assignments. Ask another administrator.";

            var role = await _roleRepo.GetRoleByIdAsync(roleId);
            if (role == null)
                return "Selected role does not exist.";

            if (!_guard.CanManageAdministrators(User) && await _guard.IsAdministrativeRoleAsync(roleId))
                return "Only a System Administrator can grant or remove an administrative role.";

            return null;
        }

        private async Task LoadAsync()
        {
            Assignments = await _userRoleRepo.GetAllAssignmentsAsync();
            Users = await _employeeRepo.GetAllActiveUsers();
            Roles = await _roleRepo.GetAllRolesAsync();
            Areas = await _areaRepo.GetAllAreas();
        }
    }
}
