using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PotProductionModel = PlantStockManager.Models.PotProduction;
using PropagationBatchModel = PlantStockManager.Models.PropagationBatch;

namespace PlantStockManager.Pages.Production.PotProduction
{
    public class CreateModel : PageModel
    {
        private readonly PotProductionRepository _potProductionRepo;
        private readonly PropagationBatchRepository _propagationBatchRepo;
        private readonly EmptyPotInventoryRepository _emptyPotInventoryRepo;
        private readonly EmployeeRepository _employeeRepo;

        public CreateModel(
            PotProductionRepository potProductionRepo,
            PropagationBatchRepository propagationBatchRepo,
            EmptyPotInventoryRepository emptyPotInventoryRepo,
            EmployeeRepository employeeRepo)
        {
            _potProductionRepo = potProductionRepo;
            _propagationBatchRepo = propagationBatchRepo;
            _emptyPotInventoryRepo = emptyPotInventoryRepo;
            _employeeRepo = employeeRepo;
        }

        [BindProperty]
        public PotProductionModel PotProduction { get; set; } = new();

        public List<PropagationBatchModel> OpenPropagationBatches { get; set; } = new();
        public List<PlantStockManager.Models.EmptyPotInventory> ActivePotSizes { get; set; } = new();
        public List<Employee> ResponsiblePersons { get; set; } = new();
        public List<Employee> Supervisors { get; set; } = new();

        public async Task OnGetAsync()
        {
            PotProduction.ProductionDate = DateTime.Today;
            await LoadDropdownsAsync();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            ModelState.Remove("PotProduction.ProductionCode");
            ModelState.Remove("PotProduction.CreatedBy");
            ModelState.Remove("PotProduction.MotherPlantId"); // server-derived from the Propagation Batch
            ModelState.Remove("PotProduction.SpeciesId");      // server-derived from the Propagation Batch
            ModelState.Remove("PotProduction.PotSize");        // server-derived from the selected Empty Pot pool
            ModelState.Remove("PotProduction.AreaId");         // server-derived from the selected Empty Pot pool
            ModelState.Remove("PotProduction.Status");

            if (!PotProduction.PropagationBatchId.HasValue || PotProduction.PropagationBatchId.Value <= 0)
                ModelState.AddModelError("PotProduction.PropagationBatchId", "Propagation Batch is required.");
            if (PotProduction.EmptyPotInventoryId <= 0)
                ModelState.AddModelError("PotProduction.EmptyPotInventoryId", "Pot Size / Area is required.");
            if (PotProduction.Quantity <= 0)
                ModelState.AddModelError("PotProduction.Quantity", "Quantity must be greater than zero.");

            if (!ModelState.IsValid)
            {
                await LoadDropdownsAsync();
                return Page();
            }

            // The selected option identifies one specific (PotSize, Area)
            // Empty Pot pool -- PotSize/AreaId are always derived from it
            // server-side, never trusted directly from the form.
            var selectedPool = await _emptyPotInventoryRepo.GetByIdAsync(PotProduction.EmptyPotInventoryId);
            if (selectedPool == null)
            {
                ModelState.AddModelError("PotProduction.EmptyPotInventoryId", "Selected Pot Size / Area no longer exists.");
                await LoadDropdownsAsync();
                return Page();
            }
            PotProduction.PotSize = selectedPool.PotSize;
            PotProduction.AreaId = selectedPool.AreaId;

            PotProduction.CreatedBy = User.Identity?.Name ?? "System";
            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;

            var (success, message, _) = await _potProductionRepo.InsertAsync(PotProduction, userId);
            if (!success)
            {
                // Covers the "exceeds Survived Quantity" rule, the "not
                // enough Empty Pot stock" rule, and any DB-level
                // constraint failure -- surfaced as a friendly message,
                // never a raw SQL error.
                ModelState.AddModelError(string.Empty, message ?? "Failed to record Pot Production.");
                await LoadDropdownsAsync();
                return Page();
            }

            TempData["Success"] = $"Pot Production {PotProduction.ProductionCode} recorded successfully. Potted Plant Stock updated.";
            return RedirectToPage("/Production/PotProduction/Index");
        }

        private async Task LoadDropdownsAsync()
        {
            OpenPropagationBatches = await _propagationBatchRepo.GetOpenForPotProductionAsync();
            ActivePotSizes = await _emptyPotInventoryRepo.GetAllAsync(activeOnly: true);
            var activeUsers = await _employeeRepo.GetAllActiveUsers();
            ResponsiblePersons = activeUsers;
            Supervisors = activeUsers;
        }
    }
}
