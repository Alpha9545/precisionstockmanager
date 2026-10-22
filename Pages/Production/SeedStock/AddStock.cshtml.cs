using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using SeedStockModel = PlantStockManager.Models.SeedStock;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.SeedStock
{
    // Records a manual seed receipt (e.g. Main Office receiving seed
    // from a supplier) directly against an existing pool. Mirrors
    // EmptyPotInventory/AddStock's Area check on both verbs -- fetch
    // the actual record first, check its REAL AreaId, never trust a
    // posted id alone. 2026-10-21: a double click / browser retry / replay of the
    // same form is stopped by SubmissionTokenGuard (it used to add the receipt again).
    public class AddStockModel : PageModel
    {
        public const string TokenScope = "SeedStockAddStock";

        private readonly SeedStockRepository _seedStockRepo;
        private readonly SeedlingAreaScope _areaAccessService; // seedling-only Area scope (Authorization/SeedlingAreaScope.cs)
        private readonly SubmissionTokenGuard _tokens;

        public AddStockModel(SeedStockRepository seedStockRepo, SeedlingAreaScope areaAccessService, SubmissionTokenGuard tokens)
        {
            _seedStockRepo = seedStockRepo;
            _areaAccessService = areaAccessService;
            _tokens = tokens;
        }

        public SeedStockModel? SeedStock { get; set; }

        [BindProperty]
        public decimal Quantity { get; set; }

        [BindProperty]
        public string? Remarks { get; set; }

        [BindProperty]
        public string? SubmitToken { get; set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            SeedStock = await _seedStockRepo.GetByIdAsync(id);
            if (SeedStock == null)
                return RedirectToPage("/Production/SeedStock/Index");

            if (!_areaAccessService.CanAccessArea(User, SeedStock.AreaId))
            {
                TempData["Error"] = "You are not authorized to add stock to this Seed Stock record.";
                return RedirectToPage("/Production/SeedStock/Index");
            }

            SubmitToken = SubmissionTokenGuard.NewToken();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(int id)
        {
            SeedStock = await _seedStockRepo.GetByIdAsync(id);
            if (SeedStock == null)
                return RedirectToPage("/Production/SeedStock/Index");

            if (!_areaAccessService.CanAccessArea(User, SeedStock.AreaId))
            {
                TempData["Error"] = "You are not authorized to add stock to this Seed Stock record.";
                return RedirectToPage("/Production/SeedStock/Index");
            }

            if (Quantity <= 0)
                ModelState.AddModelError(nameof(Quantity), "Quantity to add must be greater than zero.");
            else if (!PlantStockManager.Services.DirectSowingRules.IsWholeNumber(Quantity))
                ModelState.AddModelError(nameof(Quantity), "Quantity to add must be a whole number.");
            else if (Quantity > SeedStockEditRules.MaxQuantity)
                ModelState.AddModelError(nameof(Quantity), SeedStockEditRules.MaxQuantityMessage);

            if (!ModelState.IsValid)
                return Page();

            if (!_tokens.TryConsume(TokenScope, SubmitToken))
            {
                TempData["Error"] = "This form was already submitted. Check the lot's current stock below before adding again.";
                return RedirectToPage("/Production/SeedStock/Details", new { id });
            }

            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;

            var (success, message) = await _seedStockRepo.AddStockAsync(id, Quantity, userId, Remarks);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to add stock.");
                // The token was used up; issue a fresh one so the corrected form can be sent again.
                ModelState.Remove(nameof(SubmitToken));
                SubmitToken = SubmissionTokenGuard.NewToken();
                return Page();
            }

            TempData["Success"] = $"Added {QuantityFormat.Qty(Quantity)} to '{SeedStock.SpeciesName}' at {SeedStock.AreaName}.";
            return RedirectToPage("/Production/SeedStock/Details", new { id });
        }
    }
}
