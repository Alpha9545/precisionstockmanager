using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PlantStockManager.Data;

namespace PlantStockManager.Pages
{
    
    public class IndexModel : PageModel
    {
        private readonly DatabaseHelper _db;

        public IndexModel(DatabaseHelper db)
        {
            _db = db;
        }

        public List<PlantTypeStats> Stats { get; set; } = new();

        public void OnGet()
        {
            using var connection = _db.GetConnection();
            connection.Open();

            string query = @"
WITH SeedStats AS (
    SELECT 
        PlantId,
        COUNT(*) AS ActiveSowingBatches,
        SUM(ISNULL(SeedsPlanted, 0)) AS ActiveSowingQuantity
    FROM SeedEntries
    WHERE ReadyForInventory = 0
    GROUP BY PlantId
),
InventoryStats AS (
    SELECT 
        PlantId,
        SUM(ISNULL(RemainingQuantity, 0)) AS TotalReadyStock
    FROM Inventory
    WHERE IsUtilized = 'N'
    GROUP BY PlantId
),
BookingStats AS (
    SELECT 
        PlantId,
        COUNT(*) AS PendingBookings,
        SUM(ISNULL(Quantity, 0)) AS PendingBookingQuantity
    FROM Bookings
    WHERE Status = 'Pending'
    GROUP BY PlantId
)
SELECT 
    pt.Name AS PlantType,
    ISNULL(ss.ActiveSowingBatches, 0) AS ActiveSowingBatches,
    ISNULL(ss.ActiveSowingQuantity, 0) AS ActiveSowingQuantity,
    ISNULL(inv.TotalReadyStock, 0) AS TotalReadyStock,
    ISNULL(bs.PendingBookings, 0) AS PendingBookings,
    ISNULL(bs.PendingBookingQuantity, 0) AS PendingBookingQuantity
FROM PlantTypes pt
LEFT JOIN SeedStats ss ON pt.Id = ss.PlantId
LEFT JOIN InventoryStats inv ON pt.Id = inv.PlantId
LEFT JOIN BookingStats bs ON pt.Id = bs.PlantId
ORDER BY pt.Name;
";
            using SqlCommand cmd = new SqlCommand(query, connection);
            using SqlDataReader reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                Stats.Add(new PlantTypeStats
                {
                    PlantType = reader["PlantType"].ToString(),
                    ActiveBatches = Convert.ToInt32(reader["ActiveSowingBatches"]),
                    ActiveBatchQuantity = Convert.ToInt32(reader["ActiveSowingQuantity"]),
                    ReadyStock = Convert.ToInt32(reader["TotalReadyStock"]),
                    PendingBookings = Convert.ToInt32(reader["PendingBookings"]),
                    PendingBookingQuantity = Convert.ToInt32(reader["PendingBookingQuantity"])
                });

            }
        }

        public class PlantTypeStats
        {
            public string PlantType { get; set; }
            public int ActiveBatches { get; set; }
            public int ActiveBatchQuantity { get; set; }
            public int ReadyStock { get; set; }
            public int PendingBookings { get; set; }
            public int PendingBookingQuantity { get; set; }
        }

    }
}
