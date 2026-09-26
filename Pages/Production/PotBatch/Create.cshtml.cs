using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using CuttingStockModel = PlantStockManager.Models.CuttingStock;

namespace PlantStockManager.Pages.Production.PotBatch
{
    // Start a pot production batch: choose the cuttings, then the empty pots
    // issued to a production Area -- the Area and pot size come from that
    // choice (pots are only ever used in the Area they were issued to). One
    // cutting makes one pot. Then the expected ready date and the supervisor
    // who will confirm it READY.
    public class CreateModel : PageModel
    {
        private readonly PotBatchRepository _batchRepo;
        private readonly CuttingStockRepository _cuttingStockRepo;
        private readonly EmptyPotInventoryRepository _emptyPotRepo;
        private readonly UserRoleRepository _userRoleRepo;
        private readonly AreaAccessService _areaAccess;

        public CreateModel(PotBatchRepository batchRepo, CuttingStockRepository cuttingStockRepo, 
            EmptyPotInventoryRepository emptyPotRepo, UserRoleRepository userRoleRepo, AreaAccessService areaAccess)
        {
            _batchRepo = batchRepo;
            _cuttingStockRepo = cuttingStockRepo;
            _emptyPotRepo = emptyPotRepo;
            _userRoleRepo = userRoleRepo;
            _areaAccess = areaAccess;
        }

        [BindProperty] public int SourceCuttingStockId { get; set; }
        [BindProperty] public decimal CuttingAllocated { get; set; }
        [BindProperty] public int EmptyPotInventoryId { get; set; }
        [BindProperty] public DateTime ProductionStartDate { get; set; } = DateTime.Today;
        [BindProperty] public DateTime ExpectedReadyDate { get; set; } = DateTime.Today.AddDays(30);
        [BindProperty] public int SupervisorId { get; set; }
        [BindProperty] public string? Remarks { get; set; }

        public List<CuttingStockModel> StockPools { get; set; } = new();
        public List<PlantStockManager.Models.EmptyPotInventory> PotPools { get; set; } = new();

        public async Task OnGetAsync(int? cuttingStockId)
        {
            await LoadAsync();
            if (cuttingStockId.HasValue && StockPools.Any(s => s.Id == cuttingStockId))
                SourceCuttingStockId = cuttingStockId.Value;
            if (PotPools.Count == 1)
                EmptyPotInventoryId = PotPools[0].Id;   // only one Area / size issued: chosen automatically
        }

        // Supervisors who may confirm READY: Mother Plant Supervisors of the
        // production Area, never the person creating the batch.
        public async Task<JsonResult> OnGetSupervisorsAsync(int areaId)
        {
            if (!_areaAccess.CanAccessArea(User, areaId))
                return new JsonResult(Array.Empty<object>());
            var me = User.GetUserId();
            var list = (await _userRoleRepo.GetUsersInRoleAsync(SupervisorRules.MotherPlantSupervisor, areaId)).Where(u => u.EmployeeID != me);
            return new JsonResult(list.Select(u => new { id = u.EmployeeID, name = u.Name }));
        }

        public async Task<IActionResult> OnPostAsync()
        {
            var me = User.GetUserId();
            var stock = await _cuttingStockRepo.GetByIdAsync(SourceCuttingStockId);
            if (stock == null || !CanUseSource(stock))
                ModelState.AddModelError(string.Empty, "Choose a Cutting Stock you can use.");
            var pool = await _emptyPotRepo.GetByIdAsync(EmptyPotInventoryId);
            if (pool == null || !IsProductionPool(pool))
                ModelState.AddModelError(string.Empty, "Choose the empty pots issued to your production Area.");
            if (!me.HasValue)
                ModelState.AddModelError(string.Empty, "Your user could not be identified.");

            if (ModelState.IsValid)
            {
                var eligible = (await _userRoleRepo.GetUsersInRoleAsync(SupervisorRules.MotherPlantSupervisor, pool!.AreaId)).Select(u => u.EmployeeID).ToList();
                var (ok, error) = PotBatchRules.ValidateCreate(CuttingAllocated, stock!.AvailableQuantity, ProductionStartDate, ExpectedReadyDate,
                    SupervisorId, me!.Value, eligible);
                if (!ok)
                    ModelState.AddModelError(string.Empty, error!);
            }
            if (!ModelState.IsValid)
            {
                await LoadAsync();
                return Page();
            }

            var entry = new PotProductionBatch
            {
                SourceCuttingStockId = SourceCuttingStockId,
                CuttingAllocated = CuttingAllocated,
                AreaId = pool!.AreaId!.Value,
                PotSize = pool.PotSize,
                ProductionStartDate = ProductionStartDate,
                ExpectedReadyDate = ExpectedReadyDate,
                SupervisorId = SupervisorId,
                Remarks = Remarks,
                CreatedBy = User.Identity?.Name ?? "System"
            };
            var (success, message, id) = await _batchRepo.CreateAsync(entry, me!.Value);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to start the batch.");
                await LoadAsync();
                return Page();
            }
            TempData["Success"] = $"Pot batch {entry.BatchCode} started with {CuttingAllocated:N0} cuttings.";
            return RedirectToPage("/Production/PotBatch/Details", new { id });
        }

        // Main Office Cutting Stock, or cuttings held in the user's own Areas.
        private bool CanUseSource(CuttingStockModel s)
            => CuttingRules.CanUseAsSource(s.AreaType == DirectSowingRules.MainOfficeAreaType, _areaAccess.CanAccessArea(User, s.AreaId));

        // Empty pots issued to a production Area the user works in (never the
        // Main Office store itself).
        private bool IsProductionPool(PlantStockManager.Models.EmptyPotInventory p)
            => p.IsActive && p.AreaId.HasValue && p.AreaType != DirectSowingRules.MainOfficeAreaType
               && _areaAccess.CanAccessArea(User, p.AreaId);

        private async Task LoadAsync()
        {
            StockPools = (await _cuttingStockRepo.GetAllAsync())
                .Where(s => s.AvailableQuantity > 0 && CanUseSource(s)).OrderBy(s => s.SpeciesName).ThenBy(s => s.AreaName).ToList();
            PotPools = (await _emptyPotRepo.GetAllAsync(activeOnly: true))
                .Where(p => IsProductionPool(p) && p.PhysicalQuantity > 0)
                .OrderBy(p => p.AreaName).ThenBy(p => p.PotSize).ToList();
        }
    }
}
