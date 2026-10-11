using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Maintenance
{
    // Assigned Tickets (Maintenance.Work): the worker dashboard.
    //   Available -- Open tickets of the worker's own trade(s), from any Area, ready to accept
    //   Mine      -- tickets assigned to the worker and not yet closed
    //   Done      -- the worker's closed tickets (latest 50)
    // A full-access administrator is not a worker: they see the Open / unassigned queue of
    // every trade instead, to assign it.
    public class AssignedModel : PageModel
    {
        private readonly MaintenanceRepository _repo;
        private readonly MaintenanceAuthorization _auth;

        public AssignedModel(MaintenanceRepository repo, MaintenanceAuthorization auth)
        {
            _repo = repo;
            _auth = auth;
        }

        public IReadOnlyCollection<string> Trades { get; set; } = Array.Empty<string>();
        public List<MaintenanceTicket> Available { get; set; } = new();
        public List<MaintenanceTicket> Mine { get; set; } = new();
        public List<MaintenanceTicket> Done { get; set; } = new();
        public bool IsWorker => Trades.Count > 0;

        public async Task OnGetAsync()
        {
            Trades = _auth.WorkerRoles(User);
            var categories = await _repo.GetCategoriesAsync();
            if (IsWorker)
            {
                var mineCategories = categories.Where(c => Trades.Contains(c.WorkerRoleName, StringComparer.OrdinalIgnoreCase)).Select(c => c.Id).ToList();
                Available = await _repo.SearchAsync(new MaintenanceTicketFilter { Status = MaintenanceRules.Open, CategoryIds = mineCategories }, null);
                var me = User.GetUserId();
                var assigned = await _repo.SearchAsync(new MaintenanceTicketFilter { WorkerId = me }, null);
                Mine = assigned.Where(t => MaintenanceRules.IsActive(t.Status)).ToList();
                Done = assigned.Where(t => t.Status == MaintenanceRules.Closed).Take(50).ToList();
            }
            else if (_auth.IsManager(User))
            {
                Available = await _repo.SearchAsync(new MaintenanceTicketFilter { Status = MaintenanceRules.Open }, null);
                Mine = (await _repo.SearchAsync(new MaintenanceTicketFilter { ActiveOnly = true }, null)).Where(t => t.Status != MaintenanceRules.Open).ToList();
            }
        }
    }
}
