using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Models;
using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.Pages.FieldWork.Expenses
{
    // One expense: the record, the bill photo (served ONLY by OnGetPhotoAsync after the same per-record check) and its
    // history. The owner cancels it while pending (reason required); a field manager approves or rejects it (comment required
    // to reject), never their own unless an administrator. Anyone else gets 404. Every action is re-checked in the service
    // on the locked row.
    public class DetailsModel : PageModel
    {
        private readonly FieldExpenseService _service;

        public DetailsModel(FieldExpenseService service) => _service = service;

        [BindProperty(SupportsGet = true)] public int Id { get; set; }
        [BindProperty] public string? Reason { get; set; }
        [BindProperty] public string? Comment { get; set; }

        public bool SchemaReady { get; private set; } = true;
        public FieldExpense Expense { get; private set; } = new();
        public List<FieldExpenseEvent> Events { get; private set; } = new();
        public bool CanCancel { get; private set; }
        public bool CanDecide { get; private set; }
        public bool IsManager { get; private set; }
        public bool IsOwn { get; private set; }
        public string? Message { get; private set; }
        public List<string> Errors { get; } = new();

        private async Task<IActionResult?> LoadAsync()
        {
            SchemaReady = await _service.SchemaReadyAsync();
            if (!SchemaReady)
                return Page();
            var e = await _service.GetVisibleAsync(User, Id);
            if (e == null)
                return NotFound();
            Expense = e;
            Events = await _service.ListEventsAsync(Id);
            CanCancel = FieldExpenseService.MayCancel(User, e);
            CanDecide = FieldExpenseService.MayDecide(User, e);
            IsManager = FieldExpenseService.MayManage(User);
            IsOwn = e.UserId == User.GetUserId();
            return null;
        }

        public async Task<IActionResult> OnGetAsync()
        {
            Message = TempData["FieldWorkMessage"] as string;
            return await LoadAsync() ?? Page();
        }

        public async Task<IActionResult> OnGetPhotoAsync()
        {
            if (!await _service.SchemaReadyAsync())
                return NotFound();
            var photo = await _service.GetBillPhotoAsync(User, Id);
            if (photo == null)
                return NotFound();
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            Response.Headers["Cache-Control"] = "private, max-age=3600";
            return PhysicalFile(photo.Value.Path, photo.Value.ContentType);
        }

        public Task<IActionResult> OnPostCancelAsync() => ActAsync(() => _service.CancelAsync(User, Id, Reason), "Expense cancelled.");
        public Task<IActionResult> OnPostApproveAsync() => ActAsync(() => _service.DecideAsync(User, Id, true, Comment), "Expense approved.");
        public Task<IActionResult> OnPostRejectAsync() => ActAsync(() => _service.DecideAsync(User, Id, false, Comment), "Expense rejected.");

        private async Task<IActionResult> ActAsync(Func<Task<FieldExpenseService.Outcome>> act, string done)
        {
            var denied = await LoadAsync();
            if (denied != null)
                return denied;
            var result = await act();
            if (result.Ok)
            {
                TempData["FieldWorkMessage"] = done;
                return RedirectToPage(new { id = Id });
            }
            Errors.AddRange(result.Errors);
            await LoadAsync();
            return Page();
        }
    }
}
