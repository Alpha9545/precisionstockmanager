using Microsoft.Data.SqlClient;

namespace PlantStockManager.Data
{
    // Phase D: Stock History over the CURRENT stock ledgers -- one list of
    // movements for every stock kind, so the report can show
    // Opening + Incoming - Outgoing = Balance per stock item.
    // Reservation rows (booking reservations on Ready Stock / Potted Plant
    // Stock) are not stock movements and are left out.
    public class StockHistoryRepository
    {
        public const string Seed = "Seed";
        public const string Cutting = "Cutting";
        public const string ReadySeedling = "ReadySeedling";
        public const string EmptyPot = "EmptyPot";
        public const string PottedPlant = "PottedPlant";

        public static readonly IReadOnlyDictionary<string, string> StockTypes = new Dictionary<string, string>
        {
            [Seed] = "Seed Stock",
            [Cutting] = "Cutting Stock",
            [ReadySeedling] = "Ready Seedling Stock",
            [EmptyPot] = "Empty Pot Stock",
            [PottedPlant] = "Potted Plant Stock",
        };

        private readonly DatabaseHelper _dbHelper;

        public StockHistoryRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        public sealed class Movement
        {
            public string ItemKey { get; set; } = string.Empty;   // stock type + id
            public string Item { get; set; } = string.Empty;      // what (variety / lot / pot size / batch)
            public string? Variety { get; set; }
            public int? AreaId { get; set; }
            public string? AreaName { get; set; }
            public DateTime Date { get; set; }
            public string TransactionType { get; set; } = string.Empty;
            public decimal Quantity { get; set; }
            public string? Reference { get; set; }
            public string? UserName { get; set; }
            public string? Remarks { get; set; }
        }

        // All movements of one stock kind up to (and including) the 'to' date
        // -- everything before 'from' makes up the opening balance.
        public async Task<List<Movement>> GetMovementsAsync(string stockType, DateTime to, int? areaId = null, string? search = null)
        {
            var sql = stockType switch
            {
                Seed => @"
SELECT 'S' + CAST(s.Id AS varchar(12)) AS ItemKey,
       RTRIM(ps.Name) + ' - lot ' + CASE WHEN s.BatchNo = '' THEN '(none)' ELSE s.BatchNo END AS Item, RTRIM(ps.Name) AS Variety,
       s.AreaId, a.Name AS AreaName, t.TransactionDate, t.TransactionType, t.Quantity,
       ISNULL(t.ReferenceType, '') + ISNULL(' #' + CAST(t.ReferenceId AS varchar(12)), '') AS Reference, u.Name AS UserName, t.Remarks
FROM dbo.SeedStockTransactions t
INNER JOIN dbo.SeedStock s ON s.Id = t.SeedStockId
INNER JOIN dbo.PlantSpecies ps ON ps.Id = s.SpeciesId
INNER JOIN dbo.Area a ON a.Id = s.AreaId
LEFT JOIN dbo.IMSUsers u ON u.Id = t.UserId",
                Cutting => @"
SELECT 'C' + CAST(c.Id AS varchar(12)) AS ItemKey, RTRIM(ps.Name) + ISNULL(' (' + ps.Color + ')', '') AS Item, RTRIM(ps.Name) AS Variety,
       c.AreaId, a.Name AS AreaName, t.TransactionDate, t.TransactionType, t.Quantity,
       ISNULL(t.ReferenceType, '') + ISNULL(' #' + CAST(t.ReferenceId AS varchar(12)), '') AS Reference, u.Name AS UserName, t.Remarks
FROM dbo.CuttingStockTransactions t
INNER JOIN dbo.CuttingStock c ON c.Id = t.CuttingStockId
INNER JOIN dbo.PlantSpecies ps ON ps.Id = c.SpeciesId
INNER JOIN dbo.Area a ON a.Id = c.AreaId
LEFT JOIN dbo.IMSUsers u ON u.Id = t.UserId",
                ReadySeedling => @"
SELECT 'R' + CAST(r.Id AS varchar(12)) AS ItemKey,
       sw.SowingCode + ' - ' + RTRIM(ps.Name) + ' (' + r.CavityType + ')' AS Item, RTRIM(ps.Name) AS Variety,
       r.AreaId, a.Name AS AreaName, t.TransactionDate, t.TransactionType, t.Quantity,
       ISNULL(t.ReferenceType, '') + ISNULL(' #' + CAST(t.ReferenceId AS varchar(12)), '') AS Reference, u.Name AS UserName, t.Remarks
FROM dbo.ReadyStockTransactions t
INNER JOIN dbo.ReadyStock r ON r.Id = t.ReadyStockId
INNER JOIN dbo.SeedSowings sw ON sw.Id = r.SeedSowingId
INNER JOIN dbo.PlantSpecies ps ON ps.Id = r.SpeciesId
INNER JOIN dbo.Area a ON a.Id = r.AreaId
LEFT JOIN dbo.IMSUsers u ON u.Id = t.UserId
WHERE t.TransactionType NOT IN ('Reservation', 'ReservationRelease')",
                EmptyPot => @"
SELECT 'E' + CAST(e.Id AS varchar(12)) AS ItemKey, e.PotSize AS Item, CAST(NULL AS nvarchar(200)) AS Variety,
       e.AreaId, a.Name AS AreaName, t.TransactionDate, t.TransactionType, t.Quantity,
       ISNULL(t.ReferenceType, '') + ISNULL(' #' + CAST(t.ReferenceId AS varchar(12)), '') AS Reference, u.Name AS UserName, t.Remarks
FROM dbo.EmptyPotInventoryTransactions t
INNER JOIN dbo.EmptyPotInventory e ON e.Id = t.EmptyPotInventoryId
LEFT JOIN dbo.Area a ON a.Id = e.AreaId
LEFT JOIN dbo.IMSUsers u ON u.Id = t.UserId",
                PottedPlant => @"
SELECT 'P' + CAST(p.Id AS varchar(12)) AS ItemKey, RTRIM(ps.Name) + ISNULL(' (' + ps.Color + ')', '') + ' - ' + p.PotSize AS Item, RTRIM(ps.Name) AS Variety,
       p.AreaId, a.Name AS AreaName, t.TransactionDate, t.TransactionType, t.Quantity,
       ISNULL(t.ReferenceType, '') + ISNULL(' #' + CAST(t.ReferenceId AS varchar(12)), '') AS Reference, u.Name AS UserName, t.Remarks
FROM dbo.PottedPlantStockTransactions t
INNER JOIN dbo.PottedPlantStock p ON p.Id = t.PottedPlantStockId
INNER JOIN dbo.PlantSpecies ps ON ps.Id = p.SpeciesId
LEFT JOIN dbo.Area a ON a.Id = p.AreaId
LEFT JOIN dbo.IMSUsers u ON u.Id = t.UserId
WHERE t.TransactionType NOT IN ('Reservation', 'ReservationRelease')",
                _ => throw new ArgumentOutOfRangeException(nameof(stockType))
            };

            var wrapped = $@"
SELECT * FROM ({sql}) m
WHERE m.TransactionDate < @EndExclusive
  AND (@AreaId IS NULL OR m.AreaId = @AreaId)
  AND (@Search IS NULL OR m.Item LIKE @Like OR ISNULL(m.AreaName, '') LIKE @Like)
ORDER BY m.TransactionDate, m.ItemKey";

            var list = new List<Movement>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(wrapped, conn);
            var term = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
            cmd.Parameters.AddWithValue("@EndExclusive", to.Date.AddDays(1));
            cmd.Parameters.AddWithValue("@AreaId", (object?)areaId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Search", (object?)term ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Like", term == null ? DBNull.Value : "%" + term.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%");
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                list.Add(new Movement
                {
                    ItemKey = r.GetString(r.GetOrdinal("ItemKey")),
                    Item = r.GetString(r.GetOrdinal("Item")),
                    Variety = r.IsDBNull(r.GetOrdinal("Variety")) ? null : r.GetString(r.GetOrdinal("Variety")),
                    AreaId = r.IsDBNull(r.GetOrdinal("AreaId")) ? null : r.GetInt32(r.GetOrdinal("AreaId")),
                    AreaName = r.IsDBNull(r.GetOrdinal("AreaName")) ? null : r.GetString(r.GetOrdinal("AreaName")),
                    Date = r.GetDateTime(r.GetOrdinal("TransactionDate")),
                    TransactionType = r.GetString(r.GetOrdinal("TransactionType")),
                    Quantity = r.GetDecimal(r.GetOrdinal("Quantity")),
                    Reference = r.IsDBNull(r.GetOrdinal("Reference")) ? null : r.GetString(r.GetOrdinal("Reference")),
                    UserName = r.IsDBNull(r.GetOrdinal("UserName")) ? null : r.GetString(r.GetOrdinal("UserName")),
                    Remarks = r.IsDBNull(r.GetOrdinal("Remarks")) ? null : r.GetString(r.GetOrdinal("Remarks"))
                });
            }
            return list;
        }
    }
}
