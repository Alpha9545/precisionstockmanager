using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Admin
{
    public class AreaModel : PageModel
    {
        private readonly AreaRepository _areaRepo;
        private readonly PolyhouseRepository _polyhouseRepo;
        private readonly EmployeeRepository _employeeRepo;

        private readonly UserRoleRepository _userRoleRepo;

        public AreaModel(AreaRepository areaRepo, PolyhouseRepository polyhouseRepo, EmployeeRepository employeeRepo,
            UserRoleRepository userRoleRepo)
        {
            _userRoleRepo = userRoleRepo;
            _areaRepo = areaRepo;
            _polyhouseRepo = polyhouseRepo;
            _employeeRepo = employeeRepo;
        }

        public List<Area> Areas { get; set; } = new();
        public List<Polyhouse> Polyhouses { get; set; } = new();
        // Phase D: an Area's supervisor is chosen only from users who hold a
        // "... Supervisor" role FOR that Area (Admin > User Roles). The edit
        // dialog shows each Area only its own supervisors.
        public List<Employee> Supervisors { get; set; } = new();
        public Dictionary<int, List<int>> SupervisorAreaIds { get; set; } = new();
        // Phase 17: optional. "Internal / No Growing Partner" (the default,
        // GrowingPartnerId = null) always remains available -- this list
        // only adds the option to assign one.

        // Phase 14: fixed AreaType list, mirrors CK_Area_AreaType in the DB.
        // Exposed as an instance property (not a bare static field) so the
        // Razor view can read it via Model.AreaTypes -- a static member
        // cannot be accessed through an instance reference in C# (CS0176).
        private static readonly string[] _areaTypes = { "MotherPlant", "Kunjir", "Kiran", "Outlet", "MainOffice" };
        public string[] AreaTypes => _areaTypes;

        [BindProperty]
        public Area NewArea { get; set; } = new();

        [BindProperty]
        public Area EditArea { get; set; } = new();

        // Delete confirmation (shown in a dialog): the Area and every
        // relationship checked, with how many records use it.
        public Area? DeleteCandidate { get; set; }
        public IReadOnlyList<DependencyCount> DeleteDependencies { get; set; } = Array.Empty<DependencyCount>();
        public bool CanDeleteCandidate => DeletionRules.CanDelete(DeleteDependencies);
        public string? DeleteBlockedMessage { get; set; }
        // Current stock in the Area: Deactivate is offered only when there is none.
        public IReadOnlyList<DependencyCount> CandidateStock { get; set; } = Array.Empty<DependencyCount>();
        public bool CanDeactivateCandidate => DeletionRules.CanDeactivateArea(CandidateStock);
        public string CandidateStockMessage => DeletionRules.AreaDeactivationBlockedMessage(CandidateStock);

        public async Task OnGetAsync()
        {
            await LoadDropdownsAsync();
            Areas = await _areaRepo.GetAllAreasForAdminAsync();
        }

        public async Task<IActionResult> OnGetConfirmDeleteAsync(int id)
        {
            var area = await _areaRepo.GetAreaById(id);
            if (area == null)
            {
                TempData["Error"] = "Area not found.";
                return RedirectToPage();
            }
            DeleteCandidate = area;
            DeleteDependencies = await _areaRepo.GetDeletionCheckAsync(id);
            if (!CanDeleteCandidate && area.IsActive)
                CandidateStock = await _areaRepo.GetStockCheckAsync(id);
            await LoadDropdownsAsync();
            Areas = await _areaRepo.GetAllAreasForAdminAsync();
            return Page();
        }

        // Deletes only when nothing uses the Area; otherwise nothing changes
        // and the dialog shows what is blocking it.
        public async Task<IActionResult> OnPostDeleteAsync(int id)
        {
            var area = await _areaRepo.GetAreaById(id);
            var result = await _areaRepo.DeleteAsync(id);
            if (result.Outcome == DeleteOutcome.Deleted)
            {
                TempData["Success"] = result.Message;
                return RedirectToPage();
            }
            if (result.Outcome == DeleteOutcome.NotFound || area == null)
            {
                TempData["Error"] = "Area not found.";
                return RedirectToPage();
            }
            DeleteCandidate = area;
            DeleteDependencies = result.Dependencies;
            DeleteBlockedMessage = result.Message;
            if (area.IsActive)
                CandidateStock = await _areaRepo.GetStockCheckAsync(id);
            await LoadDropdownsAsync();
            Areas = await _areaRepo.GetAllAreasForAdminAsync();
            return Page();
        }

        // Deactivate / Activate: keeps every record that uses the Area.
        // Deactivate is refused while the Area still holds current stock.
        public async Task<IActionResult> OnPostSetActiveAsync(int id, bool isActive)
        {
            var area = await _areaRepo.GetAreaById(id);
            if (area == null)
            {
                TempData["Error"] = "Area not found.";
                return RedirectToPage();
            }

            if (isActive)
            {
                if (await _areaRepo.ActivateAsync(id))
                    TempData["Success"] = $"Area '{area.Name}' activated.";
                else
                    TempData["Error"] = "Area not found.";
                return RedirectToPage();
            }

            var result = await _areaRepo.DeactivateAsync(id);
            if (result.Succeeded)
                TempData["Success"] = $"Area '{area.Name}' deactivated. It is hidden from selection lists for new work; all of its existing records are kept.";
            else
                TempData["Error"] = result.NotFound ? "Area not found." : $"Area '{area.Name}': {result.Message}";
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostAddAsync()
        {
            ValidateArea(NewArea, "NewArea");
            // A new Area has nobody assigned to it yet (User Roles), so its
            // supervisor is chosen afterwards with Edit.
            NewArea.SupervisorId = null;

            if (!ModelState.IsValid)
            {
                await LoadDropdownsAsync();
                Areas = await _areaRepo.GetAllAreasForAdminAsync();
                return Page();
            }

            await _areaRepo.AddArea(NewArea);
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostEditAsync()
        {
            if (EditArea.Id <= 0)
                ModelState.AddModelError("EditArea.Id", "Invalid Area.");

            ValidateArea(EditArea, "EditArea");

            if (EditArea.Id > 0 && EditArea.SupervisorId.HasValue)
            {
                var existing = await _areaRepo.GetAreaById(EditArea.Id);
                var unchanged = existing != null && existing.SupervisorId == EditArea.SupervisorId;
                var eligible = (await _userRoleRepo.GetAreaSupervisorsAsync(EditArea.Id)).Select(e => e.EmployeeID);
                if (!unchanged && !eligible.Contains(EditArea.SupervisorId.Value))
                    ModelState.AddModelError("EditArea.SupervisorId", "The supervisor must hold a Supervisor role for this Area (Admin > User Roles).");
            }

            if (!ModelState.IsValid)
            {
                await LoadDropdownsAsync();
                Areas = await _areaRepo.GetAllAreasForAdminAsync();
                return Page();
            }

            // Unticking "Active" in Edit is a deactivation: same stock rule as
            // the Deactivate button (checked atomically before saving).
            var current = await _areaRepo.GetAreaById(EditArea.Id);
            if (current != null && current.IsActive && !EditArea.IsActive)
            {
                var deactivate = await _areaRepo.DeactivateAsync(EditArea.Id);
                if (!deactivate.Succeeded)
                {
                    ModelState.AddModelError("EditArea.IsActive", deactivate.Message);
                    await LoadDropdownsAsync();
                    Areas = await _areaRepo.GetAllAreasForAdminAsync();
                    return Page();
                }
            }

            await _areaRepo.UpdateArea(EditArea);
            return RedirectToPage();
        }

        // Mirrors the CHECK constraints enforced in the database
        // (Phase2_Area_MotherPlant.sql, Phase14_RoleFoundation_AreaExtension.sql)
        // so obviously invalid input is rejected with a friendly message before it ever
        // reaches SQL Server.
        //
        // Polyhouses are assigned to an Area on the Polyhouses page
        // (dbo.Polyhouses.AreaId) -- the only Area <-> Polyhouse link.
        private void ValidateArea(Area area, string prefix)
        {
            if (string.IsNullOrWhiteSpace(area.Name))
                ModelState.AddModelError($"{prefix}.Name", "Area Name is required.");
            if (area.AreaSize.HasValue && area.AreaSize <= 0)
                ModelState.AddModelError($"{prefix}.AreaSize", "Area Size must be greater than zero.");
            if (area.Capacity.HasValue && area.Capacity <= 0)
                ModelState.AddModelError($"{prefix}.Capacity", "Capacity must be greater than zero.");
            if (!string.IsNullOrWhiteSpace(area.AreaType) && !AreaTypes.Contains(area.AreaType))
                ModelState.AddModelError($"{prefix}.AreaType", "Invalid Area Type.");
        }

        private async Task LoadDropdownsAsync()
        {
            Polyhouses = await _polyhouseRepo.GetAllPolyhouses();
            var assignments = (await _userRoleRepo.GetRoleAssignmentsAsync())
                .Where(a => a.IsActive && a.AreaId.HasValue && PlantStockManager.Services.SupervisorRules.IsSupervisorRole(a.RoleName))
                .ToList();
            SupervisorAreaIds = assignments.GroupBy(a => a.UserId).ToDictionary(g => g.Key, g => g.Select(a => a.AreaId!.Value).Distinct().ToList());
            Supervisors = assignments.GroupBy(a => a.UserId)
                .Select(g => new Employee { EmployeeID = g.Key, Name = g.First().UserName })
                .OrderBy(e => e.Name).ToList();
            // Keep the names of supervisors already stored on an Area visible.
            foreach (var a in await _areaRepo.GetAllAreas())
                if (a.SupervisorId.HasValue && Supervisors.All(s => s.EmployeeID != a.SupervisorId))
                    Supervisors.Add(new Employee { EmployeeID = a.SupervisorId.Value, Name = (a.SupervisorName ?? "User " + a.SupervisorId) + " (current)" });
        }
    }
}
