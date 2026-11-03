using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Models;
using PlantStockManager.Services;
using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.Pages.FieldWork
{
    // One field day: start / end photos beside the entered km, GPS, flags, audit trail. Visible to the day's employee and
    // to field managers / administrators only; anyone else gets 404 (also for the photos), so changing the id in the URL
    // reveals nothing. Approve / Flag / Reopen: FieldWork.Manage (page Write rule + FieldDayService re-check, on the
    // locked row).
    public class DayDetailsModel : PageModel
    {
        private readonly FieldDayService _service;

        public DayDetailsModel(FieldDayService service) => _service = service;

        [BindProperty] public string? Comment { get; set; }

        public bool SchemaReady { get; private set; } = true;
        public FieldDay Day { get; private set; } = new();
        public List<FieldDayEvent> Events { get; private set; } = new();
        public bool CanManage { get; private set; }
        public bool IsOwnDay { get; private set; }
        public List<string> Errors { get; } = new();
        public string? Message { get; private set; }

        public bool CanReopen => CanManage && (!IsOwnDay || User.IsFullAccess()) && Day.Status != FieldDayRules.StatusOpen && Day.WorkDate.Date == AppClock.Today;
        public bool CanReview => CanManage && (!IsOwnDay || User.IsFullAccess());

        private async Task<IActionResult?> LoadAsync(int id)
        {
            SchemaReady = await _service.SchemaReadyAsync();
            if (!SchemaReady)
                return Page();
            var day = await _service.GetVisibleDayAsync(User, id);
            if (day == null)
                return NotFound();
            Day = day;
            Events = await _service.GetEventsAsync(id);
            CanManage = FieldDayService.MayManage(User);
            IsOwnDay = day.UserId == User.GetUserId();
            return null;
        }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            Message = TempData["FieldWorkMessage"] as string;
            return await LoadAsync(id) ?? Page();
        }

        public async Task<IActionResult> OnGetPhotoAsync(int id, int photoId)
        {
            if (!await _service.SchemaReadyAsync())
                return NotFound();
            var photo = await _service.GetDayPhotoAsync(User, id, photoId);
            if (photo == null)
                return NotFound();
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            Response.Headers["Cache-Control"] = "private, max-age=3600";
            return PhysicalFile(photo.Value.Path, photo.Value.ContentType);
        }

        public Task<IActionResult> OnPostApproveAsync(int id, bool back) => ReviewAsync(id, FieldDayRules.ReviewAction.Approve, back, "Day approved.");
        public Task<IActionResult> OnPostFlagAsync(int id, bool back) => ReviewAsync(id, FieldDayRules.ReviewAction.Flag, back, "Day flagged.");
        public Task<IActionResult> OnPostReopenAsync(int id) => ReviewAsync(id, FieldDayRules.ReviewAction.Reopen, false,
            "Day reopened. The employee can now End Day again.");

        private async Task<IActionResult> ReviewAsync(int id, FieldDayRules.ReviewAction action, bool back, string done)
        {
            if (!await _service.SchemaReadyAsync())
                return NotFound();
            // only a day the reviewer may see exists for them
            if (await _service.GetVisibleDayAsync(User, id) == null)
                return NotFound();
            var result = await _service.ReviewAsync(User, id, action, Comment);
            if (result.Ok)
            {
                TempData["FieldWorkMessage"] = done;
                return back ? RedirectToPage("/FieldWork/Manage/Days") : RedirectToPage(new { id });
            }
            if (back)
            {
                TempData["FieldWorkError"] = string.Join(" ", result.Errors);
                return RedirectToPage("/FieldWork/Manage/Days");
            }
            Errors.AddRange(result.Errors);
            return await LoadAsync(id) ?? Page();
        }
    }
}
