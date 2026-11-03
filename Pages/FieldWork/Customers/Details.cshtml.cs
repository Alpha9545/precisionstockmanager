using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Models;
using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.Pages.FieldWork.Customers
{
    // Customer profile, timeline (visits with items / photos / corrections, calls / notes), follow-ups and change history.
    // A customer the user may not see is 404 -- also for its photos. Manager actions (status, reassign, archive / restore)
    // are re-checked in FieldCustomerService; the employee's follow-up actions likewise.
    public class DetailsModel : PageModel
    {
        private readonly FieldCustomerService _service;

        public DetailsModel(FieldCustomerService service) => _service = service;

        [BindProperty(SupportsGet = true)] public int Id { get; set; }
        [BindProperty] public string? Status { get; set; }
        [BindProperty] public string? Comment { get; set; }
        [BindProperty] public string? Reason { get; set; }
        [BindProperty] public int? ToUserId { get; set; }
        [BindProperty] public DateTime? FollowUpDate { get; set; }
        [BindProperty] public string? FollowUpNote { get; set; }
        [BindProperty] public int? FollowUpId { get; set; }

        public bool SchemaReady { get; private set; } = true;
        public FieldCustomer Customer { get; private set; } = new();
        public FieldCustomerTimeline Timeline { get; private set; } = new();
        public FieldFollowUp? Pending { get; private set; }
        public bool CanManage { get; private set; }
        public bool ShowPhone { get; private set; }
        public bool CanEdit { get; private set; }
        public bool CanWork { get; private set; }
        public List<FieldEmployeeOption> Employees { get; private set; } = new();
        public List<string> Errors { get; } = new();
        public string? Message { get; private set; }
        public bool AskNextDate { get; private set; }

        public bool MayCorrect(FieldVisit v) => FieldCustomerRules.MayCorrectVisit(v.UserId, User.GetUserId(), CanManage);

        private async Task<IActionResult?> LoadAsync()
        {
            SchemaReady = await _service.SchemaReadyAsync();
            if (!SchemaReady)
                return Page();
            var c = await _service.GetVisibleAsync(User, Id);
            if (c == null)
                return NotFound();
            Customer = c;
            CanManage = FieldCustomerService.MayManage(User);
            ShowPhone = FieldCustomerService.MaySeePhone(User, c);
            CanEdit = FieldCustomerService.MayEdit(User, c);
            CanWork = FieldCustomerService.MayWork(User, c);
            Timeline = await _service.GetTimelineAsync(Id);
            Pending = Timeline.FollowUps.FirstOrDefault(f => f.Status == FieldCustomerRules.FollowUpPending);
            if (CanManage)
                Employees = await _service.ListAssignableEmployeesAsync();
            return null;
        }

        public async Task<IActionResult> OnGetAsync()
        {
            Message = TempData["FieldWorkMessage"] as string;
            AskNextDate = TempData["FieldWorkAskNext"] as string == "1";
            var denied = await LoadAsync();
            if (denied != null)
                return denied;
            AskNextDate &= Pending == null && CanWork;
            return Page();
        }

        public async Task<IActionResult> OnGetPhotoAsync(int photoId)
        {
            if (!await _service.SchemaReadyAsync())
                return NotFound();
            var photo = await _service.GetPhotoAsync(User, Id, photoId);
            if (photo == null)
                return NotFound();
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            Response.Headers["Cache-Control"] = "private, max-age=3600";
            return PhysicalFile(photo.Value.Path, photo.Value.ContentType);
        }

        public Task<IActionResult> OnPostSetStatusAsync() => ActAsync(() => _service.SetStatusAsync(User, Id, Status, Comment), "Lead status changed.");
        public Task<IActionResult> OnPostArchiveAsync() => ActAsync(() => _service.ArchiveAsync(User, Id, Reason), "Customer archived.");
        public Task<IActionResult> OnPostRestoreAsync() => ActAsync(() => _service.RestoreAsync(User, Id), "Customer restored.");
        public Task<IActionResult> OnPostReassignAsync()
            => ActAsync(() => _service.ReassignAsync(User, new[] { Id }, null, ToUserId ?? 0, Comment), "Customer reassigned (with the pending follow-up).");
        public Task<IActionResult> OnPostSetFollowUpAsync()
            => ActAsync(() => _service.SetFollowUpAsync(User, Id, FollowUpDate, FollowUpNote), "Follow-up set.");
        public Task<IActionResult> OnPostCancelFollowUpAsync()
            => ActAsync(() => _service.CancelFollowUpAsync(User, FollowUpId ?? 0, Reason), "Follow-up cancelled.");

        private async Task<IActionResult> ActAsync(Func<Task<FieldCustomerService.Outcome>> act, string done)
        {
            if (!await _service.SchemaReadyAsync() || await _service.GetVisibleAsync(User, Id) == null)
                return NotFound();
            var result = await act();
            if (result.Ok)
            {
                TempData["FieldWorkMessage"] = done;
                return RedirectToPage(new { id = Id });
            }
            Errors.AddRange(result.Errors);
            return await LoadAsync() ?? Page();
        }
    }
}
