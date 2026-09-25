using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PottedPlantStockModel = PlantStockManager.Models.PottedPlantStock;

namespace PlantStockManager.Pages.Production.PottedPlantStock
{
    // Phase 8: activates dbo.PottedPlantStockTransactions' own 'Wastage'
    // TransactionType (allowed since Phase 7, never written until now) --
    // potted plants damaged or died while sitting in stock, distinct from
    // a Dispatch (sale) or a ReversalRemoval (undoing a mistaken entry).
    public class RecordWastageModel : PageModel
    {
        private readonly PottedPlantStockRepository _pottedPlantStockRepo;
        private readonly AreaAccessService _areaAccessService;

        public RecordWastageModel(PottedPlantStockRepository pottedPlantStockRepo, AreaAccessService areaAccessService)
        {
            _pottedPlantStockRepo = pottedPlantStockRepo;
            _areaAccessService = areaAccessService;
        }

        public PottedPlantStockModel? Stock { get; set; }

        [BindProperty]
        public decimal Quantity { get; set; }

        [BindProperty]
        public string? Reason { get; set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            Stock = await _pottedPlantStockRepo.GetByIdAsync(id);
            if (Stock == null)
                return RedirectToPage("/Production/PottedPlantStock/Index");

            if (!_areaAccessService.CanAccessArea(User, Stock.AreaId))
            {
                TempData["Error"] = "You are not authorized to record wastage for this Potted Plant Stock record.";
                return RedirectToPage("/Production/PottedPlantStock/Index");
            }

            return Page();
        }

        public async Task<IActionResult> OnPostAsync(int id)
        {
            Stock = await _pottedPlantStockRepo.GetByIdAsync(id);
            if (Stock == null)
                return RedirectToPage("/Production/PottedPlantStock/Index");

            if (!_areaAccessService.CanAccessArea(User, Stock.AreaId))
            {
                TempData["Error"] = "You are not authorized to record wastage for this Potted Plant Stock record.";
                return RedirectToPage("/Production/PottedPlantStock/Index");
            }

            if (Quantity <= 0)
                ModelState.AddModelError(nameof(Quantity), "Quantity must be greater than zero.");
            if (Quantity > Stock.AvailableQuantity)
                ModelState.AddModelError(nameof(Quantity), $"Cannot waste more than what is Available ({Stock.AvailableQuantity:N2}) -- reserved/in-transit stock is not yours to write off here.");
            if (string.IsNullOrWhiteSpace(Reason))
                ModelState.AddModelError(nameof(Reason), "A reason is required (e.g. Disease, Damaged, Died in stock).");

            if (!ModelState.IsValid)
                return Page();

            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;
            var modifiedBy = User.Identity?.Name ?? "System";

            var (success, message) = await _pottedPlantStockRepo.RecordWastageAsync(id, Quantity, Reason, userId, modifiedBy);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to record wastage.");
                return Page();
            }

            TempData["Success"] = $"Recorded {Quantity:N2} wasted. Potted Plant Stock updated.";
            return RedirectToPage("/Production/PottedPlantStock/Details", new { id });
        }
    }
}
