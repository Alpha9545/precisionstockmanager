using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Admin
{
    public class AreaModel : PageModel
    {
        private readonly AreaRepository _areaRepo;
        private readonly PolyhouseRepository _polyhouseRepo;
        private readonly SupervisorSelectionService _supervisors;
        private readonly GrowingPartnerRepository _growingPartnerRepo;

        public AreaModel(AreaRepository areaRepo, PolyhouseRepository polyhouseRepo, SupervisorSelectionService supervisors, GrowingPartnerRepository growingPartnerRepo)
        {
            _areaRepo = areaRepo;
            _polyhouseRepo = polyhouseRepo;
            _supervisors = supervisors;
            _growingPartnerRepo = growingPartnerRepo;
        }

        public List<Area> Areas { get; set; } = new();
        public List<Polyhouse> Polyhouses { get; set; } = new();
        // Phase 1 (D-3): an Area's supervisor must be an ACTIVE user who holds
        // the permission of that Area's type (SupervisorRules.KindForAreaType)
        // through a role assigned to THIS Area (or an all-Area role). One entry
        // per (kind, user); the view narrows it to the chosen Area Type and Area.
        public List<KindedSupervisorOption> Supervisors { get; set; } = new();
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
            // A new Area has no role assignments yet: only an all-Area
            // supervisor can be set now (Area id 0 matches no assignment).
            await ValidateSupervisorAsync(NewArea, "NewArea", areaId: 0, currentSupervisorId: null);

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
            var existing = EditArea.Id > 0 ? await _areaRepo.GetAreaById(EditArea.Id) : null;
            await ValidateSupervisorAsync(EditArea, "EditArea", EditArea.Id, existing?.SupervisorId);

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

        // Phase 1 (D-3): server-side check of the chosen supervisor.
        private async Task ValidateSupervisorAsync(Area area, string prefix, int areaId, int? currentSupervisorId)
        {
            if (!area.SupervisorId.HasValue || area.SupervisorId <= 0)
                return;
            if (area.SupervisorId == currentSupervisorId)
                return; // unchanged value is kept
            var kind = SupervisorRules.KindForAreaType(area.AreaType);
            if (kind == null)
            {
                ModelState.AddModelError($"{prefix}.SupervisorId", "Choose the Area Type before assigning a supervisor.");
                return;
            }
            var error = await _supervisors.ValidateAsync(kind.Value, areaId, area.SupervisorId, currentSupervisorId);
            if (error != null)
                ModelState.AddModelError($"{prefix}.SupervisorId", areaId == 0
                    ? $"{error} A new Area has no assigned users yet: create the Area, assign the supervisor's role to it (Administration > User Role / Area Assignments), then set the supervisor."
                    : error);
        }

        private async Task LoadDropdownsAsync()
        {
            Polyhouses = await _polyhouseRepo.GetAllPolyhouses();
            Supervisors = await _supervisors.AllAreaKindOptionsAsync();
            GrowingPartners = await _growingPartnerRepo.GetAllAsync(activeOnly: true);
        }
    }
}
