using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using ReadyStockModel = PlantStockManager.Models.ReadyStock;

namespace PlantStockManager.Pages.Production.ReadyStock
{
    // Where a Ready Stock batch came from and where its plants went:
    //   original Main Area sowing -> transfer -> received batch -> transplanting -> output batch -> ...
    // plus the batches derived from this one and its own complete ledger (incl. customer
    // dispatches). Read-only (ReadyStock.View | Transplant.View) for a batch in the user's Areas.
    public class LineageModel : PageModel
    {
        private readonly ReadyStockRepository _readyStockRepo;
        private readonly MotherPlantTransferRepository _transferRepo;
        private readonly TransplantRepository _transplantRepo;
        private readonly SeedlingAreaScope _areaAccess;

        public LineageModel(ReadyStockRepository readyStockRepo, MotherPlantTransferRepository transferRepo,
            TransplantRepository transplantRepo, SeedlingAreaScope areaAccess)
        {
            _readyStockRepo = readyStockRepo;
            _transferRepo = transferRepo;
            _transplantRepo = transplantRepo;
            _areaAccess = areaAccess;
        }

        public ReadyStockModel Batch { get; private set; } = new();
        public List<ReadyStockModel> Lineage { get; private set; } = new();     // oldest first, ends with Batch
        public List<ReadyStockModel> Children { get; private set; } = new();
        public MotherPlantTransfer? ReceivedFrom { get; private set; }          // when Batch was received
        public List<MotherPlantTransfer> TransfersOut { get; private set; } = new();
        public List<ReadyStockTransplant> Transplants { get; private set; } = new();
        public List<ReadyStockTransaction> Ledger { get; private set; } = new();

        public async Task<IActionResult> OnGetAsync(int id)
        {
            var batch = await _readyStockRepo.GetByIdAsync(id);
            if (batch == null || !_areaAccess.CanAccessArea(User, batch.AreaId))
            {
                TempData["Error"] = "Ready Stock batch not found, or it belongs to an Area you cannot access.";
                return RedirectToPage("/Production/ReadyStock/Index");
            }
            Batch = batch;
            Lineage = await _readyStockRepo.GetLineageAsync(id);
            Children = await _readyStockRepo.GetChildrenAsync(id);
            ReceivedFrom = batch.OriginType == ReadyStockModel.OriginTransfer ? await _transferRepo.GetByReceivedBatchAsync(id) : null;
            TransfersOut = (await _transferRepo.ListAsync(new[] { batch.AreaId })).Where(t => t.SourceReadyStockId == id).ToList();
            Transplants = await _transplantRepo.GetByBatchAsync(id);
            Ledger = (await _readyStockRepo.GetTransactionsAsync(id)).OrderBy(l => l.TransactionDate).ThenBy(l => l.Id).ToList();
            return Page();
        }
    }
}
