using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Models;
using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.Pages.FieldWork.Customers
{
    // Add customer: optional live photo (with the permission reminder), GPS (required for a field employee, optional for a
    // manager adding from the office), and a duplicate-phone warning that can be confirmed ("Save anyway").
    [RequestSizeLimit(6 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 6 * 1024 * 1024)]
    public class CreateModel : PageModel
    {
        private readonly FieldCustomerService _service;

        public CreateModel(FieldCustomerService service) => _service = service;

        [BindProperty] public string? Name { get; set; }
        [BindProperty] public string? ContactPerson { get; set; }
        [BindProperty] public string? Phone { get; set; }
        [BindProperty] public string? AlternatePhone { get; set; }
        [BindProperty] public string? CustomerType { get; set; }
        [BindProperty] public string? VillageCity { get; set; }
        [BindProperty] public string? District { get; set; }
        [BindProperty] public string? Address { get; set; }
        [BindProperty] public int? AssignedUserId { get; set; }
        [BindProperty] public string? Latitude { get; set; }
        [BindProperty] public string? Longitude { get; set; }
        [BindProperty] public string? AccuracyM { get; set; }
        [BindProperty] public string? GpsExceptionReason { get; set; }
        [BindProperty] public int? ConsentReminderVersion { get; set; }
        [BindProperty] public List<IFormFile>? Photos { get; set; }
        [BindProperty] public bool ConfirmDuplicate { get; set; }
        [BindProperty] public string? SubmitToken { get; set; }

        public bool SchemaReady { get; private set; } = true;
        public bool CanManage { get; private set; }
        public bool GpsRequired { get; private set; }
        public List<string> Errors { get; } = new();
        public IReadOnlyList<FieldCustomerRules.DuplicateMatch> Duplicates { get; private set; } = Array.Empty<FieldCustomerRules.DuplicateMatch>();
        public List<string> DistrictSuggestions { get; private set; } = new();
        public List<FieldEmployeeOption> Employees { get; private set; } = new();

        public decimal MaxAccuracyMeters => _service.Options.MaxGpsAccuracyMeters;
        public long MaxPhotoBytes => _service.Options.MaxPhotoBytes;

        public PhotosCaptureModel Capture => new(1, _service.Options.CustomerPhotoConsentVersion, GpsRequired, Latitude, Longitude, AccuracyM,
            GpsExceptionReason, "Customer photo");

        private async Task LoadAsync()
        {
            CanManage = FieldCustomerService.MayManage(User);
            GpsRequired = FieldDayService.IsFieldEmployee(User);
            DistrictSuggestions = await _service.AllDistrictsAsync();
            if (CanManage)
                Employees = await _service.ListAssignableEmployeesAsync();
        }

        public async Task<IActionResult> OnGetAsync()
        {
            SchemaReady = await _service.SchemaReadyAsync();
            if (!SchemaReady)
                return Page();
            await LoadAsync();
            SubmitToken = Guid.NewGuid().ToString("D");
            return Page();
        }

        // JSON for the as-you-type duplicate warning. Answers only whether the number exists and who has it.
        public async Task<IActionResult> OnGetCheckPhoneAsync(string? phone, string? alt)
        {
            if (!await _service.SchemaReadyAsync())
                return new JsonResult(new { warning = (string?)null });
            var matches = await _service.FindDuplicatesAsync(phone, alt, null);
            return new JsonResult(new { warning = matches.Count > 0 ? FieldCustomerRules.DuplicateWarning(matches) : null });
        }

        public async Task<IActionResult> OnPostAsync()
        {
            SchemaReady = await _service.SchemaReadyAsync();
            if (!SchemaReady)
                return Page();
            await LoadAsync();
            var photos = await FieldWorkForm.ReadPhotosAsync(Photos, 1, _service.Options.MaxPhotoBytes, Errors);
            if (Errors.Count == 0)
            {
                var result = await _service.CreateAsync(User, new FieldCustomerService.CreateRequest(
                    new FieldCustomerRules.CustomerInput(Name, ContactPerson, Phone, AlternatePhone, CustomerType, VillageCity, District, Address),
                    AssignedUserId, FieldWorkForm.ParseDecimal(Latitude), FieldWorkForm.ParseDecimal(Longitude), FieldWorkForm.ParseDecimal(AccuracyM),
                    GpsExceptionReason, photos.FirstOrDefault(), ConsentReminderVersion, ConfirmDuplicate, SubmitToken));
                if (result.Ok)
                {
                    TempData["FieldWorkMessage"] = "Customer saved.";
                    return RedirectToPage("/FieldWork/Customers/Details", new { id = result.Id });
                }
                Errors.AddRange(result.Errors);
                Duplicates = result.Duplicates ?? Array.Empty<FieldCustomerRules.DuplicateMatch>();
            }
            // same token again: a resubmit after an error must still be recognised as the same customer
            return Page();
        }
    }
}
