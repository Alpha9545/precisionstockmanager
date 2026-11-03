using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Models;
using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.Pages.FieldWork.FollowUps
{
    // Employee: their own pending follow-ups (Overdue / Today / Upcoming). Manager: the team's overdue follow-ups (or all
    // pending), filter by employee -- the employee options are the users who have customers, so a value outside them is
    // dropped.
    public class IndexModel : PageModel
    {
        private readonly FieldCustomerService _service;

        public IndexModel(FieldCustomerService service) => _service = service;

        [BindProperty(SupportsGet = true)] public int? EmployeeId { get; set; }
        [BindProperty(SupportsGet = true)] public bool AllPending { get; set; }

        public bool SchemaReady { get; private set; } = true;
        public bool CanManage { get; private set; }
        public List<FieldFollowUp> Items { get; private set; } = new();
        public List<FieldEmployeeOption> Employees { get; private set; } = new();

        public async Task<IActionResult> OnGetAsync()
        {
            SchemaReady = await _service.SchemaReadyAsync();
            if (!SchemaReady)
                return Page();
            CanManage = FieldCustomerService.MayManage(User);
            if (CanManage)
            {
                Employees = (await _service.ListAssigneesAsync())
                    .Select(a => new FieldEmployeeOption(a.UserId, a.IsActive ? a.Name : a.Name + " (deactivated)")).ToList();
                if (EmployeeId.HasValue && Employees.All(e => e.UserId != EmployeeId.Value))
                    EmployeeId = null;
            }
            else
            {
                EmployeeId = null;
                AllPending = true;
            }
            Items = await _service.ListFollowUpsAsync(User, EmployeeId, overdueOnly: CanManage && !AllPending);
            return Page();
        }
    }
}
