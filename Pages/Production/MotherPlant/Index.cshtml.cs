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
        private readonly AreaRepository _areaRepo;
        private readonly EmployeeRepository _employeeRepo;
        private readonly AreaAccessService _areaAccessService;
        private readonly UserRoleRepository _userRoleRepo;
        private readonly FeatureAccessService _featureAccess;

        public IndexModel(
            MotherPlantRepository motherPlantRepo,
            AreaRepository areaRepo,
            EmployeeRepository employeeRepo,
            AreaAccessService areaAccessService,
            UserRoleRepository userRoleRepo,
            FeatureAccessService featureAccess)
        {
            _featureAccess = featureAccess;
            _userRoleRepo = userRoleRepo;
            _motherPlantRepo = motherPlantRepo;
            _areaRepo = areaRepo;
            _employeeRepo = employeeRepo;
            _areaAccessService = areaAccessService;
        }

        public List<MotherPlantModel> MotherPlants { get; set; } = new();
        public List<Area> Areas { get; set; } = new();
        public List<Employee> ResponsiblePersons { get; set; } = new();

        // Same rule the Delete page itself enforces (MotherPlant.Enter).
        public bool CanDelete { get; set; }

        [BindProperty(SupportsGet = true)] public int? AreaId { get; set; }
        [BindProperty(SupportsGet = true)] public int? SpeciesId { get; set; }
        [BindProperty(SupportsGet = true)] public string? Status { get; set; }
        [BindProperty(SupportsGet = true)] public int? ResponsiblePersonId { get; set; }

        // Simple dashboard counters over the current (filtered) result set.
        public int ActiveBatchCount => MotherPlants.Count(m => m.Status == "Active");
        public decimal TotalMotherPlantQuantity => MotherPlants.Sum(m => m.MotherPlantQuantity);
        public decimal TotalExpectedMonthlyCutting => MotherPlants.Sum(m => m.ExpectedMonthlyCuttingQuantity);

        public async Task OnGetAsync()
        {
            // Phase D: the person filter is the Mother Plant Supervisor.
            // The list is filtered by Area (MotherPlant.AreaId), not Polyhouse.
            var all = (await _motherPlantRepo.GetAllAsync(null, SpeciesId, Status))
                .Where(m => !AreaId.HasValue || m.AreaId == AreaId)
                .Where(m => !ResponsiblePersonId.HasValue || m.SupervisorId == ResponsiblePersonId)
                .ToList();

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

            // Same Area-scoped list Mother Plant Create offers: only Areas this user may access.
            Areas = (await _areaRepo.GetAllAreas()).Where(a => _areaAccessService.CanAccessArea(User, a.Id)).OrderBy(a => a.Name).ToList();
            ResponsiblePersons = await _userRoleRepo.GetUsersInRoleAsync(PlantStockManager.Services.SupervisorRules.MotherPlantSupervisor);
            CanDelete = await _featureAccess.CanAccessPageAsync(User, "/Production/MotherPlant/Delete");
        }
    }
}
