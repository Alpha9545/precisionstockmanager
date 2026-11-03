using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Models;
using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.Pages.FieldWork.Visits
{
    // One-step New Visit (2026-11-03): "New customer" (created together with the visit in ONE transaction) or "Existing
    // customer" (search among the customers this user may log a visit for). Needs today's open Field Day. GPS once, used for
    // the visit and -- for a new customer -- the customer location. Photos optional; the first one also becomes the customer's
    // profile photo when the customer has none. All rules live in FieldVisitService.SaveVisitAsync.
    [RequestSizeLimit(24 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 24 * 1024 * 1024)]
    public class NewModel : PageModel
    {
        private readonly FieldVisitService _visits;
        private readonly FieldCustomerService _customers;

        public NewModel(FieldVisitService visits, FieldCustomerService customers)
        {
            _visits = visits;
            _customers = customers;
        }

        [BindProperty] public string? Mode { get; set; }
        [BindProperty(SupportsGet = true)] public int? CustomerId { get; set; }
        [BindProperty] public string? Name { get; set; }
        [BindProperty] public string? ContactPerson { get; set; }
        [BindProperty] public string? Phone { get; set; }
        [BindProperty] public string? AlternatePhone { get; set; }
        [BindProperty] public string? CustomerType { get; set; }
        [BindProperty] public string? VillageCity { get; set; }
        [BindProperty] public string? District { get; set; }
        [BindProperty] public string? Address { get; set; }
        [BindProperty] public bool ConfirmDuplicate { get; set; }
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
        public FieldCustomer? Selected { get; private set; }
        public List<string> Errors { get; } = new();
        public IReadOnlyList<FieldCustomerRules.DuplicateMatch> Duplicates { get; private set; } = Array.Empty<FieldCustomerRules.DuplicateMatch>();
        public List<string> DistrictSuggestions { get; private set; } = new();

        public decimal MaxAccuracyMeters => _visits.Options.MaxGpsAccuracyMeters;
        public long MaxPhotoBytes => _visits.Options.MaxPhotoBytes;
        public int MaxPhotos => _visits.Options.EffectiveMaxPhotosPerVisit;
        public PhotosCaptureModel Capture => new(MaxPhotos, _visits.Options.CustomerPhotoConsentVersion, true, Latitude, Longitude, AccuracyM,
            GpsExceptionReason, "Photos");
        public bool IsUsable(FieldCustomerRules.DuplicateMatch m) => FieldCustomerRules.DuplicateUsable(m, User.GetUserId());

        private async Task<IActionResult?> LoadAsync(bool get)
        {
            SchemaReady = await _customers.SchemaReadyAsync();
            if (!SchemaReady)
                return Page();
            DayMessage = await _visits.CheckOpenDayAsync(User);
            DistrictSuggestions = await _customers.AllDistrictsAsync();
            if (CustomerId.HasValue && (get || Mode == FieldVisitService.ModeExisting))
            {
                var c = await _customers.GetVisibleAsync(User, CustomerId.Value);
                if (c == null || !FieldCustomerService.MayWork(User, c))
                    return NotFound();   // another employee's customer by changing the id
                Selected = c;
            }
            if (get)
                Mode = Selected != null ? FieldVisitService.ModeExisting : FieldVisitService.ModeNew;
            return null;
        }

        public async Task<IActionResult> OnGetAsync()
        {
            var denied = await LoadAsync(true);
            if (denied != null)
                return denied;
            SubmitToken = Guid.NewGuid().ToString("D");
            return Page();
        }

        // "Existing customer" search: only customers this user may log a visit for.
        public async Task<IActionResult> OnGetSearchAsync(string? q)
        {
            if (!await _customers.SchemaReadyAsync())
                return new JsonResult(Array.Empty<object>());
            var rows = await _visits.SearchMyCustomersAsync(User, q);
            return new JsonResult(rows.Select(r => new
            {
                id = r.Id, name = r.Name, village = r.VillageCity, status = FieldCustomerRules.LeadLabel(r.LeadStatus),
                followUp = r.PendingFollowUpDate?.ToString("dd-MMM-yyyy"),
            }));
        }

        // Duplicate-phone check while typing. "usable" = the user may log a visit for that customer ("Use this customer").
        public async Task<IActionResult> OnGetCheckPhoneAsync(string? phone, string? alt)
        {
            if (!await _customers.SchemaReadyAsync())
                return new JsonResult(new { warning = (string?)null, matches = Array.Empty<object>() });
            var matches = await _customers.FindDuplicatesAsync(phone, alt, null);
            return new JsonResult(new
            {
                warning = matches.Count > 0 ? FieldCustomerRules.DuplicateWarning(matches) : null,
                matches = matches.Select(m => new { id = m.CustomerId, name = m.Name, village = m.VillageCity, owner = m.AssignedName, usable = IsUsable(m) }),
            });
        }

        public async Task<IActionResult> OnPostAsync()
        {
            var denied = await LoadAsync(false);
            if (denied != null)
                return denied;
            var photos = await FieldWorkForm.ReadPhotosAsync(Photos, MaxPhotos, MaxPhotoBytes, Errors);
            if (Errors.Count == 0)
            {
                var visit = new FieldVisitService.VisitRequest(CustomerId ?? 0, FieldWorkForm.ParseDecimal(Latitude), FieldWorkForm.ParseDecimal(Longitude),
                    FieldWorkForm.ParseDecimal(AccuracyM), GpsExceptionReason, Discussed, Notes, ItemText, ItemQty, photos, ConsentReminderVersion,
                    NewLeadStatus, NextFollowUpDate, NextFollowUpNote, SubmitToken);
                var result = await _visits.SaveVisitAsync(User, new FieldVisitService.NewVisitRequest(Mode, CustomerId,
                    new FieldCustomerRules.CustomerInput(Name, ContactPerson, Phone, AlternatePhone, CustomerType, VillageCity, District, Address),
                    ConfirmDuplicate, visit));
                if (result.Ok)
                {
                    TempData["FieldWorkMessage"] = Mode == FieldVisitService.ModeNew ? "Customer and visit saved." : "Visit saved.";
                    if (result.AskNextDate)
                        TempData["FieldWorkAskNext"] = "1";
                    return RedirectToPage("/FieldWork/Customers/Details", null, new { id = result.CustomerId }, $"visit-{result.Id}");
                }
                Errors.AddRange(result.Errors);
                Duplicates = result.Duplicates ?? Array.Empty<FieldCustomerRules.DuplicateMatch>();
            }
            // same token again: a resubmit after an error must still be recognised as the same visit
            return Page();
        }
    }
}
