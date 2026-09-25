using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PotProductionBatchModel = PlantStockManager.Models.PotProductionBatch;
using PotProductionModel = PlantStockManager.Models.PotProduction;

namespace PlantStockManager.Pages.Production.PotProductionBatch
{
    // Rule 2/3: one daily production entry against an existing batch --
    // Area/Variety/Species/Pot Size/Cutting source are all INHERITED from
    // the batch (never re-chosen, rule 5), so the operator only ever
    // enters how many cuttings were consumed and how many potted plants
    // were produced today. Reuses
    // PotProductionRepository.InsertFromCuttingStockAsync unchanged for
    // the actual stock movement -- this page only supplies
    // PotProductionBatchId so that method's own batch-aware steps apply.
    public class DailyEntryModel : PageModel
    {
        private readonly PotProductionBatchRepository _batchRepo;
        private readonly PotProductionRepository _potProductionRepo;
        private readonly AreaAccessService _areaAccessService;

        public DailyEntryModel(PotProductionBatchRepository batchRepo, PotProductionRepository potProductionRepo, AreaAccessService areaAccessService)
        {
            _batchRepo = batchRepo;
            _potProductionRepo = potProductionRepo;
            _areaAccessService = areaAccessService;
        }

        public PotProductionBatchModel? Batch { get; set; }

        [BindProperty]
        public decimal CuttingQuantityConsumed { get; set; }
        [BindProperty]
        public decimal QuantityProduced { get; set; }
        [BindProperty]
        public DateTime ProductionDate { get; set; } = DateTime.Today;
        [BindProperty]
        public string? Remarks { get; set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            Batch = await _batchRepo.GetByIdAsync(id);
            if (Batch == null)
                return NotFound();
            if (!_areaAccessService.CanAccessArea(User, Batch.AreaId))
                return Forbid();
            if (Batch.Status != "InProduction")
            {
                TempData["Error"] = $"This batch is {Batch.Status} and can no longer take daily production entries.";
                return RedirectToPage("/Production/PotProductionBatch/Details", new { id });
            }
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(int id)
        {
            Batch = await _batchRepo.GetByIdAsync(id);
            if (Batch == null)
                return NotFound();
            if (!_areaAccessService.CanAccessArea(User, Batch.AreaId))
                return Forbid();

            if (CuttingQuantityConsumed <= 0)
                ModelState.AddModelError(nameof(CuttingQuantityConsumed), "Cuttings Consumed must be greater than zero.");
            if (QuantityProduced <= 0)
                ModelState.AddModelError(nameof(QuantityProduced), "Potted Plants Produced must be greater than zero.");
            if (CuttingQuantityConsumed > 0 && QuantityProduced > CuttingQuantityConsumed)
                ModelState.AddModelError(nameof(QuantityProduced), "Potted Plants Produced cannot exceed Cuttings Consumed (loss cannot be negative).");

            if (!ModelState.IsValid)
                return Page();

            var entry = new PotProductionModel
            {
                SourceCuttingStockId = Batch.SourceCuttingStockId,
                CuttingQuantityConsumed = CuttingQuantityConsumed,
                Quantity = QuantityProduced,
                PotSize = Batch.PotSize,
                ProductionDate = ProductionDate,
                Remarks = Remarks,
                SupervisorId = Batch.SupervisorId,
                PotProductionBatchId = Batch.Id,
                CreatedBy = User.Identity?.Name ?? "System"
            };

            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;

            var (success, message, _) = await _potProductionRepo.InsertFromCuttingStockAsync(entry, userId);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to record daily production.");
                return Page();
            }

            TempData["Success"] = $"Daily production {entry.ProductionCode} recorded: {QuantityProduced:N0} potted plants produced. Potted Plant Stock updated.";
            return RedirectToPage("/Production/PotProductionBatch/Details", new { id });
        }
    }
}
