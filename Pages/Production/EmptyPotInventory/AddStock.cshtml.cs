using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using EmptyPotInventoryModel = PlantStockManager.Models.EmptyPotInventory;

namespace PlantStockManager.Pages.Production.EmptyPotInventory
{
    public class AddStockModel : PageModel
    {
        private readonly EmptyPotInventoryRepository _emptyPotInventoryRepo;
        private readonly AreaAccessService _areaAccessService;

        public AddStockModel(EmptyPotInventoryRepository emptyPotInventoryRepo, AreaAccessService areaAccessService)
        {
            _emptyPotInventoryRepo = emptyPotInventoryRepo;
            _areaAccessService = areaAccessService;
        }

        public EmptyPotInventoryModel? EmptyPotInventory { get; set; }

        [BindProperty]
        public decimal Quantity { get; set; }

        [BindProperty]
        public string? Remarks { get; set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            EmptyPotInventory = await _emptyPotInventoryRepo.GetByIdAsync(id);
            if (EmptyPotInventory == null)
                return RedirectToPage("/Production/EmptyPotInventory/Index");

            // Phase E (spec item 17): this page previously had NO Area check
            // at all on either verb -- a Growing Partner Supervisor could
            // add stock to another Area's empty-pot pool by direct URL, or
            // by POSTing with the id changed. Guard both verbs; GET simply
            // refuses to show the form for a record outside the user's
            // scope, and POST (below) refuses to act on it even if the
            // page's hidden id field was tampered with.
            if (!_areaAccessService.CanAccessArea(User, EmptyPotInventory.AreaId))
            {
                TempData["Error"] = "You are not authorized to add stock to this Empty Pot Inventory record.";
                return RedirectToPage("/Production/EmptyPotInventory/Index");
            }

            return Page();
        }

        public async Task<IActionResult> OnPostAsync(int id)
        {
            EmptyPotInventory = await _emptyPotInventoryRepo.GetByIdAsync(id);
            if (EmptyPotInventory == null)
                return RedirectToPage("/Production/EmptyPotInventory/Index");

            if (!_areaAccessService.CanAccessArea(User, EmptyPotInventory.AreaId))
            {
                TempData["Error"] = "You are not authorized to add stock to this Empty Pot Inventory record.";
                return RedirectToPage("/Production/EmptyPotInventory/Index");
            }

            if (Quantity <= 0)
                ModelState.AddModelError(nameof(Quantity), "Quantity to add must be greater than zero.");

            if (!ModelState.IsValid)
                return Page();

            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;

            var (success, message) = await _emptyPotInventoryRepo.AddStockAsync(id, Quantity, "StockIn", userId, Remarks);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to add stock.");
                return Page();
            }

            TempData["Success"] = $"Added {Quantity:N2} to '{EmptyPotInventory.PotSize}'.";
            return RedirectToPage("/Production/EmptyPotInventory/Details", new { id });
        }
    }
}
