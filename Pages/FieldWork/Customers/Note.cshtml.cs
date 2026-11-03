using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Models;
using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.Pages.FieldWork.Customers
{
    // A Call / Note without a visit (no GPS), or -- with visitId -- a Correction to a saved visit (visits themselves are
    // never edited by the employee). A call / note can update the lead status, complete the pending follow-up ("This
    // completes the follow-up", ticked by default) and set the next one. Append-only (TR_FieldCustomerNotes_Guard).
    public class NoteModel : PageModel
    {
        private readonly FieldCustomerService _service;

        public NoteModel(FieldCustomerService service) => _service = service;

        [BindProperty(SupportsGet = true)] public int CustomerId { get; set; }
        [BindProperty(SupportsGet = true)] public int? VisitId { get; set; }
        [BindProperty] public string? NoteType { get; set; } = FieldCustomerRules.NoteCall;
        [BindProperty] public string? Text { get; set; }
        [BindProperty] public string? NewLeadStatus { get; set; }
        [BindProperty] public bool CompletesFollowUp { get; set; } = true;
        [BindProperty] public DateTime? NextFollowUpDate { get; set; }
        [BindProperty] public string? NextFollowUpNote { get; set; }
        [BindProperty] public string? SubmitToken { get; set; }

        public bool SchemaReady { get; private set; } = true;
        public FieldCustomer Customer { get; private set; } = new();
        public FieldVisit? Visit { get; private set; }
        public FieldFollowUp? Pending { get; private set; }
        public List<string> Errors { get; } = new();
        public bool IsCorrection => VisitId.HasValue;

        private async Task<IActionResult?> LoadAsync()
        {
            SchemaReady = await _service.SchemaReadyAsync();
            if (!SchemaReady)
                return Page();
            var c = await _service.GetVisibleAsync(User, CustomerId);
            if (c == null)
                return NotFound();
            Customer = c;
            var manage = FieldCustomerService.MayManage(User);
            var timeline = await _service.GetTimelineAsync(CustomerId);
            if (VisitId.HasValue)
            {
                Visit = timeline.Visits.FirstOrDefault(v => v.Id == VisitId.Value);
                if (Visit == null || !FieldCustomerRules.MayCorrectVisit(Visit.UserId, User.GetUserId(), manage))
                    return NotFound();
            }
            else if (!FieldCustomerService.MayWork(User, c))
                return NotFound();
            Pending = timeline.FollowUps.FirstOrDefault(f => f.Status == FieldCustomerRules.FollowUpPending);
            return null;
        }

        public async Task<IActionResult> OnGetAsync()
        {
            var denied = await LoadAsync();
            if (denied != null)
                return denied;
            SubmitToken = Guid.NewGuid().ToString("D");
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            var denied = await LoadAsync();
            if (denied != null)
                return denied;
            var result = await _service.AddNoteAsync(User, new FieldCustomerService.NoteRequest(CustomerId, NoteType, Text, VisitId, NewLeadStatus,
                CompletesFollowUp, NextFollowUpDate, NextFollowUpNote, SubmitToken));
            if (result.Ok)
            {
                TempData["FieldWorkMessage"] = IsCorrection ? "Correction added." : $"{NoteType} saved.";
                if (result.AskNextDate)
                    TempData["FieldWorkAskNext"] = "1";
                return RedirectToPage("/FieldWork/Customers/Details", null, new { id = CustomerId }, IsCorrection ? $"visit-{VisitId}" : null);
            }
            Errors.AddRange(result.Errors);
            return Page();
        }
    }
}
