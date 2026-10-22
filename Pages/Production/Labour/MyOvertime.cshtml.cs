using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.Labour
{
    // My Overtime Entries (Labour.Enter, 2026-10-20): the overtime entries the SIGNED-IN user recorded
    // (dbo.LabourOvertime.CreatedById = the user id of the login, never a value from the request), in the
    // Areas they are still assigned to, newest first -- so a recorder can open and correct their own entries
    // now that Overtime History is a System Administrator report. Only a date range can be chosen: no
    // Area / Polyhouse filters, no totals, nobody else's entries.
    public class MyOvertimeModel : PageModel
    {
        public const int MaxListRows = 500;
        public const int DefaultDays = 31;

        private readonly LabourOvertimeRepository _overtimeRepo;
        private readonly AreaAccessService _areaAccess;

        public MyOvertimeModel(LabourOvertimeRepository overtimeRepo, AreaAccessService areaAccess)
        {
            _overtimeRepo = overtimeRepo;
            _areaAccess = areaAccess;
        }

        [BindProperty(SupportsGet = true)] public DateTime? FromDate { get; set; }
        [BindProperty(SupportsGet = true)] public DateTime? ToDate { get; set; }

        public List<LabourOvertimeEntry> Entries { get; set; } = new();
        public int MatchingEntries { get; set; }

        public async Task OnGetAsync()
        {
            if (!FromDate.HasValue && !ToDate.HasValue)
            {
                ToDate = AppClock.Today;
                FromDate = AppClock.Today.AddDays(-(DefaultDays - 1));
            }
            if (FromDate > ToDate)
                (FromDate, ToDate) = (ToDate, FromDate);

            // no user id on the login = no entries (never "everyone's")
            var userId = User.GetUserId();
            if (userId == null)
                return;

            var scope = _areaAccess.HasFullAreaAccess(User) ? null : _areaAccess.GetAccessibleAreaIds(User);
            var mine = await _overtimeRepo.SearchAsync(
                new LabourOvertimeFilter { FromDate = FromDate, ToDate = ToDate, CreatedById = userId.Value }, scope);
            MatchingEntries = mine.Count;
            Entries = mine.Take(MaxListRows).ToList();
        }
    }
}
