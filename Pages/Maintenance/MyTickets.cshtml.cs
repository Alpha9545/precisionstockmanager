using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Maintenance
{
    // My Tickets (Maintenance.View): the tickets of the user's own Areas (a manager / full-access
    // user sees every Area), active ones by default; optionally only the ones they raised.
    public class MyTicketsModel : PageModel
    {
        private readonly MaintenanceRepository _repo;
        private readonly AreaAccessService _areaAccess;
        private readonly MaintenanceAuthorization _auth;

        public MyTicketsModel(MaintenanceRepository repo, AreaAccessService areaAccess, MaintenanceAuthorization auth)
        {
            _repo = repo;
            _areaAccess = areaAccess;
            _auth = auth;
        }

        [BindProperty(SupportsGet = true)] public string? Status { get; set; }
        [BindProperty(SupportsGet = true)] public int? CategoryId { get; set; }
        [BindProperty(SupportsGet = true)] public string? Priority { get; set; }
        [BindProperty(SupportsGet = true)] public bool RaisedByMe { get; set; }
        [BindProperty(SupportsGet = true)] public bool All { get; set; }

        public List<MaintenanceCategory> Categories { get; set; } = new();
        public List<MaintenanceTicket> Tickets { get; set; } = new();

        public async Task OnGetAsync()
        {
            Categories = await _repo.GetCategoriesAsync(activeOnly: false);
            if (!MaintenanceRules.Statuses.Contains(Status)) Status = null;
            if (!MaintenanceRules.Priorities.Contains(Priority)) Priority = null;
            var scope = _auth.IsManager(User) || _areaAccess.HasFullAreaAccess(User) ? null : _areaAccess.GetAccessibleAreaIds(User);
            Tickets = await _repo.SearchAsync(new MaintenanceTicketFilter
            {
                Status = Status,
                CategoryId = CategoryId,
                Priority = Priority,
                RequestedById = RaisedByMe ? User.GetUserId() : null,
                ActiveOnly = !All && Status == null,
            }, scope);
        }
    }
}
