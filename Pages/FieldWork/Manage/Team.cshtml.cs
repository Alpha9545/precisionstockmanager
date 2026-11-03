using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Services;
using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.Pages.FieldWork.Manage
{
    // Field manager: every active field employee for one date (today by default) -- status, visits, GPS route km vs odometer,
    // flags; "Not started" first. Links to the route and the day details.
    public class TeamModel : PageModel
    {
        private readonly FieldRouteService _routes;

        public TeamModel(FieldRouteService routes) => _routes = routes;

        [BindProperty(SupportsGet = true)] public DateTime? Date { get; set; }

        public bool SchemaReady { get; private set; } = true;
        public DateTime ShownDate { get; private set; }
        public List<FieldRouteService.TeamRow> Rows { get; private set; } = new();

        public async Task<IActionResult> OnGetAsync()
        {
            SchemaReady = await _routes.SchemaReadyAsync();
            if (!SchemaReady)
                return Page();
            ShownDate = (Date ?? AppClock.Today).Date;
            if (ShownDate > AppClock.Today)
                ShownDate = AppClock.Today;
            Rows = await _routes.GetTeamAsync(User, ShownDate);
            return Page();
        }
    }
}
