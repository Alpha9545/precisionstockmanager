using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.Pages.FieldWork.Expenses
{
    // My Expenses (field employee): one month at a time, the month's total by status, Add expense. Only the signed-in
    // user's own expenses are read (FieldExpenseService.ListMineAsync). Opens without an Open day (after consent).
    public class IndexModel : PageModel
    {
        private readonly FieldExpenseService _service;

        public IndexModel(FieldExpenseService service) => _service = service;

        [BindProperty(SupportsGet = true)] public string? Month { get; set; }

        public bool SchemaReady { get; private set; } = true;
        public bool MayAdd { get; private set; }
        public FieldExpenseService.MonthSummary? Summary { get; private set; }
        public string? Message { get; private set; }

        public async Task<IActionResult> OnGetAsync()
        {
            SchemaReady = await _service.SchemaReadyAsync();
            if (!SchemaReady)
                return Page();
            Message = TempData["FieldWorkMessage"] as string;
            MayAdd = FieldExpenseService.MayAdd(User);
            Summary = await _service.ListMineAsync(User, Month);
            return Page();
        }
    }
}
