using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Admin
{
    // Phase 14: assigns Roles (optionally scoped to an Area) to real
    // dbo.IMSUsers rows. This is the page an Admin uses to narrow everyone
    // down from the temporary blanket "every existing user gets Admin"
    // safety-net grant that Phase14_RoleFoundation_AreaExtension.sql
    // applies (Migration Plan step 4) to their real, permission-scoped role(s).
    [Authorize(Policy = "Admin.ManageUsers")]
    public class UserRolesModel : PageModel
    {
        private readonly UserRoleRepository _userRoleRepo;
        private readonly EmployeeRepository _employeeRepo;
        private readonly RoleRepository _roleRepo;
        private readonly AreaRepository _areaRepo;

        public UserRolesModel(
            UserRoleRepository userRoleRepo,
            EmployeeRepository employeeRepo,
            RoleRepository roleRepo,
            AreaRepository areaRepo)
        {
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
            await _userRoleRepo.RemoveAssignmentAsync(id);
            return RedirectToPage();
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
