using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.Pages.FieldWork
{
    // My Summary (field employee): the month's own numbers -- days worked, visits, new customers, km, expenses -- and the
    // days behind them. Always the signed-in user only (FieldReportService.MySummaryAsync has no user parameter). Opens
    // without an Open day (after consent).
    public class MySummaryModel : PageModel
    {
        private readonly FieldReportService _service;

        public MySummaryModel(FieldReportService service) => _service = service;

        [BindProperty(SupportsGet = true)] public string? Month { get; set; }

        public bool SchemaReady { get; private set; } = true;
        public FieldReportService.MySummary? View { get; private set; }

        public async Task<IActionResult> OnGetAsync()
        {
            SchemaReady = await _service.SchemaReadyAsync();
            if (SchemaReady)
                View = await _service.MySummaryAsync(User, Month);
            return Page();
        }
    }
}
