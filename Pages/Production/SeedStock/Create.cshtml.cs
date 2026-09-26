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

        // Phase 9: re-displayed in the search box after a failed POST, so
        // the user's already-made variety choice isn't silently lost just
        // because the search box itself has no server-rendered options.
        public string? SelectedSpeciesName { get; set; }

        public async Task OnGetAsync()
        {
            await LoadDropdownsAsync();
        }

        // Phase 9 (Seed Stock Usability): a fast, capped variety search --
        // never renders 1,000+ options into one <select>. plantTypeId is
        // an OPTIONAL narrowing filter (Plant Type chosen first, same as
        // before); q is the free-text name search typed into the box.
        // Always capped (PlantSpeciesRepository.SearchAsync's own limit)
        // so a broad/empty query never returns the whole table.
        public async Task<JsonResult> OnGetSearchSpeciesAsync(string? q, int? plantTypeId)
        {
            var species = await _plantSpeciesRepo.SearchAsync(q, plantTypeId);
            return new JsonResult(species.Select(s => new { id = s.Id, name = s.Name, scientificName = s.ScientificName }));
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
                if (SeedStock.SpeciesId > 0)
                    SelectedSpeciesName = (await _plantSpeciesRepo.GetByIdAsync(SeedStock.SpeciesId))?.Name;
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
