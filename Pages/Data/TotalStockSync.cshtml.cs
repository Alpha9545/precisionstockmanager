using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PlantStockManager.Data;

namespace PlantStockManager.Pages.Data
{
    public class TotalStockSyncModel : PageModel
    {
        private readonly DatabaseHelper _db;

        public TotalStockSyncModel(DatabaseHelper db)
        {
            _db = db;
        }
        public List<PlantStockSummary> PlantSummary { get; set; } = new();

        public List<PlantSpeciesStockSummary> StockSummary { get; set; } = new();

        public async Task OnGetAsync()
        {

            await LoadPlantSummary();

            using var con = _db.GetConnection();
            string sql = @"WITH SowingAgg AS (
    SELECT
        PlantId,
        SpeciesId,
        SUM(CAST(SeedsPlanted AS BIGINT)) AS TotalSowingNotInInventory
    FROM SeedEntries
    WHERE ReadyForInventory = 0
    GROUP BY PlantId, SpeciesId
),
InventoryAgg AS (
    SELECT
        PlantId,
        SpeciesId,
        SUM(CAST(RemainingQuantity AS BIGINT)) AS InventoryRemaining
    FROM Inventory
    GROUP BY PlantId, SpeciesId
),
BookingAgg AS (
    SELECT
        PlantId,
        SpeciesId,
        SUM(CAST(Quantity AS BIGINT)) AS PendingBookings
    FROM Bookings
    WHERE Status = 'Pending'
    GROUP BY PlantId, SpeciesId
)
SELECT
    p.Id AS PlantId,
    sp.Id AS SpeciesId,
    p.Name AS PlantName,
    sp.Name AS SpeciesName,
    ISNULL(sa.TotalSowingNotInInventory, 0) AS TotalSowingNotInInventory,
    ISNULL(ia.InventoryRemaining, 0) AS InventoryRemaining,
    ISNULL(ba.PendingBookings, 0) AS PendingBookings
FROM PlantTypes p
JOIN PlantSpecies sp ON sp.PlantTypeId = p.Id
LEFT JOIN SowingAgg sa ON sa.PlantId = p.Id AND sa.SpeciesId = sp.Id
LEFT JOIN InventoryAgg ia ON ia.PlantId = p.Id AND ia.SpeciesId = sp.Id
LEFT JOIN BookingAgg ba ON ba.PlantId = p.Id AND ba.SpeciesId = sp.Id
WHERE
    ISNULL(sa.TotalSowingNotInInventory, 0) > 0
 OR ISNULL(ia.InventoryRemaining, 0) > 0
 OR ISNULL(ba.PendingBookings, 0) > 0
ORDER BY p.Name, sp.Name;";

         
            await con.OpenAsync();

            using var cmd = new SqlCommand(sql, con);
            using var r = await cmd.ExecuteReaderAsync();

            while (await r.ReadAsync())
            {
                StockSummary.Add(new PlantSpeciesStockSummary
                {
                    PlantId = r.GetInt32(0),
                    SpeciesId = r.GetInt32(1),
                    PlantName = r.GetString(2),
                    SpeciesName = r.GetString(3),
                    TotalSowingNotInInventory = r.GetFieldValue<long>(4),
                    InventoryRemaining = r.GetFieldValue<long>(5),
                    PendingBookings = r.GetFieldValue<long>(6)
                });
            }
        }



        private async Task LoadPlantSummary()
        {
            using var con = _db.GetConnection();

            string sql = @"
SELECT 
    p.Id,
    p.Name,

    ISNULL(se.TotalSowed, 0) AS TotalSowing,
    ISNULL(inv.TotalInventory, 0) AS InventoryRemaining,
    ISNULL(b.TotalBookings, 0) AS PendingBookings

FROM PlantTypes p

LEFT JOIN (
    SELECT PlantId, SUM(CAST(SeedsPlanted AS BIGINT)) AS TotalSowed
    FROM SeedEntries
    WHERE ReadyForInventory = 0
    GROUP BY PlantId
) se ON se.PlantId = p.Id

LEFT JOIN (
    SELECT PlantId, SUM(CAST(RemainingQuantity AS BIGINT)) AS TotalInventory
    FROM Inventory
    GROUP BY PlantId
) inv ON inv.PlantId = p.Id

LEFT JOIN (
    SELECT PlantId, SUM(CAST(Quantity AS BIGINT)) AS TotalBookings
    FROM Bookings
    WHERE Status = 'Pending'
    GROUP BY PlantId
) b ON b.PlantId = p.Id

ORDER BY p.Name;";

            await con.OpenAsync();
            using var cmd = new SqlCommand(sql, con);
            using var r = await cmd.ExecuteReaderAsync();

            while (await r.ReadAsync())
            {
                PlantSummary.Add(new PlantStockSummary
                {
                    PlantId = r.GetInt32(0),
                    PlantName = r.GetString(1),
                    TotalSowingNotInInventory = r.GetFieldValue<long>(2),
                    InventoryRemaining = r.GetFieldValue<long>(3),
                    PendingBookings = r.GetFieldValue<long>(4)
                });
            }
        }
    }
}




public class PlantSpeciesStockSummary
{
    public int PlantId { get; set; }
    public int SpeciesId { get; set; }

    public string PlantName { get; set; }
    public string SpeciesName { get; set; }

    public long TotalSowingNotInInventory { get; set; }
    public long InventoryRemaining { get; set; }
    public long PendingBookings { get; set; }

    public long NetAvailable => InventoryRemaining - PendingBookings;
}



public class PlantStockSummary
{
    public int PlantId { get; set; }
    public string PlantName { get; set; }

    public long TotalSowingNotInInventory { get; set; }
    public long InventoryRemaining { get; set; }
    public long PendingBookings { get; set; }
}
