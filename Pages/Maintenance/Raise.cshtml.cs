using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Maintenance
{
    // Raise Ticket (Maintenance.Raise): a supervisor reports a problem for one of their own Areas
    // (optionally a Polyhouse of that Area), with up to 5 problem photos. The Area / Polyhouse /
    // category are re-loaded from the database and checked on every POST (AreaAccessService via
    // MaintenanceAuthorization) -- posted ids are never trusted. A double click / retry of the
    // same form is stopped by SubmissionTokenGuard.
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    public class RaiseModel : PageModel
    {
        public const string TokenScope = "MaintenanceRaise";
        public const long MaxRequestBytes = 30L * 1024 * 1024;   // 5 photos x 5 MB + the form

        private readonly MaintenanceRepository _repo;
        private readonly AreaRepository _areaRepo;
        private readonly PolyhouseRepository _polyhouseRepo;
        private readonly AreaAccessService _areaAccess;
        private readonly MaintenanceAuthorization _auth;
        private readonly MaintenancePhotoStore _photos;
        private readonly SubmissionTokenGuard _tokens;

        public RaiseModel(MaintenanceRepository repo, AreaRepository areaRepo, PolyhouseRepository polyhouseRepo, AreaAccessService areaAccess,
            MaintenanceAuthorization auth, MaintenancePhotoStore photos, SubmissionTokenGuard tokens)
        {
            _repo = repo;
            _areaRepo = areaRepo;
            _polyhouseRepo = polyhouseRepo;
            _areaAccess = areaAccess;
            _auth = auth;
            _photos = photos;
            _tokens = tokens;
        }

        [BindProperty] public int? CategoryId { get; set; }
        [BindProperty] public int? AreaId { get; set; }
        [BindProperty] public int? PolyhouseId { get; set; }
        [BindProperty] public string? Priority { get; set; } = "Normal";
        [BindProperty] public string? Title { get; set; }
        [BindProperty] public string? Description { get; set; }
        [BindProperty] public List<IFormFile> Photos { get; set; } = new();
        [BindProperty] public string? SubmitToken { get; set; }

        public List<MaintenanceCategory> Categories { get; set; } = new();
        public List<Area> Areas { get; set; } = new();
        public List<Polyhouse> Polyhouses { get; set; } = new();

        public async Task OnGetAsync()
        {
            SubmitToken = SubmissionTokenGuard.NewToken();
            await LoadAsync();
            if (Areas.Count == 1)
                AreaId = Areas[0].Id;
            Polyhouses = await PolyhousesForAsync(AreaId);
        }

        // Dependent dropdown: the Polyhouses of an Area the user may raise tickets for.
        public async Task<JsonResult> OnGetPolyhousesAsync(int areaId)
            => new JsonResult((await PolyhousesForAsync(areaId)).Select(p => new { id = p.Id, name = p.Name }));

        public async Task<IActionResult> OnPostAsync()
        {
            await LoadAsync();
            Polyhouses = await PolyhousesForAsync(AreaId);

            var category = Categories.FirstOrDefault(c => c.Id == CategoryId);
            if (category == null)
                ModelState.AddModelError(nameof(CategoryId), "Choose a category.");
            if (!MaintenanceRules.Priorities.Contains(Priority))
                ModelState.AddModelError(nameof(Priority), "Choose a priority.");
            var titleError = MaintenanceRules.Required(Title, "Problem", MaintenanceRules.MaxTitleLength);
            if (titleError != null) ModelState.AddModelError(nameof(Title), titleError);
            var descriptionError = MaintenanceRules.Required(Description, "Description", MaintenanceRules.MaxDescriptionLength);
            if (descriptionError != null) ModelState.AddModelError(nameof(Description), descriptionError);

            // Location: re-loaded from the database, never taken from the form.
            var area = AreaId > 0 ? await _areaRepo.GetAreaById(AreaId.Value) : null;
            if (area == null || !area.IsActive)
                ModelState.AddModelError(nameof(AreaId), "Choose an Area.");
            else if (!_auth.CanRaiseFor(User, area.Id))
                ModelState.AddModelError(nameof(AreaId), "You are not authorized to raise tickets for this Area.");
            Polyhouse? polyhouse = null;
            if (PolyhouseId > 0)
            {
                polyhouse = await _polyhouseRepo.GetByIdAsync(PolyhouseId.Value);
                if (polyhouse == null || area == null || polyhouse.AreaId != area.Id)
                    ModelState.AddModelError(nameof(PolyhouseId), "This Polyhouse does not belong to the selected Area.");
            }

            var uploads = (Photos ?? new()).Where(f => f != null && f.Length > 0).ToList();
            if (uploads.Count > MaintenanceRules.MaxPhotosPerStage)
                ModelState.AddModelError(nameof(Photos), $"At most {MaintenanceRules.MaxPhotosPerStage} photos.");
            var types = new List<MaintenancePhotoStore.DetectedType>();
            foreach (var file in uploads)
            {
                var (type, error) = await MaintenancePhotoStore.ValidateAsync(file);
                if (error != null) ModelState.AddModelError(nameof(Photos), error);
                else types.Add(type!);
            }

            if (!ModelState.IsValid)
                return PageWithNewToken();

            if (!_tokens.TryConsume(TokenScope, SubmitToken))
            {
                TempData["Error"] = "This ticket form was already submitted. Check My Tickets before raising it again.";
                return RedirectToPage("/Maintenance/MyTickets");
            }

            var saved = new List<MaintenancePhotoStore.SavedPhoto>();
            int id;
            try
            {
                for (var i = 0; i < uploads.Count; i++)
                    saved.Add(await _photos.SaveAsync(uploads[i], types[i]));
                id = await _repo.CreateAsync(new MaintenanceRepository.NewTicket(category!.Id, area!.Id, polyhouse?.Id, Title!, Description!, Priority!),
                    User.GetUserId()!.Value, saved);
            }
            catch (SqlException ex) when (ex.Number is 51713 or 51766)
            {
                _photos.DeleteQuietly(saved.Select(s => s.StoredFileName));
                ModelState.AddModelError(string.Empty, ex.Message);
                return PageWithNewToken();
            }
            catch
            {
                _photos.DeleteQuietly(saved.Select(s => s.StoredFileName));
                throw;
            }

            var ticket = (await _repo.GetByIdAsync(id))!;
            TempData["Success"] = $"Ticket {ticket.TicketNo} raised ({ticket.CategoryName}, {ticket.Priority}).";
            return RedirectToPage("/Maintenance/Details", new { id });
        }

        private IActionResult PageWithNewToken()
        {
            ModelState.Remove(nameof(SubmitToken));
            SubmitToken = SubmissionTokenGuard.NewToken();
            return Page();
        }

        private async Task LoadAsync()
        {
            Categories = await _repo.GetCategoriesAsync();
            Areas = (await _areaRepo.GetAllAreas())
                .Where(a => a.IsActive && _auth.CanRaiseFor(User, a.Id))
                .OrderBy(a => a.Name)
                .ToList();
        }

        private async Task<List<Polyhouse>> PolyhousesForAsync(int? areaId)
        {
            if (!(areaId > 0) || !_auth.CanRaiseFor(User, areaId.Value))
                return new List<Polyhouse>();
            return (await _polyhouseRepo.GetByAreaIdAsync(areaId.Value)).OrderBy(p => p.Name).ToList();
        }
    }
}
