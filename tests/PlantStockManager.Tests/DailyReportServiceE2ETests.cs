using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PlantStockManager.Data;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Daily Report (Step 3A) -- end to end against a REAL scratch copy of
    // the test database (same safety rules as every other E2E class in
    // this project: PSM_SCRATCH_CONNECTION, database name must start with
    // PlantsIMS2_Scratch_). Read-only: DailyReportService never writes
    // anything, and this test inserts no data either -- it only calls the
    // real service against whatever the scratch copy already contains.
    //
    // Deliberately does NOT assert on specific known historical figures
    // (e.g. "today's Sowing quantity is exactly X") -- that would tie this
    // test to whatever data happens to be in a given scratch copy at
    // whatever moment it was taken, which is not guaranteed and would make
    // this test flaky. Instead it asserts two things that must ALWAYS be
    // true regardless of the data: every field is non-negative and the
    // service never throws, and -- the one genuinely data-independent
    // correctness check -- a definitely-empty far-future date returns every
    // count/quantity as exactly zero, proving the date range is actually
    // being applied (not silently ignored in favour of an all-time total).
    [Collection("ScratchDb")]
    public class DailyReportServiceE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";

        private static async Task<DailyReportService> OpenAsync()
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

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs }).Build());
            services.AddSingleton<DatabaseHelper>();
            services.AddSingleton<Microsoft.Extensions.Options.IOptions<PlantStockManager.Authorization.SecurityOptions>>(Microsoft.Extensions.Options.Options.Create(new PlantStockManager.Authorization.SecurityOptions()));   // UserRoleRepository (via ReadyConfirmationRepository)
            // Same reflection-based auto-registration every *Repository E2E
            // test class in this project already uses, so DailyReportService's
            // full dependency graph (ManagementDashboardRepository ->
            // SeedSowingRepository, BookingRepository -> SeedlingFulfilmentRepository,
            // etc.) resolves without hand-listing every transitive dependency.
            foreach (var t in typeof(DatabaseHelper).Assembly.GetTypes().Where(t => t.Namespace == "PlantStockManager.Data" && t.IsClass && t.Name.EndsWith("Repository")))
                services.AddScoped(t);
            services.AddScoped<DailyReportService>();
            return services.BuildServiceProvider().GetRequiredService<DailyReportService>();
        }

        [SkippableFact]
        public async Task BuildReportForDateAsync_Today_RunsEndToEnd_AllFieldsNonNegative()
        {
            var service = await OpenAsync();
            var today = DateTime.Today;

            var report = await service.BuildReportForDateAsync(today);

            Assert.Equal(today, report.ReportDate);
            Assert.True(report.SowingQuantity >= 0);
            Assert.True(report.ReadyStockOverdueCount >= 0);
            Assert.True(report.ReadyStockOverdueQuantity >= 0);
            Assert.True(report.ReadyStockReadyTodayCount >= 0);
            Assert.True(report.ReadyStockReadyTodayQuantity >= 0);
            Assert.True(report.ReadyStockReadySoonCount >= 0);
            Assert.True(report.ReadyStockReadySoonQuantity >= 0);
            Assert.True(report.ReadyStockMeriGoldQuantity >= 0);
            Assert.True(report.ReadyStockChrysanthemumQuantity >= 0);
            Assert.True(report.ReadyStockZinniaQuantity >= 0);
            Assert.True(report.ReadyStockSeasonalVaritiesQuantity >= 0);
            // Step 8D: ReadyStockByPlantType is the formatter's own source --
            // must carry the live database's actual 4 Plant Types (never
            // fewer), each non-negative, each with a non-empty real name.
            Assert.Equal(4, report.ReadyStockByPlantType.Count);
            Assert.All(report.ReadyStockByPlantType, p => Assert.True(p.Quantity >= 0));
            Assert.All(report.ReadyStockByPlantType, p => Assert.False(string.IsNullOrWhiteSpace(p.PlantTypeName)));
            Assert.True(report.SeedlingBookingsCount >= 0);
            Assert.True(report.SeedlingBookingsQuantity >= 0);
            Assert.True(report.PottedPlantBookingsCount >= 0);
            Assert.True(report.SeedlingDispatchCount >= 0);
            Assert.True(report.SeedlingDispatchQuantity >= 0);
            Assert.True(report.PottedPlantDispatchCount >= 0);
            Assert.True(report.PottedPlantDispatchQuantity >= 0);
            Assert.True(report.OutletSalesCount >= 0);
            Assert.True(report.FertilizerUsageQuantity >= 0);
            Assert.True(report.WastageQuantity >= 0);
            Assert.True(report.TotalActiveEmployees >= 0);
            Assert.True(report.LoggedInEmployeeCount >= 0);
            Assert.True(report.NotLoggedInEmployeeCount >= 0);
            Assert.Equal(report.TotalActiveEmployees, report.LoggedInEmployeeCount + report.NotLoggedInEmployeeCount);
            Assert.Equal(report.LoggedInEmployeeCount, report.EmployeesLoggedInToday.Count);
            Assert.Equal(report.NotLoggedInEmployeeCount, report.EmployeesNotLoggedInToday.Count);
        }

        [SkippableFact]
        public async Task BuildReportForDateAsync_FarFutureDate_EveryFieldIsExactlyZero_ProvingTheDateFilterIsApplied()
        {
            var service = await OpenAsync();
            var farFuture = new DateTime(2999, 1, 1);

            var report = await service.BuildReportForDateAsync(farFuture);

            Assert.Equal(farFuture, report.ReportDate);
            Assert.Equal(0m, report.SowingQuantity);
            Assert.Equal(0, report.SeedlingBookingsCount);
            Assert.Equal(0m, report.SeedlingBookingsQuantity);
            Assert.Equal(0, report.PottedPlantBookingsCount);
            Assert.Equal(0, report.SeedlingDispatchCount);
            Assert.Equal(0m, report.SeedlingDispatchQuantity);
            Assert.Equal(0, report.PottedPlantDispatchCount);
            Assert.Equal(0m, report.PottedPlantDispatchQuantity);
            Assert.Equal(0, report.OutletSalesCount);
            Assert.Equal(0m, report.FertilizerUsageQuantity);
            Assert.Equal(0m, report.WastageQuantity);
            // ReadyStockOverdue/ReadyToday/ReadySoon are deliberately NOT
            // asserted here -- they are Balance (as-of-now) figures, not
            // date-ranged, so they are unaffected by ReportDate by design
            // (see Models/DailyReport.cs).

            // Employee activity is a different shape from the rest: TotalActiveEmployees
            // is a Balance figure (dbo.IMSUsers.IsActive = 1 right now, not date-ranged),
            // so it is NOT expected to be zero for a far-future date -- only the LOGIN
            // activity for that (empty) date is. No one could possibly have logged in on
            // 2999-01-01, so LoggedInEmployeeCount must be 0 and every active employee
            // must appear in "did not log in."
            Assert.Equal(0, report.LoggedInEmployeeCount);
            Assert.Empty(report.EmployeesLoggedInToday);
            Assert.Equal(report.TotalActiveEmployees, report.NotLoggedInEmployeeCount);
            Assert.Equal(report.TotalActiveEmployees, report.EmployeesNotLoggedInToday.Count);
        }
    }
}
