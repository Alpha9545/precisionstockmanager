using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PotProductionBatchModel = PlantStockManager.Models.PotProductionBatch;
using PotProductionModel = PlantStockManager.Models.PotProduction;

namespace PlantStockManager.Pages.Production.PotProductionBatch
{
    // Batch info + its daily production history (rule 2/3: Day 1 = 600,
    // Day 2 = 500, Day 3 = 200 -> Potted Stock shows 1,300 -- the running
    // totals shown here are exactly what PotProductionBatchRepository's
    // AccumulateDailyEntryAsync rolled forward on each entry).
    public class DetailsModel : PageModel
    {
        private readonly PotProductionBatchRepository _batchRepo;
        private readonly PotProductionRepository _potProductionRepo;

        public DetailsModel(PotProductionBatchRepository batchRepo, PotProductionRepository potProductionRepo)
        {
            _batchRepo = batchRepo;
            _potProductionRepo = potProductionRepo;
        }

        public PotProductionBatchModel? Batch { get; set; }
        public List<PotProductionModel> DailyEntries { get; set; } = new();
        public bool IsAssignedSupervisor { get; set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            Batch = await _batchRepo.GetByIdAsync(id);
            if (Batch == null)
                return NotFound();

            DailyEntries = await _potProductionRepo.GetByBatchIdAsync(id);

            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;
            IsAssignedSupervisor = Batch.SupervisorId.HasValue && userId.HasValue && Batch.SupervisorId.Value == userId.Value;

            return Page();
        }
    }
}
