using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Store
{
    // Permanent Transfers (read: Store.View / Store.Manage; POST: Store.Manage via the page rule):
    // the Store Keeper permanently assigns items from Main Office to an Area / Polyhouse without a
    // request -- a PERMANENT_TRANSFER ledger row that keeps the destination, so the item stays
    // visible where it went (Area-wise / Polyhouse-wise inventory). A supervisor sees the transfers
    // to their own Areas.
    public class TransfersModel : PageModel
    {
        public const string TokenScope = "StoreTransfer";

        private readonly StoreRepository _repo;
        private readonly AreaRepository _areaRepo;
        private readonly PolyhouseRepository _polyhouseRepo;
        private readonly StoreAuthorization _auth;
        private readonly SubmissionTokenGuard _tokens;

        public TransfersModel(StoreRepository repo, AreaRepository areaRepo, PolyhouseRepository polyhouseRepo, StoreAuthorization auth, SubmissionTokenGuard tokens)
        {
            _repo = repo;
            _areaRepo = areaRepo;
            _polyhouseRepo = polyhouseRepo;
            _auth = auth;
            _tokens = tokens;
        }

        [BindProperty] public int? ItemId { get; set; }
        [BindProperty] public decimal? Quantity { get; set; }
        [BindProperty] public int? AreaId { get; set; }
        [BindProperty] public int? PolyhouseId { get; set; }
        [BindProperty] public DateTime? TransferDate { get; set; }
        [BindProperty] public string? Reference { get; set; }
        [BindProperty] public string? Remarks { get; set; }
        [BindProperty] public string? SubmitToken { get; set; }

        public bool IsManager { get; set; }
        public List<StoreItem> Items { get; set; } = new();
        public List<Area> Areas { get; set; } = new();
        public List<Polyhouse> Polyhouses { get; set; } = new();
        public List<StoreTransaction> Transfers { get; set; } = new();
        public DateTime Today => AppClock.Today;

        public async Task OnGetAsync()
        {
            TransferDate = Today;
            SubmitToken = SubmissionTokenGuard.NewToken();
            await LoadAsync();
        }

        public async Task<JsonResult> OnGetPolyhousesAsync(int areaId)
            => new JsonResult(_auth.IsManager(User) ? (await _polyhouseRepo.GetByAreaIdAsync(areaId)).OrderBy(p => p.Name).Select(p => new { id = p.Id, name = p.Name }) : Enumerable.Empty<object>());

        public async Task<IActionResult> OnPostAsync()
        {
            await LoadAsync();
            if (!IsManager) return Forbid();
            var item = Items.FirstOrDefault(i => i.Id == ItemId);
            if (item == null) ModelState.AddModelError(nameof(ItemId), "Choose an active item.");
            var qtyError = StoreRules.ValidateQuantity(Quantity, "Quantity", item?.ItemType ?? StoreRules.Consumable);
            if (qtyError != null) ModelState.AddModelError(nameof(Quantity), qtyError);
            var area = Areas.FirstOrDefault(a => a.Id == AreaId);
            if (area == null) ModelState.AddModelError(nameof(AreaId), "Choose an active Area.");
            Polyhouse? polyhouse = null;
            if (PolyhouseId > 0)
            {
                polyhouse = await _polyhouseRepo.GetByIdAsync(PolyhouseId.Value);
                if (polyhouse == null || area == null || polyhouse.AreaId != area.Id)
                    ModelState.AddModelError(nameof(PolyhouseId), StoreRules.PolyhouseNotInAreaMessage);
            }
            if (area != null) Polyhouses = (await _polyhouseRepo.GetByAreaIdAsync(area.Id)).OrderBy(p => p.Name).ToList();
            if (!TransferDate.HasValue) ModelState.AddModelError(nameof(TransferDate), "Date is required.");
            else if (TransferDate.Value.Date > Today) ModelState.AddModelError(nameof(TransferDate), "Future dates are not allowed.");
            var remarksError = StoreRules.Required(Remarks, "Reason / remarks");
            if (remarksError != null) ModelState.AddModelError(nameof(Remarks), remarksError);
            if (!ModelState.IsValid)
                return PageWithNewToken();

            if (!_tokens.TryConsume(TokenScope, SubmitToken))
            {
                TempData["Error"] = StoreRules.AlreadySubmittedMessage;
                return RedirectToPage();
            }
            try
            {
                var tx = await _repo.RecordMovementAsync(new StoreRepository.StockMove(item!.Id, -Quantity!.Value, StoreRules.PermanentTransfer, TransferDate!.Value,
                    AreaId: area!.Id, PolyhouseId: polyhouse?.Id, Reference: Reference, Remarks: Remarks), User.GetUserId());
                TempData["Success"] = $"{StoreRules.Qty(Quantity.Value)} {item.UnitName} {item.Display} permanently transferred to {tx.Location}: Main Office stock {StoreRules.Qty(tx.BalanceBefore)} → {StoreRules.Qty(tx.BalanceAfter)}.";
                return RedirectToPage();
            }
            catch (StoreStockException ex)
            {
                ModelState.AddModelError(nameof(Quantity), ex.Message);
                return PageWithNewToken();
            }
            catch (SqlException ex) when (ex.Number is 547 or 51713 or 51814)
            {
                ModelState.AddModelError(string.Empty, "The transfer was refused by the store rules. Nothing was changed.");
                return PageWithNewToken();
            }
        }

        private IActionResult PageWithNewToken()
        {
            ModelState.Remove(nameof(SubmitToken));
            SubmitToken = SubmissionTokenGuard.NewToken();
            return Page();
        }

        private async Task LoadAsync()
        {
            IsManager = _auth.IsManager(User);
            Items = await _repo.GetItemsAsync(activeOnly: true);
            Areas = (await _areaRepo.GetAllAreas()).Where(a => a.IsActive).OrderBy(a => a.Name).ToList();
            Transfers = await _repo.SearchTransactionsAsync(new StoreTransactionFilter { TransactionType = StoreRules.PermanentTransfer }, _auth.AreaScope(User));
        }
    }
}
