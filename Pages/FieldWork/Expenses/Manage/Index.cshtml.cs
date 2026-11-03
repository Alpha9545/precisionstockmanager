using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Services;
using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.Pages.FieldWork.Expenses.Manage
{
    // Field manager: every employee's expenses with filters (employee, date range -- default this month --, category,
    // status), totals per employee (approved and pending separately), a quick Approve per row (one at a time; Reject needs
    // a comment on Details) and an Excel export of EXACTLY the filtered list. Filters are normalised on the server
    // (FieldExpenseRules.NormalizeFilter): unknown values are dropped, the range is capped.
    public class IndexModel : PageModel
    {
        private readonly FieldExpenseService _service;

        public IndexModel(FieldExpenseService service) => _service = service;

        [BindProperty(SupportsGet = true)] public DateTime? From { get; set; }
        [BindProperty(SupportsGet = true)] public DateTime? To { get; set; }
        [BindProperty(SupportsGet = true)] public int? EmployeeId { get; set; }
        [BindProperty(SupportsGet = true)] public string? Category { get; set; }
        [BindProperty(SupportsGet = true)] public string? Status { get; set; }
        [BindProperty] public int ExpenseId { get; set; }

        public bool SchemaReady { get; private set; } = true;
        public List<PlantStockManager.Models.FieldEmployeeOption> Employees { get; private set; } = new();
        public FieldExpenseService.ManagerView? View { get; private set; }
        public string? Message { get; private set; }
        public string? Error { get; private set; }

        // the filter as route values (export button, quick-approve redirect)
        public Dictionary<string, string> RouteFilter => View == null ? new() : new Dictionary<string, string>
        {
            ["From"] = View.Filter.From.ToString("yyyy-MM-dd"),
            ["To"] = View.Filter.To.ToString("yyyy-MM-dd"),
            ["EmployeeId"] = View.Filter.UserId?.ToString() ?? "",
            ["Category"] = View.Filter.Category ?? "",
            ["Status"] = View.Filter.Status ?? "",
        };

        private async Task<bool> LoadAsync()
        {
            SchemaReady = await _service.SchemaReadyAsync();
            if (!SchemaReady)
                return false;
            Employees = await _service.ListEmployeesAsync();
            var filter = FieldExpenseRules.NormalizeFilter(From, To, EmployeeId, Category, Status, Employees.Select(e => e.UserId).ToList(), AppClock.Today);
            View = await _service.ListForManagerAsync(filter);
            return true;
        }

        public async Task<IActionResult> OnGetAsync()
        {
            Message = TempData["FieldWorkMessage"] as string;
            Error = TempData["FieldWorkError"] as string;
            await LoadAsync();
            return Page();
        }

        public async Task<IActionResult> OnGetExportExcelAsync()
        {
            if (!FieldExpenseService.MayManage(User) || !await LoadAsync())
                return NotFound();
            var f = View!.Filter;
            var bytes = ExportHelper.BuildExcel("Expenses", FieldExpenseService.ExportHeaders, FieldExpenseService.ExportRows(View.Expenses));
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"FieldExpenses_{f.From:yyyyMMdd}_{f.To:yyyyMMdd}.xlsx");
        }

        public async Task<IActionResult> OnPostApproveAsync()
        {
            var result = await _service.DecideAsync(User, ExpenseId, true, null);
            if (result.Ok)
                TempData["FieldWorkMessage"] = "Expense approved.";
            else
                TempData["FieldWorkError"] = string.Join(" ", result.Errors);
            return RedirectToPage(new { From = From?.ToString("yyyy-MM-dd"), To = To?.ToString("yyyy-MM-dd"), EmployeeId, Category, Status });
        }
    }
}
