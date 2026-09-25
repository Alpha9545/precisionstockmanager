using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using SeedStockModel = PlantStockManager.Models.SeedStock;

namespace PlantStockManager.Pages.Production.SeedStock
{
    // Creates a new, empty (PhysicalQuantity = 0) Species+Area+Lot
    // pool. Mirrors EmptyPotInventory/Create's shape and its Area
    // check (spec item 11/17: dropdown pre-filtered AND independently
    // re-validated server-side against a posted AreaId).
    public class CreateModel : PageModel
    {
        private readonly SeedStockRepository _seedStockRepo;
        private readonly AreaRepository _areaRepo;
        private readonly PlantTypeRepository _plantTypeRepo;
        private readonly PlantSpeciesRepository _plantSpeciesRepo;
        private readonly SeedSourcesRepository _seedSourcesRepo;
        private readonly SeedlingAreaScope _areaAccessService; // seedling-only Area scope (Authorization/SeedlingAreaScope.cs)

        public CreateModel(
            SeedStockRepository seedStockRepo,
            AreaRepository areaRepo,
            PlantTypeRepository plantTypeRepo,
            PlantSpeciesRepository plantSpeciesRepo,
            SeedSourcesRepository seedSourcesRepo,
            SeedlingAreaScope areaAccessService)
        {
            _seedStockRepo = seedStockRepo;
            _areaRepo = areaRepo;
            _plantTypeRepo = plantTypeRepo;
            _plantSpeciesRepo = plantSpeciesRepo;
            _seedSourcesRepo = seedSourcesRepo;
            _areaAccessService = areaAccessService;
        }

        [BindProperty]
        public SeedStockModel SeedStock { get; set; } = new();

        public List<Area> Areas { get; set; } = new();
        public List<PlantType> PlantTypes { get; set; } = new();
        public List<SeedSource> SeedSources { get; set; } = new();

        public async Task OnGetAsync()
        {
            await LoadDropdownsAsync();
        }

        public async Task<JsonResult> OnGetSpeciesAsync(int plantTypeId)
        {
            var species = await _plantSpeciesRepo.GetSpeciesByPlantType(plantTypeId);
            return new JsonResult(species.Select(s => new { id = s.Id, name = s.Name }));
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (SeedStock.SpeciesId <= 0)
                ModelState.AddModelError("SeedStock.SpeciesId", "Species/Variety is required.");

            // Never rely on dropdown filtering alone (spec item 11) --
            // re-check server-side that the posted AreaId is one this
            // user may actually create a pool for.
            if (!_areaAccessService.CanAccessArea(User, SeedStock.AreaId))
                ModelState.AddModelError("SeedStock.AreaId", "You are not authorized to create a Seed Stock pool for that Area.");

            if (!ModelState.IsValid)
            {
                await LoadDropdownsAsync();
                return Page();
            }

            SeedStock.CreatedBy = User.Identity?.Name ?? "System";

            var (success, message, _) = await _seedStockRepo.InsertAsync(SeedStock);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to create Seed Stock pool.");
                await LoadDropdownsAsync();
                return Page();
            }

            TempData["Success"] = "Seed Stock pool created. Use Add Stock to bring in physical quantity.";
            return RedirectToPage("/Production/SeedStock/Index");
        }

        private async Task LoadDropdownsAsync()
        {
            // Phase B: Seed Stock lives at the Main Office only (Direct
            // Sowing consumes it from there). The repository re-checks this.
            var all = (await _areaRepo.GetAllAreas())
                .Where(a => PlantStockManager.Services.DirectSowingRules.IsMainOfficeSeedLocation(a.AreaType, a.IsActive))
                .ToList();
            Areas = _areaAccessService.HasFullAreaAccess(User)
                ? all
                : all.Where(a => _areaAccessService.CanAccessArea(User, a.Id)).ToList();
            PlantTypes = await _plantTypeRepo.GetAllPlantTypes();
            SeedSources = await _seedSourcesRepo.GetAllSeedSources();
        }
    }
}
