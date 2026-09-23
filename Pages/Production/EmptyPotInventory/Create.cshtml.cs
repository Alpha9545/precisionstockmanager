using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using EmptyPotInventoryModel = PlantStockManager.Models.EmptyPotInventory;

namespace PlantStockManager.Pages.Production.EmptyPotInventory
{
    public class CreateModel : PageModel
    {
        private readonly EmptyPotInventoryRepository _emptyPotInventoryRepo;
        private readonly AreaRepository _areaRepo;
        private readonly AreaAccessService _areaAccessService;

        public CreateModel(EmptyPotInventoryRepository emptyPotInventoryRepo, AreaRepository areaRepo, AreaAccessService areaAccessService)
        {
            _emptyPotInventoryRepo = emptyPotInventoryRepo;
            _areaRepo = areaRepo;
            _areaAccessService = areaAccessService;
        }

        [BindProperty]
        public EmptyPotInventoryModel EmptyPotInventory { get; set; } = new();

        public List<Area> Areas { get; set; } = new();

        public async Task OnGetAsync()
        {
            Areas = await LoadAccessibleAreasAsync();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (string.IsNullOrWhiteSpace(EmptyPotInventory.PotSize))
                ModelState.AddModelError("EmptyPotInventory.PotSize", "Pot Size is required.");

            // Phase E (spec item 12/17): the Area dropdown is already
            // pre-filtered below, but that alone is not enough (spec item
            // 12: "do not rely only on dropdown filtering") -- a POST with
            // a tampered AreaId for another Area's pool must be rejected
            // here too, exactly like CreateFromCutting's Cutting Stock
            // dropdown/server-check pair.
            if (!_areaAccessService.CanAccessArea(User, EmptyPotInventory.AreaId))
                ModelState.AddModelError("EmptyPotInventory.AreaId", "You are not authorized to create a Pot Size pool for that Area.");

            if (!ModelState.IsValid)
            {
                Areas = await LoadAccessibleAreasAsync();
                return Page();
            }

            EmptyPotInventory.CreatedBy = User.Identity?.Name ?? "System";

            var (success, message, _) = await _emptyPotInventoryRepo.InsertAsync(EmptyPotInventory);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to save Pot Size.");
                Areas = await LoadAccessibleAreasAsync();
                return Page();
            }

            TempData["Success"] = $"Pot Size '{EmptyPotInventory.PotSize}' added. Use Add Stock to bring in physical quantity.";
            return RedirectToPage("/Production/EmptyPotInventory/Index");
        }

        private async Task<List<Area>> LoadAccessibleAreasAsync()
        {
            var all = await _areaRepo.GetAllAreas();
            return _areaAccessService.HasFullAreaAccess(User)
                ? all
                : all.Where(a => _areaAccessService.CanAccessArea(User, a.Id)).ToList();
        }
    }
}
