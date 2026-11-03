using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Services;
using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.Pages.FieldWork.Expenses
{
    // Add Expense (field employee): category, amount, date (today, or up to FieldWork:ExpenseBackdateDays back), optional
    // note (required for Other) and ONE live bill photo (no gallery / file picker; wwwroot/js/fieldwork-expense.js). The
    // "photograph only the bill" reminder appears when the camera opens and its version is stored on the photo. One
    // submission token per form: a repeated submit opens the expense it already saved. Opens without an Open day.
    [RequestSizeLimit(4 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 4 * 1024 * 1024)]
    public class CreateModel : PageModel
    {
        private readonly FieldExpenseService _service;

        public CreateModel(FieldExpenseService service) => _service = service;

        [BindProperty] public string? Category { get; set; }
        [BindProperty] public string? Amount { get; set; }
        [BindProperty] public DateTime? ExpenseDate { get; set; }
        [BindProperty] public string? Note { get; set; }
        [BindProperty] public IFormFile? Photo { get; set; }
        [BindProperty] public int? ReminderVersion { get; set; }
        [BindProperty] public string? SubmitToken { get; set; }

        public bool SchemaReady { get; private set; } = true;
        public bool MayAdd { get; private set; }
        public List<string> Errors { get; } = new();

        public DateTime Today => AppClock.Today;
        public DateTime Earliest => FieldExpenseRules.EarliestDate(AppClock.Today, _service.Options.ExpenseBackdateDays);
        public int CurrentReminderVersion => _service.Options.ExpenseBillReminderVersion;
        public long MaxPhotoBytes => _service.MaxPhotoBytes;

        private async Task<bool> LoadAsync()
        {
            SchemaReady = await _service.SchemaReadyAsync();
            MayAdd = FieldExpenseService.MayAdd(User);
            return SchemaReady && MayAdd;
        }

        public async Task<IActionResult> OnGetAsync()
        {
            if (await LoadAsync())
            {
                ExpenseDate = Today;
                SubmitToken = Guid.NewGuid().ToString("D");
            }
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (!await LoadAsync())
                return Page();
            byte[]? bytes = null;
            if (Photo is { Length: > 0 })
            {
                if (Photo.Length > MaxPhotoBytes)
                    Errors.Add($"The bill photo is too large (at most {MaxPhotoBytes / 1024 / 1024.0:0.#} MB).");
                else
                {
                    using var ms = new MemoryStream((int)Photo.Length);
                    await Photo.CopyToAsync(ms);
                    bytes = ms.ToArray();
                }
            }
            if (Errors.Count == 0)
            {
                var result = await _service.CreateAsync(User, new FieldExpenseService.CreateRequest(Category, Amount, ExpenseDate, Note, bytes,
                    ReminderVersion, SubmitToken));
                if (result.Ok)
                {
                    TempData["FieldWorkMessage"] = result.Replay ? "This expense was already saved." : "Expense added. It is waiting for your manager's approval.";
                    return RedirectToPage("/FieldWork/Expenses/Details", new { id = result.Id });
                }
                Errors.AddRange(result.Errors);
            }
            return Page();
        }
    }
}
