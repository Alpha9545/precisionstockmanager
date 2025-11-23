using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PlantStockManager.Data;
using System.ComponentModel.DataAnnotations;

namespace PlantStockManager.Pages.Office
{
    public class SeedBankModel : PageModel
    {
        private readonly DatabaseHelper _db;
        public SeedBankModel(DatabaseHelper db) => _db = db;

        // Grid rows
        public List<RowVM> Rows { get; set; } = new();

        // Dropdown data
        public List<PlantVM> Plants { get; set; } = new();
        public List<SourceVM> Sources { get; set; } = new();

        [TempData] public string? WarningMessage { get; set; }
        [TempData] public string? SuccessMessage { get; set; }

        // For default input
        public string DefaultReceivedOnLocal =>
            DateTime.Now.ToString("yyyy-MM-ddTHH:mm");

        // ----- GET -----
        public async Task OnGetAsync()
        {
            await LoadLookupAsync();
            await LoadRowsAsync();
        }

        private async Task LoadLookupAsync()
        {
            Plants = new();
            Sources = new();

            using var conn = _db.GetConnection();
            await conn.OpenAsync();

            // Plants
            using (var cmd = new SqlCommand("SELECT Id, Name FROM dbo.PlantTypes ORDER BY Name", conn))
            using (var r = await cmd.ExecuteReaderAsync())
            {
                while (await r.ReadAsync())
                {
                    Plants.Add(new PlantVM { PlantId = r.GetInt32(0), PlantName = r.GetString(1) });
                }
            }

            // Sources
            using (var cmd = new SqlCommand("SELECT Id, Name FROM dbo.SeedSources ORDER BY Name", conn))
            using (var r = await cmd.ExecuteReaderAsync())
            {
                while (await r.ReadAsync())
                {
                    Sources.Add(new SourceVM { SourceId = r.GetInt32(0), SourceName = r.GetString(1) });
                }
            }
        }

        private async Task LoadRowsAsync()
        {
            Rows = new();
            using var conn = _db.GetConnection();
            await conn.OpenAsync();

            var sql = @"
SELECT b.Id, b.PlantId, b.SpeciesId, b.SourceId, b.BatchNo, b.Unit,
       b.Quantity, b.UtilizedQuantity, b.Available, b.ReceivedOn, b.Notes,
       p.Name, s.Name, ss.Name
FROM dbo.SeedCuttingBank b
JOIN dbo.PlantTypes p     ON p.Id   = b.PlantId
JOIN dbo.PlantSpecies s   ON s.Id = b.SpeciesId
LEFT JOIN dbo.SeedSources ss ON ss.Id = b.SourceId WHERE b.UtilizedQuantity = 0
ORDER BY b.ReceivedOn DESC, b.Id DESC;";
            using var cmd = new SqlCommand(sql, conn);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                Rows.Add(new RowVM
                {
                    Id = r.GetInt32(0),
                    PlantId = r.GetInt32(1),
                    SpeciesId = r.GetInt32(2),
                    SourceId = r.IsDBNull(3) ? (int?)null : r.GetInt32(3),
                    BatchNo = r.IsDBNull(4) ? null : r.GetString(4),
                    Unit = r.GetString(5),
                    Quantity = r.GetInt32(6),
                    UtilizedQuantity = r.GetInt32(7),
                    Available = r.GetInt32(8),
                    ReceivedOnUtc = r.GetDateTime(9),
                    Notes = r.IsDBNull(10) ? null : r.GetString(10),
                    PlantName = r.GetString(11),
                    SpeciesName = r.GetString(12),
                    SourceName = r.IsDBNull(13) ? null : r.GetString(13)
                });
            }
        }

        // ----- JSON: get species for a plant -----
        public async Task<JsonResult> OnGetSpeciesAsync(int plantId)
        {
            var list = new List<object>();
            using var conn = _db.GetConnection();
            await conn.OpenAsync();

            var sql = "SELECT Id, Name FROM dbo.PlantSpecies WHERE PlantTypeId = @p ORDER BY Name;";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@p", plantId);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                list.Add(new { speciesId = r.GetInt32(0), speciesName = r.GetString(1) });
            }
            return new JsonResult(list);
        }

        // ----- ADD -----
        public class AddVM
        {
            [Range(1, int.MaxValue, ErrorMessage = "Select a Plant.")]
            public int? PlantId { get; set; }
            [Range(1, int.MaxValue, ErrorMessage = "Select a Species.")]
            public int? SpeciesId { get; set; }
            public int? SourceId { get; set; }
            [MaxLength(50)] public string? BatchNo { get; set; }
            [Required, MaxLength(20)] public string Unit { get; set; } = "pcs";
            [Range(0, int.MaxValue)] public int Quantity { get; set; } = 0;
            [DataType(DataType.DateTime)] public DateTime? ReceivedOn { get; set; }
            [MaxLength(200)] public string? Notes { get; set; }
        }

        [BindProperty] public AddVM NewItem { get; set; } = new();

        public async Task<IActionResult> OnPostAddAsync()
        {
            if (!ModelState.IsValid)
            {
                await OnGetAsync();
                WarningMessage = "Please fix validation errors.";
                return Page();
            }

            using var conn = _db.GetConnection();
            await conn.OpenAsync();

            var sql = @"
INSERT INTO dbo.SeedCuttingBank
(PlantId, SpeciesId, SourceId, BatchNo, Unit, Quantity, UtilizedQuantity, ReceivedOn, Notes)
VALUES (@PlantId, @SpeciesId, @SourceId, @BatchNo, @Unit, @Qty, 0, @Recv, @Notes);";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@PlantId", NewItem.PlantId!.Value);
            cmd.Parameters.AddWithValue("@SpeciesId", NewItem.SpeciesId!.Value);
            cmd.Parameters.AddWithValue("@SourceId", (object?)NewItem.SourceId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@BatchNo", (object?)NewItem.BatchNo ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Unit", NewItem.Unit);
            cmd.Parameters.AddWithValue("@Qty", NewItem.Quantity);
            cmd.Parameters.AddWithValue("@Recv", (object?)(NewItem.ReceivedOn ?? DateTime.UtcNow) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Notes", (object?)NewItem.Notes ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();

            SuccessMessage = "Lot added to Seed/Cuttings bank.";
            return RedirectToPage();
        }

        // ----- EDIT -----
        public class EditVM : AddVM
        {
            [Required] public int Id { get; set; }
            [Range(0, int.MaxValue)] public int UtilizedQuantity { get; set; } = 0; // allow edit with guard
        }

        [BindProperty] public EditVM EditItem { get; set; } = new();

        public async Task<IActionResult> OnPostEditAsync()
        {
            if (!ModelState.IsValid)
            {
                await OnGetAsync();
                WarningMessage = "Please fix validation errors.";
                return Page();
            }

            // Guard: UtilizedQuantity cannot exceed Quantity
            if (EditItem.UtilizedQuantity > EditItem.Quantity)
            {
                await OnGetAsync();
                WarningMessage = "Utilized quantity cannot exceed Quantity.";
                return Page();
            }

            using var conn = _db.GetConnection();
            await conn.OpenAsync();

            var sql = @"
UPDATE dbo.SeedCuttingBank
SET PlantId = @PlantId,
    SpeciesId = @SpeciesId,
    SourceId = @SourceId,
    BatchNo = @BatchNo,
    Unit = @Unit,
    Quantity = @Qty,
    UtilizedQuantity = @Util,
    ReceivedOn = @Recv,
    Notes = @Notes
WHERE Id = @Id;";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", EditItem.Id);
            cmd.Parameters.AddWithValue("@PlantId", EditItem.PlantId!.Value);
            cmd.Parameters.AddWithValue("@SpeciesId", EditItem.SpeciesId!.Value);
            cmd.Parameters.AddWithValue("@SourceId", (object?)EditItem.SourceId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@BatchNo", (object?)EditItem.BatchNo ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Unit", EditItem.Unit);
            cmd.Parameters.AddWithValue("@Qty", EditItem.Quantity);
            cmd.Parameters.AddWithValue("@Util", EditItem.UtilizedQuantity);
            cmd.Parameters.AddWithValue("@Recv", (object?)(EditItem.ReceivedOn ?? DateTime.UtcNow) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Notes", (object?)EditItem.Notes ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();

            SuccessMessage = "Lot updated.";
            return RedirectToPage();
        }

        // ----- DELETE -----
        public async Task<IActionResult> OnPostDeleteAsync(int id)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();

            // block delete if any tx exists
            var hasTxCmd = new SqlCommand("SELECT 1 FROM dbo.SeedCuttingTx WHERE BankId = @Id;", conn);
            hasTxCmd.Parameters.AddWithValue("@Id", id);
            var hasTx = await hasTxCmd.ExecuteScalarAsync();
            if (hasTx != null)
            {
                WarningMessage = "Cannot delete: transactions exist for this lot. Revert/adjust transactions instead.";
                return RedirectToPage();
            }

            var del = new SqlCommand("DELETE FROM dbo.SeedCuttingBank WHERE Id = @Id;", conn);
            del.Parameters.AddWithValue("@Id", id);
            await del.ExecuteNonQueryAsync();

            SuccessMessage = "Lot deleted.";
            return RedirectToPage();
        }

        // ----- VMs -----
        public class PlantVM { public int PlantId { get; set; } public string PlantName { get; set; } = ""; }
        public class SourceVM { public int SourceId { get; set; } public string SourceName { get; set; } = ""; }

        public class RowVM
        {
            public int Id { get; set; }
            public int PlantId { get; set; }
            public int SpeciesId { get; set; }
            public int? SourceId { get; set; }
            public string? BatchNo { get; set; }
            public string Unit { get; set; } = "pcs";
            public int Quantity { get; set; }
            public int UtilizedQuantity { get; set; }
            public int Available { get; set; }
            public DateTime ReceivedOnUtc { get; set; }
            public string? Notes { get; set; }

            public string PlantName { get; set; } = "";
            public string SpeciesName { get; set; } = "";
            public string? SourceName { get; set; }
        }
    }
}
