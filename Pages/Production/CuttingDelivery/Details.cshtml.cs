using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using CuttingDeliveryModel = PlantStockManager.Models.CuttingDelivery;

namespace PlantStockManager.Pages.Production.CuttingDelivery
{
    public class DetailsModel : PageModel
    {
        private readonly CuttingDeliveryRepository _cuttingDeliveryRepo;

        public DetailsModel(CuttingDeliveryRepository cuttingDeliveryRepo)
        {
            _cuttingDeliveryRepo = cuttingDeliveryRepo;
        }

        public CuttingDeliveryModel? CuttingDelivery { get; set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            CuttingDelivery = await _cuttingDeliveryRepo.GetByIdAsync(id);
            if (CuttingDelivery == null)
                return RedirectToPage("/Production/CuttingDelivery/Index");

            return Page();
        }
    }
}
