using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using PotProductionModel = PlantStockManager.Models.PotProduction;

namespace PlantStockManager.Pages.Production.PotProduction
{
    public class EditModel : PageModel
    {
        private readonly PotProductionRepository _potProductionRepo;
        private readonly SupervisorSelectionService _supervisors;
        private readonly AreaAccessService _areaAccessService;

        public EditModel(PotProductionRepository potProductionRepo, SupervisorSelectionService supervisors, AreaAccessService areaAccessService)
        {
            _potProductionRepo = potProductionRepo;
            _supervisors = supervisors;
            _areaAccessService = areaAccessService;
        }

        // PropagationBatchId / PotSize / Quantity are permanently
        // immutable -- they already moved real stock (empty pots
        // consumed, potted stock produced). Editing here only touches
        // Responsible Person / Supervisor / Remarks. The only way to
        // undo the stock effect is the explicit Cancel action, which
        // reverses both ledgers atomically (PotProductionRepository.CancelAsync).
        [BindProperty]
        public PotProductionModel PotProduction { get; set; } = new();

        // Phase 1: supervisors of this record's Area, plus the current one.
        public List<SupervisorOption> Supervisors { get; set; } = new();

        public async Task<IActionResult> OnGetAsync(int id)
        {
            var existing = await _potProductionRepo.GetByIdAsync(id);
            if (existing == null)
                return RedirectToPage("/Production/PotProduction/Index");

            // Phase E (spec item 12): this Edit/Cancel page had no Area
            // check at all -- a Growing Partner Supervisor could edit or
            // cancel another Area's Production record by direct URL/id.
            if (!_areaAccessService.CanAccessArea(User, existing.AreaId))
            {
                TempData["Error"] = "You are not authorized to edit this Pot Production record.";
                return RedirectToPage("/Production/PotProduction/Index");
            }

            PotProduction = existing;
            await LoadDropdownsAsync(existing);
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            ModelState.Remove("PotProduction.ProductionCode");
            ModelState.Remove("PotProduction.CreatedBy");
            ModelState.Remove("PotProduction.PropagationBatchId");
            ModelState.Remove("PotProduction.MotherPlantId");
            ModelState.Remove("PotProduction.SpeciesId");
            ModelState.Remove("PotProduction.PotSize");
            ModelState.Remove("PotProduction.Quantity");
            ModelState.Remove("PotProduction.Status");

            var existing = await _potProductionRepo.GetByIdAsync(PotProduction.Id);
            if (existing == null)
            {
                ModelState.AddModelError(string.Empty, "Pot Production record not found.");
                await LoadDropdownsAsync(existing);
                return Page();
            }

            // Phase E (spec item 12): re-derive the Area from the locked/
            // fetched record itself -- never trust a posted AreaId (there
            // isn't one on this form, but this also guards against a
            // tampered Id routing to another Area's record).
            if (!_areaAccessService.CanAccessArea(User, existing.AreaId))
            {
                TempData["Error"] = "You are not authorized to edit this Pot Production record.";
                return RedirectToPage("/Production/PotProduction/Index");
            }

            if (existing.Status == "Cancelled")
            {
                ModelState.AddModelError(string.Empty, "This Pot Production record is already Cancelled and cannot be edited further.");
                PotProduction = existing;
                await LoadDropdownsAsync(existing);
                return Page();
            }

            var supervisorError = await _supervisors.ValidateForAreaAsync(
                existing.AreaId, SupervisorKind.ProductionArea, PotProduction.SupervisorId, existing.SupervisorId);
            if (supervisorError != null)
            {
                ModelState.AddModelError("PotProduction.SupervisorId", supervisorError);
                PotProduction = existing;
                await LoadDropdownsAsync(existing);
                return Page();
            }

            PotProduction.ModifiedBy = User.Identity?.Name ?? "System";

            var (success, message) = await _potProductionRepo.UpdateDetailsAsync(PotProduction);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to update Pot Production.");
                PotProduction.ProductionCode = existing.ProductionCode;
                PotProduction.PropagationBatchCode = existing.PropagationBatchCode;
                PotProduction.MotherPlantCode = existing.MotherPlantCode;
                PotProduction.SpeciesName = existing.SpeciesName;
                PotProduction.PotSize = existing.PotSize;
                PotProduction.Quantity = existing.Quantity;
                PotProduction.Status = existing.Status;
                await LoadDropdownsAsync(existing);
                return Page();
            }

            TempData["Success"] = $"Pot Production {existing.ProductionCode} updated successfully.";
            return RedirectToPage("/Production/PotProduction/Index");
        }

        public async Task<IActionResult> OnPostCancelAsync(int id)
        {
            // Phase E (spec item 12/19): fetch first, check Area access
            // against the RECORD's own AreaId (never the posted id blindly)
            // before allowing a reversal that touches Cutting Stock, Empty
            // Pot Inventory, and Potted Plant Stock all at once.
            var target = await _potProductionRepo.GetByIdAsync(id);
            if (target == null)
            {
                TempData["Error"] = "Pot Production record not found.";
                return RedirectToPage("/Production/PotProduction/Index");
            }
            if (!_areaAccessService.CanAccessArea(User, target.AreaId))
            {
                TempData["Error"] = "You are not authorized to cancel this Pot Production record.";
                return RedirectToPage("/Production/PotProduction/Index");
            }

            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;

            var (success, message) = await _potProductionRepo.CancelAsync(id, User.Identity?.Name ?? "System", userId);
            if (!success)
            {
                TempData["Error"] = message ?? "Failed to cancel Pot Production.";
                return RedirectToPage("/Production/PotProduction/Edit", new { id });
            }

            TempData["Success"] = "Pot Production cancelled. Empty Pot stock returned and Potted Plant Stock reduced.";
            return RedirectToPage("/Production/PotProduction/Index");
        }

        private async Task LoadDropdownsAsync(PotProductionModel? existing)
        {
            Supervisors = existing == null
                ? new List<SupervisorOption>()
                : await _supervisors.ForAreaAsync(existing.AreaId, SupervisorKind.ProductionArea, existing.SupervisorId, existing.SupervisorName);
        }
    }
}
