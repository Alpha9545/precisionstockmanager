using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using SeedStockModel = PlantStockManager.Models.SeedStock;

namespace PlantStockManager.Pages.Production.SeedStock
{
    // Records a manual seed receipt (e.g. Main Office receiving seed
    // from a supplier) directly against an existing pool. Mirrors
    // EmptyPotInventory/AddStock's Area check on both verbs -- fetch
    // the actual record first, check its REAL AreaId, never trust a
    // posted id alone.
    public class AddStockModel : PageModel
    {
        private readonly SeedStockRepository _seedStockRepo;
        private readonly AreaAccessService _areaAccessService;

        public AddStockModel(SeedStockRepository seedStockRepo, AreaAccessService areaAccessService)
        {
            _seedStockRepo = seedStockRepo;
            _areaAccessService = areaAccessService;
        }

        public SeedStockModel? SeedStock { get; set; }

        [BindProperty]
        public decimal Quantity { get; set; }

        [BindProperty]
        public string? Remarks { get; set; }

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

            if (!ModelState.IsValid)
                return Page();

            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;

            var (success, message) = await _seedStockRepo.AddStockAsync(id, Quantity, userId, Remarks);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to add stock.");
                return Page();
            }

            TempData["Success"] = $"Added {Quantity:N2} to '{SeedStock.SpeciesName}' at {SeedStock.AreaName}.";
            return RedirectToPage("/Production/SeedStock/Details", new { id });
        }
    }
}
