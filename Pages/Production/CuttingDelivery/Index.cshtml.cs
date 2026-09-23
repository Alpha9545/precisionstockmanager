using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using CuttingDeliveryModel = PlantStockManager.Models.CuttingDelivery;

namespace PlantStockManager.Pages.Production.CuttingDelivery
{
    public class IndexModel : PageModel
    {
        private readonly CuttingDeliveryRepository _cuttingDeliveryRepo;

        public IndexModel(CuttingDeliveryRepository cuttingDeliveryRepo)
        {
            _cuttingDeliveryRepo = cuttingDeliveryRepo;
        }

        public List<CuttingDeliveryModel> CuttingDeliveries { get; set; } = new();

        public decimal TotalDelivered => CuttingDeliveries.Sum(c => c.DeliveredQuantity);
        public decimal TotalNet => CuttingDeliveries.Sum(c => c.NetQuantity);
        public decimal TotalLossAndDamage => CuttingDeliveries.Sum(c => c.LossQuantity + c.RemovedQuantity + c.RejectedQuantity + c.DamagedQuantity);

        public async Task OnGetAsync()
        {
            CuttingDeliveries = await _cuttingDeliveryRepo.GetAllAsync();
        }
    }
}
