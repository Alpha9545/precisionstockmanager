using Microsoft.Data.SqlClient;
using PlantStockManager.Models;

namespace PlantStockManager.Data
{
    // Step 3B: the ONLY writer of dbo.UserLoginHistory is RecordLoginAsync,
    // called once from Pages/Account/Login.cshtml.cs right after a
    // successful sign-in. This class never touches dbo.IMSUsers' own
    // columns (Password, IsActive, etc.) -- login tracking is fully
    // isolated from the existing authentication/identity table.
    public class UserLoginHistoryRepository
    {
        private readonly DatabaseHelper _dbHelper;

        public UserLoginHistoryRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        // Deliberately never throws: a login-history write failure must
        // NEVER block a real login. The caller (LoginModel) checks Success
        // only to decide whether to log a warning -- it never lets a
        // failure here affect the sign-in that already succeeded.
        public async Task<(bool Success, string? Message)> RecordLoginAsync(int userId)
        {
            try
            {
                using var conn = _dbHelper.GetConnection();
                await conn.OpenAsync();
                using var cmd = new SqlCommand(
                    "INSERT INTO dbo.UserLoginHistory (UserId, LoginAt) VALUES (@UserId, SYSUTCDATETIME());", conn);
                cmd.Parameters.AddWithValue("@UserId", userId);
                await cmd.ExecuteNonQueryAsync();
                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        // Every ACTIVE employee, with their first login of the given day
        // (UTC calendar day) and whether they have any qualifying recorded
        // activity that same day. Directly answers the approved report's
        // employee-activity questions:
        //   Total active employees        = result.Count
        //   Logged in today                = result.Where(e => e.LoggedInToday)
        //   Active Users                   = result.Where(e => e.IsActiveUser)
        //   No Activity Recorded           = LoggedIn minus Active Users
        //   Did NOT log in today           = result.Where(e => !e.LoggedInToday)
        //
        // Step 7: HasActivityToday uses the EXACT, approved list of
        // qualifying sources -- nothing inferred, nothing guessed:
        //   1. The 5 stock ledger tables' UserId (SeedStock/CuttingStock/
        //      ReadyStock/PottedPlantStock/EmptyPotInventory Transactions) --
        //      covers Sowing, Dispatch, most Wastage, Ready Confirmation,
        //      cutting/pot production harvest, all via TransactionDate.
        //   2. dbo.Bookings.BookedById (seedling bookings), by BookingDate --
        //      "= u.Id" is NULL-safe by construction (NULL never equals an
        //      Id), so Bookings.BookedByOther-only rows are automatically
        //      excluded without a separate IS NOT NULL check.
        //   3. dbo.PottedPlantBookings.BookedById (note: the actual column
        //      is BookedById, not BookedBy -- the FK constraint is named
        //      FK_PottedPlantBookings_BookedBy but the column itself
        //      matches dbo.Bookings' naming exactly; verified live against
        //      PlantsIMS2_Test), by BookingDate.
        //   4. dbo.FertilizerUsage.EnteredById (never ReceivedById -- that
        //      identifies who received the fertilizer, not who used the
        //      software to record it), by IssueDate.
        // Deliberately NOT included (approved exclusions): master-data
        // maintenance (MotherPlants, Area, Admin pages), any cookie/session/
        // page-view signal (none of which exist server-side anyway).
        // Same @From/@To day-boundary parameters as LoginAt -- one single
        // day-boundary implementation, not a second independent one.
        public async Task<List<EmployeeLoginActivity>> GetTodaysActivityAsync(DateTime day)
        {
            var from = day.Date;
            var toExclusive = from.AddDays(1);

            var list = new List<EmployeeLoginActivity>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
SELECT u.Id, u.Name, d.DesignationName, h.FirstLoginAt,
       CAST(CASE WHEN EXISTS (
           SELECT 1 FROM dbo.SeedStockTransactions t WHERE t.UserId = u.Id AND t.TransactionDate >= @From AND t.TransactionDate < @To
           UNION ALL
           SELECT 1 FROM dbo.CuttingStockTransactions t WHERE t.UserId = u.Id AND t.TransactionDate >= @From AND t.TransactionDate < @To
           UNION ALL
           SELECT 1 FROM dbo.ReadyStockTransactions t WHERE t.UserId = u.Id AND t.TransactionDate >= @From AND t.TransactionDate < @To
           UNION ALL
           SELECT 1 FROM dbo.PottedPlantStockTransactions t WHERE t.UserId = u.Id AND t.TransactionDate >= @From AND t.TransactionDate < @To
           UNION ALL
           SELECT 1 FROM dbo.EmptyPotInventoryTransactions t WHERE t.UserId = u.Id AND t.TransactionDate >= @From AND t.TransactionDate < @To
           UNION ALL
           SELECT 1 FROM dbo.Bookings b WHERE b.BookedById = u.Id AND b.BookingDate >= @From AND b.BookingDate < @To
           UNION ALL
           SELECT 1 FROM dbo.PottedPlantBookings pb WHERE pb.BookedById = u.Id AND pb.BookingDate >= @From AND pb.BookingDate < @To
           UNION ALL
           SELECT 1 FROM dbo.FertilizerUsage fu WHERE fu.EnteredById = u.Id AND fu.IssueDate >= @From AND fu.IssueDate < @To
       ) THEN 1 ELSE 0 END AS BIT) AS HasActivityToday
FROM dbo.IMSUsers u
LEFT JOIN dbo.Designation d ON d.DesignationID = u.DesignationID
OUTER APPLY (
    SELECT MIN(LoginAt) AS FirstLoginAt
    FROM dbo.UserLoginHistory
    WHERE UserId = u.Id AND LoginAt >= @From AND LoginAt < @To
) h
WHERE u.IsActive = 1
ORDER BY u.Name", conn);
            cmd.Parameters.AddWithValue("@From", from);
            cmd.Parameters.AddWithValue("@To", toExclusive);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new EmployeeLoginActivity
                {
                    UserId = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    DesignationName = reader.IsDBNull(2) ? null : reader.GetString(2),
                    FirstLoginAtToday = reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                    HasActivityToday = reader.GetBoolean(4)
                });
            }
            return list;
        }
    }
}
