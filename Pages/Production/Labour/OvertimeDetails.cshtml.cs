using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.Labour
{
    // Overtime Details (Labour.View): one entry with its database-computed duration and
    // worker-hours and its full audit history. Only the user's own Areas (a full-access user
    // sees every Area) -- another Area's id in the URL is Forbidden.
    public class OvertimeDetailsModel : PageModel
    {
        private readonly LabourOvertimeRepository _overtimeRepo;
        private readonly AreaAccessService _areaAccess;

        public OvertimeDetailsModel(LabourOvertimeRepository overtimeRepo, AreaAccessService areaAccess)
        {
            _overtimeRepo = overtimeRepo;
            _areaAccess = areaAccess;
        }

        [BindProperty(SupportsGet = true)] public int Id { get; set; }

        public LabourOvertimeEntry Entry { get; set; } = null!;
        public List<LabourOvertimeChange> Changes { get; set; } = new();

        public async Task<IActionResult> OnGetAsync()
        {
            var entry = await _overtimeRepo.GetByIdAsync(Id);
            if (entry == null)
                return NotFound();
            if (!_areaAccess.CanAccessRequiredArea(User, entry.AreaId) || !DailyLabourRules.IsLabourArea(entry.AreaType))
                return Forbid();
            Entry = entry;
            Changes = await _overtimeRepo.GetChangesAsync(Id);
            return Page();
        }
    }
}
