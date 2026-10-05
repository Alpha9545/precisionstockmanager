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
    // cutting makes one pot. Then the expected ready date and who will
    // confirm it READY ("Ready Confirmation By": any authorized user assigned
    // to the production Area -- see PotBatchRules.CanConfirmReady).
    public class CreateModel : PageModel
    {
        private readonly PotBatchRepository _batchRepo;
        private readonly CuttingStockRepository _cuttingStockRepo;
        private readonly EmptyPotInventoryRepository _emptyPotRepo;
        private readonly UserRoleRepository _userRoleRepo;
        private readonly PolyhouseRepository _polyhouseRepo;
        private readonly AreaAccessService _areaAccess;

        public CreateModel(PotBatchRepository batchRepo, CuttingStockRepository cuttingStockRepo,
            EmptyPotInventoryRepository emptyPotRepo, UserRoleRepository userRoleRepo, PolyhouseRepository polyhouseRepo, AreaAccessService areaAccess)
        {
            _batchRepo = batchRepo;
            _cuttingStockRepo = cuttingStockRepo;
            _emptyPotRepo = emptyPotRepo;
            _userRoleRepo = userRoleRepo;
            _polyhouseRepo = polyhouseRepo;
            _areaAccess = areaAccess;
        }

        [BindProperty] public int SourceCuttingStockId { get; set; }
        [BindProperty] public decimal CuttingAllocated { get; set; }
        [BindProperty] public int EmptyPotInventoryId { get; set; }
        [BindProperty] public int? PolyhouseId { get; set; }
        [BindProperty] public DateTime ProductionStartDate { get; set; } = DateTime.Today;
        [BindProperty] public DateTime ExpectedReadyDate { get; set; } = DateTime.Today.AddDays(30);
        // Stored in dbo.PotProductionBatches.SupervisorId (the column keeps its
        // historical name): the user expected to confirm READY. It does not
        // limit who may confirm -- any authorized user of the Area can.
        [BindProperty] public int ReadyConfirmerId { get; set; }
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

        // Users who may confirm READY for the production Area: active users
        // assigned to that Area who hold a Pot Production permission, whatever
        // their role is called. The person creating the batch is included.
        public async Task<JsonResult> OnGetReadyConfirmersAsync(int areaId)
        {
            if (!_areaAccess.CanAccessArea(User, areaId))
                return new JsonResult(Array.Empty<object>());
            var list = await _userRoleRepo.GetReadyConfirmersAsync(areaId);
            return new JsonResult(list.Select(u => new { id = u.EmployeeID, name = u.Name }));
        }

        // Area first (fixed by the chosen Empty Pot pool, not a separate
        // field here): only the Polyhouses assigned to that Area, same
        // pattern as every other Area -> Polyhouse cascade in this app.
        public async Task<JsonResult> OnGetPolyhousesAsync(int areaId)
        {
            if (areaId > 0 && !_areaAccess.CanAccessArea(User, areaId))
                return new JsonResult(Array.Empty<object>());
            var list = (await _polyhouseRepo.GetAllPolyhouses())
                .Where(p => !p.AreaId.HasValue || (areaId > 0 && p.AreaId == areaId))
                .OrderBy(p => p.Name);
            return new JsonResult(list.Select(p => new { id = p.Id, name = p.AreaId.HasValue ? p.Name : p.Name + " (no Area assigned)" }));
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
                var eligible = (await _userRoleRepo.GetReadyConfirmersAsync(pool!.AreaId!.Value)).Select(u => u.EmployeeID).ToList();
                var (ok, error) = PotBatchRules.ValidateCreate(CuttingAllocated, stock!.AvailableQuantity, ProductionStartDate, ExpectedReadyDate,
                    ReadyConfirmerId, eligible);
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
                PolyhouseId = PolyhouseId,
                PotSize = pool.PotSize,
                ProductionStartDate = ProductionStartDate,
                ExpectedReadyDate = ExpectedReadyDate,
                SupervisorId = ReadyConfirmerId,
                Remarks = Remarks,
                CreatedBy = User.Identity?.Name ?? "System"
            };
            // Area isolation: the repository re-checks the pool's own Area under the pool's lock.
            var (success, message, id) = await _batchRepo.CreateAsync(entry, me!.Value,
                areaId => CuttingRules.CanUseAsSource(_areaAccess.CanAccessArea(User, areaId)));
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to start the batch.");
                await LoadAsync();
                return Page();
            }
            TempData["Success"] = $"Pot batch {entry.BatchCode} started with {QuantityFormat.Qty(CuttingAllocated)} cuttings.";
            return RedirectToPage("/Production/PotBatch/Details", new { id });
        }

        // Only cuttings held in an Area the user may access (Main Office stock is the Main Office Area's stock).
        private bool CanUseSource(CuttingStockModel s)
            => CuttingRules.CanUseAsSource(_areaAccess.CanAccessArea(User, s.AreaId));

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
