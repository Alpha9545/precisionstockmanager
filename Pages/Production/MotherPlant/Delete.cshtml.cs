using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Services;
using MotherPlantModel = PlantStockManager.Models.MotherPlant;

namespace PlantStockManager.Pages.Production.MotherPlant
{
    // Delete confirmation for a Mother Plant batch. It is deleted only when
    // no record uses it; otherwise the blocking records are listed and
    // Deactivate (status 'Removed', all history kept) is offered instead.
    // Page permission: MotherPlant.Enter; plus the user's Area scope.
    public class DeleteModel : PageModel
    {
        private readonly MotherPlantRepository _motherPlantRepo;
        private readonly AreaAccessService _areaAccessService;

        public DeleteModel(MotherPlantRepository motherPlantRepo, AreaAccessService areaAccessService)
        {
            _motherPlantRepo = motherPlantRepo;
            _areaAccessService = areaAccessService;
        }

        public MotherPlantModel MotherPlant { get; set; } = new();
        public IReadOnlyList<DependencyCount> Dependencies { get; set; } = Array.Empty<DependencyCount>();
        public string? BlockedMessage { get; set; }

        public bool CanDelete => DeletionRules.CanDelete(Dependencies);
        public bool CanDeactivate => DeletionRules.CanDeactivateMotherPlant(MotherPlant.Status);
        public string DeactivatedStatus => DeletionRules.MotherPlantDeactivatedStatus;

        public async Task<IActionResult> OnGetAsync(int id)
        {
            if (!await LoadAsync(id))
                return Denied();
            Dependencies = await _motherPlantRepo.GetDeletionCheckAsync(id);
            return Page();
        }

        public async Task<IActionResult> OnPostDeleteAsync(int id)
        {
            if (!await LoadAsync(id))
                return Denied();

            var result = await _motherPlantRepo.DeleteAsync(id);
            if (result.Outcome == DeleteOutcome.Deleted)
            {
                TempData["Success"] = result.Message;
                return RedirectToPage("/Production/MotherPlant/Index");
            }
            if (result.Outcome == DeleteOutcome.NotFound)
                return Denied();

            Dependencies = result.Dependencies;
            BlockedMessage = result.Message;
            return Page();
        }

        public async Task<IActionResult> OnPostDeactivateAsync(int id)
        {
            if (!await LoadAsync(id))
                return Denied();

            var (success, message) = await _motherPlantRepo.DeactivateAsync(id, User.Identity?.Name ?? "System");
            if (success)
                TempData["Success"] = $"Mother Plant batch {MotherPlant.MotherPlantCode} deactivated (status '{DeletionRules.MotherPlantDeactivatedStatus}'). All of its history is kept.";
            else
                TempData["Error"] = message;
            return RedirectToPage("/Production/MotherPlant/Index");
        }

        // The record must exist and belong to an Area this user may access --
        // checked on every request, never only when the button was shown.
        private async Task<bool> LoadAsync(int id)
        {
            var existing = await _motherPlantRepo.GetByIdAsync(id);
            if (existing == null || !_areaAccessService.CanAccessArea(User, existing.AreaId))
                return false;
            MotherPlant = existing;
            return true;
        }

        private IActionResult Denied()
        {
            TempData["Error"] = "Mother Plant batch not found, or it belongs to an Area you cannot access.";
            return RedirectToPage("/Production/MotherPlant/Index");
        }
    }
}
