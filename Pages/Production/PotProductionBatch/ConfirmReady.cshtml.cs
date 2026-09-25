using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PotProductionBatchModel = PlantStockManager.Models.PotProductionBatch;

namespace PlantStockManager.Pages.Production.PotProductionBatch
{
    // Rule 6: "When the batch is actually ready: Supervisor confirms
    // READY." Reuses the EXISTING assigned-supervisor rule
    // (DirectSowingRules.CanApprove, via
    // PotProductionBatchRepository.ConfirmReadyAsync) -- only the
    // batch's own SupervisorId may confirm it, never a bypass. This is a
    // ONE-WAY status marker (InProduction -> Ready): the potted plants
    // themselves are already available in Potted Plant Stock as soon as
    // each daily entry produces them (unchanged, existing behavior) --
    // Ready here closes the batch to further daily entries, it does not
    // gate stock availability.
    public class ConfirmReadyModel : PageModel
    {
        private readonly PotProductionBatchRepository _batchRepo;

        public ConfirmReadyModel(PotProductionBatchRepository batchRepo)
        {
            _batchRepo = batchRepo;
        }

        public PotProductionBatchModel? Batch { get; set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            Batch = await _batchRepo.GetByIdAsync(id);
            if (Batch == null)
                return NotFound();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(int id)
        {
            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;

            var (success, message) = await _batchRepo.ConfirmReadyAsync(id, userId, User.Identity?.Name ?? "System");
            if (!success)
            {
                TempData["Error"] = message;
                return RedirectToPage("/Production/PotProductionBatch/Details", new { id });
            }

            TempData["Success"] = "Batch confirmed Ready.";
            return RedirectToPage("/Production/PotProductionBatch/Details", new { id });
        }
    }
}
