using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.BedSowing
{
    // 2026-11-07 Bed Sowing Alerts: open beds due for transplanting (bed Sowing Date + the variety's Bed Transplant Days),
    // as Overdue / Due Today / Due Soon, plus the open beds whose variety has no schedule. Read-only, computed on every
    // request (BedTransplantScheduleRepository + BedTransplantAlertRules), beds of the user's Areas only (SeedlingAreaScope,
    // as Bed Sowing's own list). Separate from Ready Alerts (Direct Seed Sowing tray batches), which are unchanged.
    public class AlertsModel : PageModel
    {
        private readonly BedTransplantScheduleRepository _schedule;
        private readonly BedSowingRepository _beds;
        private readonly SeedlingAreaScope _scope;

        public AlertsModel(BedTransplantScheduleRepository schedule, BedSowingRepository beds, SeedlingAreaScope scope)
        {
            _schedule = schedule;
            _beds = beds;
            _scope = scope;
        }

        // ?WindowDays=N as on Ready Alerts (default 3, capped at 90).
        [BindProperty(SupportsGet = true)]
        public int WindowDays { get; set; } = SeedSowingRepository.DefaultReadySoonWindowDays;

        public bool SchemaReady { get; private set; }
        public DateTime Today { get; private set; }
        public BedTransplantAlertGroups Alerts { get; private set; } = new();
        // Beds whose Area this user may transplant in (Bed Transplanting is offered only for those; null = none known).
        public IReadOnlyCollection<int> TransplantAreaIds { get; private set; } = Array.Empty<int>();

        public async Task OnGetAsync()
        {
            WindowDays = BedTransplantAlertRules.NormalizeWindow(WindowDays);
            Today = AppClock.Today;
            SchemaReady = await _schedule.SchemaReadyAsync();
            if (!SchemaReady)
                return;

            var candidates = await _schedule.GetAlertCandidatesAsync(_scope.GetAccessibleAreaIdsOrNull(User), Today.AddDays(WindowDays));
            Alerts = BedTransplantAlertRules.Group(candidates, Today, WindowDays);
            // As Bed Sowing Details: only the Bed Sowing Transplanter of the bed's Area transplants (never by full access).
            TransplantAreaIds = await _beds.TransplantAreaIdsAsync(User.GetUserId(), isFullAccess: false) ?? Array.Empty<int>();
        }

        public bool CanTransplant(BedTransplantAlert a) => TransplantAreaIds.Contains(a.AreaId);

        public int DaysFromToday(BedTransplantAlert a) => a.DueDate.HasValue ? (a.DueDate.Value.Date - Today).Days : 0;
    }
}
