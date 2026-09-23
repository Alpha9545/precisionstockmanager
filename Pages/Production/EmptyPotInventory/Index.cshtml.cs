using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using EmptyPotInventoryModel = PlantStockManager.Models.EmptyPotInventory;

namespace PlantStockManager.Pages.Production.EmptyPotInventory
{
    public class IndexModel : PageModel
    {
        private readonly EmptyPotInventoryRepository _emptyPotInventoryRepo;
        private readonly AreaAccessService _areaAccessService;

        public IndexModel(EmptyPotInventoryRepository emptyPotInventoryRepo, AreaAccessService areaAccessService)
        {
            _emptyPotInventoryRepo = emptyPotInventoryRepo;
            _areaAccessService = areaAccessService;
        }

        public List<EmptyPotInventoryModel> EmptyPotInventories { get; set; } = new();

        public decimal TotalPhysical => EmptyPotInventories.Sum(e => e.PhysicalQuantity);

        public async Task OnGetAsync()
        {
            var all = await _emptyPotInventoryRepo.GetAllAsync();

            // Phase E (spec item 12/16): same Area-scoping as
            // PottedPlantStock/Index -- Empty Pot Inventory is a separate
            // inventory but still belongs to an Area, so it is scoped the
            // same way.
            EmptyPotInventories = _areaAccessService.HasFullAreaAccess(User)
                ? all
                : all.Where(e => _areaAccessService.CanAccessArea(User, e.AreaId)).ToList();
        }
    }
}
