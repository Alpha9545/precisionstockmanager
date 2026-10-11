using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Store
{
    // New Request (Store.Request): one request number for several items, for one of the user's
    // own Areas (optionally a Polyhouse of that Area). Area / Polyhouse / items are re-loaded from
    // the database and checked on every POST -- posted ids are never trusted. A double click /
    // retry of the same form is stopped by SubmissionTokenGuard.
    public class NewRequestModel : PageModel
    {
        public const string TokenScope = "StoreNewRequest";

        private readonly StoreRepository _repo;
        private readonly AreaRepository _areaRepo;
        private readonly PolyhouseRepository _polyhouseRepo;
        private readonly StoreAuthorization _auth;
        private readonly SubmissionTokenGuard _tokens;

        public NewRequestModel(StoreRepository repo, AreaRepository areaRepo, PolyhouseRepository polyhouseRepo, StoreAuthorization auth, SubmissionTokenGuard tokens)
        {
            _repo = repo;
            _areaRepo = areaRepo;
            _polyhouseRepo = polyhouseRepo;
            _auth = auth;
            _tokens = tokens;
        }

        [BindProperty] public int? AreaId { get; set; }
        [BindProperty] public int? PolyhouseId { get; set; }
        [BindProperty] public string? Purpose { get; set; }
        [BindProperty] public List<int?> LineItemIds { get; set; } = new();
        [BindProperty] public List<decimal?> LineQuantities { get; set; } = new();
        [BindProperty] public string? SubmitToken { get; set; }

        public List<Area> Areas { get; set; } = new();
        public List<Polyhouse> Polyhouses { get; set; } = new();
        public List<StoreItem> Items { get; set; } = new();

        public async Task OnGetAsync()
        {
            SubmitToken = SubmissionTokenGuard.NewToken();
            await LoadAsync();
            if (Areas.Count == 1)
                AreaId = Areas[0].Id;
            Polyhouses = await PolyhousesForAsync(AreaId);
        }

        public async Task<JsonResult> OnGetPolyhousesAsync(int areaId)
            => new JsonResult((await PolyhousesForAsync(areaId)).Select(p => new { id = p.Id, name = p.Name }));

        public async Task<IActionResult> OnPostAsync()
        {
            await LoadAsync();
            Polyhouses = await PolyhousesForAsync(AreaId);

            var area = AreaId > 0 ? await _areaRepo.GetAreaById(AreaId.Value) : null;
            if (area == null || !area.IsActive)
                ModelState.AddModelError(nameof(AreaId), "Choose an Area.");
            else if (!_auth.CanRequestFor(User, area.Id))
                ModelState.AddModelError(nameof(AreaId), StoreRules.NotAuthorizedAreaMessage);
            Polyhouse? polyhouse = null;
            if (PolyhouseId > 0)
            {
                polyhouse = await _polyhouseRepo.GetByIdAsync(PolyhouseId.Value);
                if (polyhouse == null || area == null || polyhouse.AreaId != area.Id)
                    ModelState.AddModelError(nameof(PolyhouseId), StoreRules.PolyhouseNotInAreaMessage);
            }
            var purposeError = StoreRules.Required(Purpose, "Purpose");
            if (purposeError != null) ModelState.AddModelError(nameof(Purpose), purposeError);

            var lines = new List<StoreRequestLineInput>();
            for (var i = 0; i < Math.Max(LineItemIds.Count, LineQuantities.Count); i++)
            {
                var itemId = i < LineItemIds.Count ? LineItemIds[i] : null;
                var qty = i < LineQuantities.Count ? LineQuantities[i] : null;
                if (itemId == null && qty == null)
                    continue;   // an empty row
                var item = Items.FirstOrDefault(x => x.Id == itemId);
                if (item == null) { ModelState.AddModelError("Lines", $"Line {i + 1}: choose an item."); continue; }
                if (lines.Any(l => l.ItemId == item.Id)) { ModelState.AddModelError("Lines", $"{item.Name} is listed twice -- one line per item."); continue; }
                var error = StoreRules.ValidateQuantity(qty, item.Name, item.ItemType);
                if (error != null) { ModelState.AddModelError("Lines", error); continue; }
                lines.Add(new StoreRequestLineInput(item.Id, qty!.Value));
            }
            if (lines.Count == 0 && ModelState.IsValid)
                ModelState.AddModelError("Lines", "Add at least one item.");
            if (lines.Count > StoreRules.MaxLinesPerRequest)
                ModelState.AddModelError("Lines", $"At most {StoreRules.MaxLinesPerRequest} items per request.");
            if (!ModelState.IsValid)
                return PageWithNewToken();

            if (!_tokens.TryConsume(TokenScope, SubmitToken))
            {
                TempData["Error"] = "This request form was already submitted. Check Requests before sending it again.";
                return RedirectToPage("/Store/Requests");
            }
            try
            {
                var id = await _repo.CreateRequestAsync(area!.Id, polyhouse?.Id, Purpose!, lines, User.GetUserId()!.Value);
                var request = (await _repo.GetRequestAsync(id))!;
                TempData["Success"] = $"Request {request.RequestNo} sent with {lines.Count} item(s) for {request.Destination}.";
                return RedirectToPage("/Store/RequestDetails", new { id });
            }
            catch (StoreStockException ex)
            {
                ModelState.AddModelError("Lines", ex.Message);
                return PageWithNewToken();
            }
            catch (SqlException ex) when (ex.Number is 51713 or 51818)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
                return PageWithNewToken();
            }
        }

        private IActionResult PageWithNewToken()
        {
            ModelState.Remove(nameof(SubmitToken));
            SubmitToken = SubmissionTokenGuard.NewToken();
            return Page();
        }

        private async Task LoadAsync()
        {
            Areas = (await _areaRepo.GetAllAreas()).Where(a => a.IsActive && _auth.CanRequestFor(User, a.Id)).OrderBy(a => a.Name).ToList();
            Items = await _repo.GetItemsAsync(activeOnly: true);
        }

        private async Task<List<Polyhouse>> PolyhousesForAsync(int? areaId)
        {
            if (!(areaId > 0) || !_auth.CanRequestFor(User, areaId.Value))
                return new List<Polyhouse>();
            return (await _polyhouseRepo.GetByAreaIdAsync(areaId.Value)).OrderBy(p => p.Name).ToList();
        }
    }
}
