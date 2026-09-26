using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Production.EmptyPotInventory
{
    // Office Officer records empty pots bought from a supplier. The pots go
    // into the Office (Main Office) store's Empty Pot stock; from there they
    // are issued to production Areas (Issue).
    public class PurchaseModel : PageModel
    {
        private readonly EmptyPotPurchaseRepository _purchaseRepo;
        private readonly PotSizeRepository _potSizeRepo;
        private readonly AreaRepository _areaRepo;
        private readonly VendorRepository _vendorRepo;

        public PurchaseModel(EmptyPotPurchaseRepository purchaseRepo, PotSizeRepository potSizeRepo, AreaRepository areaRepo, VendorRepository vendorRepo)
        {
            _purchaseRepo = purchaseRepo;
            _potSizeRepo = potSizeRepo;
            _areaRepo = areaRepo;
            _vendorRepo = vendorRepo;
        }

        [BindProperty] public DateTime PurchaseDate { get; set; } = DateTime.Today;
        [BindProperty] public string? PotSize { get; set; }
        [BindProperty] public decimal Quantity { get; set; }
        [BindProperty] public string? SupplierName { get; set; }
        [BindProperty] public string? InvoiceRef { get; set; }
        [BindProperty] public int AreaId { get; set; }
        [BindProperty] public string? Remarks { get; set; }

        public List<PotSize> PotSizes { get; set; } = new();
        public List<Area> OfficeStores { get; set; } = new();
        public List<string> SupplierSuggestions { get; set; } = new();
        public List<EmptyPotPurchase> Recent { get; set; } = new();

        public async Task OnGetAsync()
        {
            await LoadAsync();
            if (OfficeStores.Count == 1)
                AreaId = OfficeStores[0].Id;
        }

        public async Task<IActionResult> OnPostAsync()
        {
            var entry = new EmptyPotPurchase
            {
                PurchaseDate = PurchaseDate,
                PotSize = PotSize ?? "",
                Quantity = Quantity,
                SupplierName = SupplierName ?? "",
                InvoiceRef = InvoiceRef,
                AreaId = AreaId,
                Remarks = Remarks,
                CreatedBy = User.Identity?.Name ?? "System"
            };
            var (success, message, _) = await _purchaseRepo.InsertAsync(entry, User.GetUserId());
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to record the purchase.");
                await LoadAsync();
                return Page();
            }
            TempData["Success"] = $"Purchase {entry.PurchaseCode}: {Quantity:N0} x {PotSize} pots added to Office stock.";
            return RedirectToPage();
        }

        private async Task LoadAsync()
        {
            PotSizes = await _potSizeRepo.GetAllAsync(activeOnly: true);
            OfficeStores = (await _areaRepo.GetByAreaTypesAsync(PlantStockManager.Services.DirectSowingRules.MainOfficeAreaType))
                .Where(a => a.IsActive).ToList();
            SupplierSuggestions = (await _vendorRepo.GetAllAsync()).Where(v => v.IsActive).Select(v => v.Name).OrderBy(n => n).ToList();
            Recent = (await _purchaseRepo.GetAllAsync(DateTime.Today.AddDays(-60), DateTime.Today)).Take(25).ToList();
        }
    }
}
