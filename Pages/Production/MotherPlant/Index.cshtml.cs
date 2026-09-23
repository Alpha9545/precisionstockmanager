using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using MotherPlantModel = PlantStockManager.Models.MotherPlant;

namespace PlantStockManager.Pages.Production.MotherPlant
{
    public class IndexModel : PageModel
    {
        private readonly MotherPlantRepository _motherPlantRepo;
        private readonly PolyhouseRepository _polyhouseRepo;
        private readonly EmployeeRepository _employeeRepo;
        private readonly AreaAccessService _areaAccessService;

        public IndexModel(
            MotherPlantRepository motherPlantRepo,
            PolyhouseRepository polyhouseRepo,
            EmployeeRepository employeeRepo,
            AreaAccessService areaAccessService)
        {
            _motherPlantRepo = motherPlantRepo;
            _polyhouseRepo = polyhouseRepo;
            _employeeRepo = employeeRepo;
            _areaAccessService = areaAccessService;
        }

        public List<MotherPlantModel> MotherPlants { get; set; } = new();
        public List<Polyhouse> Polyhouses { get; set; } = new();
        public List<Employee> ResponsiblePersons { get; set; } = new();

        [BindProperty(SupportsGet = true)] public int? PolyhouseId { get; set; }
        [BindProperty(SupportsGet = true)] public int? SpeciesId { get; set; }
        [BindProperty(SupportsGet = true)] public string? Status { get; set; }
        [BindProperty(SupportsGet = true)] public int? ResponsiblePersonId { get; set; }

        // Simple dashboard counters over the current (filtered) result set.
        public int ActiveBatchCount => MotherPlants.Count(m => m.Status == "Active");
        public decimal TotalMotherPlantQuantity => MotherPlants.Sum(m => m.MotherPlantQuantity);
        public decimal TotalExpectedMonthlyCutting => MotherPlants.Sum(m => m.ExpectedMonthlyCuttingQuantity);

        public async Task OnGetAsync()
        {
            var all = await _motherPlantRepo.GetAllAsync(PolyhouseId, SpeciesId, Status, ResponsiblePersonId);

            // Phase 17/B: in-memory post-filter, not a repository change --
            // GetAllAsync is also called by CuttingPlan Create/Edit and the
            // Dashboard for cross-Area dropdown/aggregate purposes that must
            // keep seeing everything, so the Area-scope restriction is
            // applied only here, to what THIS page displays. A full-access
            // user (Admin/Management/MainOfficeOfficer) sees every row,
            // exactly as today.
            MotherPlants = _areaAccessService.HasFullAreaAccess(User)
                ? all
                : all.Where(m => _areaAccessService.CanAccessArea(User, m.AreaId)).ToList();

            Polyhouses = await _polyhouseRepo.GetAllPolyhouses();
            ResponsiblePersons = await _employeeRepo.GetAllActiveUsers();
        }
    }
}
