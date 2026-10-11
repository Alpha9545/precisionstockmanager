using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Suggestions
{
    // Create a Suggestion / Problem (page rule: any signed-in user -- a global module). Any active
    // Area may be chosen; the Polyhouse must belong to it and is required for the MainOffice Area.
    // Area, Polyhouse and type are RE-LOADED from the database -- never trusted from the form. One optional photo, from the camera or the gallery, validated by its bytes
    // (SuggestionPhotoStore = the Maintenance photo rules). Lifetime is set by the database.
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    public class CreateModel : PageModel
    {
        public const long MaxRequestBytes = 12L * 1024 * 1024;   // camera + gallery input (5 MB each) + the form
        public const string TokenScope = "SuggestionCreate";

        private readonly SuggestionRepository _repo;
        private readonly AreaRepository _areaRepo;
        private readonly PolyhouseRepository _polyhouseRepo;
        private readonly SuggestionAuthorization _auth;
        private readonly SuggestionPhotoStore _photos;
        private readonly SubmissionTokenGuard _tokens;

        public CreateModel(SuggestionRepository repo, AreaRepository areaRepo, PolyhouseRepository polyhouseRepo,
            SuggestionAuthorization auth, SuggestionPhotoStore photos, SubmissionTokenGuard tokens)
        {
            _repo = repo;
            _areaRepo = areaRepo;
            _polyhouseRepo = polyhouseRepo;
            _auth = auth;
            _photos = photos;
            _tokens = tokens;
        }

        [BindProperty] public string? TypeCode { get; set; }
        [BindProperty] public int? AreaId { get; set; }
        [BindProperty] public int? PolyhouseId { get; set; }
        [BindProperty] public string? Title { get; set; }
        [BindProperty] public string? Description { get; set; }
        // Both the "Take Photo" (camera) and the "Choose from Gallery" input post here; at most one file.
        [BindProperty] public List<IFormFile> Photos { get; set; } = new();
        [BindProperty] public string? SubmitToken { get; set; }

        public List<SuggestionPostType> Types { get; set; } = new();
        public List<Area> Areas { get; set; } = new();
        public List<Polyhouse> Polyhouses { get; set; } = new();
        public bool PolyhouseRequired { get; set; }

        public async Task<IActionResult> OnGetAsync()
        {
            if (!_auth.CanCreate(User)) return Forbid();
            SubmitToken = SubmissionTokenGuard.NewToken();
            await LoadAsync();
            TypeCode ??= SuggestionRules.Problem;
            if (Areas.Count == 1)
                AreaId = Areas[0].Id;
            await LoadPolyhousesAsync();
            return Page();
        }

        // Dependent dropdown: the Polyhouses of an Area, and whether one is required.
        public async Task<JsonResult> OnGetPolyhousesAsync(int areaId)
        {
            var area = _auth.CanCreateFor(User, areaId) ? await _areaRepo.GetAreaById(areaId) : null;
            if (area == null || !area.IsActive)
                return new JsonResult(new { required = false, polyhouses = Array.Empty<object>() });
            var list = await _polyhouseRepo.GetByAreaIdAsync(areaId);
            return new JsonResult(new { required = SuggestionRules.RequiresPolyhouse(area.AreaType), polyhouses = list.OrderBy(p => p.Name).Select(p => new { id = p.Id, name = p.Name }) });
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (!_auth.CanCreate(User)) return Forbid();
            await LoadAsync();

            if (!Types.Any(t => t.Code == TypeCode))
                ModelState.AddModelError(nameof(TypeCode), "Choose a type.");
            var titleError = SuggestionRules.Required(Title, "Title", SuggestionRules.MaxTitleLength);
            if (titleError != null) ModelState.AddModelError(nameof(Title), titleError);
            var descriptionError = SuggestionRules.Required(Description, "Details", SuggestionRules.MaxDescriptionLength);
            if (descriptionError != null) ModelState.AddModelError(nameof(Description), descriptionError);

            // Location: re-loaded from the database, never taken from the form. Every ACTIVE Area is
            // allowed (global module); a missing / inactive / unknown Area is refused.
            var area = AreaId > 0 ? await _areaRepo.GetAreaById(AreaId.Value) : null;
            if (area == null || !area.IsActive || !_auth.CanCreateFor(User, area.Id))
            {
                ModelState.AddModelError(nameof(AreaId), SuggestionRules.ChooseAreaMessage);
                area = null;
            }
            await LoadPolyhousesAsync(area);
            Polyhouse? polyhouse = null;
            if (PolyhouseId > 0)
            {
                polyhouse = await _polyhouseRepo.GetByIdAsync(PolyhouseId.Value);
                if (polyhouse == null || area == null || polyhouse.AreaId != area.Id)
                {
                    if (area != null) ModelState.AddModelError(nameof(PolyhouseId), SuggestionRules.PolyhouseNotInAreaMessage);
                    polyhouse = null;
                }
            }
            else if (area != null && SuggestionRules.RequiresPolyhouse(area.AreaType))
                ModelState.AddModelError(nameof(PolyhouseId), SuggestionRules.PolyhouseRequiredMessage);

            var uploads = (Photos ?? new()).Where(f => f != null && f.Length > 0).ToList();
            MaintenancePhotoStore.DetectedType? photoType = null;
            if (uploads.Count > 1)
                ModelState.AddModelError(nameof(Photos), "Attach one photo -- either from the camera or from the gallery.");
            else if (uploads.Count == 1)
            {
                var (type, error) = await MaintenancePhotoStore.ValidateAsync(uploads[0]);
                if (error != null) ModelState.AddModelError(nameof(Photos), error);
                photoType = type;
            }

            if (!ModelState.IsValid)
                return PageWithNewToken();

            if (!_tokens.TryConsume(TokenScope, SubmitToken))
            {
                TempData["Error"] = SuggestionRules.AlreadySubmittedMessage + " Check the list before posting it again.";
                return RedirectToPage("/Suggestions/Index");
            }

            int id;
            try
            {
                id = await _photos.SaveThenAsync(uploads.FirstOrDefault(), photoType, saved =>
                    _repo.CreateAsync(new SuggestionRepository.NewPost(TypeCode!, Title!, Description!, area!.Id, polyhouse?.Id), User.GetUserId()!.Value, saved));
            }
            catch (SqlException ex) when (ex.Number is 51713 or 51834 or 51835 or 51836)
            {
                // The database repeated a location / type rule (e.g. changed in the meantime); the photo is already removed.
                ModelState.AddModelError(string.Empty, ex.Message);
                return PageWithNewToken();
            }

            var post = (await _repo.GetPostAsync(id))!;
            TempData["Success"] = $"{post.TypeName} {post.PostNo} posted for {post.Location}. It stays open for discussion for {SuggestionRules.LifetimeHours} hours.";
            return RedirectToPage("/Suggestions/Details", new { id });
        }

        private IActionResult PageWithNewToken()
        {
            ModelState.Remove(nameof(SubmitToken));
            SubmitToken = SubmissionTokenGuard.NewToken();
            return Page();
        }

        private async Task LoadAsync()
        {
            Types = await _repo.GetTypesAsync();
            Areas = (await _areaRepo.GetAllAreas())
                .Where(a => a.IsActive)
                .OrderBy(a => a.Name)
                .ToList();
        }

        private async Task LoadPolyhousesAsync(Area? area = null)
        {
            area ??= Areas.FirstOrDefault(a => a.Id == AreaId);
            if (area == null)
            {
                Polyhouses = new();
                PolyhouseRequired = false;
                return;
            }
            Polyhouses = (await _polyhouseRepo.GetByAreaIdAsync(area.Id)).OrderBy(p => p.Name).ToList();
            PolyhouseRequired = SuggestionRules.RequiresPolyhouse(area.AreaType);
        }
    }
}
