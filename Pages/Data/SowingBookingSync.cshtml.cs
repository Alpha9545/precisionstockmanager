using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using System.Text.Json;

namespace PlantStockManager.Pages.Data
{
    public class SowingBookingSyncModel : PageModel
    {
        private readonly IConfiguration _configuration;

        public SowingBookingSyncModel(IConfiguration configuration)
        {
            _configuration = configuration;
            WeeklyData = new List<WeeklyData>();
        }

        [BindProperty(SupportsGet = true)]
        public int PlantId { get; set; }

        [BindProperty(SupportsGet = true)]
        public int SpeciesId { get; set; }


        public string PlantName { get; set; }
        public string SpeciesName { get; set; }


        public List<WeeklyData> WeeklyData { get; set; }
        public DashboardSummary Summary { get; set; }

        public async Task<IActionResult> OnGetAsync()
        {
            if (PlantId <= 0 || SpeciesId <= 0)
                return BadRequest("Invalid PlantId or SpeciesId");

            await LoadPlantAndSpeciesNames();   // ✅ ADD THIS

            await LoadWeeklyDataFromDatabase();
            CalculateSummary();
            return Page();
        }

        private async Task LoadPlantAndSpeciesNames()
        {
            string connectionString = _configuration.GetConnectionString("DefaultConnection");

            using (SqlConnection con = new SqlConnection(connectionString))
            {
                await con.OpenAsync();

                string sql = @"
SELECT 
    p.Name AS PlantName,
    s.Name AS SpeciesName
FROM PlantTypes p
INNER JOIN PlantSpecies s ON s.PlantTypeId = p.Id
WHERE p.Id = @PlantId
  AND s.Id = @SpeciesId";

                using (SqlCommand cmd = new SqlCommand(sql, con))
                {
                    cmd.Parameters.AddWithValue("@PlantId", PlantId);
                    cmd.Parameters.AddWithValue("@SpeciesId", SpeciesId);

                    using (SqlDataReader r = await cmd.ExecuteReaderAsync())
                    {
                        if (await r.ReadAsync())
                        {
                            PlantName = r["PlantName"].ToString();
                            SpeciesName = r["SpeciesName"].ToString();
                        }
                    }
                }
            }
        }


        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> OnGetSowingDetails( string weekStart, string weekEnd)
        {
            try
            {
                if (!DateTime.TryParse(weekStart, out DateTime startDate) ||
                    !DateTime.TryParse(weekEnd, out DateTime endDate))
                {
                    return BadRequest("Invalid date format");
                }

                var sowingDetails = await GetSowingDetails(startDate, endDate);
                return new JsonResult(sowingDetails);
            }
            catch (Exception ex)
            {
                // Log the exception
                Console.WriteLine($"Error in OnGetSowingDetails: {ex.Message}");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // Booking details handler - make it public and add IgnoreAntiforgeryToken
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> OnGetBookingDetails(int weekNumber, string weekStart, string weekEnd)
        {
            try
            {
                if (!DateTime.TryParse(weekStart, out DateTime startDate) ||
                    !DateTime.TryParse(weekEnd, out DateTime endDate))
                {
                    return BadRequest("Invalid date format");
                }

                var bookingDetails = await GetBookingDetails(startDate, endDate);
                return new JsonResult(bookingDetails);
            }
            catch (Exception ex)
            {
                // Log the exception
                Console.WriteLine($"Error in OnGetBookingDetails: {ex.Message}");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // Separate method to get sowing details
        private async Task<List<SowingDetail>> GetSowingDetails(DateTime weekStart, DateTime weekEnd)
        {
            var sowingDetails = new List<SowingDetail>();

            string connectionString = _configuration.GetConnectionString("DefaultConnection");

            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();

                // Query for sowing ready in this week (harvest ready)
                string sowingReadyQuery = @"
SELECT 
    se.Id,
    se.SeedingDate,
    se.SeedsPlanted,
    DATEADD(DAY, 21, se.SeedingDate) AS ReadyDate,
    p.Name AS PlantName,
    sp.Name AS SpeciesName,
    se.SeedsPlanted,
    'Ready This Week' AS SowingType
FROM SeedEntries se
INNER JOIN PlantTypes p ON se.PlantId = p.Id
INNER JOIN PlantSpecies sp ON se.SpeciesId = sp.Id
WHERE se.ReadyForInventory = 0 
    AND se.PlantId = @PlantId
AND se.SpeciesId = @SpeciesId
    AND CAST(DATEADD(DAY, 21, se.SeedingDate) AS DATE) >= @WeekStart
    AND CAST(DATEADD(DAY, 21, se.SeedingDate) AS DATE) <= @WeekEnd
ORDER BY se.SeedingDate";

                using (SqlCommand command = new SqlCommand(sowingReadyQuery, connection))
                {
                    command.Parameters.AddWithValue("@PlantId", PlantId);
                    command.Parameters.AddWithValue("@SpeciesId", SpeciesId);
                    command.Parameters.AddWithValue("@WeekStart", weekStart);
                    command.Parameters.AddWithValue("@WeekEnd", weekEnd);

                    using (SqlDataReader reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var detail = new SowingDetail
                            {
                                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                                SeedingDate = reader.GetDateTime(reader.GetOrdinal("SeedingDate")),
                                SeedsPlanted = reader.GetInt32(reader.GetOrdinal("SeedsPlanted")),
                                ReadyDate = reader.GetDateTime(reader.GetOrdinal("ReadyDate")),
                                PlantName = reader.GetString(reader.GetOrdinal("PlantName")),
                                SpeciesName = reader.GetString(reader.GetOrdinal("SpeciesName")),
                                Notes = reader.IsDBNull(reader.GetOrdinal("SowingType"))
                                    ? string.Empty
                                    : reader.GetString(reader.GetOrdinal("SowingType")),
                                SowingType = reader.GetString(reader.GetOrdinal("SowingType"))
                            };
                            sowingDetails.Add(detail);
                        }
                    }
                }

                // Query for sowing planted in this week
                string sowingPlantedQuery = @"
SELECT 
    se.Id,
    se.SeedingDate,
    se.SeedsPlanted,
    DATEADD(DAY, 21, se.SeedingDate) AS ReadyDate,
    p.Name AS PlantName,
    sp.Name AS SpeciesName,
    se.SeedsPlanted,
    'Planted This Week' AS SowingType
FROM SeedEntries se
INNER JOIN PlantTypes p ON se.PlantId = p.Id
INNER JOIN PlantSpecies sp ON se.SpeciesId = sp.Id
WHERE se.ReadyForInventory = 0 
     AND se.PlantId = @PlantId
AND se.SpeciesId = @SpeciesId
    AND CAST(se.SeedingDate AS DATE) >= @WeekStart
    AND CAST(se.SeedingDate AS DATE) <= @WeekEnd
ORDER BY se.SeedingDate";

                using (SqlCommand command = new SqlCommand(sowingPlantedQuery, connection))
                {
                    command.Parameters.AddWithValue("@PlantId", PlantId);
                    command.Parameters.AddWithValue("@SpeciesId", SpeciesId);
                    command.Parameters.AddWithValue("@WeekStart", weekStart);
                    command.Parameters.AddWithValue("@WeekEnd", weekEnd);

                    using (SqlDataReader reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var detail = new SowingDetail
                            {
                                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                                SeedingDate = reader.GetDateTime(reader.GetOrdinal("SeedingDate")),
                                SeedsPlanted = reader.GetInt32(reader.GetOrdinal("SeedsPlanted")),
                                ReadyDate = reader.GetDateTime(reader.GetOrdinal("ReadyDate")),
                                PlantName = reader.GetString(reader.GetOrdinal("PlantName")),
                                SpeciesName = reader.GetString(reader.GetOrdinal("SpeciesName")),
                                Notes = reader.IsDBNull(reader.GetOrdinal("SowingType"))
                                    ? string.Empty
                                    : reader.GetString(reader.GetOrdinal("SowingType")),
                                SowingType = reader.GetString(reader.GetOrdinal("SowingType"))
                            };
                            sowingDetails.Add(detail);
                        }
                    }
                }
            }

            return sowingDetails;
        }

        // Separate method to get booking details
        private async Task<List<BookingDetail>> GetBookingDetails(DateTime weekStart, DateTime weekEnd)
        {
            var bookingDetails = new List<BookingDetail>();

            string connectionString = _configuration.GetConnectionString("DefaultConnection");

            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();

                // Query for current pending bookings
                string bookingQuery = @"
SELECT 
    b.Id,
    b.CustomerName,
    b.Quantity,
    b.DeliveryDate,
    b.Status,
    b.Status,
    p.Name AS PlantName,
    sp.Name AS SpeciesName,
    b.BookingDate,
    'Current Week' AS BookingType
FROM Bookings b
INNER JOIN PlantTypes p ON b.PlantId = p.Id
INNER JOIN PlantSpecies sp ON b.SpeciesId = sp.Id
WHERE b.Status = 'Pending'
      AND b.PlantId = @PlantId
AND b.SpeciesId = @SpeciesId
    AND CAST(b.DeliveryDate AS DATE) >= @WeekStart
    AND CAST(b.DeliveryDate AS DATE) <= @WeekEnd
ORDER BY b.DeliveryDate";

                using (SqlCommand command = new SqlCommand(bookingQuery, connection))
                {
                    command.Parameters.AddWithValue("@PlantId", PlantId);
                    command.Parameters.AddWithValue("@SpeciesId", SpeciesId);
                    command.Parameters.AddWithValue("@WeekStart", weekStart);
                    command.Parameters.AddWithValue("@WeekEnd", weekEnd);

                    using (SqlDataReader reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var detail = new BookingDetail
                            {
                                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                                CustomerName = reader.GetString(reader.GetOrdinal("CustomerName")),
                                Quantity = reader.GetInt32(reader.GetOrdinal("Quantity")),
                                DeliveryDate = reader.GetDateTime(reader.GetOrdinal("DeliveryDate")),
                                Status = reader.GetString(reader.GetOrdinal("Status")),
                                Notes = reader.IsDBNull(reader.GetOrdinal("Status"))
                                    ? string.Empty
                                    : reader.GetString(reader.GetOrdinal("Status")),
                                PlantName = reader.GetString(reader.GetOrdinal("PlantName")),
                                SpeciesName = reader.GetString(reader.GetOrdinal("SpeciesName")),
                                OrderDate = reader.GetDateTime(reader.GetOrdinal("BookingDate")),
                                BookingType = reader.GetString(reader.GetOrdinal("BookingType"))
                            };
                            bookingDetails.Add(detail);
                        }
                    }
                }

                // Query for confirmed bookings (additional context)
                string confirmedBookingQuery = @"
SELECT 
    b.Id,
    b.CustomerName,
    b.Quantity,
    b.DeliveryDate,
    b.Status,
    b.Status,
    p.Name AS PlantName,
    sp.Name AS SpeciesName,
    b.BookingDate,
    'Confirmed' AS BookingType
FROM Bookings b
INNER JOIN PlantTypes p ON b.PlantId = p.Id
INNER JOIN PlantSpecies sp ON b.SpeciesId = sp.Id
WHERE b.Status = 'Confirmed'
   AND b.PlantId = @PlantId
AND b.SpeciesId = @SpeciesId
    AND CAST(b.DeliveryDate AS DATE) >= @WeekStart
    AND CAST(b.DeliveryDate AS DATE) <= @WeekEnd
ORDER BY b.DeliveryDate";

                using (SqlCommand command = new SqlCommand(confirmedBookingQuery, connection))
                {
                    command.Parameters.AddWithValue("@PlantId", PlantId);
                    command.Parameters.AddWithValue("@SpeciesId", SpeciesId);
                    command.Parameters.AddWithValue("@WeekStart", weekStart);
                    command.Parameters.AddWithValue("@WeekEnd", weekEnd);

                    using (SqlDataReader reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var detail = new BookingDetail
                            {
                                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                                CustomerName = reader.GetString(reader.GetOrdinal("CustomerName")),
                                Quantity = reader.GetInt32(reader.GetOrdinal("Quantity")),
                                DeliveryDate = reader.GetDateTime(reader.GetOrdinal("DeliveryDate")),
                                Status = reader.GetString(reader.GetOrdinal("Status")),
                                Notes = reader.IsDBNull(reader.GetOrdinal("Status"))
                                    ? string.Empty
                                    : reader.GetString(reader.GetOrdinal("Status")),
                                PlantName = reader.GetString(reader.GetOrdinal("PlantName")),
                                SpeciesName = reader.GetString(reader.GetOrdinal("SpeciesName")),
                                OrderDate = reader.GetDateTime(reader.GetOrdinal("BookingDate")),
                                BookingType = reader.GetString(reader.GetOrdinal("BookingType"))
                            };
                            bookingDetails.Add(detail);
                        }
                    }
                }
            }

            return bookingDetails;
        }

        private async Task LoadWeeklyDataFromDatabase()
        {
            WeeklyData.Clear();

            string connectionString = _configuration.GetConnectionString("DefaultConnection");

            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();

                string sqlQuery = @"/* ============================================
   WEEKLY PLANNING QUERY – FINAL SAFE VERSION
   (Column names preserved)
   ============================================ */

DECLARE @StartDate DATE;

------------------------------------------------
-- 1️⃣ Earliest date from all relevant data
------------------------------------------------
SELECT @StartDate = MIN(StartDate)
FROM
(
    SELECT MIN(CAST(se.SeedingDate AS DATE)) AS StartDate
    FROM SeedEntries se
    WHERE se.PlantId = @PlantId
      AND se.SpeciesId = @SpeciesId
      AND se.ReadyForInventory = 0

    UNION ALL

    SELECT MIN(CAST(inv.LastUpdated AS DATE))
    FROM Inventory inv
    WHERE inv.PlantId = @PlantId
      AND inv.SpeciesId = @SpeciesId
      AND inv.IsUtilized = 'N'

    UNION ALL

    SELECT MIN(CAST(b.DeliveryDate AS DATE))
    FROM Bookings b
    WHERE b.Status = 'Pending'
      AND b.PlantId = @PlantId
      AND b.SpeciesId = @SpeciesId
) AS AllDates;

SET @StartDate = ISNULL(@StartDate, CAST(GETDATE() AS DATE));

------------------------------------------------
-- 2️⃣ Align to Monday
------------------------------------------------
SET DATEFIRST 1;
SET @StartDate = DATEADD(DAY, 1 - DATEPART(WEEKDAY, @StartDate), @StartDate);

------------------------------------------------
-- 3️⃣ Generate 52 weeks
------------------------------------------------
WITH WeekCalendar AS
(
    SELECT 0 AS WeekNum
    UNION ALL
    SELECT WeekNum + 1
    FROM WeekCalendar
    WHERE WeekNum < 51
),

WeeklyDates AS
(
    SELECT
        WeekNum + 1 AS WeekNumber,
        DATEADD(WEEK, WeekNum, @StartDate) AS WeekStart,
        DATEADD(DAY, 6, DATEADD(WEEK, WeekNum, @StartDate)) AS WeekEnd
    FROM WeekCalendar
),

------------------------------------------------
-- 4️⃣ Sowing
------------------------------------------------
SowingByPlantingWeek AS
(
    SELECT
        wd.WeekNumber,
        COUNT(DISTINCT se.Id) AS BatchCount,
        MIN(se.SeedingDate) AS FirstBatchSowDate,
        SUM(se.SeedsPlanted) AS TotalSowed
    FROM WeeklyDates wd
    LEFT JOIN SeedEntries se
        ON se.ReadyForInventory = 0
       AND se.PlantId = @PlantId
       AND se.SpeciesId = @SpeciesId
       AND CAST(se.SeedingDate AS DATE)
           BETWEEN wd.WeekStart AND wd.WeekEnd
    GROUP BY wd.WeekNumber
),

------------------------------------------------
-- 5️⃣ Ready This Week
------------------------------------------------
SowingReadyByWeek AS
(
    SELECT
        wd.WeekNumber,
        SUM(se.SeedsPlanted) AS ReadyQuantity
    FROM WeeklyDates wd
    LEFT JOIN SeedEntries se
        ON se.ReadyForInventory = 0
       AND se.PlantId = @PlantId
       AND se.SpeciesId = @SpeciesId
       AND CAST(DATEADD(DAY, 21, se.SeedingDate) AS DATE)
           BETWEEN wd.WeekStart AND wd.WeekEnd
    GROUP BY wd.WeekNumber
),

------------------------------------------------
-- 6️⃣ Inventory (Not Utilized Only)
------------------------------------------------
InventoryByWeek AS
(
    SELECT
        wd.WeekNumber,
        SUM(inv.RemainingQuantity) AS InventoryQuantity
    FROM WeeklyDates wd
    LEFT JOIN Inventory inv
        ON inv.IsUtilized = 'N'
       AND inv.PlantId = @PlantId
       AND inv.SpeciesId = @SpeciesId
       AND CAST(inv.LastUpdated AS DATE)
           BETWEEN wd.WeekStart AND wd.WeekEnd
    GROUP BY wd.WeekNumber
),

------------------------------------------------
-- 7️⃣ Pending Bookings
------------------------------------------------
BookingByWeek AS
(
    SELECT
        wd.WeekNumber,
        SUM(b.Quantity) AS TotalBookingQuantity,
        COUNT(DISTINCT b.Id) AS BookingCount
    FROM WeeklyDates wd
    LEFT JOIN Bookings b
        ON b.Status = 'Pending'
       AND b.PlantId = @PlantId
       AND b.SpeciesId = @SpeciesId
       AND CAST(b.DeliveryDate AS DATE)
           BETWEEN wd.WeekStart AND wd.WeekEnd
    GROUP BY wd.WeekNumber
)

------------------------------------------------
-- 8️⃣ FINAL OUTPUT (Original Column Names)
------------------------------------------------
SELECT
    w.WeekNumber,
    w.WeekStart,
    w.WeekEnd,

    s.FirstBatchSowDate,
    ISNULL(s.BatchCount, 0) AS BatchCount,
    ISNULL(s.TotalSowed, 0) AS TotalSowed,

    ISNULL(r.ReadyQuantity, 0) AS PossibleInventoryReadyThisWeek,
    ISNULL(i.InventoryQuantity, 0) AS InventoryQuantity,

    (
        ISNULL(r.ReadyQuantity, 0)
        + ISNULL(i.InventoryQuantity, 0)
    ) AS PossibleInventoryThisWeek,   -- 🔥 THIS FIXES YOUR ERROR

    ISNULL(b.TotalBookingQuantity, 0) AS TotalBookingQuantity,
    ISNULL(b.BookingCount, 0) AS BookingCount,

    CASE
        WHEN (ISNULL(r.ReadyQuantity, 0) + ISNULL(i.InventoryQuantity, 0))
             >= ISNULL(b.TotalBookingQuantity, 0)
        THEN 'OK'
        WHEN ISNULL(b.TotalBookingQuantity, 0) > 0
        THEN 'CRITICAL'
        ELSE 'OK'
    END AS StockStatus

FROM WeeklyDates w
LEFT JOIN SowingByPlantingWeek s ON s.WeekNumber = w.WeekNumber
LEFT JOIN SowingReadyByWeek r ON r.WeekNumber = w.WeekNumber
LEFT JOIN InventoryByWeek i ON i.WeekNumber = w.WeekNumber
LEFT JOIN BookingByWeek b ON b.WeekNumber = w.WeekNumber

ORDER BY w.WeekNumber
OPTION (MAXRECURSION 1000);
";

                using (SqlCommand command = new SqlCommand(sqlQuery, connection))
                {
                    command.Parameters.AddWithValue("@PlantId", PlantId);
                    command.Parameters.AddWithValue("@SpeciesId", SpeciesId);
                    using (SqlDataReader reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var weekData = new WeeklyData
                            {
                                WeekNumber = reader.GetInt32(reader.GetOrdinal("WeekNumber")),
                                WeekStart = reader.GetDateTime(reader.GetOrdinal("WeekStart")),
                                WeekEnd = reader.GetDateTime(reader.GetOrdinal("WeekEnd")),
                                FirstBatchSowDate = reader.IsDBNull(reader.GetOrdinal("FirstBatchSowDate"))
                                    ? (DateTime?)null
                                    : reader.GetDateTime(reader.GetOrdinal("FirstBatchSowDate")),
                                BatchCount = reader.GetInt32(reader.GetOrdinal("BatchCount")),
                                TotalSowed = reader.IsDBNull(reader.GetOrdinal("TotalSowed"))
                                    ? 0
                                    : reader.GetInt32(reader.GetOrdinal("TotalSowed")),
                                InventoryQuantity = reader.IsDBNull(reader.GetOrdinal("InventoryQuantity")) ? 0 : reader.GetInt32(reader.GetOrdinal("InventoryQuantity")),
                                PossibleInventoryThisWeek = reader.IsDBNull(reader.GetOrdinal("PossibleInventoryThisWeek")) ? 0 : reader.GetInt32(reader.GetOrdinal("PossibleInventoryThisWeek")),
                                PossibleInventoryReadyThisWeek = reader.IsDBNull(reader.GetOrdinal("PossibleInventoryReadyThisWeek"))
                                    ? 0
                                    : reader.GetInt32(reader.GetOrdinal("PossibleInventoryReadyThisWeek")),
                                TotalBookingQuantity = reader.IsDBNull(reader.GetOrdinal("TotalBookingQuantity"))
                                    ? 0
                                    : reader.GetInt32(reader.GetOrdinal("TotalBookingQuantity")),
                                BookingCount = reader.GetInt32(reader.GetOrdinal("BookingCount")),
                                StockStatus = reader.GetString(reader.GetOrdinal("StockStatus"))
                            };

                            weekData.CalculateDerivedFields();
                            WeeklyData.Add(weekData);
                        }
                    }
                }
            }
        }

        private void CalculateSummary()
        {
            Summary = new DashboardSummary();

            if (WeeklyData.Any())
            {
                // Calculate averages and totals
                Summary.OverallSyncRate = (int)WeeklyData.Average(w => w.SyncPercentage);
                Summary.GoodSyncWeeks = WeeklyData.Count(w => w.SyncPercentage >= 85);
                Summary.NeedOptimizationWeeks = WeeklyData.Count(w => w.SyncPercentage >= 70 && w.SyncPercentage < 85);
                Summary.CriticalWeeks = WeeklyData.Count(w => w.SyncPercentage < 70);
                Summary.TotalExcess = WeeklyData.Sum(w => w.ExcessUnits);
                Summary.TotalShortage = WeeklyData.Sum(w => w.ShortageUnits);
                Summary.TotalBookings = WeeklyData.Sum(w => w.TotalBookingQuantity);
                Summary.TotalSowing = WeeklyData.Sum(w => w.TotalSowed);
                Summary.TotalHarvestReady = WeeklyData.Sum(w => w.PossibleInventoryReadyThisWeek);

                // Find current week
                var currentWeek = WeeklyData
                    .FirstOrDefault(w => w.WeekStart <= DateTime.Today && w.WeekEnd >= DateTime.Today);

                if (currentWeek != null)
                {
                    Summary.CurrentWeek = currentWeek.WeekNumber;
                    Summary.CurrentWeekBookings = currentWeek.TotalBookingQuantity;
                    Summary.CurrentWeekSowing = currentWeek.TotalSowed;
                    Summary.CurrentWeekHarvest = currentWeek.PossibleInventoryReadyThisWeek;
                    Summary.CurrentWeekRisk = currentWeek.SyncGap > 0
                        ? (int)((currentWeek.SyncGap / (double)currentWeek.HarvestReady) * 100)
                        : 0;
                }
            }
        }

        public IActionResult OnPostExportData()
        {
            var csvContent = "Week,Start Date,End Date,Bookings,Sowing,Harvest Ready,Capacity Used,Excess Units,Shortage Units,Sync %,Sync Gap,Status,Optimization Action\n";

            foreach (var week in WeeklyData)
            {
                csvContent += $"{week.WeekNumber},{week.WeekStart:yyyy-MM-dd},{week.WeekEnd:yyyy-MM-dd},{week.TotalBookingQuantity},{week.TotalSowed},{week.PossibleInventoryReadyThisWeek},{week.CapacityUsed}%,{week.ExcessUnits},{week.ShortageUnits},{week.SyncPercentage}%,{week.SyncGap},{week.Status},{week.OptimizationAction}\n";
            }

            var bytes = System.Text.Encoding.UTF8.GetBytes(csvContent);
            return File(bytes, "text/csv", $"marigold_sync_data_{DateTime.Now:yyyy-MM-dd}.csv");
        }
    }

    public class WeeklyData
    {
        public int WeekNumber { get; set; }
        public DateTime WeekStart { get; set; }
        public DateTime WeekEnd { get; set; }
        public DateTime? FirstBatchSowDate { get; set; }
        public int BatchCount { get; set; }
        public int TotalSowed { get; set; }
        public int InventoryQuantity { get; set; }
         public int PossibleInventoryThisWeek { get; set; }
        public int PossibleInventoryReadyThisWeek { get; set; }
        public int TotalBookingQuantity { get; set; }
        public int BookingCount { get; set; }
        public string StockStatus { get; set; }

        public string SyncClass { get; private set; }


        // Calculated properties
        public int CapacityUsed { get; private set; }
        public int SyncPercentage { get; private set; }
        public int SyncGap { get; private set; }
        public int ExcessUnits { get; private set; }
        public int ShortageUnits { get; private set; }
        public string Status { get; private set; }
        public string OptimizationAction { get; private set; }
        public int HarvestReady => PossibleInventoryReadyThisWeek;

        public int AvailableInventory => PossibleInventoryReadyThisWeek + InventoryQuantity;

        public int Bookings => TotalBookingQuantity;
        public bool HasSowing => TotalSowed > 0;
        public bool HasBookings => TotalBookingQuantity > 0;
        public bool HasAnyData => HasSowing || HasBookings;

        public void CalculateDerivedFields()
        {

                int available = AvailableInventory;
            // Calculate sync percentage (how well harvest matches bookings)
          if (Bookings > 0)
{
    SyncPercentage = (int)Math.Round(
        Math.Min(available / (double)Bookings, 1.0) * 100
    );
}
else
{
    // No bookings → perfect sync by definition
    SyncPercentage = 0;
}


            // Calculate capacity used
            CapacityUsed = SyncPercentage;

           

            // Calculate excess and shortage
            ExcessUnits = Math.Max(0, available - Bookings);
            ShortageUnits = Math.Max(0, Bookings - available);

             // Calculate sync gap
              SyncGap = ExcessUnits + ShortageUnits;

            // Determine status
            if (SyncPercentage >= 85)
            {
                Status = "good";
                        SyncClass = "sync-good";     // 🟢
                OptimizationAction = "Maintain";
            }
            else if (SyncPercentage >= 70)
            {
                Status = "warning";
                        SyncClass = "sync-warning";  // 🟠
                OptimizationAction = "Optimize";
            }
            else
            {
                Status = "danger";
                        SyncClass = "sync-critical"; // 🔴
                OptimizationAction = "Critical";
            }
        }
    }

    public class DashboardSummary
    {
        public int OverallSyncRate { get; set; }
        public int GoodSyncWeeks { get; set; }
        public int NeedOptimizationWeeks { get; set; }
        public int CriticalWeeks { get; set; }
        public int TotalExcess { get; set; }
        public int TotalShortage { get; set; }
        public int TotalBookings { get; set; }
        public int TotalSowing { get; set; }
        public int TotalHarvestReady { get; set; }
        public int CurrentWeek { get; set; }
        public int CurrentWeekBookings { get; set; }
        public int CurrentWeekSowing { get; set; }
        public int CurrentWeekHarvest { get; set; }
        public int CurrentWeekRisk { get; set; }
    }

    public class SowingDetail
    {
        public int Id { get; set; }
        public DateTime SeedingDate { get; set; }
        public int SeedsPlanted { get; set; }
        public DateTime ReadyDate { get; set; }
        public string PlantName { get; set; }
        public string SpeciesName { get; set; }
        public string Notes { get; set; }
        public string SowingType { get; set; }
    }

    public class BookingDetail
    {
        public int Id { get; set; }
        public string CustomerName { get; set; }
        public int Quantity { get; set; }
        public DateTime DeliveryDate { get; set; }
        public string Status { get; set; }
        public string Notes { get; set; }
        public string PlantName { get; set; }
        public string SpeciesName { get; set; }
        public DateTime OrderDate { get; set; }
        public string BookingType { get; set; }
    }
}