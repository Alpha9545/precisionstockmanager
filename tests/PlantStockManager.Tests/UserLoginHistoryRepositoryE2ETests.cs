using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using PlantStockManager.Data;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Step 3B: GetTodaysActivityAsync against a REAL scratch copy of the
    // test database that already has 2026-09-30_UserLoginHistory.sql
    // applied (same safety rules as every other E2E class in this project:
    // PSM_SCRATCH_CONNECTION, database name must start with
    // PlantsIMS2_Scratch_). Every test inserts/deletes ONLY rows in
    // dbo.UserLoginHistory (a brand-new, empty-until-now table -- nothing
    // pre-existing to collide with), scoped to a fixed test date
    // (2020-01-15) chosen specifically because no real login could predate
    // this feature's existence, so it can never collide with another
    // test's data. dbo.IMSUsers itself is never written to.
    [Collection("ScratchDb")]
    public class UserLoginHistoryRepositoryE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";
        private static readonly DateTime TestDay = new(2020, 1, 15);

        private sealed class Env
        {
            public required string ConnectionString { get; init; }
            public required UserLoginHistoryRepository Repo { get; init; }

            public async Task<int> InsertLoginAsync(int userId, DateTime loginAtUtc)
            {
                using var conn = new SqlConnection(ConnectionString);
                await conn.OpenAsync();
                using var cmd = new SqlCommand(
                    "INSERT INTO dbo.UserLoginHistory (UserId, LoginAt) OUTPUT INSERTED.Id VALUES (@UserId, @LoginAt);", conn);
                cmd.Parameters.AddWithValue("@UserId", userId);
                cmd.Parameters.AddWithValue("@LoginAt", loginAtUtc);
                return (int)(await cmd.ExecuteScalarAsync())!;
            }

            public async Task DeleteLoginsForDayAsync(DateTime day)
            {
                using var conn = new SqlConnection(ConnectionString);
                await conn.OpenAsync();
                using var cmd = new SqlCommand(
                    "DELETE FROM dbo.UserLoginHistory WHERE LoginAt >= @From AND LoginAt < @To;", conn);
                cmd.Parameters.AddWithValue("@From", day.Date);
                cmd.Parameters.AddWithValue("@To", day.Date.AddDays(1));
                await cmd.ExecuteNonQueryAsync();
            }

            public async Task<int?> ScalarNullableIntAsync(string sql)
            {
                using var conn = new SqlConnection(ConnectionString);
                await conn.OpenAsync();
                using var cmd = new SqlCommand(sql, conn);
                var r = await cmd.ExecuteScalarAsync();
                return r == null || r == DBNull.Value ? null : (int)r;
            }

            // ---- Step 7: qualifying-activity source #1 (dbo.Bookings.BookedById) ----
            public async Task InsertBookingActivityAsync(int bookedById, DateTime bookingDate, int plantId, int speciesId)
            {
                using var conn = new SqlConnection(ConnectionString);
                await conn.OpenAsync();
                using var cmd = new SqlCommand(@"
INSERT INTO dbo.Bookings (SpeciesId, Quantity, PlantId, BookingDate, AddedBy, BookedById, Status)
VALUES (@SpeciesId, 1, @PlantId, @BookingDate, N'E2E_TEST', @BookedById, N'Pending');", conn);
                cmd.Parameters.AddWithValue("@SpeciesId", speciesId);
                cmd.Parameters.AddWithValue("@PlantId", plantId);
                cmd.Parameters.AddWithValue("@BookingDate", bookingDate);
                cmd.Parameters.AddWithValue("@BookedById", bookedById);
                await cmd.ExecuteNonQueryAsync();
            }

            // Same source, but the free-text BookedByOther fallback ONLY --
            // must NEVER count as activity (approved exclusion).
            public async Task InsertBookingWithOnlyTypedBookerAsync(DateTime bookingDate, int plantId, int speciesId)
            {
                using var conn = new SqlConnection(ConnectionString);
                await conn.OpenAsync();
                using var cmd = new SqlCommand(@"
INSERT INTO dbo.Bookings (SpeciesId, Quantity, PlantId, BookingDate, AddedBy, BookedByOther, Status)
VALUES (@SpeciesId, 1, @PlantId, @BookingDate, N'E2E_TEST', N'Some Typed Name', N'Pending');", conn);
                cmd.Parameters.AddWithValue("@SpeciesId", speciesId);
                cmd.Parameters.AddWithValue("@PlantId", plantId);
                cmd.Parameters.AddWithValue("@BookingDate", bookingDate);
                await cmd.ExecuteNonQueryAsync();
            }

            public async Task DeleteBookingActivityForDayAsync(DateTime day)
            {
                using var conn = new SqlConnection(ConnectionString);
                await conn.OpenAsync();
                using var cmd = new SqlCommand("DELETE FROM dbo.Bookings WHERE AddedBy = N'E2E_TEST' AND BookingDate >= @From AND BookingDate < @To;", conn);
                cmd.Parameters.AddWithValue("@From", day.Date);
                cmd.Parameters.AddWithValue("@To", day.Date.AddDays(1));
                await cmd.ExecuteNonQueryAsync();
            }

            // ---- Step 7: qualifying-activity source #2 (dbo.FertilizerUsage.EnteredById) ----
            public async Task InsertFertilizerActivityAsync(int enteredById, DateTime issueDate, int stockId)
            {
                using var conn = new SqlConnection(ConnectionString);
                await conn.OpenAsync();
                using var cmd = new SqlCommand(@"
INSERT INTO dbo.FertilizerUsage (StockId, UsedQuantity, IssueDate, ReceivedBy, EnteredById, Remarks)
VALUES (@StockId, 1, @IssueDate, N'E2E_TEST_RECEIVER', @EnteredById, N'E2E_TEST');", conn);
                cmd.Parameters.AddWithValue("@StockId", stockId);
                cmd.Parameters.AddWithValue("@IssueDate", issueDate.Date);
                cmd.Parameters.AddWithValue("@EnteredById", enteredById);
                await cmd.ExecuteNonQueryAsync();
            }

            public async Task DeleteFertilizerActivityForDayAsync(DateTime day)
            {
                using var conn = new SqlConnection(ConnectionString);
                await conn.OpenAsync();
                using var cmd = new SqlCommand("DELETE FROM dbo.FertilizerUsage WHERE Remarks = N'E2E_TEST' AND IssueDate >= @From AND IssueDate < @To;", conn);
                cmd.Parameters.AddWithValue("@From", day.Date);
                cmd.Parameters.AddWithValue("@To", day.Date.AddDays(1));
                await cmd.ExecuteNonQueryAsync();
            }

            public async Task<(int PlantId, int SpeciesId)> FindValidPlantSpeciesAsync()
            {
                using var conn = new SqlConnection(ConnectionString);
                await conn.OpenAsync();
                using var cmd = new SqlCommand("SELECT TOP 1 Id, PlantTypeId FROM dbo.PlantSpecies", conn);
                using var r = await cmd.ExecuteReaderAsync();
                await r.ReadAsync();
                return (r.GetInt32(1), r.GetInt32(0)); // (PlantId, SpeciesId)
            }

            public async Task<int> FindValidFertilizerStockIdAsync()
            {
                using var conn = new SqlConnection(ConnectionString);
                await conn.OpenAsync();
                using var cmd = new SqlCommand("SELECT TOP 1 StockId FROM dbo.FertilizerStock", conn);
                return (int)(await cmd.ExecuteScalarAsync())!;
            }
        }

        private static async Task<Env> OpenAsync()
        {
            var cs = Environment.GetEnvironmentVariable(ConnectionVariable);
            Skip.If(string.IsNullOrWhiteSpace(cs), $"Set {ConnectionVariable} to a {RequiredPrefix}* database connection string to run the end-to-end tests.");
            string name;
            using (var conn = new SqlConnection(cs))
            {
                await conn.OpenAsync();
                using var cmd = new SqlCommand("SELECT DB_NAME()", conn);
                name = (string)(await cmd.ExecuteScalarAsync())!;
            }
            if (!name.StartsWith(RequiredPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException($"Refusing to run end-to-end tests against '{name}'. Only databases named {RequiredPrefix}* are allowed.");

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs }).Build();
            var dbHelper = new DatabaseHelper(config);
            var env = new Env { ConnectionString = cs!, Repo = new UserLoginHistoryRepository(dbHelper) };

            // the migration must be on the scratch copy (it is applied by the test setup, never by a test)
            Assert.Equal(1, await env.ScalarNullableIntAsync(
                "SELECT COUNT(*) FROM sys.tables WHERE name = 'UserLoginHistory' AND schema_id = SCHEMA_ID('dbo')") ?? 0);

            await env.DeleteLoginsForDayAsync(TestDay); // clean slate for this fixed test date
            await env.DeleteBookingActivityForDayAsync(TestDay);
            await env.DeleteBookingActivityForDayAsync(new DateTime(2020, 1, 14));
            await env.DeleteFertilizerActivityForDayAsync(TestDay);
            return env;
        }

        private static async Task<(int ActiveId, int InactiveId)> FindTestUsersAsync(Env env)
        {
            using var conn = new SqlConnection(env.ConnectionString);
            await conn.OpenAsync();
            using var activeCmd = new SqlCommand("SELECT TOP 1 Id FROM dbo.IMSUsers WHERE IsActive = 1 ORDER BY Id", conn);
            var activeId = (int)(await activeCmd.ExecuteScalarAsync())!;
            using var inactiveCmd = new SqlCommand("SELECT TOP 1 Id FROM dbo.IMSUsers WHERE IsActive = 0 ORDER BY Id", conn);
            var inactiveResult = await inactiveCmd.ExecuteScalarAsync();
            return (activeId, inactiveResult == null ? -1 : (int)inactiveResult);
        }

        [SkippableFact]
        public async Task NoLoginsForTheDay_EveryActiveEmployeeIsInDidNotLogIn()
        {
            var env = await OpenAsync();
            var activity = await env.Repo.GetTodaysActivityAsync(TestDay);

            Assert.NotEmpty(activity);
            Assert.All(activity, e => Assert.False(e.LoggedInToday));
            Assert.All(activity, e => Assert.Null(e.FirstLoginAtToday));
        }

        [SkippableFact]
        public async Task EmployeeWithOneLogin_IsCountedAsLoggedIn_WithThatTime()
        {
            var env = await OpenAsync();
            var (activeId, _) = await FindTestUsersAsync(env);
            var loginTime = new DateTime(2020, 1, 15, 9, 30, 0, DateTimeKind.Utc);
            await env.InsertLoginAsync(activeId, loginTime);

            var activity = await env.Repo.GetTodaysActivityAsync(TestDay);
            var employee = activity.Single(e => e.UserId == activeId);

            Assert.True(employee.LoggedInToday);
            Assert.Equal(loginTime, employee.FirstLoginAtToday);
        }

        [SkippableFact]
        public async Task MultipleLoginsSameDay_ReportsOnlyTheEarliest_CountedOnce()
        {
            var env = await OpenAsync();
            var (activeId, _) = await FindTestUsersAsync(env);
            var first = new DateTime(2020, 1, 15, 8, 0, 0, DateTimeKind.Utc);
            var second = new DateTime(2020, 1, 15, 13, 15, 0, DateTimeKind.Utc);
            var third = new DateTime(2020, 1, 15, 18, 45, 0, DateTimeKind.Utc);
            await env.InsertLoginAsync(activeId, second);
            await env.InsertLoginAsync(activeId, first);
            await env.InsertLoginAsync(activeId, third);

            var activity = await env.Repo.GetTodaysActivityAsync(TestDay);
            var matches = activity.Where(e => e.UserId == activeId).ToList();

            var employee = Assert.Single(matches); // counted once, not 3 times
            Assert.Equal(first, employee.FirstLoginAtToday); // earliest, not latest
        }

        [SkippableFact]
        public async Task EmployeeWhoNeverLoggedInToday_ButLoggedInAnotherDay_IsInDidNotLogInToday()
        {
            var env = await OpenAsync();
            var (activeId, _) = await FindTestUsersAsync(env);
            // a login on a DIFFERENT day must never count as "today"
            await env.InsertLoginAsync(activeId, new DateTime(2020, 1, 14, 10, 0, 0, DateTimeKind.Utc));

            var activity = await env.Repo.GetTodaysActivityAsync(TestDay);
            var employee = activity.Single(e => e.UserId == activeId);

            Assert.False(employee.LoggedInToday);
            Assert.Null(employee.FirstLoginAtToday);

            await env.DeleteLoginsForDayAsync(new DateTime(2020, 1, 14)); // cleanup outside the fixed test window too
        }

        [SkippableFact]
        public async Task InactiveEmployee_NeverAppearsInResults_EvenWithALoginRow()
        {
            var env = await OpenAsync();
            var (_, inactiveId) = await FindTestUsersAsync(env);
            Skip.If(inactiveId < 0, "No inactive dbo.IMSUsers row exists in this scratch copy to test against.");
            await env.InsertLoginAsync(inactiveId, new DateTime(2020, 1, 15, 9, 0, 0, DateTimeKind.Utc));

            var activity = await env.Repo.GetTodaysActivityAsync(TestDay);

            Assert.DoesNotContain(activity, e => e.UserId == inactiveId);
        }

        [SkippableFact]
        public async Task DateBoundary_LoginJustBeforeMidnight_IsNotCountedForTheNextDay()
        {
            var env = await OpenAsync();
            var (activeId, _) = await FindTestUsersAsync(env);
            var justBeforeMidnight = new DateTime(2020, 1, 14, 23, 59, 59, DateTimeKind.Utc);
            await env.InsertLoginAsync(activeId, justBeforeMidnight);

            var activityOnTestDay = await env.Repo.GetTodaysActivityAsync(TestDay); // 2020-01-15
            var employee = activityOnTestDay.Single(e => e.UserId == activeId);
            Assert.False(employee.LoggedInToday);

            var activityOnPriorDay = await env.Repo.GetTodaysActivityAsync(new DateTime(2020, 1, 14));
            var employeePriorDay = activityOnPriorDay.Single(e => e.UserId == activeId);
            Assert.True(employeePriorDay.LoggedInToday);
            Assert.Equal(justBeforeMidnight, employeePriorDay.FirstLoginAtToday);

            await env.DeleteLoginsForDayAsync(new DateTime(2020, 1, 14));
        }

        [SkippableFact]
        public async Task DateBoundary_LoginAtExactlyMidnight_CountsForTheNewDay()
        {
            var env = await OpenAsync();
            var (activeId, _) = await FindTestUsersAsync(env);
            var exactlyMidnight = new DateTime(2020, 1, 15, 0, 0, 0, DateTimeKind.Utc);
            await env.InsertLoginAsync(activeId, exactlyMidnight);

            var activity = await env.Repo.GetTodaysActivityAsync(TestDay);
            var employee = activity.Single(e => e.UserId == activeId);

            Assert.True(employee.LoggedInToday);
            Assert.Equal(exactlyMidnight, employee.FirstLoginAtToday);
        }

        // ---- Step 7: Active Users / No Activity Recorded --------------------

        [SkippableFact]
        public async Task LoggedIn_NoQualifyingActivity_HasActivityFalse_NotAnActiveUser()
        {
            var env = await OpenAsync();
            var (activeId, _) = await FindTestUsersAsync(env);
            await env.InsertLoginAsync(activeId, new DateTime(2020, 1, 15, 9, 0, 0, DateTimeKind.Utc));

            var employee = (await env.Repo.GetTodaysActivityAsync(TestDay)).Single(e => e.UserId == activeId);

            Assert.True(employee.LoggedInToday);
            Assert.False(employee.HasActivityToday);
            Assert.False(employee.IsActiveUser);
        }

        [SkippableFact]
        public async Task LoggedIn_OneQualifyingBooking_IsActiveUser()
        {
            var env = await OpenAsync();
            var (activeId, _) = await FindTestUsersAsync(env);
            var (plantId, speciesId) = await env.FindValidPlantSpeciesAsync();
            await env.InsertLoginAsync(activeId, new DateTime(2020, 1, 15, 9, 0, 0, DateTimeKind.Utc));
            await env.InsertBookingActivityAsync(activeId, TestDay, plantId, speciesId);

            var employee = (await env.Repo.GetTodaysActivityAsync(TestDay)).Single(e => e.UserId == activeId);

            Assert.True(employee.LoggedInToday);
            Assert.True(employee.HasActivityToday);
            Assert.True(employee.IsActiveUser);
        }

        [SkippableFact]
        public async Task LoggedIn_MultipleBookingsFromSameSource_StillOneActiveUser_NotDuplicated()
        {
            var env = await OpenAsync();
            var (activeId, _) = await FindTestUsersAsync(env);
            var (plantId, speciesId) = await env.FindValidPlantSpeciesAsync();
            await env.InsertLoginAsync(activeId, new DateTime(2020, 1, 15, 9, 0, 0, DateTimeKind.Utc));
            await env.InsertBookingActivityAsync(activeId, TestDay, plantId, speciesId);
            await env.InsertBookingActivityAsync(activeId, TestDay, plantId, speciesId);
            await env.InsertBookingActivityAsync(activeId, TestDay, plantId, speciesId);

            var matches = (await env.Repo.GetTodaysActivityAsync(TestDay)).Where(e => e.UserId == activeId).ToList();

            var employee = Assert.Single(matches); // one row per employee, never duplicated by multiple activity rows
            Assert.True(employee.IsActiveUser);
        }

        [SkippableFact]
        public async Task LoggedIn_ActivityFromTwoDifferentSources_StillOneActiveUser_NoDoubleCount()
        {
            var env = await OpenAsync();
            var (activeId, _) = await FindTestUsersAsync(env);
            var (plantId, speciesId) = await env.FindValidPlantSpeciesAsync();
            var fertilizerStockId = await env.FindValidFertilizerStockIdAsync();
            await env.InsertLoginAsync(activeId, new DateTime(2020, 1, 15, 9, 0, 0, DateTimeKind.Utc));
            await env.InsertBookingActivityAsync(activeId, TestDay, plantId, speciesId);       // source 1
            await env.InsertFertilizerActivityAsync(activeId, TestDay, fertilizerStockId);      // source 2 (different table)

            var matches = (await env.Repo.GetTodaysActivityAsync(TestDay)).Where(e => e.UserId == activeId).ToList();

            var employee = Assert.Single(matches); // still exactly one row, activity from 2 sources doesn't duplicate the employee
            Assert.True(employee.IsActiveUser);
        }

        [SkippableFact]
        public async Task ActivityButNoLogin_HasActivityTrue_ButNotAnActiveUser_AndCountsAsNotLoggedIn()
        {
            var env = await OpenAsync();
            var (activeId, _) = await FindTestUsersAsync(env);
            var (plantId, speciesId) = await env.FindValidPlantSpeciesAsync();
            // NO login inserted -- only a qualifying activity row.
            await env.InsertBookingActivityAsync(activeId, TestDay, plantId, speciesId);

            var employee = (await env.Repo.GetTodaysActivityAsync(TestDay)).Single(e => e.UserId == activeId);

            Assert.False(employee.LoggedInToday);
            Assert.True(employee.HasActivityToday);
            Assert.False(employee.IsActiveUser); // approved rule: activity alone is never enough

            var (loggedIn, notLoggedIn) = DailyReportCalculations.PartitionEmployeeActivity(
                await env.Repo.GetTodaysActivityAsync(TestDay));
            Assert.DoesNotContain(loggedIn, e => e.UserId == activeId);
            Assert.Contains(notLoggedIn, e => e.UserId == activeId); // still correctly "Not Logged In"
        }

        [SkippableFact]
        public async Task TypedBookerOnly_BookedByOther_NeverCountsAsActivity()
        {
            var env = await OpenAsync();
            var (activeId, _) = await FindTestUsersAsync(env);
            var (plantId, speciesId) = await env.FindValidPlantSpeciesAsync();
            await env.InsertLoginAsync(activeId, new DateTime(2020, 1, 15, 9, 0, 0, DateTimeKind.Utc));
            // A booking recorded with only a typed name (BookedByOther), no
            // BookedById -- approved exclusion, must never count.
            await env.InsertBookingWithOnlyTypedBookerAsync(TestDay, plantId, speciesId);

            var employee = (await env.Repo.GetTodaysActivityAsync(TestDay)).Single(e => e.UserId == activeId);

            Assert.True(employee.LoggedInToday);
            Assert.False(employee.HasActivityToday);
            Assert.False(employee.IsActiveUser);
        }

        [SkippableFact]
        public async Task InactiveEmployee_ActivityAndLogin_StillNeverAppearsInResults()
        {
            var env = await OpenAsync();
            var (_, inactiveId) = await FindTestUsersAsync(env);
            Skip.If(inactiveId < 0, "No inactive dbo.IMSUsers row exists in this scratch copy to test against.");
            var (plantId, speciesId) = await env.FindValidPlantSpeciesAsync();
            await env.InsertLoginAsync(inactiveId, new DateTime(2020, 1, 15, 9, 0, 0, DateTimeKind.Utc));
            await env.InsertBookingActivityAsync(inactiveId, TestDay, plantId, speciesId);

            var activity = await env.Repo.GetTodaysActivityAsync(TestDay);

            Assert.DoesNotContain(activity, e => e.UserId == inactiveId);
        }

        [SkippableFact]
        public async Task DateBoundary_ActivityOnPriorDay_DoesNotCountForToday()
        {
            var env = await OpenAsync();
            var (activeId, _) = await FindTestUsersAsync(env);
            var (plantId, speciesId) = await env.FindValidPlantSpeciesAsync();
            await env.InsertLoginAsync(activeId, new DateTime(2020, 1, 15, 9, 0, 0, DateTimeKind.Utc));
            // Booking dated the PRIOR day -- must not count for TestDay.
            await env.InsertBookingActivityAsync(activeId, new DateTime(2020, 1, 14), plantId, speciesId);

            var employee = (await env.Repo.GetTodaysActivityAsync(TestDay)).Single(e => e.UserId == activeId);

            Assert.True(employee.LoggedInToday);
            Assert.False(employee.HasActivityToday); // yesterday's activity doesn't leak into today
            Assert.False(employee.IsActiveUser);
        }
    }
}
