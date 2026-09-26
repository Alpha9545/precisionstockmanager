using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Production.SeedStock
{
    // Simplified, single-page "Add Seed Stock" workflow for nursery staff:
    // one form finds-or-creates the (Species, Area, BatchNo) pool AND credits
    // it, instead of the two separate existing steps (Create then AddStock/{id}).
    // Reuses the exact same repository primitives, validation and permission
    // (SeedStock.Enter) those two pages already use -- SeedStockRepository.ReceiveAsync
    // is the only new code, and it is itself just GetOrCreateLockedAsync +
    // RecordTransactionAsync composed in one transaction, mirroring how
    // CuttingProductionRepository.InsertAsync already composes the same two
    // kinds of calls for Cutting Stock. Create.cshtml and AddStock/{id}.cshtml
    // are untouched and remain available for their existing narrower uses.
    public class AddSeedStockModel : PageModel
    {
        private readonly SeedStockRepository _seedStockRepo;
        private readonly AreaRepository _areaRepo;
        private readonly PlantTypeRepository _plantTypeRepo;
        private readonly PlantSpeciesRepository _plantSpeciesRepo;
        private readonly SeedSourcesRepository _seedSourcesRepo;
        private readonly SeedlingAreaScope _areaAccessService; // seedling-only Area scope (Authorization/SeedlingAreaScope.cs)

        public AddSeedStockModel(
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

        [BindProperty] public int PlantTypeId { get; set; }
        [BindProperty] public int SpeciesId { get; set; }
        [BindProperty] public int AreaId { get; set; }
        [BindProperty] public string? SeedSourceId { get; set; }
        [BindProperty] public string? BatchNo { get; set; }
        [BindProperty] public string Unit { get; set; } = "Seeds";
        [BindProperty] public decimal Quantity { get; set; }
        [BindProperty] public DateTime ReceivedOn { get; set; } = DateTime.Now;
        [BindProperty] public string? Notes { get; set; }

        public List<PlantType> PlantTypes { get; set; } = new();
        public List<Area> Areas { get; set; } = new();
        public List<SeedSource> SeedSources { get; set; } = new();

        public async Task OnGetAsync()
        {
            ReceivedOn = DateTime.Now;
            await LoadDropdownsAsync();
            // A single accessible Main Office Area is pre-selected -- most
            // deployments only have one, and Area is not a field the nursery
            // form asks about; a dropdown only appears when there is a
            // genuine choice to make (see the .cshtml).
            if (Areas.Count == 1)
                AreaId = Areas[0].Id;
        }

        public async Task<JsonResult> OnGetSpeciesAsync(int plantTypeId)
        {
            var species = await _plantSpeciesRepo.GetSpeciesByPlantType(plantTypeId);
            return new JsonResult(species.Select(s => new { id = s.Id, name = s.Name }));
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (SpeciesId <= 0)
                ModelState.AddModelError(nameof(SpeciesId), "Seed / Variety is required.");
            if (!_areaAccessService.CanAccessArea(User, AreaId))
                ModelState.AddModelError(nameof(AreaId), "You are not authorized to receive Seed Stock for the selected Area.");
            if (Quantity <= 0)
                ModelState.AddModelError(nameof(Quantity), "Quantity must be greater than zero.");
            else if (!PlantStockManager.Services.DirectSowingRules.IsWholeNumber(Quantity))
                ModelState.AddModelError(nameof(Quantity), "Quantity must be a whole number.");
            if (ReceivedOn.Date > DateTime.Today)
                ModelState.AddModelError(nameof(ReceivedOn), "Received On cannot be in the future.");

            if (!ModelState.IsValid)
            {
                await LoadDropdownsAsync();
                return Page();
            }

            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;
            var createdBy = User.Identity?.Name ?? "System";
            int? seedSourceId = int.TryParse(SeedSourceId, out var parsedSourceId) ? parsedSourceId : null;

            var (success, message, seedStockId) = await _seedStockRepo.ReceiveAsync(
                SpeciesId, AreaId, BatchNo, seedSourceId, Unit, Quantity, ReceivedOn, Notes, userId, createdBy);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to add Seed Stock.");
                await LoadDropdownsAsync();
                return Page();
            }

            TempData["Success"] = $"Added {Quantity:N0} {Unit} to Seed Stock.";
            return RedirectToPage("/Production/SeedStock/Details", new { id = seedStockId });
        }

        private async Task LoadDropdownsAsync()
        {
            PlantTypes = await _plantTypeRepo.GetAllPlantTypes();
            SeedSources = await _seedSourcesRepo.GetAllSeedSources();

            // Phase B: Seed Stock is received only at an active Main Office Area.
            var all = (await _areaRepo.GetAllAreas())
                .Where(a => PlantStockManager.Services.DirectSowingRules.IsMainOfficeSeedLocation(a.AreaType, a.IsActive))
                .ToList();
            Areas = _areaAccessService.HasFullAreaAccess(User)
                ? all
                : all.Where(a => _areaAccessService.CanAccessArea(User, a.Id)).ToList();
        }
    }
}
