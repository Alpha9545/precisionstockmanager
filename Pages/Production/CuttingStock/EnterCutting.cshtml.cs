using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Production.CuttingStock
{
    // "Enter Cutting" -- step 1 of the approved Cutting workflow. A fresh,
    // independent harvest record at the supervisor's own Area; no
    // CuttingPlan/ActualCutting link (per the earlier confirmed decision).
    // Calls CuttingStockRepository.EnterCuttingAsync directly.
    public class EnterCuttingModel : PageModel
    {
        private readonly CuttingStockRepository _cuttingStockRepo;
        private readonly PlantTypeRepository _plantTypeRepo;
        private readonly PlantSpeciesRepository _plantSpeciesRepo;
        private readonly AreaRepository _areaRepo;

        public EnterCuttingModel(
            CuttingStockRepository cuttingStockRepo,
            PlantTypeRepository plantTypeRepo,
            PlantSpeciesRepository plantSpeciesRepo,
            AreaRepository areaRepo)
        {
            _cuttingStockRepo = cuttingStockRepo;
            _plantTypeRepo = plantTypeRepo;
            _plantSpeciesRepo = plantSpeciesRepo;
            _areaRepo = areaRepo;
        }

        [BindProperty]
        public int PlantTypeId { get; set; }

        [BindProperty]
        public int SpeciesId { get; set; }

        [BindProperty]
        public int AreaId { get; set; }

        [BindProperty]
        public decimal Quantity { get; set; }

        [BindProperty]
        public string? Remarks { get; set; }

        public List<PlantType> PlantTypes { get; set; } = new();
        public List<Area> Areas { get; set; } = new();

        // Cutting-producing Area types only -- Outlet/MainOffice never
        // harvest cuttings themselves.
        private static readonly string[] _sourceAreaTypes = { "MotherPlant", "Kunjir", "Kiran" };

        public async Task OnGetAsync()
        {
            await LoadDropdownsAsync();
        }

        public async Task<JsonResult> OnGetSpeciesByPlantType(int plantTypeId)
        {
            var species = await _plantSpeciesRepo.GetSpeciesByPlantType(plantTypeId);
            return new JsonResult(species);
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (SpeciesId <= 0)
                ModelState.AddModelError(nameof(SpeciesId), "Variety is required.");
            if (AreaId <= 0)
                ModelState.AddModelError(nameof(AreaId), "Area is required.");
            if (Quantity <= 0)
                ModelState.AddModelError(nameof(Quantity), "Cutting Quantity must be greater than zero.");

            if (!ModelState.IsValid)
            {
                await LoadDropdownsAsync();
                return Page();
            }

            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;
            var createdBy = User.Identity?.Name ?? "System";

            var (success, message, _) = await _cuttingStockRepo.EnterCuttingAsync(SpeciesId, AreaId, Quantity, userId, createdBy, Remarks);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to record the cutting.");
                await LoadDropdownsAsync();
                return Page();
            }

            TempData["Success"] = $"Recorded {Quantity:N2} cuttings.";
            return RedirectToPage("/Production/CuttingStock/EnterCutting");
        }

        private async Task LoadDropdownsAsync()
        {
            PlantTypes = await _plantTypeRepo.GetAllPlantTypes();
            Areas = await _areaRepo.GetByAreaTypesAsync(_sourceAreaTypes);
        }
    }
}
