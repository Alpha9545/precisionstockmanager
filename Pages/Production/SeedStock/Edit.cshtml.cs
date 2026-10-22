using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Services;
using SeedStockModel = PlantStockManager.Models.SeedStock;
using SeedStockTransactionModel = PlantStockManager.Models.SeedStockTransaction;

namespace PlantStockManager.Pages.Production.SeedStock
{
    // 2026-10-20: System Administrator only (FeatureAuthorizationConventions -- every verb and handler) -- corrects one
    // receipt of a Seed Stock lot: Received On, Quantity and Notes. The ledger is never rewritten: the corrected receipt
    // and the reversal of the original are appended together (SeedStockRepository.EditReceiptAsync, SeedStockEditRules).
    public class EditModel : PageModel
    {
        private readonly SeedStockRepository _seedStockRepo;
        private readonly SeedlingAreaScope _areaAccessService; // seedling-only Area scope (Authorization/SeedlingAreaScope.cs)

        public EditModel(SeedStockRepository seedStockRepo, SeedlingAreaScope areaAccessService)
        {
            _seedStockRepo = seedStockRepo;
            _areaAccessService = areaAccessService;
        }

        public SeedStockModel? SeedStock { get; set; }
        public List<SeedStockTransactionModel> Receipts { get; set; } = new();
        public SeedStockTransactionModel? Receipt { get; set; }

        [BindProperty] public int ReceiptId { get; set; }
        [BindProperty] public DateTime? ReceivedOn { get; set; }
        [BindProperty] public decimal? Quantity { get; set; }
        [BindProperty] public string? Notes { get; set; }
        [BindProperty] public string? Reason { get; set; }

        public async Task<IActionResult> OnGetAsync(int id, int? receiptId)
        {
            var load = await LoadAsync(id);
            if (load != null)
                return load;

            // the chosen receipt, or the only one
            Receipt = receiptId.HasValue
                ? Receipts.FirstOrDefault(r => r.Id == receiptId.Value)
                : Receipts.Count == 1 ? Receipts[0] : null;
            if (receiptId.HasValue && Receipt == null)
            {
                TempData["Error"] = SeedStockEditRules.StaleMessage;
                return RedirectToPage(SeedStockEditRules.EditPage, new { id });
            }

            if (Receipt != null)
            {
                ReceiptId = Receipt.Id;
                ReceivedOn = SeedStockEditRules.ToMinute(Receipt.TransactionDate.ToLocalTime());
                Quantity = Receipt.Quantity;
                Notes = Receipt.Remarks;
            }
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(int id)
        {
            var load = await LoadAsync(id);
            if (load != null)
                return load;

            Receipt = Receipts.FirstOrDefault(r => r.Id == ReceiptId);
            if (Receipt == null)
            {
                TempData["Error"] = SeedStockEditRules.StaleMessage;
                return RedirectToPage(SeedStockEditRules.EditPage, new { id });
            }

            foreach (var e in SeedStockEditRules.Validate(Quantity, ReceivedOn, Notes, Reason, DateTime.Today))
                ModelState.AddModelError(e.Field, e.Message);
            if (!ModelState.IsValid)
                return Page();

            var (success, message, changed, _) = await _seedStockRepo.EditReceiptAsync(
                new SeedStockRepository.ReceiptEdit(id, ReceiptId, Quantity!.Value, ReceivedOn!.Value, Notes, Reason!),
                User.GetUserId(), User.Identity?.Name ?? "System");
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "The receipt could not be corrected.");
                return Page();
            }

            TempData["Success"] = changed
                ? $"Receipt corrected: {QuantityFormat.Qty(Receipt.Quantity)} on {Receipt.TransactionDate.ToLocalTime():dd/MM/yyyy HH:mm} -> "
                  + $"{QuantityFormat.Qty(Quantity.Value)} on {ReceivedOn.Value:dd/MM/yyyy HH:mm}. The original and its reversal stay in the history."
                : message;
            return RedirectToPage("/Production/SeedStock/Details", new { id });
        }

        private async Task<IActionResult?> LoadAsync(int id)
        {
            SeedStock = await _seedStockRepo.GetByIdAsync(id);
            if (SeedStock == null)
                return RedirectToPage("/Production/SeedStock/Index");

            if (!_areaAccessService.CanAccessArea(User, SeedStock.AreaId))
            {
                TempData["Error"] = "You are not authorized to edit this Seed Stock record.";
                return RedirectToPage("/Production/SeedStock/Index");
            }

            Receipts = await _seedStockRepo.GetEditableReceiptsAsync(id);
            return null;
        }
    }
}
