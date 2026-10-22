using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

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
    // 2026-10-21: a double click / browser retry / replay of the same form is
    // stopped by SubmissionTokenGuard (it used to credit the lot again).
    public class AddSeedStockModel : PageModel
    {
        public const string TokenScope = "SeedStockReceive";

        private readonly SeedStockRepository _seedStockRepo;
        private readonly AreaRepository _areaRepo;
        private readonly PlantTypeRepository _plantTypeRepo;
        private readonly PlantSpeciesRepository _plantSpeciesRepo;
        private readonly SeedSourcesRepository _seedSourcesRepo;
        private readonly SeedlingAreaScope _areaAccessService; // seedling-only Area scope (Authorization/SeedlingAreaScope.cs)
        private readonly SubmissionTokenGuard _tokens;

        public AddSeedStockModel(
            SeedStockRepository seedStockRepo,
            AreaRepository areaRepo,
            PlantTypeRepository plantTypeRepo,
            PlantSpeciesRepository plantSpeciesRepo,
            SeedSourcesRepository seedSourcesRepo,
            SeedlingAreaScope areaAccessService,
            SubmissionTokenGuard tokens)
        {
            _seedStockRepo = seedStockRepo;
            _areaRepo = areaRepo;
            _plantTypeRepo = plantTypeRepo;
            _plantSpeciesRepo = plantSpeciesRepo;
            _seedSourcesRepo = seedSourcesRepo;
            _areaAccessService = areaAccessService;
            _tokens = tokens;
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
        [BindProperty] public string? SubmitToken { get; set; }

        public List<PlantType> PlantTypes { get; set; } = new();
        public List<Area> Areas { get; set; } = new();
        public List<SeedSource> SeedSources { get; set; } = new();

        public async Task OnGetAsync()
        {
            ReceivedOn = DateTime.Now;
            SubmitToken = SubmissionTokenGuard.NewToken();
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
            else if (Quantity > SeedStockEditRules.MaxQuantity)
                ModelState.AddModelError(nameof(Quantity), SeedStockEditRules.MaxQuantityMessage);
            if (ReceivedOn.Date > DateTime.Today)
                ModelState.AddModelError(nameof(ReceivedOn), "Received On cannot be in the future.");
            else if (ReceivedOn.Date < SeedStockEditRules.EarliestDate)
                ModelState.AddModelError(nameof(ReceivedOn), SeedStockEditRules.EarliestDateMessage);

            if (!ModelState.IsValid)
            {
                await LoadDropdownsAsync();
                return Page();
            }

            if (!_tokens.TryConsume(TokenScope, SubmitToken))
            {
                TempData["Error"] = "This form was already submitted. Check Seed Stock before adding the receipt again.";
                return RedirectToPage("/Production/SeedStock/Index");
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
                // The token was used up; issue a fresh one so the corrected form can be sent again.
                ModelState.Remove(nameof(SubmitToken));
                SubmitToken = SubmissionTokenGuard.NewToken();
                await LoadDropdownsAsync();
                return Page();
            }

            TempData["Success"] = $"Added {QuantityFormat.Qty(Quantity)} {Unit} to Seed Stock.";
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
