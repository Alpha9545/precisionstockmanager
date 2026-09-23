using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;
using InternalTransferModel = PlantStockManager.Models.InternalTransfer;

namespace PlantStockManager.Pages.Production.InternalTransfer
{
    public class CreateModel : PageModel
    {
        private readonly InternalTransferRepository _internalTransferRepo;
        private readonly EmptyPotInventoryRepository _emptyPotInventoryRepo;
        private readonly PottedPlantStockRepository _pottedPlantStockRepo;
        private readonly AreaRepository _areaRepo;
        private readonly EmployeeRepository _employeeRepo;

        public CreateModel(
            InternalTransferRepository internalTransferRepo,
            EmptyPotInventoryRepository emptyPotInventoryRepo,
            PottedPlantStockRepository pottedPlantStockRepo,
            AreaRepository areaRepo,
            EmployeeRepository employeeRepo)
        {
            _internalTransferRepo = internalTransferRepo;
            _emptyPotInventoryRepo = emptyPotInventoryRepo;
            _pottedPlantStockRepo = pottedPlantStockRepo;
            _areaRepo = areaRepo;
            _employeeRepo = employeeRepo;
        }

        [BindProperty]
        public InternalTransferModel InternalTransfer { get; set; } = new();

        // Only pools that already have a real (non-null) Area and some
        // physical stock are offered as a Transfer source -- legacy
        // "unassigned location" pools must first receive stock directly
        // against a specific Area (Add Stock / Pot Production) before
        // they can be moved onward.
        public List<PlantStockManager.Models.EmptyPotInventory> EmptyPotPools { get; set; } = new();
        public List<PlantStockManager.Models.PottedPlantStock> PottedPlantPools { get; set; } = new();
        public List<Area> Areas { get; set; } = new();
        public List<Employee> ResponsiblePersons { get; set; } = new();
        public List<Employee> Supervisors { get; set; } = new();

        public async Task OnGetAsync()
        {
            InternalTransfer.StockType = "EmptyPot";
            await LoadDropdownsAsync();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            ModelState.Remove("InternalTransfer.TransferCode");
            ModelState.Remove("InternalTransfer.CreatedBy");
            ModelState.Remove("InternalTransfer.Status");
            ModelState.Remove("InternalTransfer.SourceAreaId"); // server-derived from the selected pool
            ModelState.Remove("InternalTransfer.PotSize");      // server-derived from the selected pool

            if (InternalTransfer.StockType != "EmptyPot" && InternalTransfer.StockType != "PottedPlant")
                ModelState.AddModelError("InternalTransfer.StockType", "Stock Type is required.");
            if (InternalTransfer.StockType == "EmptyPot" && !InternalTransfer.SourceEmptyPotInventoryId.HasValue)
                ModelState.AddModelError("InternalTransfer.SourceEmptyPotInventoryId", "Source Pot Size / Area is required.");
            if (InternalTransfer.StockType == "PottedPlant" && !InternalTransfer.SourcePottedPlantStockId.HasValue)
                ModelState.AddModelError("InternalTransfer.SourcePottedPlantStockId", "Source Species / Pot Size / Area is required.");
            if (!InternalTransfer.DestinationAreaId.HasValue || InternalTransfer.DestinationAreaId <= 0)
                ModelState.AddModelError("InternalTransfer.DestinationAreaId", "Destination Area is required.");
            if (InternalTransfer.Quantity <= 0)
                ModelState.AddModelError("InternalTransfer.Quantity", "Quantity must be greater than zero.");

            if (!ModelState.IsValid)
            {
                await LoadDropdownsAsync();
                return Page();
            }

            // Only the field relevant to the chosen Stock Type is kept --
            // the repository itself also enforces "exactly one of the
            // two source Ids" via the DB CHECK constraint as a backstop.
            if (InternalTransfer.StockType == "EmptyPot")
                InternalTransfer.SourcePottedPlantStockId = null;
            else
                InternalTransfer.SourceEmptyPotInventoryId = null;

            InternalTransfer.CreatedBy = User.Identity?.Name ?? "System";
            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;

            var (success, message, _) = await _internalTransferRepo.InsertAsync(InternalTransfer, userId);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to record Internal Transfer.");
                await LoadDropdownsAsync();
                return Page();
            }

            TempData["Success"] = $"Internal Transfer {InternalTransfer.TransferCode} recorded successfully. Stock moved.";
            return RedirectToPage("/Production/InternalTransfer/Index");
        }

        private async Task LoadDropdownsAsync()
        {
            var allEmptyPots = await _emptyPotInventoryRepo.GetAllAsync(activeOnly: true);
            EmptyPotPools = allEmptyPots.Where(p => p.AreaId.HasValue && p.PhysicalQuantity > 0).ToList();

            var allPotted = await _pottedPlantStockRepo.GetAllAsync();
            PottedPlantPools = allPotted.Where(p => p.AreaId.HasValue && p.PhysicalQuantity > 0).ToList();

            Areas = await _areaRepo.GetAllAreas();
            var activeUsers = await _employeeRepo.GetAllActiveUsers();
            ResponsiblePersons = activeUsers;
            Supervisors = activeUsers;
        }
    }
}
