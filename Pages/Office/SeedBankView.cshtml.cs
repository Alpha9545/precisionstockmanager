using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Data.SqlClient;
using PlantStockManager.Data;
using System.Web;

namespace PlantStockManager.Pages.Office
{
    public class SeedBankViewModel : PageModel
    {
        private readonly DatabaseHelper _db;
        public SeedBankViewModel(DatabaseHelper db) => _db = db;

        // Query-bound filters / sorting / paging
        [BindProperty(SupportsGet = true)] public int? PlantId { get; set; }
        [BindProperty(SupportsGet = true)] public int? SpeciesId { get; set; }
        [BindProperty(SupportsGet = true)] public int? SourceId { get; set; }
        [BindProperty(SupportsGet = true)] public DateTime? DateFrom { get; set; }
        [BindProperty(SupportsGet = true)] public DateTime? DateTo { get; set; }
        [BindProperty(SupportsGet = true)] public string SortBy { get; set; } = "date";  // plant|species|source|date
        [BindProperty(SupportsGet = true)] public string SortDir { get; set; } = "desc"; // asc|desc
        [BindProperty(SupportsGet = true)] public int PageIndex { get; set; } = 1;
        [BindProperty(SupportsGet = true)] public int PageSize { get; set; } = 20;

        // SelectLists for Tag Helpers
        public SelectList PlantSelect { get; set; } = default!;
        public SelectList SpeciesSelect { get; set; } = default!;
        public SelectList SourceSelect { get; set; } = default!;

        // Grid
        public List<RowVM> Rows { get; set; } = new();
        public int TotalCount { get; set; }
        public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));

        public async Task OnGetAsync()
        {
            await LoadSelectListsAsync();
            await LoadGridAsync();
        }

        private async Task LoadSelectListsAsync()
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();

            // Plants
            var plants = new List<(int id, string name)>();
            using (var cmd = new SqlCommand("SELECT Id, Name FROM dbo.PlantTypes ORDER BY Name;", conn))
            using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync()) plants.Add((r.GetInt32(0), r.GetString(1)));
            PlantSelect = new SelectList(plants.Select(x => new { Id = x.id, Name = x.name }), "Id", "Name", PlantId);

            // Species (if plant selected)
            var species = new List<(int id, string name)>();
            if (PlantId.HasValue)
            {
                using var cmdS = new SqlCommand("SELECT Id, Name FROM dbo.PlantSpecies WHERE PlantTypeId = @p ORDER BY Name;", conn);
                cmdS.Parameters.AddWithValue("@p", PlantId.Value);
                using var rs = await cmdS.ExecuteReaderAsync();
                while (await rs.ReadAsync()) species.Add((rs.GetInt32(0), rs.GetString(1)));
            }
            SpeciesSelect = new SelectList(species.Select(x => new { Id = x.id, Name = x.name }), "Id", "Name", SpeciesId);

            // Sources
            var sources = new List<(int id, string name)>();
            using (var cmd2 = new SqlCommand("SELECT Id, Name FROM dbo.SeedSources ORDER BY Name;", conn))
            using (var r2 = await cmd2.ExecuteReaderAsync())
                while (await r2.ReadAsync()) sources.Add((r2.GetInt32(0), r2.GetString(1)));
            SourceSelect = new SelectList(sources.Select(x => new { Id = x.id, Name = x.name }), "Id", "Name", SourceId);
        }

        private (string orderBySql, string extraNullOrder) BuildOrderBy()
        {
            var col = (SortBy ?? "date").ToLowerInvariant() switch
            {
                "plant" => "p.Name",
                "species" => "s.Name",
                "source" => "ss.Name",
                _ => "b.ReceivedOn"
            };
            var dir = (SortDir ?? "desc").ToLowerInvariant() == "asc" ? "ASC" : "DESC";

            // optional NULL ordering for source
            var extra = col == "ss.Name"
                ? (dir == "ASC"
                    ? ", CASE WHEN ss.Name IS NULL THEN 1 ELSE 0 END, ss.Name ASC"
                    : ", CASE WHEN ss.Name IS NULL THEN 0 ELSE 1 END, ss.Name DESC")
                : "";

            return ($"{col} {dir}", extra);
        }

        // Build filter name/value pairs (no SqlParameter instances here)
        private List<(string Name, object? Value)> BuildFilterPairs()
        {
            var pars = new List<(string, object?)>();
            if (PlantId.HasValue) pars.Add(("@plantId", PlantId.Value));
            if (SpeciesId.HasValue) pars.Add(("@speciesId", SpeciesId.Value));
            if (SourceId.HasValue) pars.Add(("@sourceId", SourceId.Value));
            if (DateFrom.HasValue) pars.Add(("@from", DateFrom.Value.Date));
            if (DateTo.HasValue) pars.Add(("@to", DateTo.Value.Date.AddDays(1)));
            return pars;
        }

        private string BuildWhereSql()
        {
            var where = new List<string> { "1=1" };
            if (PlantId.HasValue) where.Add("b.PlantId = @plantId");
            if (SpeciesId.HasValue) where.Add("b.SpeciesId = @speciesId");
            if (SourceId.HasValue) where.Add("b.SourceId = @sourceId");
            if (DateFrom.HasValue) where.Add("b.ReceivedOn >= @from");
            if (DateTo.HasValue) where.Add("b.ReceivedOn < @to");
            return string.Join(" AND ", where);
        }

        private async Task LoadGridAsync()
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();

            var whereSql = BuildWhereSql();
            var pairs = BuildFilterPairs();

            // ------- COUNT -------
            var countSql = $@"
SELECT COUNT(1)
FROM dbo.SeedCuttingBank b
JOIN dbo.PlantTypes p  ON p.Id   = b.PlantId
JOIN dbo.PlantSpecies s ON s.Id = b.SpeciesId
LEFT JOIN dbo.SeedSources ss ON ss.Id = b.SourceId
WHERE {whereSql};";

            using (var countCmd = new SqlCommand(countSql, conn))
            {
                foreach (var (name, value) in pairs)
                    countCmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

                TotalCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync());
            }

            // paging
            if (PageIndex < 1) PageIndex = 1;
            if (PageSize < 1) PageSize = 20;
            var skip = (PageIndex - 1) * PageSize;

            var (orderBySql, extraNullOrder) = BuildOrderBy();

            // ------- DATA -------
            var dataSql = $@"
SELECT b.Id, b.PlantId, b.SpeciesId, b.SourceId, b.BatchNo, b.Unit,
       b.Quantity, b.UtilizedQuantity, b.Available, b.ReceivedOn, b.Notes,
       p.Name, s.Name, ss.Name
FROM dbo.SeedCuttingBank b
JOIN dbo.PlantTypes p  ON p.Id   = b.PlantId
JOIN dbo.PlantSpecies s ON s.Id = b.SpeciesId
LEFT JOIN dbo.SeedSources ss ON ss.Id = b.SourceId
WHERE {whereSql}
ORDER BY {orderBySql} {extraNullOrder}
OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;";

            using var cmd = new SqlCommand(dataSql, conn);
            // add fresh parameters (do NOT reuse SqlParameter objects)
            foreach (var (name, value) in pairs)
                cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@skip", skip);
            cmd.Parameters.AddWithValue("@take", PageSize);

            Rows = new();
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
                    SourceName = r.IsDBNull(13) ? null : r.GetString(13),
                });
            }
        }

        // AJAX: species list by plant (used by JS)
        public async Task<JsonResult> OnGetSpeciesAsync(int plantId)
        {
            var list = new List<object>();
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand("SELECT Id, Name FROM dbo.PlantSpecies WHERE PlantTypeId = @p ORDER BY Name;", conn);
            cmd.Parameters.AddWithValue("@p", plantId);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new { speciesId = r.GetInt32(0), speciesName = r.GetString(1) });
            return new JsonResult(list);
        }

        // Pagination helpers
        public IEnumerable<int> PageNumbersToShow(int radius = 2)
        {
            var start = Math.Max(1, PageIndex - radius);
            var end = Math.Min(TotalPages, PageIndex + radius);
            return Enumerable.Range(start, end - start + 1);
        }

        public string BuildPageUrl(int targetPage)
        {
            var qs = HttpUtility.ParseQueryString(string.Empty);
            if (PlantId.HasValue) qs["PlantId"] = PlantId.Value.ToString();
            if (SpeciesId.HasValue) qs["SpeciesId"] = SpeciesId.Value.ToString();
            if (SourceId.HasValue) qs["SourceId"] = SourceId.Value.ToString();
            if (DateFrom.HasValue) qs["DateFrom"] = DateFrom.Value.ToString("yyyy-MM-dd");
            if (DateTo.HasValue) qs["DateTo"] = DateTo.Value.ToString("yyyy-MM-dd");
            qs["SortBy"] = SortBy;
            qs["SortDir"] = SortDir;
            qs["PageSize"] = PageSize.ToString();
            qs["PageIndex"] = Math.Min(Math.Max(targetPage, 1), TotalPages).ToString();
            return $"?{qs}";
        }

        // VMs
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
