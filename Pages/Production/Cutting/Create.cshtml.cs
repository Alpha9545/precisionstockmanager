using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.Cutting
{
    // Record cuttings taken from a Mother Plant. The variety, colour and Area
    // come from the Mother Plant; the cuttings are recorded in that Area's Cutting
    // Stock. The supervisor list contains only Mother Plant Supervisors of the
    // Mother Plant's Area. The supervisor also chooses where the cuttings go
    // (mandatory): Main Office (a delivery Main Office must confirm) or Use for
    // Pot Production (they stay in the Area's stock) -- see CuttingDestination.
    public class CreateModel : PageModel
    {
        private readonly CuttingProductionRepository _cuttingProductionRepo;
        private readonly MotherPlantRepository _motherPlantRepo;
        private readonly UserRoleRepository _userRoleRepo;
        private readonly AreaAccessService _areaAccess;
        private readonly AreaRepository _areaRepo;

        public CreateModel(CuttingProductionRepository cuttingProductionRepo, MotherPlantRepository motherPlantRepo,
            UserRoleRepository userRoleRepo, AreaAccessService areaAccess, AreaRepository areaRepo)
        {
            _areaRepo = areaRepo;
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
        // "Give To / Destination": CuttingDestination.MainOffice or .PotProduction (mandatory).
        [BindProperty] public string? Destination { get; set; }
        // Only used when more than one active Main Office Area exists.
        [BindProperty] public int? MainOfficeAreaId { get; set; }
        // One-time key of this form: submitting the same form twice saves it once.
        [BindProperty] public Guid SubmissionToken { get; set; }

        public List<PlantStockManager.Models.MotherPlant> MotherPlants { get; set; } = new();
        public List<Area> MainOfficeAreas { get; set; } = new();

        public async Task OnGetAsync(int? motherPlantId)
        {
            SubmissionToken = Guid.NewGuid();
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

            if (SubmissionToken == Guid.Empty)
                ModelState.AddModelError(string.Empty, "This form has expired. Reload the page and enter the cuttings again.");

            // Destination is mandatory; the repository re-derives it from the database.
            int? destinationAreaId = null;
            if (mp?.AreaId != null)
            {
                var mainOfficeIds = (await _areaRepo.GetByAreaTypesAsync("MainOffice")).Where(a => a.IsActive).Select(a => a.Id).ToList();
                var (destOk, resolvedAreaId, destError) = CuttingRules.ResolveDestination(Destination, MainOfficeAreaId, mp.AreaId.Value, mainOfficeIds);
                if (!destOk)
                    ModelState.AddModelError(string.Empty, destError!);
                destinationAreaId = resolvedAreaId;
            }
            else if (!CuttingDestination.IsValid(Destination))
                ModelState.AddModelError(string.Empty, "Choose where the cuttings go: Main Office or Use for Pot Production.");

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
                DestinationType = Destination,
                DestinationAreaId = destinationAreaId,
                SubmissionToken = SubmissionToken,
                CreatedBy = User.Identity?.Name ?? "System"
            };
            var (success, message, _) = await _cuttingProductionRepo.InsertAsync(entry, User.GetUserId());
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to record the cuttings.");
                await LoadAsync();
                return Page();
            }

            var variety = mp!.SpeciesName?.Trim();
            TempData["Success"] =
                entry.IsDuplicateSubmission
                    ? $"{entry.ProductionCode} was already saved; nothing was added twice."
                : Destination == CuttingDestination.MainOffice
                    ? $"{entry.ProductionCode}: {Quantity:N0} cuttings of {variety} recorded at {mp.AreaName} and sent to Main Office ({entry.TransferCode}). They become Main Office stock when Main Office confirms receipt."
                    : $"{entry.ProductionCode}: {Quantity:N0} cuttings of {variety} added to {mp.AreaName} Cutting Stock for Pot Production.";
            return RedirectToPage("/Production/Cutting/Index");
        }

        private async Task LoadAsync()
        {
            MainOfficeAreas = (await _areaRepo.GetByAreaTypesAsync("MainOffice")).Where(a => a.IsActive).OrderBy(a => a.Name).ToList();
            MotherPlants = (await _motherPlantRepo.GetAllAsync(status: "Active"))
                .Where(m => m.AreaId.HasValue && _areaAccess.CanAccessArea(User, m.AreaId))
                .OrderBy(m => m.SpeciesName)
                .ToList();
        }
    }
}
