using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Services;

namespace PlantStockManager.Pages
{
    // Company Home page ("/") -- the landing page after login for every user.
    //   * company identity (Precision Agritech Pvt. Ltd.) and a welcome for the user
    //   * quick access to the ERP modules THIS user may open (Services/HomeQuickAccess.cs,
    //     filtered by the same central permission rule the server enforces)
    //   * "my work": items waiting for this user or due soon, each shown only when the
    //     user may open the page it links to (unchanged from the former home dashboard)
    //   * company information that is already in the system: the Plant Types the nursery
    //     grows and its Areas (master data only -- no quantities)
    // It shows NO management figures: company-wide stock, booking and production KPIs
    // live only on the Administrator-only Management Dashboard (/ManagementDashboard/Index),
    // which this page merely links to for users who are allowed to open it.
    public class IndexModel : PageModel
    {
        private readonly DatabaseHelper _db;
        private readonly SeedSowingRepository _seedSowingRepo;
        private readonly PotBatchRepository _potBatchRepo;
        private readonly InternalTransferRepository _transferRepo;
        private readonly AreaAccessService _areaAccess;
        private readonly SeedlingAreaScope _seedlingScope;
        private readonly FeatureAccessService _featureAccess;
        private readonly PlantTypeRepository _plantTypeRepo;
        private readonly AreaRepository _areaRepo;
        private readonly SowingApprovalQueueRepository _approvalQueue;

        public IndexModel(DatabaseHelper db, SeedSowingRepository seedSowingRepo, PotBatchRepository potBatchRepo,
            InternalTransferRepository transferRepo, AreaAccessService areaAccess, SeedlingAreaScope seedlingScope, FeatureAccessService featureAccess,
            PlantTypeRepository plantTypeRepo, AreaRepository areaRepo, SowingApprovalQueueRepository approvalQueue)
        {
            _approvalQueue = approvalQueue;
            _db = db;
            _seedSowingRepo = seedSowingRepo;
            _potBatchRepo = potBatchRepo;
            _transferRepo = transferRepo;
            _areaAccess = areaAccess;
            _seedlingScope = seedlingScope;
            _featureAccess = featureAccess;
            _plantTypeRepo = plantTypeRepo;
            _areaRepo = areaRepo;
        }

        public const string CompanyName = "Precision Agritech Pvt. Ltd.";

        public sealed record Attention(string Text, int Count, string Page, string Css);
        public sealed record AreaGroup(string Label, List<string> Names);

        public string DisplayName { get; set; } = string.Empty;
        public List<string> RoleNames { get; set; } = new();
        public DateTime NowIndia { get; set; }
        public bool CanOpenManagementDashboard { get; set; }
        public List<HomeQuickAccess.Module> Modules { get; set; } = new();
        public List<Attention> Items { get; set; } = new();
        public List<string> PlantTypes { get; set; } = new();
        public List<AreaGroup> AreaGroups { get; set; } = new();

        public async Task OnGetAsync()
        {
            NowIndia = AppClock.Now;
            DisplayName = await GetDisplayNameAsync() ?? User.Identity?.Name ?? string.Empty;
            RoleNames = User.FindAll(AreaAccessService.RoleNameClaimType).Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().ToList();
            CanOpenManagementDashboard = await _featureAccess.CanAccessPageAsync(User, HomeQuickAccess.ManagementDashboardPage);
            Modules = await HomeQuickAccess.VisibleAsync(page => _featureAccess.CanAccessPageAsync(User, page));

            PlantTypes = (await _plantTypeRepo.GetAllPlantTypes())
                .Select(t => ManagementDashboardCalculations.CleanName(t.Name, ""))
                .Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
            AreaGroups = (await _areaRepo.GetAllAreas())
                .GroupBy(a => AreaTypeLabel(a.AreaType))
                .OrderBy(g => AreaTypeOrder(g.First().AreaType))
                .Select(g => new AreaGroup(g.Key, g.Select(a => ManagementDashboardCalculations.CleanName(a.Name, $"Area {a.Id}")).OrderBy(n => n).ToList()))
                .ToList();

            await LoadMyWorkAsync();
        }

        public static string AreaTypeLabel(string? areaType) => areaType switch
        {
            "MainOffice" => "Main Office",
            "MotherPlant" => "Production / Mother Plant Areas",
            "Outlet" => "Outlet",
            null or "" => "Other Areas",
            _ => areaType,
        };

        private static int AreaTypeOrder(string? areaType) => areaType switch
        {
            "MainOffice" => 0,
            "MotherPlant" => 1,
            "Outlet" => 2,
            _ => 3,
        };

        // Work waiting for THIS user or due soon -- the former home dashboard's
        // "needs attention" list, unchanged.
        private async Task LoadMyWorkAsync()
        {
            var today = DateTime.Today;

            if (await _featureAccess.CanAccessPageAsync(User, "/Production/ReadyConfirmation/Index"))
            {
                // 2026-10-19: the same pending list as Sowing Approvals (every batch in the user's Area scope). "Your approval"
                // counts the batches THIS user may approve now -- not only those with the user as assigned supervisor, which
                // left out every transplanted and Bed Sowing tray batch.
                var canApprove = await _featureAccess.CanAccessPageAsync(User, "/Production/ReadyConfirmation/Confirm");
                var queue = (await _approvalQueue.GetPendingAsync(
                    Production.ReadyConfirmation.IndexModel.Viewer(User, canApprove, _seedlingScope, _areaAccess))).Items;
                var open = queue.Select(i => i.Sowing).ToList();
                Add("Sowings waiting for your approval", queue.Count(i => i.Eligibility.CanApprove), "/Production/ReadyConfirmation/Index", "warning");
                Add("Sowings pending approval in your Areas", queue.Count, "/Production/ReadyConfirmation/Index", "secondary");
                Add("Sowings overdue for approval", open.Count(s => s.ExpectedReadyDate.HasValue && s.ExpectedReadyDate.Value.Date < today), "/Production/ReadyAlerts/Index", "danger");
                Add("Sowings ready within 3 days", open.Count(s => s.ExpectedReadyDate.HasValue && s.ExpectedReadyDate.Value.Date >= today
                                                                   && s.ExpectedReadyDate.Value.Date <= today.AddDays(SeedSowingRepository.DefaultReadySoonWindowDays)), "/Production/ReadyAlerts/Index", "info");
            }
            if (await _featureAccess.CanAccessPageAsync(User, "/Production/PotBatch/Index"))
            {
                var batches = _areaAccess.FilterByArea(User, await _potBatchRepo.GetAllAsync(PotBatchRules.InProduction), b => (int?)b.AreaId);
                Add("Pot batches waiting for your READY confirmation", batches.Count(b => PotBatchRules.CanConfirmReady(User, b.AreaId) && b.PottedQuantity > 0), "/Production/PotBatch/Index", "warning");
                Add("Pot batches overdue", batches.Count(b => b.Readiness(today) == PotBatchRules.DueOverdue), "/Production/PotBatch/Index", "danger");
                Add("Pot batches due today / within 7 days", batches.Count(b => b.Readiness(today) is PotBatchRules.DueToday or PotBatchRules.DueSoon), "/Production/PotBatch/Index", "info");
            }
            if (await _featureAccess.CanAccessPageAsync(User, "/Production/CuttingStock/PendingConfirmations"))
            {
                var pending = (await _transferRepo.GetPendingConfirmationsAsync())
                    .Where(t => _areaAccess.CanAccessRequiredArea(User, t.PendingConfirmationAreaId)).ToList();
                Add("Cutting deliveries to confirm", pending.Count, "/Production/CuttingStock/PendingConfirmations", "warning");
            }
        }

        private void Add(string text, int count, string page, string css)
        {
            if (count > 0)
                Items.Add(new Attention(text, count, page, css));
        }

        // The signed-in user's name as recorded in dbo.IMSUsers (the login claim carries
        // only the username).
        private async Task<string?> GetDisplayNameAsync()
        {
            var id = User.GetUserId();
            if (!id.HasValue)
                return null;
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand("SELECT Name FROM dbo.IMSUsers WHERE Id = @Id", conn);
            cmd.Parameters.AddWithValue("@Id", id.Value);
            var name = await cmd.ExecuteScalarAsync() as string;
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
    }
}
