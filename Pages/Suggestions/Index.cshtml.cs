using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Suggestions
{
    // Suggestions & Problems list (page rule: any signed-in user -- a global module). One page of
    // posts at a time (SuggestionRules.PageSize), newest first, filtered IN SQL, from EVERY Area:
    //   every user     ACTIVE and PERMANENT posts of all Areas; filters Active / Permanent, type,
    //                  status, Area, Polyhouse
    //   administrator  also Expired / Deleted / All, posted by, date range
    public class IndexModel : PageModel
    {
        private readonly SuggestionRepository _repo;
        private readonly AreaRepository _areaRepo;
        private readonly PolyhouseRepository _polyhouseRepo;
        private readonly SuggestionAuthorization _auth;

        public IndexModel(SuggestionRepository repo, AreaRepository areaRepo, PolyhouseRepository polyhouseRepo, SuggestionAuthorization auth)
        {
            _repo = repo;
            _areaRepo = areaRepo;
            _polyhouseRepo = polyhouseRepo;
            _auth = auth;
        }

        [BindProperty(SupportsGet = true)] public string? Lifetime { get; set; }
        [BindProperty(SupportsGet = true)] public string? Type { get; set; }
        [BindProperty(SupportsGet = true)] public string? Status { get; set; }
        [BindProperty(SupportsGet = true)] public int? AreaId { get; set; }
        [BindProperty(SupportsGet = true)] public int? PolyhouseId { get; set; }
        [BindProperty(SupportsGet = true)] public int? PostedById { get; set; }
        [BindProperty(SupportsGet = true)] public DateTime? FromDate { get; set; }
        [BindProperty(SupportsGet = true)] public DateTime? ToDate { get; set; }
        [BindProperty(SupportsGet = true, Name = "pg")] public int PageNo { get; set; } = 1;

        public bool IsManager { get; set; }
        public bool CanCreate { get; set; }
        public List<SuggestionPost> Posts { get; set; } = new();
        public int Total { get; set; }
        public int PageCount => Math.Max(1, (Total + SuggestionRules.PageSize - 1) / SuggestionRules.PageSize);
        public SuggestionCounts Counts { get; set; } = new();
        public List<SuggestionPostType> Types { get; set; } = new();
        public List<Area> Areas { get; set; } = new();
        public List<Polyhouse> Polyhouses { get; set; } = new();
        public List<SuggestionPoster> Posters { get; set; } = new();
        public DateTime NowUtc { get; } = DateTime.UtcNow;
        // Expired / Deleted / All remain administrator views; everyone sees Active and Permanent.
        public IReadOnlyList<string> LifetimeChoices => IsManager ? SuggestionRules.Lifetimes : SuggestionRules.EmployeeLifetimes;

        public async Task<IActionResult> OnGetAsync()
        {
            if (!_auth.CanUseModule(User)) return Forbid();
            IsManager = _auth.IsManager(User);
            CanCreate = _auth.CanCreate(User);
            // Global module: no Area scope -- every Area for every signed-in user.
            const IReadOnlyCollection<int>? scope = null;

            Types = await _repo.GetTypesAsync();
            Areas = (await _areaRepo.GetAllAreas()).Where(a => a.IsActive).OrderBy(a => a.Name).ToList();

            // Never trust the query string: values outside what this user may filter by are dropped.
            if (!LifetimeChoices.Contains(Lifetime)) Lifetime = SuggestionRules.LifetimeActive;
            if (!Types.Any(t => t.Code == Type)) Type = null;
            if (!SuggestionRules.Statuses.Contains(Status)) Status = null;
            if (AreaId.HasValue && !Areas.Any(a => a.Id == AreaId)) AreaId = null;
            if (!IsManager) { PostedById = null; FromDate = null; ToDate = null; }
            if (AreaId.HasValue)
                Polyhouses = (await _polyhouseRepo.GetByAreaIdAsync(AreaId.Value)).OrderBy(p => p.Name).ToList();
            if (!Polyhouses.Any(p => p.Id == PolyhouseId)) PolyhouseId = null;
            if (IsManager)
                Posters = await _repo.GetPostersAsync();
            PageNo = Math.Max(1, PageNo);

            var filter = new SuggestionFilter
            {
                Lifetime = Lifetime!, TypeCode = Type, Status = Status, AreaId = AreaId, PolyhouseId = PolyhouseId, PostedById = PostedById,
                FromUtc = FromDate.HasValue ? TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(FromDate.Value.Date, DateTimeKind.Unspecified), AppClock.IndiaTimeZone) : null,
                ToUtcExclusive = ToDate.HasValue ? TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(ToDate.Value.Date.AddDays(1), DateTimeKind.Unspecified), AppClock.IndiaTimeZone) : null,
                Page = PageNo
            };
            (Posts, Total) = await _repo.SearchAsync(filter, scope);
            Counts = await _repo.GetCountsAsync(scope);
            return Page();
        }

        // Route values that keep the current filters (paging links).
        public Dictionary<string, string> RouteFor(int page)
        {
            var values = new Dictionary<string, string> { ["pg"] = page.ToString() };
            void Add(string key, object? value) { if (value != null && value.ToString() != "") values[key] = value is DateTime d ? d.ToString("yyyy-MM-dd") : value.ToString()!; }
            Add("Lifetime", Lifetime);
            Add("Type", Type);
            Add("Status", Status);
            Add("AreaId", AreaId);
            Add("PolyhouseId", PolyhouseId);
            Add("PostedById", PostedById);
            Add("FromDate", FromDate);
            Add("ToDate", ToDate);
            return values;
        }
    }
}
