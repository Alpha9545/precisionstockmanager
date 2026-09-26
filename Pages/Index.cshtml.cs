using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Services;

namespace PlantStockManager.Pages
{
    // Home dashboard over the CURRENT workflow (Phase D). The former version
    // read the retired seed pipeline (dbo.SeedEntries / dbo.Inventory).
    //   * per crop: active sowings, ready seedlings available, pending bookings
    //   * "needs attention": work waiting for THIS user or due soon
    public class IndexModel : PageModel
    {
        private readonly DatabaseHelper _db;
        private readonly SeedSowingRepository _seedSowingRepo;
        private readonly PotBatchRepository _potBatchRepo;
        private readonly InternalTransferRepository _transferRepo;
        private readonly AreaAccessService _areaAccess;
        private readonly SeedlingAreaScope _seedlingScope;
        private readonly FeatureAccessService _featureAccess;

        public IndexModel(DatabaseHelper db, SeedSowingRepository seedSowingRepo, PotBatchRepository potBatchRepo,
            InternalTransferRepository transferRepo, AreaAccessService areaAccess, SeedlingAreaScope seedlingScope, FeatureAccessService featureAccess)
        {
            _db = db;
            _seedSowingRepo = seedSowingRepo;
            _potBatchRepo = potBatchRepo;
            _transferRepo = transferRepo;
            _areaAccess = areaAccess;
            _seedlingScope = seedlingScope;
            _featureAccess = featureAccess;
        }

        public sealed class CropRow
        {
            public string Crop { get; set; } = string.Empty;
            public int ActiveSowings { get; set; }
            public decimal ActiveSown { get; set; }
            public decimal ReadyAvailable { get; set; }
            public int PendingBookings { get; set; }
            public decimal PendingBookingQuantity { get; set; }
        }

        public sealed record Attention(string Text, int Count, string Page, string Css);

        public List<CropRow> Crops { get; set; } = new();
        public List<Attention> Items { get; set; } = new();

        public async Task OnGetAsync()
        {
            await LoadCropsAsync();

            var me = User.GetUserId();
            var today = DateTime.Today;

            if (await _featureAccess.CanAccessPageAsync(User, "/Production/ReadyConfirmation/Index"))
            {
                var open = (await _seedSowingRepo.GetReadyForConfirmationAsync()).Where(s => _seedlingScope.CanAccessArea(User, s.AreaId)).ToList();
                Add("Sowings waiting for your approval", open.Count(s => me.HasValue && s.SupervisorId == me), "/Production/ReadyConfirmation/Index", "warning");
                Add("Sowings overdue for approval", open.Count(s => s.ExpectedReadyDate.HasValue && s.ExpectedReadyDate.Value.Date < today), "/Production/ReadyAlerts/Index", "danger");
                Add("Sowings ready within 3 days", open.Count(s => s.ExpectedReadyDate.HasValue && s.ExpectedReadyDate.Value.Date >= today
                                                                   && s.ExpectedReadyDate.Value.Date <= today.AddDays(SeedSowingRepository.DefaultReadySoonWindowDays)), "/Production/ReadyAlerts/Index", "info");
            }
            if (await _featureAccess.CanAccessPageAsync(User, "/Production/PotBatch/Index"))
            {
                var batches = _areaAccess.FilterByArea(User, await _potBatchRepo.GetAllAsync(PotBatchRules.InProduction), b => (int?)b.AreaId);
                Add("Pot batches waiting for your READY confirmation", batches.Count(b => me.HasValue && b.SupervisorId == me && b.PottedQuantity > 0), "/Production/PotBatch/Index", "warning");
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

        private async Task LoadCropsAsync()
        {
            const string sql = @"
SELECT pt.Name AS Crop,
       ISNULL(sw.Cnt, 0) AS ActiveSowings, ISNULL(sw.Sown, 0) AS ActiveSown,
       ISNULL(rs.Available, 0) AS ReadyAvailable,
       ISNULL(bk.Cnt, 0) AS PendingBookings, ISNULL(bk.Qty, 0) AS PendingBookingQuantity
FROM dbo.PlantTypes pt
OUTER APPLY (SELECT COUNT(*) AS Cnt, SUM(s.QuantitySown) AS Sown
             FROM dbo.SeedSowings s INNER JOIN dbo.PlantSpecies ps ON ps.Id = s.SpeciesId
             WHERE ps.PlantTypeId = pt.Id AND s.Status = 'Sown') sw
OUTER APPLY (SELECT SUM(r.Quantity - r.ReservedQuantity - r.DispatchedQuantity) AS Available
             FROM dbo.ReadyStock r INNER JOIN dbo.PlantSpecies ps ON ps.Id = r.SpeciesId
             WHERE ps.PlantTypeId = pt.Id) rs
OUTER APPLY (SELECT COUNT(*) AS Cnt, SUM(b.Quantity - b.DispatchedQuantity) AS Qty
             FROM dbo.Bookings b
             WHERE b.PlantId = pt.Id AND b.Status = 'Pending') bk
ORDER BY pt.Name";
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(sql, conn);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                Crops.Add(new CropRow
                {
                    Crop = r.GetString(0),
                    ActiveSowings = r.GetInt32(1),
                    ActiveSown = r.GetDecimal(2),
                    ReadyAvailable = r.GetDecimal(3),
                    PendingBookings = r.GetInt32(4),
                    PendingBookingQuantity = Convert.ToDecimal(r.GetValue(5))
                });
            }
        }
    }
}
