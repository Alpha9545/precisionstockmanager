using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.BedSowing
{
    // 2026-10-12: record a Bed Sowing -- Sowing Date, Variety (+ its Main Office seed lot),
    // Polyhouse (the Bed Sowing Polyhouse, preselected), Quantity, Notes. No cavity, no trays.
    // Page access: Sowing.Enter. The Polyhouse must be an active Bed Sowing location and the
    // user must be allowed to work in its Area -- re-checked in BedSowingRepository.CreateAsync.
    public class CreateModel : PageModel
    {
        private readonly BedSowingRepository _repo;
        private readonly SeedStockRepository _seedStockRepo;
        private readonly PlantTypeRepository _plantTypeRepo;
        private readonly PlantSpeciesRepository _plantSpeciesRepo;
        private readonly SeedlingAreaScope _scope;

        public CreateModel(BedSowingRepository repo, SeedStockRepository seedStockRepo, PlantTypeRepository plantTypeRepo,
            PlantSpeciesRepository plantSpeciesRepo, SeedlingAreaScope scope)
        {
            _repo = repo;
            _seedStockRepo = seedStockRepo;
            _plantTypeRepo = plantTypeRepo;
            _plantSpeciesRepo = plantSpeciesRepo;
            _scope = scope;
        }

        [BindProperty]
        public Models.BedSowing Bed { get; set; } = new();

        [BindProperty]
        public int PlantTypeId { get; set; }

        public bool SchemaReady { get; private set; }
        public List<BedSowingLocation> Locations { get; private set; } = new();
        public List<PlantType> PlantTypes { get; private set; } = new();

        public async Task OnGetAsync()
        {
            Bed.SowingDate = AppClock.Today;
            Bed.SubmissionToken = Guid.NewGuid();
            await LoadAsync();
            if (Locations.Count == 1)
                Bed.PolyhouseId = Locations[0].PolyhouseId;   // preselected
        }

        public async Task<JsonResult> OnGetVarietiesAsync(int plantTypeId)
        {
            var list = await _plantSpeciesRepo.GetSpeciesByPlantType(plantTypeId);
            return new JsonResult(list.Select(v => new { id = v.Id, name = v.Name.Trim(), growingDays = v.ReadyStockDays }));
        }

        public async Task<JsonResult> OnGetSeedLotsAsync(int speciesId)
        {
            var list = (await _seedStockRepo.GetAllAsync())
                .Where(s => s.SpeciesId == speciesId && s.AvailableQuantity > 0 && DirectSowingRules.IsMainOfficeSeedLocation(s.AreaType, true))
                .OrderBy(s => s.CreatedDate);
            return new JsonResult(list.Select(s => new
            {
                id = s.Id,
                lot = string.IsNullOrEmpty(s.BatchNo) ? "(no lot no.)" : s.BatchNo,
                source = s.SeedSourceName,
                area = s.AreaName,
                available = s.AvailableQuantity,
                unit = s.Unit
            }));
        }

        public async Task<JsonResult> OnGetFindLotsAsync(string? term)
        {
            if (string.IsNullOrWhiteSpace(term) || term.Trim().Length < 2)
                return new JsonResult(Array.Empty<object>());
            var list = await _seedStockRepo.SearchAsync(term, onlyAvailable: true, mainOfficeOnly: true, max: 25);
            var typeIds = (await _plantSpeciesRepo.GetAllAsync()).ToDictionary(v => v.Id, v => v.PlantTypeId);
            return new JsonResult(list.Select(s => new
            {
                id = s.Id,
                speciesId = s.SpeciesId,
                plantTypeId = typeIds.TryGetValue(s.SpeciesId, out var t) ? t : 0,
                variety = (s.SpeciesName ?? "").Trim(),
                crop = s.PlantTypeName,
                lot = string.IsNullOrEmpty(s.BatchNo) ? "(no lot no.)" : s.BatchNo,
                source = s.SeedSourceName,
                available = s.AvailableQuantity
            }));
        }

        public async Task<IActionResult> OnPostAsync()
        {
            ModelState.Clear();   // validated explicitly; server-derived fields are never trusted
            if (Bed.SubmissionToken is not { } token || token == Guid.Empty)
                ModelState.AddModelError(string.Empty, "This form has expired. Reload the page and enter the bed sowing again.");
            if (Bed.SpeciesId <= 0)
                ModelState.AddModelError(string.Empty, "Variety is required.");
            if (Bed.SeedStockId <= 0)
                ModelState.AddModelError(string.Empty, "Main Office seed lot is required.");
            var (qtyOk, qtyError) = BedSowingRules.ValidateBedQuantity(Bed.Quantity);
            if (!qtyOk)
                ModelState.AddModelError(string.Empty, qtyError!);
            var (dateOk, dateError) = BedSowingRules.ValidateBusinessDate(Bed.SowingDate, AppClock.Today, "Sowing Date");
            if (!dateOk)
                ModelState.AddModelError(string.Empty, dateError!);
            if (!ModelState.IsValid)
            {
                await LoadAsync();
                return Page();
            }

            var actor = new BedSowingRepository.Actor(User.GetUserId(), User.Identity?.Name, User.IsFullAccess());
            var (ok, message, id) = await _repo.CreateAsync(Bed, actor, areaId => _scope.CanAccessArea(User, areaId));
            if (!ok)
            {
                ModelState.AddModelError(string.Empty, message ?? "The bed sowing could not be saved.");
                await LoadAsync();
                return Page();
            }
            TempData["Success"] = Bed.IsDuplicateSubmission
                ? $"Bed Sowing {Bed.BedSowingCode} was already saved from this form -- nothing was recorded twice."
                : $"Bed Sowing {Bed.BedSowingCode} recorded: {QuantityFormat.Qty(Bed.Quantity)} seeds deducted from the lot. Pending transplanting -- the Ready Date is set when seedlings are transplanted into trays.";
            return RedirectToPage("/Production/BedSowing/Details", new { id });
        }

        private async Task LoadAsync()
        {
            SchemaReady = await _repo.SchemaReadyAsync();
            Locations = (await _repo.GetLocationsAsync()).Where(l => _scope.CanAccessArea(User, l.AreaId)).ToList();
            PlantTypes = await _plantTypeRepo.GetAllPlantTypes();
        }
    }
}
