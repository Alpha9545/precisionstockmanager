using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Models;
using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.Pages.FieldWork.Customers
{
    // Customer list: an employee sees customers assigned to them or created by them; a manager sees all. Filters are
    // normalised on the server (FieldCustomerService.NormalizeFilter): options come from rows the user can see, and any
    // other value is dropped -- a filter never widens the scope.
    public class IndexModel : PageModel
    {
        private readonly FieldCustomerService _service;

        public IndexModel(FieldCustomerService service) => _service = service;

        [BindProperty(SupportsGet = true)] public string? Q { get; set; }
        [BindProperty(SupportsGet = true)] public string? Status { get; set; }
        [BindProperty(SupportsGet = true)] public string? Type { get; set; }
        [BindProperty(SupportsGet = true)] public string? District { get; set; }
        [BindProperty(SupportsGet = true)] public int? EmployeeId { get; set; }
        [BindProperty(SupportsGet = true)] public int? NoVisitDays { get; set; }
        [BindProperty(SupportsGet = true)] public bool NoFollowUp { get; set; }
        [BindProperty(SupportsGet = true)] public bool NeedsReassign { get; set; }
        [BindProperty(SupportsGet = true)] public string? Archived { get; set; }
        [BindProperty(SupportsGet = true, Name = "p")] public int? PageNumber { get; set; }

        public bool SchemaReady { get; private set; } = true;
        public bool CanManage { get; private set; }
        public FieldCustomerService.ListFilter Filter { get; private set; } = null!;
        public List<FieldCustomerListRow> Rows { get; private set; } = new();
        public int Total { get; private set; }
        public int PageSize => Math.Clamp(_service.Options.CustomerPageSize, 5, 200);
        public int PageCount => Math.Max(1, (Total + PageSize - 1) / PageSize);
        public List<string> Districts { get; private set; } = new();
        public List<FieldEmployeeOption> Employees { get; private set; } = new();
        public int NeedingReassign { get; private set; }

        public async Task<IActionResult> OnGetAsync()
        {
            SchemaReady = await _service.SchemaReadyAsync();
            if (!SchemaReady)
                return Page();
            CanManage = FieldCustomerService.MayManage(User);
            Districts = await _service.VisibleDistrictsAsync(User);
            if (CanManage)
            {
                // employee filter: everyone who has customers (incl. deactivated employees) + active field employees
                var assignees = await _service.ListAssigneesAsync();
                Employees = assignees.Select(a => new FieldEmployeeOption(a.UserId, a.IsActive ? a.Name : a.Name + " (deactivated)"))
                    .Concat((await _service.ListAssignableEmployeesAsync()).Where(e => assignees.All(a => a.UserId != e.UserId)))
                    .OrderBy(e => e.Name).ToList();
                NeedingReassign = await _service.CountNeedingReassignAsync();
            }
            Filter = FieldCustomerService.NormalizeFilter(Q, Status, Type, District, EmployeeId, NoVisitDays, NoFollowUp, NeedsReassign, Archived,
                PageNumber, CanManage, Districts, Employees.Select(e => e.UserId).ToList());
            (Rows, Total) = await _service.ListAsync(User, Filter);
            return Page();
        }

        public Dictionary<string, string?> RouteFor(int page) => new()
        {
            ["Q"] = Filter.Search, ["Status"] = Filter.Status, ["Type"] = Filter.Type, ["District"] = Filter.District,
            ["EmployeeId"] = Filter.EmployeeId?.ToString(), ["NoVisitDays"] = Filter.NoVisitDays?.ToString(),
            ["NoFollowUp"] = Filter.NoFollowUp ? "true" : null, ["NeedsReassign"] = Filter.NeedsReassign ? "true" : null,
            ["Archived"] = Filter.Archived == "Active" ? null : Filter.Archived, ["p"] = page.ToString(),
        };
    }
}
