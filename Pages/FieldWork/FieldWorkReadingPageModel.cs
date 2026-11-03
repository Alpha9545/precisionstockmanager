using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Services;
using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.Pages.FieldWork
{
    // Shared by Start Day and End Day: the posted reading (km, GPS, live photo, reason). Numbers are bound as text and
    // parsed with the invariant culture -- the browser always sends "12.3456", whatever the server's own culture is.
    // Only these properties are bound ([BindProperty] each); the employee is always the signed-in user.
    [RequestSizeLimit(6 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 6 * 1024 * 1024)]
    public abstract class FieldWorkReadingPageModel : PageModel
    {
        protected readonly FieldDayService Service;

        protected FieldWorkReadingPageModel(FieldDayService service) => Service = service;

        [BindProperty] public string? Km { get; set; }
        [BindProperty] public string? Latitude { get; set; }
        [BindProperty] public string? Longitude { get; set; }
        [BindProperty] public string? AccuracyM { get; set; }
        [BindProperty] public string? ExceptionReason { get; set; }
        [BindProperty] public IFormFile? Photo { get; set; }
        [BindProperty] public string? SubmitToken { get; set; }

        public bool SchemaReady { get; protected set; } = true;
        public List<string> Errors { get; } = new();

        public decimal MaxAccuracyMeters => Service.Options.MaxGpsAccuracyMeters;
        public long MaxPhotoBytes => Service.Options.MaxPhotoBytes;

        protected static decimal? ParseDecimal(string? text)
            => decimal.TryParse((text ?? "").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;

        protected async Task<FieldDayService.ReadingRequest> ReadRequestAsync()
        {
            byte[]? photo = null;
            if (Photo != null && Photo.Length > 0)
            {
                if (Photo.Length > Service.Options.MaxPhotoBytes)
                    Errors.Add($"The photo is too large (at most {Service.Options.MaxPhotoBytes / 1024 / 1024.0:0.#} MB).");
                else
                {
                    using var ms = new MemoryStream((int)Photo.Length);
                    await Photo.CopyToAsync(ms);
                    photo = ms.ToArray();
                }
            }
            var kmText = (Km ?? "").Trim();
            var km = ParseDecimal(kmText);
            if (kmText.Length > 0 && km == null)
                Errors.Add("The meter reading must be a number, e.g. 12345.6");
            return new FieldDayService.ReadingRequest(km, ParseDecimal(Latitude), ParseDecimal(Longitude), ParseDecimal(AccuracyM),
                ExceptionReason, photo, SubmitToken);
        }

        protected void NewToken() => SubmitToken = Guid.NewGuid().ToString("D");

        public static string Ist(DateTime? utc, string format = "dd-MMM-yyyy hh:mm tt")
            => utc.HasValue ? AppClock.ToIndiaTime(utc.Value).ToString(format, CultureInfo.InvariantCulture) : "";
    }
}
