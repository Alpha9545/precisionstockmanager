using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.Cutting
{
    // Record cuttings taken from a Mother Plant. The variety, colour and Area
    // come from the Mother Plant; the cuttings go into that Area's Cutting
    // Stock. The supervisor list contains only Mother Plant Supervisors of
    // the Mother Plant's Area.
    public class CreateModel : PageModel
    {
        private readonly CuttingProductionRepository _cuttingProductionRepo;
        private readonly MotherPlantRepository _motherPlantRepo;
        private readonly UserRoleRepository _userRoleRepo;
        private readonly AreaAccessService _areaAccess;

        public CreateModel(CuttingProductionRepository cuttingProductionRepo, MotherPlantRepository motherPlantRepo,
            UserRoleRepository userRoleRepo, AreaAccessService areaAccess)
        {
            _cuttingProductionRepo = cuttingProductionRepo;
            _motherPlantRepo = motherPlantRepo;
            _userRoleRepo = userRoleRepo;
            _areaAccess = areaAccess;
        }

        [BindProperty] public int MotherPlantId { get; set; }
        [BindProperty] public DateTime CuttingDate { get; set; } = DateTime.Today;
        [BindProperty] public decimal Quantity { get; set; }
        [BindProperty] public int SupervisorId { get; set; }
        [BindProperty] public string? Remarks { get; set; }

        public List<PlantStockManager.Models.MotherPlant> MotherPlants { get; set; } = new();

        public async Task OnGetAsync(int? motherPlantId)
        {
            await LoadAsync();
            if (motherPlantId.HasValue && MotherPlants.Any(m => m.Id == motherPlantId))
                MotherPlantId = motherPlantId.Value;
        }

        // Mother Plant Supervisors of the Mother Plant's Area (default: its own supervisor).
        public async Task<JsonResult> OnGetSupervisorsAsync(int motherPlantId)
        {
            var mp = await _motherPlantRepo.GetByIdAsync(motherPlantId);
            if (mp == null || !mp.AreaId.HasValue || !_areaAccess.CanAccessArea(User, mp.AreaId))
                return new JsonResult(Array.Empty<object>());
            var list = await _userRoleRepo.GetUsersInRoleAsync(SupervisorRules.MotherPlantSupervisor, mp.AreaId);
            return new JsonResult(list.Select(u => new { id = u.EmployeeID, name = u.Name, isDefault = u.EmployeeID == mp.SupervisorId }));
        }

        public async Task<IActionResult> OnPostAsync()
        {
            var mp = await _motherPlantRepo.GetByIdAsync(MotherPlantId);
            if (mp == null)
                ModelState.AddModelError(string.Empty, "Choose the Mother Plant.");
            else if (!mp.AreaId.HasValue)
                ModelState.AddModelError(string.Empty, "This Mother Plant has no Area. Set its Area first (Mother Plants > Edit).");
            else if (!_areaAccess.CanAccessArea(User, mp.AreaId))
                ModelState.AddModelError(string.Empty, "You are not authorized to record cuttings for this Mother Plant's Area.");

            var (qtyOk, qtyError) = CuttingRules.ValidateProductionQuantity(Quantity);
            if (!qtyOk)
                ModelState.AddModelError(string.Empty, qtyError!);

            if (mp?.AreaId != null)
            {
                var eligible = (await _userRoleRepo.GetUsersInRoleAsync(SupervisorRules.MotherPlantSupervisor, mp.AreaId)).Select(u => u.EmployeeID);
                if (!eligible.Contains(SupervisorId))
                    ModelState.AddModelError(string.Empty, "Choose a Mother Plant Supervisor of this Area.");
            }

            if (!ModelState.IsValid)
            {
                await LoadAsync();
                return Page();
            }

            var entry = new CuttingProduction
            {
                MotherPlantId = MotherPlantId,
                CuttingDate = CuttingDate,
                Quantity = Quantity,
                SupervisorId = SupervisorId,
                Remarks = Remarks,
                CreatedBy = User.Identity?.Name ?? "System"
            };
            var (success, message, _) = await _cuttingProductionRepo.InsertAsync(entry, User.GetUserId());
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to record the cuttings.");
                await LoadAsync();
                return Page();
            }

            TempData["Success"] = $"{entry.ProductionCode}: {Quantity:N0} cuttings of {mp!.SpeciesName?.Trim()} added to {mp.AreaName} Cutting Stock.";
            return RedirectToPage("/Production/Cutting/Index");
        }

        private async Task LoadAsync()
        {
            MotherPlants = (await _motherPlantRepo.GetAllAsync(status: "Active"))
                .Where(m => m.AreaId.HasValue && _areaAccess.CanAccessArea(User, m.AreaId))
                .OrderBy(m => m.SpeciesName)
                .ToList();
        }
    }
}
