using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Models;
using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.Pages.FieldWork.Visits
{
    // Log a visit: needs today's OPEN Field Day (Start Day done) -- otherwise blocked with a clear message. Server check-in
    // time, GPS (or a reason -> flagged), discussed (required), notes, items given, up to N live photos, a lead status
    // update and the next follow-up. The pending follow-up is completed by the visit.
    [RequestSizeLimit(24 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 24 * 1024 * 1024)]
    public class CreateModel : PageModel
    {
        private readonly FieldVisitService _visits;
        private readonly FieldCustomerService _customers;

        public CreateModel(FieldVisitService visits, FieldCustomerService customers)
        {
            _visits = visits;
            _customers = customers;
        }

        [BindProperty(SupportsGet = true)] public int CustomerId { get; set; }
        [BindProperty] public string? Latitude { get; set; }
        [BindProperty] public string? Longitude { get; set; }
        [BindProperty] public string? AccuracyM { get; set; }
        [BindProperty] public string? GpsExceptionReason { get; set; }
        [BindProperty] public string? Discussed { get; set; }
        [BindProperty] public string? Notes { get; set; }
        [BindProperty] public List<string?> ItemText { get; set; } = new();
        [BindProperty] public List<string?> ItemQty { get; set; } = new();
        [BindProperty] public List<IFormFile>? Photos { get; set; }
        [BindProperty] public int? ConsentReminderVersion { get; set; }
        [BindProperty] public string? NewLeadStatus { get; set; }
        [BindProperty] public DateTime? NextFollowUpDate { get; set; }
        [BindProperty] public string? NextFollowUpNote { get; set; }
        [BindProperty] public string? SubmitToken { get; set; }

        public bool SchemaReady { get; private set; } = true;
        public string? DayMessage { get; private set; }
        public bool HasOpenDay => DayMessage == null;
        public FieldCustomer Customer { get; private set; } = new();
        public FieldFollowUp? Pending { get; private set; }
        public List<string> Errors { get; } = new();

        public decimal MaxAccuracyMeters => _visits.Options.MaxGpsAccuracyMeters;
        public long MaxPhotoBytes => _visits.Options.MaxPhotoBytes;
        public int MaxPhotos => _visits.Options.EffectiveMaxPhotosPerVisit;
        public PhotosCaptureModel Capture => new(MaxPhotos, _visits.Options.CustomerPhotoConsentVersion, true, Latitude, Longitude, AccuracyM,
            GpsExceptionReason, "Visit photos");

        private async Task<IActionResult?> LoadAsync()
        {
            SchemaReady = await _customers.SchemaReadyAsync();
            if (!SchemaReady)
                return Page();
            var c = await _customers.GetVisibleAsync(User, CustomerId);
            if (c == null || !FieldCustomerService.MayWork(User, c))
                return NotFound();
            Customer = c;
            Pending = (await _customers.GetTimelineAsync(CustomerId)).FollowUps.FirstOrDefault(f => f.Status == FieldCustomerRules.FollowUpPending);
            DayMessage = await _visits.CheckOpenDayAsync(User);
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
            var photos = await FieldWorkForm.ReadPhotosAsync(Photos, MaxPhotos, MaxPhotoBytes, Errors);
            if (Errors.Count == 0)
            {
                var result = await _visits.CreateAsync(User, new FieldVisitService.VisitRequest(CustomerId, FieldWorkForm.ParseDecimal(Latitude),
                    FieldWorkForm.ParseDecimal(Longitude), FieldWorkForm.ParseDecimal(AccuracyM), GpsExceptionReason, Discussed, Notes, ItemText, ItemQty,
                    photos, ConsentReminderVersion, NewLeadStatus, NextFollowUpDate, NextFollowUpNote, SubmitToken));
                if (result.Ok)
                {
                    TempData["FieldWorkMessage"] = "Visit saved.";
                    if (result.AskNextDate)
                        TempData["FieldWorkAskNext"] = "1";
                    return RedirectToPage("/FieldWork/Customers/Details", null, new { id = CustomerId }, $"visit-{result.Id}");
                }
                Errors.AddRange(result.Errors);
            }
            return Page();
        }
    }
}
