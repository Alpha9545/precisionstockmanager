using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using CuttingDeliveryModel = PlantStockManager.Models.CuttingDelivery;

namespace PlantStockManager.Pages.Production.CuttingDelivery
{
    public class DetailsModel : PageModel
    {
        private readonly CuttingDeliveryRepository _cuttingDeliveryRepo;
        private readonly MotherPlantAreaScope _areaScope;

        public DetailsModel(CuttingDeliveryRepository cuttingDeliveryRepo, MotherPlantAreaScope areaScope)
        {
            _areaScope = areaScope;
            _cuttingDeliveryRepo = cuttingDeliveryRepo;
        }

        public CuttingDeliveryModel? CuttingDelivery { get; set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            CuttingDelivery = await _cuttingDeliveryRepo.GetByIdAsync(id);
            if (CuttingDelivery == null)
                return RedirectToPage("/Production/CuttingDelivery/Index");

            // F1: Area scope via the record's Mother Plant (MotherPlantAreaScope): block opening another Area's record by id.
            if (!await _areaScope.CanAccessAsync(User, CuttingDelivery.MotherPlantId))
            {
                TempData["Error"] = "You are not authorized to view this Cutting Delivery record.";
                return RedirectToPage("/Production/CuttingDelivery/Index");
            }

            return Page();
        }
    }
}
