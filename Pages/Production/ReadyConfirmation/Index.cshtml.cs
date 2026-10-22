using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.ReadyConfirmation
{
    // Phase 25 (Phase K): the eligible-batches list -- every ACTIVE
    // ('Sown') Seed Sowing that still has remaining un-confirmed
    // quantity, Area-scoped exactly like every other Production list
    // page (AreaAccessService, never UI-only filtering). Deliberately
    // not restricted to only currently-alerted Sowings -- see
    // SeedSowingRepository.GetReadyForConfirmationAsync's own comment
    // for why. Read-only: nothing on this page ever writes anything.
    // 2026-10-19: the list comes from SowingApprovalQueueRepository (shared with Mother Plant & Cutting > Sowing Approvals and
    // the home page): EVERY pending batch in the user's Area / workflow scope, grouped by workflow (Direct Seed and Cutting
    // sowings / Bed Sowing tray batches / Mother Plant Area transplant batches), each with who approves it and whether THIS
    // user may -- seeing a batch never means being allowed to approve it (the Confirm page and the repository re-check).
    public class IndexModel : PageModel
    {
        private readonly SowingApprovalQueueRepository _queueRepo;
        private readonly SeedlingAreaScope _areaAccessService; // seedling-only Area scope (Authorization/SeedlingAreaScope.cs)
        private readonly AreaAccessService _strictArea;         // Mother Plant Area batches: the regular Area rule, always
        private readonly FeatureAccessService _featureAccess;

        public IndexModel(SowingApprovalQueueRepository queueRepo, SeedlingAreaScope areaAccessService, AreaAccessService strictArea,
            FeatureAccessService featureAccess)
        {
            _queueRepo = queueRepo;
            _areaAccessService = areaAccessService;
            _strictArea = strictArea;
            _featureAccess = featureAccess;
        }

        // Optional workflow filter (SowingApprovalRules.Categories); anything else shows every workflow.
        [BindProperty(SupportsGet = true)] public string? Category { get; set; }
        // 2026-10-20: which list -- "history" = approval history; anything else = waiting for approval. Never an identity.
        [BindProperty(SupportsGet = true)] public string? Tab { get; set; }
        public const string TabHistory = "history";
        public bool ShowHistory => Tab == TabHistory;
        public bool IsAdministrator { get; private set; }
        public List<SowingApprovalQueueRepository.ApprovedBatch> History { get; private set; } = new();

        // 2026-10-22: Variety (dbo.SeedSowings.SpeciesId) and Sowing Date (the calendar day of dbo.SeedSowings.SowingDate) filters
        // of Waiting for approval -- a display filter only (SowingListFilter): validated here against the viewer's own queue,
        // applied in the pending-batch SQL, and never part of who may approve which batch (Confirm re-checks by the batch id).
        [BindProperty(SupportsGet = true)] public int? Variety { get; set; }
        [BindProperty(SupportsGet = true)] public DateTime? SowingDate { get; set; }
        public SowingListFilter Filter { get; private set; } = SowingListFilter.None;
        public List<VarietyOption> VarietyOptions { get; private set; } = new();
        public string? FilterNote { get; private set; }
        // what Approve / History carry so their Back links return here (2026-10-22)
        public ApprovalListState ListState => ApprovalListState.From(Category, Filter.SpeciesId, Filter.SowingDate);

        public List<SowingApprovalQueueRepository.Item> Items { get; private set; } = new();
        public SowingApprovalRules.Settings Settings { get; private set; } = new(DirectSowingRules.BedApprover.NotConfigured, false, false);
        public bool CanApprove { get; private set; }

        public async Task OnGetAsync()
        {
            if (Category != null && !SowingApprovalRules.Categories.Contains(Category))
                Category = null;
            // Same rule the server enforces (FeatureAuthorizationConventions).
            CanApprove = await _featureAccess.CanAccessPageAsync(User, "/Production/ReadyConfirmation/Confirm");
            // 2026-10-20 (SowingApprovalVisibility, shared with Mother Plant & Cutting > Sowing Approvals): an administrator sees
            // every pending batch and every user's approvals; everyone else only the batches they may approve and their own approvals.
            IsAdministrator = SowingApprovalVisibility.IsAdministrator(User);
            var viewer = Viewer(User, CanApprove, _areaAccessService, _strictArea);
            var queue = await _queueRepo.GetPendingAsync(viewer, Category,
                approvableOnly: SowingApprovalVisibility.PendingApprovableOnly(User));
            // the dropdown: the varieties of this viewer's unfiltered queue (same scope, same workflow tab)
            VarietyOptions = SowingApprovalQueueRepository.VarietiesOf(queue.Items);
            var invalidDate = ModelState.TryGetValue(nameof(SowingDate), out var dateState) && dateState.Errors.Count > 0;
            (Filter, FilterNote) = SowingListFilter.Normalize(Variety, SowingDate, VarietyOptions.Select(v => v.Id));
            if (invalidDate)
                FilterNote = FilterNote == null ? SowingListFilter.InvalidDateMessage : FilterNote + " " + SowingListFilter.InvalidDateMessage;
            (Variety, SowingDate) = (Filter.SpeciesId, Filter.SowingDate);
            if (Filter.IsActive)
                queue = await _queueRepo.GetPendingAsync(viewer, Category,
                    approvableOnly: SowingApprovalVisibility.PendingApprovableOnly(User), filter: Filter);
            Items = queue.Items;
            Settings = queue.Settings;
            History = (await _queueRepo.GetApprovalHistoryAsync(SowingApprovalVisibility.History(User), motherPlantOnly: false))
                .Where(h => Category == null || h.Category == Category).ToList();
        }

        public static SowingApprovalQueueRepository.Viewer Viewer(System.Security.Claims.ClaimsPrincipal user, bool canApprove,
            SeedlingAreaScope seedlingScope, AreaAccessService strictArea)
            => new(user.GetUserId(), user.Identity?.Name, canApprove,
                areaId => seedlingScope.CanAccessArea(user, areaId), areaId => strictArea.CanAccessArea(user, areaId));
    }
}

namespace PlantStockManager.Pages.Production.ReadyConfirmation
{
    // _ApprovalAction.cshtml: the Approve button, or why this user cannot approve the batch (both Sowing Approvals pages).
    // ListRoute (2026-10-22): the Seed Production list state (ApprovalListState.Route) for the Approve / History links; null on
    // Mother Plant & Cutting > Sowing Approvals (unchanged).
    public sealed record ApprovalActionView(SowingApprovalQueueRepository.Item Item, string? From, IDictionary<string, string>? ListRoute = null)
    {
        // A NEW dictionary per link (asp-all-route-data replaces asp-route-* values, and a shared one would be mutated):
        // the batch id (+ From for Confirm) and the list state.
        public Dictionary<string, string> LinkRoute(bool withFrom)
        {
            var r = ListRoute == null ? new Dictionary<string, string>() : new Dictionary<string, string>(ListRoute);
            r["id"] = Item.Sowing.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (withFrom && !string.IsNullOrEmpty(From))
                r["from"] = From;
            return r;
        }
    }
}

namespace PlantStockManager.Pages.Production.ReadyConfirmation
{
    // 2026-10-22: Seed Production > Sowing Approvals' list state -- the workflow pill and the Variety / Sowing Date filters --
    // carried through Approve (Confirm) and History so their Back / Cancel links and the post-approval redirect return to the
    // same filtered list. NAVIGATION ONLY: never read by the approval or the cancel (they go by the batch / approval id with
    // every existing check), and the list validates the values again (SowingListFilter.Normalize) when it is shown.
    public sealed record ApprovalListState(string? Category, int? Variety, DateTime? SowingDate)
    {
        public static readonly ApprovalListState None = new(null, null, null);

        // Shape-checked only: a known workflow, a positive id, a date in SowingListFilter's range.
        public static ApprovalListState From(string? category, int? variety, DateTime? sowingDate)
            => new(category != null && SowingApprovalRules.Categories.Contains(category) ? category : null,
                variety is > 0 ? variety : null,
                sowingDate is DateTime d && d.Date >= SowingListFilter.MinDate && d.Date <= SowingListFilter.MaxDate ? d.Date : null);

        // query values (asp-all-route-data / RedirectToPage); unset values are left out
        public Dictionary<string, string> Route
        {
            get
            {
                var r = new Dictionary<string, string>();
                if (Category != null) r["category"] = Category;
                if (Variety.HasValue) r["variety"] = Variety.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (SowingDate.HasValue) r["sowingDate"] = SowingDate.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                return r;
            }
        }

        // the list state + a batch id, for one link (a new dictionary each call)
        public Dictionary<string, string> RouteForBatch(int id)
        {
            var r = Route;
            r["id"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return r;
        }

        public Microsoft.AspNetCore.Routing.RouteValueDictionary RouteValues()
        {
            var r = new Microsoft.AspNetCore.Routing.RouteValueDictionary();
            foreach (var kv in Route)
                r[kv.Key] = kv.Value;
            return r;
        }

        public Microsoft.AspNetCore.Routing.RouteValueDictionary RouteWith(string key, object value)
        {
            var r = RouteValues();
            r[key] = value;
            return r;
        }
    }
}

namespace PlantStockManager.Pages.Production.ReadyConfirmation
{
    // _ApprovalHistory.cshtml: the approval history table of both Sowing Approvals pages (2026-10-20). AllApprovers = the
    // administrator's view of every user's approvals (the Approved by column tells them apart).
    public sealed record ApprovalHistoryView(IReadOnlyList<SowingApprovalQueueRepository.ApprovedBatch> Rows, bool AllApprovers, bool ShowWorkflow, string TableId);
}
