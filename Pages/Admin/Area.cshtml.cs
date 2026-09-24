using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Admin
{
    public class AreaModel : PageModel
    {
        private readonly AreaRepository _areaRepo;
        private readonly PolyhouseRepository _polyhouseRepo;
        private readonly EmployeeRepository _employeeRepo;
        private readonly GrowingPartnerRepository _growingPartnerRepo;

        public AreaModel(AreaRepository areaRepo, PolyhouseRepository polyhouseRepo, EmployeeRepository employeeRepo, GrowingPartnerRepository growingPartnerRepo)
        {
            _areaRepo = areaRepo;
            _polyhouseRepo = polyhouseRepo;
            _employeeRepo = employeeRepo;
            _growingPartnerRepo = growingPartnerRepo;
        }

        public List<Area> Areas { get; set; } = new();
        public List<Polyhouse> Polyhouses { get; set; } = new();
        public List<Employee> Supervisors { get; set; } = new();
        // Phase 17: optional. "Internal / No Growing Partner" (the default,
        // GrowingPartnerId = null) always remains available -- this list
        // only adds the option to assign one.
        public List<GrowingPartner> GrowingPartners { get; set; } = new();

        // Phase 14: fixed AreaType list, mirrors CK_Area_AreaType in the DB.
        // Exposed as an instance property (not a bare static field) so the
        // Razor view can read it via Model.AreaTypes -- a static member
        // cannot be accessed through an instance reference in C# (CS0176).
        private static readonly string[] _areaTypes = { "MotherPlant", "Kunjir", "Kiran", "Outlet", "MainOffice" };
        public string[] AreaTypes => _areaTypes;

        [BindProperty]
        public Area NewArea { get; set; } = new();

        [BindProperty]
        public Area EditArea { get; set; } = new();

        public async Task OnGetAsync()
        {
            await LoadDropdownsAsync();
            Areas = await _areaRepo.GetAllAreas();
        }

        public async Task<IActionResult> OnPostAddAsync()
        {
            ValidateArea(NewArea, "NewArea");

            if (!ModelState.IsValid)
            {
                await LoadDropdownsAsync();
                Areas = await _areaRepo.GetAllAreas();
                return Page();
            }

            await _areaRepo.AddArea(NewArea);
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostEditAsync()
        {
            if (EditArea.Id <= 0)
                ModelState.AddModelError("EditArea.Id", "Invalid Area.");

            ValidateArea(EditArea, "EditArea");

            if (!ModelState.IsValid)
            {
                await LoadDropdownsAsync();
                Areas = await _areaRepo.GetAllAreas();
                return Page();
            }

            await _areaRepo.UpdateArea(EditArea);
            return RedirectToPage();
        }

        // Mirrors the CHECK constraints enforced in the database
        // (Phase2_Area_MotherPlant.sql, Phase14_RoleFoundation_AreaExtension.sql)
        // so obviously invalid input is rejected with a friendly message before it ever
        // reaches SQL Server.
        //
        // PolyhouseId (legacy link: "this Area is located inside that
        // Polyhouse") is required for the operational AreaTypes used by the
        // older modules (MotherPlant / Kunjir / Kiran / Outlet). It is
        // optional for "MainOffice" (Phase 14 Decision 1) and -- Phase B --
        // for a plain site Area (no AreaType, e.g. "Main Nursery"), which
        // CONTAINS Polyhouses through dbo.Polyhouses.AreaId instead.
        private void ValidateArea(Area area, string prefix)
        {
            bool polyhouseOptional = area.AreaType == "MainOffice" || string.IsNullOrWhiteSpace(area.AreaType);

            if (!polyhouseOptional && (area.PolyhouseId == null || area.PolyhouseId <= 0))
                ModelState.AddModelError($"{prefix}.PolyhouseId", "Polyhouse is required for Mother Plant / Kunjir / Kiran / Outlet Areas.");
            if (string.IsNullOrWhiteSpace(area.Name))
                ModelState.AddModelError($"{prefix}.Name", "Area Name is required.");
            if (area.AreaSize.HasValue && area.AreaSize <= 0)
                ModelState.AddModelError($"{prefix}.AreaSize", "Area Size must be greater than zero.");
            if (area.Capacity.HasValue && area.Capacity <= 0)
                ModelState.AddModelError($"{prefix}.Capacity", "Capacity must be greater than zero.");
            if (!string.IsNullOrWhiteSpace(area.AreaType) && !AreaTypes.Contains(area.AreaType))
                ModelState.AddModelError($"{prefix}.AreaType", "Invalid Area Type.");
        }

        private async Task LoadDropdownsAsync()
        {
            Polyhouses = await _polyhouseRepo.GetAllPolyhouses();
            Supervisors = await _employeeRepo.GetAllActiveUsers();
            GrowingPartners = await _growingPartnerRepo.GetAllAsync(activeOnly: true);
        }
    }
}
