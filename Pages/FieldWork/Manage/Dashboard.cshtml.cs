using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.Pages.FieldWork.Manage
{
    // Field manager dashboard for one date (default today, India; never a future date): who started / did not start / ended,
    // visits (total and per employee), new customers, follow-ups due and overdue, the expense approval queue and flagged
    // days -- each linking to the page that already handles it. Read-only.
    public class DashboardModel : PageModel
    {
        private readonly FieldReportService _service;

        public DashboardModel(FieldReportService service) => _service = service;

        [BindProperty(SupportsGet = true)] public DateTime? Date { get; set; }

        public bool SchemaReady { get; private set; } = true;
        public FieldReportService.Dashboard? View { get; private set; }

        public async Task<IActionResult> OnGetAsync()
        {
            SchemaReady = await _service.SchemaReadyAsync();
            if (SchemaReady)
                View = await _service.DashboardAsync(User, Date);
            return Page();
        }
    }
}
