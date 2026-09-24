using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using CuttingDeliveryModel = PlantStockManager.Models.CuttingDelivery;

namespace PlantStockManager.Pages.Production.CuttingDelivery
{
    public class IndexModel : PageModel
    {
        private readonly CuttingDeliveryRepository _cuttingDeliveryRepo;
        private readonly MotherPlantAreaScope _areaScope;

        public IndexModel(CuttingDeliveryRepository cuttingDeliveryRepo, MotherPlantAreaScope areaScope)
        {
            _areaScope = areaScope;
            _cuttingDeliveryRepo = cuttingDeliveryRepo;
        }

        public List<CuttingDeliveryModel> CuttingDeliveries { get; set; } = new();

        public decimal TotalDelivered => CuttingDeliveries.Sum(c => c.DeliveredQuantity);
        public decimal TotalNet => CuttingDeliveries.Sum(c => c.NetQuantity);
        public decimal TotalLossAndDamage => CuttingDeliveries.Sum(c => c.LossQuantity + c.RemovedQuantity + c.RejectedQuantity + c.DamagedQuantity);

        public async Task OnGetAsync()
        {
            // F1: Area scope via the record's Mother Plant (MotherPlantAreaScope): previously every Area's
            // records were listed to every user.
            CuttingDeliveries = await _areaScope.FilterAsync(User, await _cuttingDeliveryRepo.GetAllAsync(), r => (int?)r.MotherPlantId);
        }
    }
}
