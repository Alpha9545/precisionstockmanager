using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using EmptyPotInventoryModel = PlantStockManager.Models.EmptyPotInventory;
using InternalTransferModel = PlantStockManager.Models.InternalTransfer;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.EmptyPotInventory
{
    // Empty Pot Issue: pots move from the Office store to ONE production Area.
    // After this only that Area's production (pot batches in that Area) can
    // use them -- a batch always consumes its own Area's pool
    // (FK_PotBatches_EmptyPotPool). 2026-10-21: a double click / browser retry /
    // replay of the same form is stopped by SubmissionTokenGuard.
    public class IssueModel : PageModel
    {
        public const string TokenScope = "EmptyPotIssue";

        private readonly EmptyPotInventoryRepository _emptyPotRepo;
        private readonly InternalTransferRepository _transferRepo;
        private readonly AreaRepository _areaRepo;
        private readonly AreaAccessService _areaAccess;
        private readonly SubmissionTokenGuard _tokens;

        public IssueModel(EmptyPotInventoryRepository emptyPotRepo, InternalTransferRepository transferRepo, AreaRepository areaRepo, AreaAccessService areaAccess,
            SubmissionTokenGuard tokens)
        {
            _tokens = tokens;
            _emptyPotRepo = emptyPotRepo;
            _transferRepo = transferRepo;
            _areaRepo = areaRepo;
            _areaAccess = areaAccess;
        }

        [BindProperty] public int SourcePoolId { get; set; }
        [BindProperty] public int DestinationAreaId { get; set; }
        [BindProperty] public decimal Quantity { get; set; }
        [BindProperty] public string? Remarks { get; set; }
        [BindProperty] public string? SubmitToken { get; set; }

        public List<EmptyPotInventoryModel> SourcePools { get; set; } = new();
        public List<Area> ProductionAreas { get; set; } = new();

        public async Task OnGetAsync(int? sourcePoolId)
        {
            await LoadAsync();
            SubmitToken = SubmissionTokenGuard.NewToken();
            if (sourcePoolId.HasValue && SourcePools.Any(p => p.Id == sourcePoolId))
                SourcePoolId = sourcePoolId.Value;
        }

        public async Task<IActionResult> OnPostAsync()
        {
            var pool = await _emptyPotRepo.GetByIdAsync(SourcePoolId);
            if (pool == null || !pool.AreaId.HasValue || pool.AreaType != PlantStockManager.Services.DirectSowingRules.MainOfficeAreaType
                || !_areaAccess.CanAccessArea(User, pool.AreaId))
                ModelState.AddModelError(string.Empty, "Choose the Office stock to issue from.");
            if (Quantity <= 0 || !PlantStockManager.Services.DirectSowingRules.IsWholeNumber(Quantity))
                ModelState.AddModelError(string.Empty, "Quantity must be a whole number greater than zero.");
            else if (pool != null && Quantity > pool.PhysicalQuantity)
                ModelState.AddModelError(string.Empty, $"Only {QuantityFormat.Qty(pool.PhysicalQuantity)} pots of {pool.PotSize} are in {pool.AreaName}.");
            var destination = await _areaRepo.GetAreaById(DestinationAreaId);
            if (destination == null || !destination.IsActive || destination.AreaType == PlantStockManager.Services.DirectSowingRules.MainOfficeAreaType)
                ModelState.AddModelError(string.Empty, "Choose the production Area that receives the pots.");

            if (!ModelState.IsValid)
            {
                await LoadAsync();
                return Page();
            }

            if (!_tokens.TryConsume(TokenScope, SubmitToken))
            {
                TempData["Error"] = "This issue form was already submitted. Check the stock below before issuing again.";
                return RedirectToPage("/Production/EmptyPotInventory/Index");
            }

            var entry = new InternalTransferModel
            {
                StockType = "EmptyPot",
                SourceEmptyPotInventoryId = SourcePoolId,
                DestinationAreaId = DestinationAreaId,
                Quantity = Quantity,
                Remarks = Remarks,
                CreatedBy = User.Identity?.Name ?? "System"
            };
            var (success, message, _) = await _transferRepo.InsertAsync(entry, User.GetUserId());
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to issue the pots.");
                // The token was used up; issue a fresh one so the corrected form can be sent again.
                ModelState.Remove(nameof(SubmitToken));
                SubmitToken = SubmissionTokenGuard.NewToken();
                await LoadAsync();
                return Page();
            }
            TempData["Success"] = $"{QuantityFormat.Qty(Quantity)} x {pool!.PotSize} pots issued to {destination!.Name} ({entry.TransferCode}). Only {destination.Name} production can use them.";
            return RedirectToPage("/Production/EmptyPotInventory/Index");
        }

        private async Task LoadAsync()
        {
            SourcePools = (await _emptyPotRepo.GetAllAsync(activeOnly: true))
                .Where(p => p.AreaType == PlantStockManager.Services.DirectSowingRules.MainOfficeAreaType && p.PhysicalQuantity > 0
                            && _areaAccess.CanAccessArea(User, p.AreaId))
                .ToList();
            ProductionAreas = (await _areaRepo.GetAllAreas())
                .Where(a => a.IsActive && a.AreaType != PlantStockManager.Services.DirectSowingRules.MainOfficeAreaType)
                .OrderBy(a => a.Name).ToList();
        }
    }
}
