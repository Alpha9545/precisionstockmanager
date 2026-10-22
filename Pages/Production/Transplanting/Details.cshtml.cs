using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using ReadyStockModel = PlantStockManager.Models.ReadyStock;

namespace PlantStockManager.Pages.Production.Transplanting
{
    // One transplanting: what was consumed and produced, the ledger rows it wrote, and the
    // output batch's lineage back to the original sowing. Reverse (Transplant.Reverse --
    // full-access users only) is a compensating operation in TransplantRepository.ReverseAsync,
    // allowed only while the output batch is untouched; nothing is ever deleted.
    public class DetailsModel : PageModel
    {
        public const string ReversePermission = "Transplant.Reverse";

        private readonly TransplantRepository _repo;
        private readonly ReadyStockRepository _readyStockRepo;
        private readonly AreaAccessService _areaAccess;   // Mother Plant Area work: always Area-scoped (no Development switch), as the Mother Plant module

        public DetailsModel(TransplantRepository repo, ReadyStockRepository readyStockRepo, AreaAccessService areaAccess)
        {
            _repo = repo;
            _readyStockRepo = readyStockRepo;
            _areaAccess = areaAccess;
        }

        public ReadyStockTransplant T { get; private set; } = new();
        public ReadyStockModel? SourceBatch { get; private set; }
        public ReadyStockModel? OutputBatch { get; private set; }
        public List<ReadyStockModel> Lineage { get; private set; } = new();
        public List<ReadyStockTransaction> Ledger { get; private set; } = new();   // rows written by this operation (both batches)
        public bool CanReverse { get; private set; }
        public string? ReverseBlockedReason { get; private set; }

        public async Task<IActionResult> OnGetAsync(int id) => await LoadAsync(id) ? Page() : NotFoundHere();

        public async Task<IActionResult> OnPostReverseAsync(int id, string? reason)
        {
            if (!User.HasPermission(ReversePermission)) return Forbid();
            if (!await LoadAsync(id)) return NotFoundHere();
            var (ok, msg) = await _repo.ReverseAsync(id, reason, new MotherPlantTransferRepository.Actor(User.GetUserId(), User.Identity?.Name),
                areaId => _areaAccess.CanAccessArea(User, areaId));
            TempData[ok ? "Success" : "Error"] = msg;
            return RedirectToPage(new { id });
        }

        private async Task<bool> LoadAsync(int id)
        {
            var t = await _repo.GetAsync(id);
            if (t == null) return false;
            SourceBatch = await _readyStockRepo.GetByIdAsync(t.SourceReadyStockId);
            if (!_areaAccess.CanAccessArea(User, t.AreaId) && (SourceBatch == null || !_areaAccess.CanAccessArea(User, SourceBatch.AreaId)))
                return false;
            T = t;
            if (t.OutputReadyStockId is int outId)
            {
                OutputBatch = await _readyStockRepo.GetByIdAsync(outId);
                Lineage = await _readyStockRepo.GetLineageAsync(outId);
                Ledger.AddRange((await _readyStockRepo.GetTransactionsAsync(outId)).Where(Mine));
            }
            Ledger.AddRange((await _readyStockRepo.GetTransactionsAsync(t.SourceReadyStockId)).Where(Mine));
            Ledger = Ledger.OrderBy(l => l.TransactionDate).ThenBy(l => l.Id).ToList();

            // 2026-10-16: a waste-only closure (no tray batch) -- reversible while Completed (the seedlings go back).
            if (User.HasPermission(ReversePermission) && t.WasteOnly)
            {
                CanReverse = t.Status == TransplantRules.StatusCompleted;
                ReverseBlockedReason = CanReverse ? null : "This transplanting is already reversed.";
            }
            // 2026-10-13: a tray-batch output -- reversible only while the batch has no Sowing Approval.
            if (User.HasPermission(ReversePermission) && t.OutputIsTrayBatch)
            {
                var (ok, err) = t.Status != TransplantRules.StatusCompleted
                    ? (false, "This transplanting is already reversed.")
                    : BedSowingRules.CanReverseTransplant(t.OutputSowingStatus, t.OutputApprovedQuantity, 0);
                CanReverse = ok;
                ReverseBlockedReason = ok ? null : err;
            }
            if (User.HasPermission(ReversePermission) && OutputBatch != null)
            {
                var children = (await _readyStockRepo.GetChildrenAsync(OutputBatch.Id)).Count;
                var (ok, err) = TransplantRules.ValidateReverse(t.Status, t.TransplantedQuantity,
                    new TransplantRules.Output(OutputBatch.Quantity, OutputBatch.ReservedQuantity, OutputBatch.DispatchedQuantity, OutputBatch.InTransitQuantity, children),
                    "preview");
                CanReverse = ok;
                ReverseBlockedReason = ok ? null : err;
            }
            return true;

            bool Mine(ReadyStockTransaction l) => l.ReferenceType == TransplantRules.ReferenceType && l.ReferenceId == id;
        }

        private IActionResult NotFoundHere()
        {
            TempData["Error"] = "Transplanting not found, or it is outside your Areas.";
            return RedirectToPage("/Production/Transplanting/History");
        }
    }
}
