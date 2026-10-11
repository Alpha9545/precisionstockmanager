using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Store
{
    // Stock Entry (Store.Manage): a PURCHASE (optional supplier from Vendors, reference, unit cost)
    // or an ADJUSTMENT (+ or -, reason required) of Main Office stock -- always a ledger
    // transaction (StoreRepository.RecordMovementAsync), never a direct stock edit. A double click
    // / retry of the same form is stopped by SubmissionTokenGuard.
    public class StockEntryModel : PageModel
    {
        public const string TokenScope = "StoreStockEntry";

        private readonly StoreRepository _repo;
        private readonly VendorRepository _vendors;
        private readonly SubmissionTokenGuard _tokens;

        public StockEntryModel(StoreRepository repo, VendorRepository vendors, SubmissionTokenGuard tokens)
        {
            _repo = repo;
            _vendors = vendors;
            _tokens = tokens;
        }

        [BindProperty(SupportsGet = true)] public int? ItemId { get; set; }
        [BindProperty] public string? EntryType { get; set; } = StoreRules.Purchase;
        [BindProperty] public bool Decrease { get; set; }
        [BindProperty] public decimal? Quantity { get; set; }
        [BindProperty] public DateTime? EntryDate { get; set; }
        [BindProperty] public int? VendorId { get; set; }
        [BindProperty] public string? Reference { get; set; }
        [BindProperty] public decimal? UnitCost { get; set; }
        [BindProperty] public string? Remarks { get; set; }
        [BindProperty] public string? SubmitToken { get; set; }

        public List<StoreItem> Items { get; set; } = new();
        public List<Vendor> Vendors { get; set; } = new();
        public List<StoreTransaction> Recent { get; set; } = new();
        public DateTime Today => AppClock.Today;

        public async Task OnGetAsync()
        {
            EntryDate = Today;
            SubmitToken = SubmissionTokenGuard.NewToken();
            await LoadAsync();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            await LoadAsync();
            var item = Items.FirstOrDefault(i => i.Id == ItemId);
            if (item == null)
                ModelState.AddModelError(nameof(ItemId), "Choose an active item.");
            if (EntryType is not (StoreRules.Purchase or StoreRules.Adjustment))
                ModelState.AddModelError(nameof(EntryType), "Choose Purchase or Adjustment.");
            var qtyError = StoreRules.ValidateQuantity(Quantity, "Quantity", item?.ItemType ?? StoreRules.Consumable);
            if (qtyError != null) ModelState.AddModelError(nameof(Quantity), qtyError);
            if (!EntryDate.HasValue) ModelState.AddModelError(nameof(EntryDate), "Date is required.");
            else if (EntryDate.Value.Date > Today) ModelState.AddModelError(nameof(EntryDate), "Future dates are not allowed.");
            if (EntryType == StoreRules.Purchase)
            {
                if (VendorId.HasValue && !Vendors.Any(v => v.Id == VendorId)) ModelState.AddModelError(nameof(VendorId), "Choose an active supplier.");
                if (UnitCost is < 0) ModelState.AddModelError(nameof(UnitCost), "Unit cost cannot be negative.");
            }
            else if (string.IsNullOrWhiteSpace(Remarks))
                ModelState.AddModelError(nameof(Remarks), "An adjustment needs a reason in Remarks.");
            AddIfError(nameof(Reference), StoreRules.Optional(Reference, "Reference", 100));
            AddIfError(nameof(Remarks), StoreRules.Optional(Remarks, "Remarks"));
            if (!ModelState.IsValid)
                return PageWithNewToken();

            if (!_tokens.TryConsume(TokenScope, SubmitToken))
            {
                TempData["Error"] = StoreRules.AlreadySubmittedMessage;
                return RedirectToPage();
            }
            var purchase = EntryType == StoreRules.Purchase;
            var signed = purchase || !Decrease ? Quantity!.Value : -Quantity!.Value;
            try
            {
                var tx = await _repo.RecordMovementAsync(new StoreRepository.StockMove(item!.Id, signed, EntryType!, EntryDate!.Value,
                    VendorId: purchase ? VendorId : null, Reference: Reference, UnitCost: purchase ? UnitCost : null, Remarks: Remarks), User.GetUserId());
                TempData["Success"] = $"{StoreRules.TypeLabel(tx.TransactionType)} of {StoreRules.Qty(Math.Abs(tx.Quantity))} {item.UnitName} {item.Display} recorded: "
                    + $"stock {StoreRules.Qty(tx.BalanceBefore)} → {StoreRules.Qty(tx.BalanceAfter)}.";
                return RedirectToPage();
            }
            catch (StoreStockException ex)
            {
                ModelState.AddModelError(nameof(Quantity), ex.Message);
                await LoadAsync();
                return PageWithNewToken();
            }
            catch (SqlException ex) when (ex.Number is 547 or 51814)
            {
                ModelState.AddModelError(string.Empty, "The stock entry was refused: it would make the stock invalid. Please check and try again.");
                await LoadAsync();
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
            Items = await _repo.GetItemsAsync(activeOnly: true);
            Vendors = await _vendors.GetAllAsync(activeOnly: true);
            Recent = (await _repo.SearchTransactionsAsync(new StoreTransactionFilter(), null))
                .Where(t => t.TransactionType is StoreRules.Purchase or StoreRules.Adjustment).Take(20).ToList();
        }

        private void AddIfError(string key, string? error)
        {
            if (error != null) ModelState.AddModelError(key, error);
        }
    }
}
