using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.BedSowing
{
    // 2026-10-12: one bed -- its figures, every transplant / loss (with the tray batch each
    // transplant created and that batch's approval state) and the actions:
    //   reverse a transplant / loss  -- the Bed Sowing Transplanter of the bed's Area (or a System
    //                                   Administrator); re-checked in BedSowingRepository.
    //   cancel the bed               -- a user who may record bed sowings (Sowing.Enter) in its Area,
    //                                   only while nothing was transplanted or lost.
    public class DetailsModel : PageModel
    {
        private readonly BedSowingRepository _repo;
        private readonly SeedlingAreaScope _scope;
        private readonly FeatureAccessService _features;

        public DetailsModel(BedSowingRepository repo, SeedlingAreaScope scope, FeatureAccessService features)
        {
            _repo = repo;
            _scope = scope;
            _features = features;
        }

        public Models.BedSowing? Bed { get; private set; }
        public List<BedSowingOperation> Operations { get; private set; } = new();
        public bool CanTransplant { get; private set; }
        public bool CanReverse { get; private set; }
        public bool CanCancel { get; private set; }
        // 2026-10-22: an untouched bed may be edited (BedSowingRules.ValidateEditable; the Edit page and the repository re-check).
        public bool CanEdit { get; private set; }
        public bool Conserved { get; private set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            if (!await LoadAsync(id))
                return NotFound();
            return Page();
        }

        public async Task<IActionResult> OnPostReverseAsync(int id, int operationId, string? reason)
        {
            var (ok, message) = await _repo.ReverseOperationAsync(operationId, reason, Actor);
            TempData[ok ? "Success" : "Error"] = ok ? "The operation was reversed. Its row stays in the history, marked Reversed." : message;
            return RedirectToPage(new { id });
        }

        public async Task<IActionResult> OnPostCancelAsync(int id, string? reason)
        {
            if (!await _features.CanAccessPageAsync(User, "/Production/BedSowing/Create"))
                return Forbid();
            var (ok, message) = await _repo.CancelAsync(id, reason, Actor, areaId => _scope.CanAccessArea(User, areaId));
            TempData[ok ? "Success" : "Error"] = ok ? "Bed Sowing cancelled; the seed was returned to the lot." : message;
            return RedirectToPage(new { id });
        }

        private BedSowingRepository.Actor Actor => new(User.GetUserId(), User.Identity?.Name, User.IsFullAccess());

        private async Task<bool> LoadAsync(int id)
        {
            Bed = await _repo.GetAsync(id);
            if (Bed == null || !_scope.CanAccessArea(User, Bed.AreaId))
                return false;
            Operations = await _repo.GetOperationsAsync(id);
            // Transplant / loss: only the Bed Sowing Transplanter of this Area (never by full access).
            // Reverse: the Transplanter, or a System Administrator (management; audited).
            var transplantAreas = await _repo.TransplantAreaIdsAsync(User.GetUserId(), isFullAccess: false);
            CanTransplant = transplantAreas!.Contains(Bed.AreaId);
            CanReverse = CanTransplant || User.IsFullAccess();
            CanCancel = Bed.Status == Models.BedSowing.StatusPending
                        && await _features.CanAccessPageAsync(User, "/Production/BedSowing/Create");
            CanEdit = BedSowingRules.ValidateEditable(Bed.Status, Bed.TransplantedQuantity, Bed.LostQuantity, Operations.Count, Bed.ReadyStockDays).Ok
                      && await _features.CanAccessPageAsync(User, "/Production/BedSowing/Edit");
            Conserved = BedSowingRules.IsConserved(Bed.Quantity, Bed.TransplantedQuantity, Bed.LostQuantity, Bed.RemainingQuantity);
            return true;
        }
    }
}
