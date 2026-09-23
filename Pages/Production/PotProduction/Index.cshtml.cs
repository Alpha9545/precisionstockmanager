using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PotProductionModel = PlantStockManager.Models.PotProduction;

namespace PlantStockManager.Pages.Production.PotProduction
{
    public class IndexModel : PageModel
    {
        private readonly PotProductionRepository _potProductionRepo;
        private readonly AreaAccessService _areaAccessService;

        public IndexModel(PotProductionRepository potProductionRepo, AreaAccessService areaAccessService)
        {
            _potProductionRepo = potProductionRepo;
            _areaAccessService = areaAccessService;
        }

        public List<PotProductionModel> PotProductions { get; set; } = new();

        public decimal TotalProduced => PotProductions.Where(p => p.Status != "Cancelled").Sum(p => p.Quantity);

        public async Task OnGetAsync()
        {
            var all = await _potProductionRepo.GetAllAsync();

            // Phase E (spec item 21): Production History is one of the
            // minimum functions a Growing Partner Supervisor needs, but
            // this page (built in Phase D/19 and, before that, Phase 7) had
            // never been Area-scoped -- closing that pre-existing gap now,
            // using the same pattern as every other Phase E list page.
            PotProductions = _areaAccessService.HasFullAreaAccess(User)
                ? all
                : all.Where(p => _areaAccessService.CanAccessArea(User, p.AreaId)).ToList();
        }
    }
}
