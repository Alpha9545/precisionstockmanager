using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Services;
using ReadyBatch = PlantStockManager.Models.ReadyStock;

namespace PlantStockManager.Pages.Production.MotherPlant
{
    // 2026-10-18 (owner decision 2026-10-12): Mother Plant & Cutting > Ready Stock > Sell to customer -- ready trays of a Mother
    // Plant Area sold straight to a customer (name + contact required, quantity in whole trays), no Seedling Booking first.
    // Same permission as the potted-plant customer sale (Outlet.Sell); the batch must be in a Mother Plant Area the user works
    // in (the regular Area rule, always enforced). SeedlingFulfilmentRepository.DirectTraySaleAsync re-reads and re-checks
    // everything under the batch's lock and records the booking, allocation and dispatch in one transaction.
    public class SellTraysModel : PageModel
    {
        public const string SellPolicy = "Outlet.Sell";

        private readonly SeedlingFulfilmentRepository _fulfilment;
        private readonly ReadyStockRepository _readyStockRepo;
        private readonly MotherPlantTransferRepository _transferRepo;
        private readonly AreaAccessService _areaAccess;
        private readonly DatabaseHelper _db;

        public SellTraysModel(SeedlingFulfilmentRepository fulfilment, ReadyStockRepository readyStockRepo, MotherPlantTransferRepository transferRepo,
            AreaAccessService areaAccess, DatabaseHelper db)
        {
            _fulfilment = fulfilment;
            _readyStockRepo = readyStockRepo;
            _transferRepo = transferRepo;
            _areaAccess = areaAccess;
            _db = db;
        }

        public ReadyBatch Batch { get; private set; } = new();
        public bool SchemaReady { get; private set; }
        public int AvailableTrays => Batch.CavitySize is > 0 ? (int)decimal.Floor(Math.Max(Batch.AvailableQuantity, 0) / Batch.CavitySize.Value) : 0;

        [BindProperty] public int Trays { get; set; }
        [BindProperty] public string? CustomerName { get; set; }
        [BindProperty] public string? Contact { get; set; }
        [BindProperty] public string? Address { get; set; }
        [BindProperty] public string? Remarks { get; set; }
        [BindProperty] public Guid SubmissionToken { get; set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            var refusal = await LoadAsync(id);
            if (refusal != null) return NotHere(refusal);
            SubmissionToken = Guid.NewGuid();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(int id)
        {
            var refusal = await LoadAsync(id);
            if (refusal != null) return NotHere(refusal);
            if (!SchemaReady)
            {
                ModelState.AddModelError(string.Empty, SeedlingFulfilmentRepository.DirectSaleNeedsMigrationMessage);
                return Page();
            }
            var (ok, msg, _) = await _fulfilment.DirectTraySaleAsync(
                new SeedlingFulfilmentRepository.DirectTraySaleRequest(id, Trays, CustomerName, Contact, Address, Remarks, SubmissionToken),
                new SeedlingFulfilmentRepository.Actor(User.Identity?.Name, User.GetUserId()),
                areaId => _areaAccess.CanAccessArea(User, areaId));
            if (!ok)
            {
                ModelState.AddModelError(string.Empty, msg ?? "The sale could not be recorded.");
                return Page();
            }
            TempData["Success"] = msg;
            return RedirectToPage("/Production/MotherPlant/ReadyStock");
        }

        // null = fine; otherwise why this batch cannot be sold here.
        private async Task<string?> LoadAsync(int id)
        {
            var batch = await _readyStockRepo.GetByIdAsync(id);
            if (batch == null || !batch.InMotherPlantArea || !_areaAccess.CanAccessArea(User, batch.AreaId))
                return "That Ready Stock batch is not in one of your Mother Plant Areas.";
            if ((await _transferRepo.PotProductionBatchIdsAsync()).Contains(id))
                return "This stock was received for Pot Production; it is potted, not sold as trays.";
            Batch = batch;
            SchemaReady = await WorkflowCorrectionsSchema.SeedlingSaleTokenAsync(_db);
            return null;
        }

        private IActionResult NotHere(string message)
        {
            TempData["Error"] = message;
            return RedirectToPage("/Production/MotherPlant/ReadyStock");
        }
    }
}
