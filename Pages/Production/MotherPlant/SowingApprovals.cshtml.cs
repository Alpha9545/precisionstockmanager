using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.MotherPlant
{
    // 2026-10-19 (owner requirement 2026-10-12): Mother Plant & Cutting > Sowing Approvals.
    // 2026-10-20 (owner requirements): visibility = SowingApprovalVisibility (the same policy as Seed Production > Sowing
    // Approvals): a System Administrator sees every pending batch and every user's approvals (with the approver); everyone else
    // only the batches they may approve and the approvals they made. Two SEPARATE lists, never mixed --
    //   Approved by me (default)  the Mother Plant transplant batches THIS supervisor approved (SowingApprovalQueueRepository.
    //                             GetMotherPlantApprovedByAsync): filtered by dbo.ReadyConfirmations.ApprovedById = the signed-in
    //                             user's id from the server-side principal. No user / approver id is ever bound from the request.
    //   Waiting for approval      the pending batches of the user's Mother Plant Areas that THIS user is authorised to approve
    //                             (2026-10-20: GetPendingAsync approvableOnly -- by permission, role and the batch's Area, never by
    //                             a browser-supplied id; not by who transplanted it, so every Area Supervisor of an Area still sees
    //                             all of that Area's batches). Batches the user may not approve are left out (no "Not your
    //                             approval"). Read from the same queue as Seed Production > Sowing Approvals. Approve opens the existing
    //                             ReadyConfirmation/Confirm page (the one approval transaction and Ready Stock posting).
    // Area scope of the queue: the regular Area rule, always enforced.
    // 2026-10-21: both lists also hold the Direct Sowings recorded in Mother Plant Areas (Mother Plant & Cutting > Direct Sowing),
    // approved -- like a transplant batch -- by an Area Supervisor of the batch's Area or a System Administrator.
    public class SowingApprovalsModel : PageModel
    {
        public const string TabApproved = "approved";
        public const string TabPending = "pending";

        private readonly SowingApprovalQueueRepository _queueRepo;
        private readonly SeedlingAreaScope _seedlingScope;
        private readonly AreaAccessService _areaAccess;
        private readonly FeatureAccessService _featureAccess;
        private readonly AreaRepository _areaRepo;

        public SowingApprovalsModel(SowingApprovalQueueRepository queueRepo, SeedlingAreaScope seedlingScope, AreaAccessService areaAccess,
            FeatureAccessService featureAccess, AreaRepository areaRepo)
        {
            _queueRepo = queueRepo;
            _seedlingScope = seedlingScope;
            _areaAccess = areaAccess;
            _featureAccess = featureAccess;
            _areaRepo = areaRepo;
        }

        // The ONLY bound value: which tab. Anything other than "pending" shows the user's own approvals.
        [BindProperty(SupportsGet = true)] public string? Tab { get; set; }
        public bool ShowPending => Tab == TabPending;

        public List<SowingApprovalQueueRepository.Item> Items { get; private set; } = new();                 // waiting (shared)
        public List<SowingApprovalQueueRepository.ApprovedBatch> History { get; private set; } = new();       // my history / all (admin)
        public bool IsAdministrator { get; private set; }
        public SowingApprovalRules.Settings Settings { get; private set; } = new(DirectSowingRules.BedApprover.NotConfigured, false, false);
        public List<string> AreaNames { get; private set; } = new();
        public bool CanApprove { get; private set; }

        public async Task OnGetAsync()
        {
            CanApprove = await _featureAccess.CanAccessPageAsync(User, "/Production/ReadyConfirmation/Confirm");
            var queue = await _queueRepo.GetPendingAsync(
                Pages.Production.ReadyConfirmation.IndexModel.Viewer(User, CanApprove, _seedlingScope, _areaAccess),
                SowingApprovalQueueRepository.MotherPlantCategories,   // 2026-10-21: transplant batches AND Direct Sowings of Mother Plant Areas
                approvableOnly: SowingApprovalVisibility.PendingApprovableOnly(User));   // 2026-10-20: admin all; others only their own
            Items = queue.Items.OrderBy(i => i.Sowing.AreaName).ThenBy(i => i.Sowing.ExpectedReadyDate).ThenBy(i => i.Sowing.Id).ToList();
            Settings = queue.Settings;
            // identity and role from the authenticated principal only (SowingApprovalVisibility): own approvals, or all for admin
            IsAdministrator = SowingApprovalVisibility.IsAdministrator(User);
            History = await _queueRepo.GetApprovalHistoryAsync(SowingApprovalVisibility.History(User), motherPlantOnly: true);
            var full = _areaAccess.HasFullAreaAccess(User);
            AreaNames = (await _areaRepo.GetAllAreas())
                .Where(a => a.IsActive && a.AreaType == MotherPlantAreaSchema.MotherPlantAreaType && (full || _areaAccess.CanAccessArea(User, a.Id)))
                .Select(a => a.Name).OrderBy(n => n).ToList();
        }
    }
}
