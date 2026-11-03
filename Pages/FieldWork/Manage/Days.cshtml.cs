using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Models;
using PlantStockManager.Services;
using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.Pages.FieldWork.Manage
{
    // Field manager: every field employee's days with the start / end photos beside the entered km, status and review.
    // Filters are normalised on the server (FieldDayService.NormalizeFilter): an unknown value is dropped, the date range
    // is capped. Quick Approve posts to DayDetails (which re-checks everything); Flag / Reopen need a comment there.
    public class DaysModel : PageModel
    {
        private readonly FieldDayService _service;

        public DaysModel(FieldDayService service) => _service = service;

        [BindProperty(SupportsGet = true)] public DateTime? From { get; set; }
        [BindProperty(SupportsGet = true)] public DateTime? To { get; set; }
        [BindProperty(SupportsGet = true)] public int? EmployeeId { get; set; }
        [BindProperty(SupportsGet = true)] public string? Status { get; set; }
        [BindProperty(SupportsGet = true)] public string? Review { get; set; }

        public bool SchemaReady { get; private set; } = true;
        public FieldDayService.DayFilter Filter { get; private set; } = new(DateTime.Today, DateTime.Today, null, null, null);
        public List<FieldEmployeeOption> Employees { get; private set; } = new();
        public List<FieldDay> Days { get; private set; } = new();
        public string? Message { get; private set; }
        public string? Error { get; private set; }

        public async Task<IActionResult> OnGetAsync()
        {
            SchemaReady = await _service.SchemaReadyAsync();
            if (!SchemaReady)
                return Page();
            Message = TempData["FieldWorkMessage"] as string;
            Error = TempData["FieldWorkError"] as string;
            Employees = await _service.ListFieldEmployeesAsync();
            Filter = FieldDayService.NormalizeFilter(From, To, EmployeeId, Status, Review, Employees.Select(e => e.UserId).ToList(), AppClock.Today);
            Days = await _service.ListDaysAsync(Filter);
            return Page();
        }
    }
}
