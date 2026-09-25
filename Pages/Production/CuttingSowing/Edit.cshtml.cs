using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Services;
using CuttingSowingModel = PlantStockManager.Models.CuttingSowing;

namespace PlantStockManager.Pages.Production.CuttingSowing
{
    // Only Supervisor/Remarks are editable -- Source/Area/Species/Cavity/
    // Quantities are immutable after creation (CuttingSowingRepository.
    // UpdateDetailsAsync). Phase 1 (F1/F2): the editor can never assign
    // themselves as supervisor (DirectSowingRules.ValidateSupervisorChange,
    // re-applied under lock by the repository).
    public class EditModel : PageModel
    {
        private readonly CuttingSowingRepository _cuttingSowingRepo;
        private readonly UserRoleRepository _userRoleRepo;
        private readonly AreaAccessService _areaAccessService;

        public EditModel(CuttingSowingRepository cuttingSowingRepo, UserRoleRepository userRoleRepo, AreaAccessService areaAccessService)
        {
            _cuttingSowingRepo = cuttingSowingRepo;
            _userRoleRepo = userRoleRepo;
            _areaAccessService = areaAccessService;
        }

        [BindProperty]
        public CuttingSowingModel CuttingSowing { get; set; } = new();

        // Eligible approvers (plus the stored one, kept selectable), never
        // the creator, never the person editing.
        public List<PlantStockManager.Models.Employee> Supervisors { get; set; } = new();

        public async Task<IActionResult> OnGetAsync(int id)
        {
            var existing = await _cuttingSowingRepo.GetByIdAsync(id);
            if (existing == null)
                return RedirectToPage("/Production/CuttingSowing/Index");

            if (!_areaAccessService.CanAccessArea(User, existing.AreaId))
            {
                TempData["Error"] = "You are not authorized to edit this Cutting Sowing record.";
                return RedirectToPage("/Production/CuttingSowing/Index");
            }
            if (existing.Status != "Sown")
            {
                TempData["Error"] = $"This Cutting Sowing batch is '{existing.Status}' and cannot be edited.";
                return RedirectToPage("/Production/CuttingSowing/Details", new { id });
            }

            CuttingSowing = existing;
            await LoadDropdownsAsync(existing);
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            var existing = await _cuttingSowingRepo.GetByIdAsync(CuttingSowing.Id);
            if (existing == null)
            {
                ModelState.AddModelError(string.Empty, "Cutting Sowing record not found.");
                await LoadDropdownsAsync(null);
                return Page();
            }
            if (!_areaAccessService.CanAccessArea(User, existing.AreaId))
            {
                TempData["Error"] = "You are not authorized to edit this Cutting Sowing record.";
                return RedirectToPage("/Production/CuttingSowing/Index");
            }
            if (existing.Status != "Sown")
            {
                ModelState.AddModelError(string.Empty, $"This Cutting Sowing batch is '{existing.Status}' and cannot be edited.");
                await LoadDropdownsAsync(existing);
                return Page();
            }

            CuttingSowing.ModifiedBy = User.Identity?.Name ?? "System";

            var (success, message) = await _cuttingSowingRepo.UpdateDetailsAsync(CuttingSowing, User.GetUserId());
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to update Cutting Sowing.");
                await LoadDropdownsAsync(existing);
                return Page();
            }

            TempData["Success"] = $"Cutting Sowing {existing.SowingCode} updated successfully.";
            return RedirectToPage("/Production/CuttingSowing/Details", new { id = CuttingSowing.Id });
        }

        private async Task LoadDropdownsAsync(CuttingSowingModel? existing)
        {
            var me = User.GetUserId();
            var eligible = await _userRoleRepo.GetEligibleSupervisorsAsync(SupervisorKind.Sowing, areaId: null, enforceArea: false);
            Supervisors = eligible
                .Where(a => existing?.CreatedById == null || a.EmployeeID != existing.CreatedById)
                .Where(a => a.EmployeeID != me || a.EmployeeID == existing?.SupervisorId)
                .ToList();
            if (existing?.SupervisorId is int currentId && currentId > 0 && !Supervisors.Any(s => s.EmployeeID == currentId))
                Supervisors.Add(new PlantStockManager.Models.Employee { EmployeeID = currentId, Name = (existing.SupervisorName ?? "#" + currentId) + " (current)" });
        }
    }
}
