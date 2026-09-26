using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PurchaseOrderModel = PlantStockManager.Models.PurchaseOrder;

namespace PlantStockManager.Pages.Production.PurchaseOrder
{
    public class CreateModel : PageModel
    {
        private readonly PurchaseOrderRepository _purchaseOrderRepo;
        private readonly VendorRepository _vendorRepo;
        private readonly AreaRepository _areaRepo;
        private readonly DatabaseHelper _dbHelper;
        private readonly AreaAccessService _areaAccessService;

        public CreateModel(
            PurchaseOrderRepository purchaseOrderRepo,
            VendorRepository vendorRepo,
            AreaRepository areaRepo,
            DatabaseHelper dbHelper,
            AreaAccessService areaAccessService)
        {
            _areaAccessService = areaAccessService;
            _purchaseOrderRepo = purchaseOrderRepo;
            _vendorRepo = vendorRepo;
            _areaRepo = areaRepo;
            _dbHelper = dbHelper;
        }

        [BindProperty]
        public PurchaseOrderModel Order { get; set; } = new();

        public List<Vendor> Vendors { get; set; } = new();
        public List<Area> Areas { get; set; } = new();
        public List<PurchaseOrderLookupItem> Fertilizers { get; set; } = new();
        public List<PurchaseOrderLookupItem> FertilizerSources { get; set; } = new();
        public List<PurchaseOrderLookupItem> Units { get; set; } = new();

        public async Task OnGetAsync()
        {
            Order.OrderDate = DateTime.Today;
            await LoadDropdownsAsync();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            ModelState.Remove("Order.PurchaseOrderCode");
            ModelState.Remove("Order.CreatedBy");
            ModelState.Remove("Order.Status");

            if (Order.VendorId <= 0)
                ModelState.AddModelError("Order.VendorId", "Vendor is required.");
            if (Order.Items == null || Order.Items.Count == 0)
                ModelState.AddModelError(string.Empty, "At least one line item is required.");

            // Phase D: empty pots are bought through Empty Pot Purchase (one
            // path into Empty Pot Stock), never through a purchase order.
            if (Order.Items != null && Order.Items.Any(i => i.ItemCategory == "EmptyPot"))
                ModelState.AddModelError(string.Empty, "Empty pots are recorded with Pot Production > Empty Pot Purchase, not with a purchase order.");

            if (!ModelState.IsValid)
            {
                await LoadDropdownsAsync();
                return Page();
            }

            Order.CreatedBy = User.Identity?.Name ?? "System";
            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;

            var (success, message, _) = await _purchaseOrderRepo.InsertAsync(Order, userId);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to create Purchase Order.");
                await LoadDropdownsAsync();
                return Page();
            }

            TempData["Success"] = $"Purchase Order {Order.PurchaseOrderCode} created successfully.";
            return RedirectToPage("/Production/PurchaseOrder/Index");
        }

        private async Task LoadDropdownsAsync()
        {
            Vendors = await _vendorRepo.GetAllAsync(activeOnly: true);
            Areas = _areaAccessService.FilterByArea(User, await _areaRepo.GetAllAreas(), a => (int?)a.Id); // F1

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            Fertilizers = await LoadLookupAsync(conn, "SELECT FertilizerId, FertilizerName FROM FertilizerMaster ORDER BY FertilizerName");
            FertilizerSources = await LoadLookupAsync(conn, "SELECT SourceId, SourceName FROM FertilizerSource ORDER BY SourceName");
            Units = await LoadLookupAsync(conn, "SELECT UnitId, UnitName FROM UnitMaster ORDER BY UnitName");
        }

        private static async Task<List<PurchaseOrderLookupItem>> LoadLookupAsync(SqlConnection conn, string sql)
        {
            var list = new List<PurchaseOrderLookupItem>();
            using var cmd = new SqlCommand(sql, conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new PurchaseOrderLookupItem { Id = reader.GetInt32(0), Name = reader.GetString(1) });
            }
            return list;
        }
    }

    // Small read-only lookup pair (Id/Name) for the Fertilizer/Source/Unit
    // master dropdowns -- these have no dedicated repository class in the
    // existing app (Pages/Fertilizer/*.cshtml.cs queries them directly
    // the same way), so Create queries them directly too rather than
    // inventing a repository around three lookup lists.
    public class PurchaseOrderLookupItem
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }
}
