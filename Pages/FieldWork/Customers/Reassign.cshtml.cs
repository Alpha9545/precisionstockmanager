using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.Pages.FieldWork.Customers
{
    // Manager: move all or selected active customers from one employee (deactivated employees first) to an active field
    // employee, in ONE transaction; their pending follow-ups move with them. A repeated submit moves nothing twice.
    public class ReassignModel : PageModel
    {
        private readonly FieldCustomerService _service;

        public ReassignModel(FieldCustomerService service) => _service = service;

        [BindProperty(SupportsGet = true)] public int? FromUserId { get; set; }
        [BindProperty] public int? ToUserId { get; set; }
        [BindProperty] public List<int> CustomerIds { get; set; } = new();
        [BindProperty] public string? Reason { get; set; }

        public bool SchemaReady { get; private set; } = true;
        public List<FieldCustomerRepository.AssigneeSummary> Sources { get; private set; } = new();
        public List<FieldEmployeeOption> Targets { get; private set; } = new();
        public List<FieldCustomerListRow> Customers { get; private set; } = new();
        public FieldCustomerRepository.AssigneeSummary? From { get; private set; }
        public List<string> Errors { get; } = new();
        public string? Message { get; private set; }

        private async Task LoadAsync()
        {
            SchemaReady = await _service.SchemaReadyAsync();
            if (!SchemaReady)
                return;
            Sources = await _service.ListAssigneesAsync();
            Targets = await _service.ListAssignableEmployeesAsync();
            From = FromUserId.HasValue ? Sources.FirstOrDefault(s => s.UserId == FromUserId.Value) : null;
            if (From != null)
                Customers = await _service.ListAssignedToAsync(User, From.UserId);
        }

        public async Task<IActionResult> OnGetAsync()
        {
            Message = TempData["FieldWorkMessage"] as string;
            await LoadAsync();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (!await _service.SchemaReadyAsync())
                return NotFound();
            if (FromUserId == null)
                Errors.Add("Choose whose customers to move.");
            else
            {
                var result = await _service.ReassignAsync(User, CustomerIds, FromUserId, ToUserId ?? 0, Reason);
                if (result.Ok)
                {
                    TempData["FieldWorkMessage"] = $"{result.Id} customer(s) moved, with their pending follow-ups.";
                    return RedirectToPage(new { FromUserId });
                }
                Errors.AddRange(result.Errors);
            }
            await LoadAsync();
            return Page();
        }
    }
}
