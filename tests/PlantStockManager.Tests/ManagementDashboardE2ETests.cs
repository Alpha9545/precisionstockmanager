using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Management Dashboard overview (ManagementDashboardRepository.GetOverviewAsync) against a
    // REAL scratch copy of the database (PSM_SCRATCH_CONNECTION, name must start with
    // PlantsIMS2_Scratch_). Current-state figures are checked as before/after deltas (the scratch
    // copy holds real data); period figures use a fixed 2020 date nothing else touches.
    [Collection("ScratchDb")]
    public class ManagementDashboardE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";
        private const string Marker = "E2E_DASHBOARD";
        private static readonly DateTime TestDay = new(2020, 1, 21);

        private sealed class Env
        {
            public required string Cs { get; init; }
            public required ManagementDashboardRepository Repo { get; init; }
            public required IServiceProvider Services { get; init; }

            public async Task<T> ScalarAsync<T>(string sql, params (string, object)[] ps)
            {
                using var conn = new SqlConnection(Cs);
                await conn.OpenAsync();
                using var cmd = new SqlCommand(sql, conn);
                foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
                return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync())!, typeof(T));
            }

            public Task<int> ExecAsync(string sql, params (string, object)[] ps) => ScalarAsync<int>(sql + "; SELECT @@ROWCOUNT", ps);

            public async Task CleanAsync()
            {
                await ExecAsync($"UPDATE dbo.Bookings SET OrderBookingId = NULL WHERE AddedBy = N'{Marker}'");
                await ExecAsync($"DELETE FROM dbo.Bookings WHERE AddedBy = N'{Marker}'");
                await ExecAsync($"DELETE FROM dbo.ReadyStockTransactions WHERE Remarks = N'{Marker}'");
                await ExecAsync($"DELETE FROM dbo.Dispatches WHERE Remarks = N'{Marker}'");
            }
        }

        private static async Task<Env> OpenAsync()
        {
            var cs = Environment.GetEnvironmentVariable(ConnectionVariable);
            Skip.If(string.IsNullOrWhiteSpace(cs), $"Set {ConnectionVariable} to a {RequiredPrefix}* database connection string to run the end-to-end tests.");
            using (var conn = new SqlConnection(cs))
            {
                await conn.OpenAsync();
                using var cmd = new SqlCommand("SELECT DB_NAME()", conn);
                var name = (string)(await cmd.ExecuteScalarAsync())!;
                if (!name.StartsWith(RequiredPrefix, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Refusing to run end-to-end tests against '{name}'. Only databases named {RequiredPrefix}* are allowed.");
            }
            // Same reflection-based registration as DailyReportServiceE2ETests.
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs }).Build());
            services.AddSingleton<DatabaseHelper>();
            services.AddSingleton<Microsoft.Extensions.Options.IOptions<PlantStockManager.Authorization.SecurityOptions>>(Microsoft.Extensions.Options.Options.Create(new PlantStockManager.Authorization.SecurityOptions()));
            foreach (var t in typeof(DatabaseHelper).Assembly.GetTypes().Where(t => t.Namespace == "PlantStockManager.Data" && t.IsClass && t.Name.EndsWith("Repository")))
                services.AddScoped(t);
            services.AddScoped<DailyReportService>();
            var provider = services.BuildServiceProvider().CreateScope().ServiceProvider;
            var env = new Env { Cs = cs!, Repo = provider.GetRequiredService<ManagementDashboardRepository>(), Services = provider };
            await env.CleanAsync();
            return env;
        }

        private static DashboardPeriod Day(DateTime d) => DashboardPeriod.Resolve(DashboardPeriod.Custom, d, d, d);

        private static Task<ManagementDashboardRepository.OverviewData> NowAsync(Env env)
            => env.Repo.GetOverviewAsync(DashboardPeriod.Resolve(DashboardPeriod.Today, null, null, AppClock.Today), AppClock.Today);

        // Inserts one Pending booking line; returns its Id.
        private static Task<int> InsertLineAsync(Env env, int plantId, int speciesId, int quantity, int? orderId, DateTime deliveryDate, decimal dispatched = 0)
            => env.ScalarAsync<int>($@"
INSERT INTO dbo.Bookings (SpeciesId, Quantity, PlantId, BookingDate, AddedBy, Status, DeliveryDate, OrderBookingId, DispatchedQuantity, CustomerName)
VALUES (@S, @Q, @P, GETDATE(), N'{Marker}', N'Pending', @D, @O, @Disp, N'{Marker}');
SELECT CAST(SCOPE_IDENTITY() AS INT);",
                ("@S", speciesId), ("@Q", quantity), ("@P", plantId), ("@D", deliveryDate.Date), ("@O", (object?)orderId ?? DBNull.Value), ("@Disp", dispatched));

        private static async Task<(int PlantId, int SpeciesId, int Species2)> TwoVarietiesAsync(Env env)
        {
            var plant = await env.ScalarAsync<int>("SELECT TOP 1 PlantTypeId FROM dbo.PlantSpecies WHERE PlantTypeId IS NOT NULL GROUP BY PlantTypeId HAVING COUNT(*) >= 2 ORDER BY PlantTypeId");
            var s1 = await env.ScalarAsync<int>("SELECT MIN(Id) FROM dbo.PlantSpecies WHERE PlantTypeId = @P", ("@P", plant));
            var s2 = await env.ScalarAsync<int>("SELECT MIN(Id) FROM dbo.PlantSpecies WHERE PlantTypeId = @P AND Id > @S", ("@P", plant), ("@S", s1));
            return (plant, s1, s2);
        }

        [SkippableFact]
        public async Task TwoLinesOfOneOrder_CountAsOneOrder_AndTheirQuantitiesAreSummed()
        {
            var env = await OpenAsync();
            try
            {
                var (plant, s1, s2) = await TwoVarietiesAsync(env);
                var before = (await NowAsync(env)).Seedlings;

                var first = await InsertLineAsync(env, plant, s1, 300, null, AppClock.Today.AddDays(10));
                await env.ExecAsync("UPDATE dbo.Bookings SET OrderBookingId = Id WHERE Id = @Id", ("@Id", first));
                await InsertLineAsync(env, plant, s2, 200, first, AppClock.Today.AddDays(10), dispatched: 50);

                var after = (await NowAsync(env)).Seedlings;
                Assert.Equal(before.OpenOrders + 1, after.OpenOrders);                   // ONE order
                Assert.Equal(before.OpenLines + 2, after.OpenLines);                     // two lines
                Assert.Equal(before.OpenBookedQuantity + 500, after.OpenBookedQuantity); // 300 + 200
                Assert.Equal(before.OpenDispatchedQuantity + 50, after.OpenDispatchedQuantity);
                Assert.Equal(before.OpenRemainingQuantity + 450, after.OpenRemainingQuantity);
            }
            finally { await env.CleanAsync(); }
        }

        [SkippableFact]
        public async Task OverdueAndDueToday_CountOrders_NotLines()
        {
            var env = await OpenAsync();
            try
            {
                var (plant, s1, s2) = await TwoVarietiesAsync(env);
                var before = (await NowAsync(env)).Seedlings;

                var late = await InsertLineAsync(env, plant, s1, 100, null, AppClock.Today.AddDays(-3));
                await env.ExecAsync("UPDATE dbo.Bookings SET OrderBookingId = Id WHERE Id = @Id", ("@Id", late));
                await InsertLineAsync(env, plant, s2, 40, late, AppClock.Today.AddDays(-3));
                await InsertLineAsync(env, plant, s1, 70, null, AppClock.Today);
                // fully dispatched but still Pending: nothing left, so not overdue
                await InsertLineAsync(env, plant, s1, 10, null, AppClock.Today.AddDays(-3), dispatched: 10);

                var after = (await NowAsync(env)).Seedlings;
                Assert.Equal(before.OverdueOrders + 1, after.OverdueOrders);
                Assert.Equal(before.OverdueRemainingQuantity + 140, after.OverdueRemainingQuantity);
                Assert.Equal(before.DueTodayOrders + 1, after.DueTodayOrders);
                Assert.Equal(before.DueTodayRemainingQuantity + 70, after.DueTodayRemainingQuantity);
            }
            finally { await env.CleanAsync(); }
        }

        [SkippableFact]
        public async Task OpenBookingByVariety_SumsTheRemainingOfEachVariety()
        {
            var env = await OpenAsync();
            try
            {
                var (plant, s1, _) = await TwoVarietiesAsync(env);
                var before = (await NowAsync(env)).OpenBookingByVariety.Where(v => v.SpeciesId == s1).Sum(v => v.Quantity);
                await InsertLineAsync(env, plant, s1, 123, null, AppClock.Today.AddDays(5));
                await InsertLineAsync(env, plant, s1, 77, null, AppClock.Today.AddDays(5), dispatched: 7);
                var rows = (await NowAsync(env)).OpenBookingByVariety.Where(v => v.SpeciesId == s1).ToList();
                Assert.Single(rows);
                Assert.Equal(before + 193, rows[0].Quantity);
                Assert.False(string.IsNullOrWhiteSpace(rows[0].VarietyName));
            }
            finally { await env.CleanAsync(); }
        }

        [SkippableFact]
        public async Task ReadyStock_Reconciles_WithTheTable_AndWithItsComputedColumns()
        {
            var env = await OpenAsync();
            var data = await NowAsync(env);
            var totals = ManagementDashboardCalculations.Totals(data.ReadyStock);

            Assert.Equal(await env.ScalarAsync<decimal>("SELECT ISNULL(SUM(PhysicalQuantity), 0) FROM dbo.ReadyStock"), totals.Physical);
            Assert.Equal(await env.ScalarAsync<decimal>("SELECT ISNULL(SUM(ReservedQuantity), 0) FROM dbo.ReadyStock"), totals.Reserved);
            Assert.Equal(await env.ScalarAsync<decimal>("SELECT ISNULL(SUM(AvailableQuantity), 0) FROM dbo.ReadyStock"), totals.Available);
            Assert.Equal(totals.Physical - totals.Reserved, totals.Available);

            var areas = ManagementDashboardCalculations.BuildAreaRows(data.ReadyStock, data.PottedStock);
            Assert.Equal(totals.Physical, areas.Sum(a => a.ReadyPhysical));
            Assert.All(areas, a => Assert.Equal(a.ReadyPhysical, a.Polyhouses.Sum(p => p.Physical)));
            Assert.Equal(await env.ScalarAsync<decimal>("SELECT ISNULL(SUM(PhysicalQuantity), 0) FROM dbo.PottedPlantStock"), areas.Sum(a => a.PottedPhysical));
            Assert.Equal(totals.Physical, ManagementDashboardCalculations.ByPlantType(data.ReadyStock).Sum(p => p.Physical));
        }

        [SkippableFact]
        public async Task Top3ByReadyStock_MatchesAnIndependentQuery()
        {
            var env = await OpenAsync();
            var data = await NowAsync(env);
            var top = ManagementDashboardCalculations.RankTop(ManagementDashboardCalculations.ReadyPhysicalByVariety(data.ReadyStock), 3);

            var expected = new List<(int Id, decimal Qty)>();
            using (var conn = new SqlConnection(env.Cs))
            {
                await conn.OpenAsync();
                using var cmd = new SqlCommand(@"
SELECT TOP 3 SpeciesId, SUM(PhysicalQuantity) FROM dbo.ReadyStock
GROUP BY SpeciesId HAVING SUM(PhysicalQuantity) > 0
ORDER BY SUM(PhysicalQuantity) DESC", conn);
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) expected.Add((r.GetInt32(0), r.GetDecimal(1)));
            }
            Assert.Equal(expected.Select(e => e.Qty), top.Select(t => t.Quantity));
            Assert.All(top, t => Assert.False(string.IsNullOrWhiteSpace(t.VarietyName)));
            Assert.All(top, t => Assert.DoesNotContain('\n', t.VarietyName));
        }

        [SkippableFact]
        public async Task Period_UtcTimestamps_CountOnTheirIndiaDay()
        {
            var env = await OpenAsync();
            try
            {
                var readyStockId = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.ReadyStock ORDER BY Id");
                // 19:00 UTC on 20 Jan = 00:30 IST on 21 Jan
                await env.ExecAsync($@"
INSERT INTO dbo.ReadyStockTransactions (ReadyStockId, TransactionDate, TransactionType, Quantity, BeforeQuantity, Remarks, CreatedAt)
VALUES (@R, '2020-01-20T19:00:00', 'Confirmed', 37, 0, N'{Marker}', SYSUTCDATETIME())", ("@R", readyStockId));

                Assert.Equal(37m, (await env.Repo.GetOverviewAsync(Day(TestDay), TestDay)).Production.ReadyApprovedQuantity);
                Assert.Equal(0m, (await env.Repo.GetOverviewAsync(Day(TestDay.AddDays(-1)), TestDay)).Production.ReadyApprovedQuantity);

                // The production-detail trend buckets it on the India day as well.
                var trend = await env.Repo.GetSowingVsReadyTrendAsync(new ManagementDashboardFilters { FromDate = TestDay.AddDays(-1), ToDate = TestDay });
                var point = Assert.Single(trend, p => p.Series == "Confirmed Ready");
                Assert.Equal(TestDay, point.BucketDate.Date);
            }
            finally { await env.CleanAsync(); }
        }

        // ---- midnight IST boundaries -------------------------------------------------
        // 2020-01-20 20:30 UTC = 2020-01-21 02:00 IST (belongs to 21 Jan);
        // 2020-01-20 18:29 UTC = 2020-01-20 23:59 IST (belongs to 20 Jan).
        private static readonly DateTime AfterMidnightUtc = new(2020, 1, 20, 20, 30, 0);
        private static readonly DateTime BeforeMidnightUtc = new(2020, 1, 20, 18, 29, 0);

        private static Task InsertApprovalAsync(Env env, int readyStockId, DateTime utc, decimal qty)
            => env.ExecAsync($@"
INSERT INTO dbo.ReadyStockTransactions (ReadyStockId, TransactionDate, TransactionType, Quantity, BeforeQuantity, Remarks, CreatedAt)
VALUES (@R, @T, 'Confirmed', @Q, 0, N'{Marker}', SYSUTCDATETIME())", ("@R", readyStockId), ("@T", utc), ("@Q", qty));

        // A Completed potted-plant dispatch stamped at a given UTC instant (copies a real
        // booking's keys so CK_Dispatches_MatchesBooking holds; quantity 1).
        private static Task InsertPottedDispatchAsync(Env env, DateTime utc, string code)
            => env.ExecAsync($@"
INSERT INTO dbo.Dispatches (DispatchCode, PottedPlantBookingId, PottedPlantStockId, SpeciesId, PotSize, AreaId, Quantity, DispatchDate, Status, Remarks, CreatedDate, CreatedBy)
SELECT TOP 1 @Code, b.Id, b.PottedPlantStockId, b.SpeciesId, b.PotSize, b.AreaId, 1, @T, 'Completed', N'{Marker}', @T, N'{Marker}'
FROM dbo.PottedPlantBookings b WHERE b.Quantity >= 1 ORDER BY b.Id", ("@Code", code), ("@T", utc));

        private static async Task<PlantStockManager.Pages.ManagementDashboard.IndexModel> LoadPageAsync(Env env, string range, DateTime? from = null, DateTime? to = null)
        {
            var page = ActivatorUtilities.CreateInstance<PlantStockManager.Pages.ManagementDashboard.IndexModel>(env.Services);
            page.Range = range;
            page.FromDate = from;
            page.ToDate = to;
            await page.OnGetAsync();
            return page;
        }

        [SkippableFact]
        public async Task ReadyApproval_AtMidnightIST_CountsOnItsIndiaDay_BothDirections()
        {
            var env = await OpenAsync();
            try
            {
                var rs = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.ReadyStock ORDER BY Id");
                await InsertApprovalAsync(env, rs, AfterMidnightUtc, 11);
                await InsertApprovalAsync(env, rs, BeforeMidnightUtc, 5);

                // "Today" on 21 Jan (India) sees only the 02:00 IST approval; 20 Jan only the 23:59 one.
                var on21 = await env.Repo.GetOverviewAsync(DashboardPeriod.Resolve(DashboardPeriod.Today, null, null, TestDay), TestDay);
                var on20 = await env.Repo.GetOverviewAsync(DashboardPeriod.Resolve(DashboardPeriod.Today, null, null, TestDay.AddDays(-1)), TestDay.AddDays(-1));
                Assert.Equal(11m, on21.Production.ReadyApprovedQuantity);
                Assert.Equal(5m, on20.Production.ReadyApprovedQuantity);

                var trend = await env.Repo.GetSowingVsReadyTrendAsync(new ManagementDashboardFilters { FromDate = TestDay.AddDays(-1), ToDate = TestDay });
                var confirmed = trend.Where(p => p.Series == "Confirmed Ready").ToDictionary(p => p.BucketDate.Date, p => p.Value);
                Assert.Equal(11m, confirmed[TestDay]);
                Assert.Equal(5m, confirmed[TestDay.AddDays(-1)]);
            }
            finally { await env.CleanAsync(); }
        }

        [SkippableFact]
        public async Task PottedDispatch_AtMidnightIST_DailyReportAndDashboardAgree()
        {
            var env = await OpenAsync();
            try
            {
                await InsertPottedDispatchAsync(env, AfterMidnightUtc, "E2E-DASH-PD-1");
                await InsertPottedDispatchAsync(env, BeforeMidnightUtc, "E2E-DASH-PD-2");

                var reports = env.Services.GetRequiredService<DailyReportService>();
                var report21 = await reports.BuildReportForDateAsync(TestDay);
                var report20 = await reports.BuildReportForDateAsync(TestDay.AddDays(-1));
                Assert.Equal((1, 1m), (report21.PottedPlantDispatchCount, report21.PottedPlantDispatchQuantity));
                Assert.Equal((1, 1m), (report20.PottedPlantDispatchCount, report20.PottedPlantDispatchQuantity));

                // The dashboard for the same India day shows the same figures, and its trend
                // puts the dispatch in that day's bar.
                foreach (var (day, report) in new[] { (TestDay, report21), (TestDay.AddDays(-1), report20) })
                {
                    var page = await LoadPageAsync(env, DashboardPeriod.Custom, day, day);
                    Assert.Equal(report.PottedPlantDispatchCount, page.Overview.Potted.CompletedDispatchCountInRange);
                    Assert.Equal(report.PottedPlantDispatchQuantity, page.Overview.Potted.CompletedDispatchQuantityInRange);
                    var bar = Assert.Single(page.Dashboard.OutletDispatchTrend);
                    Assert.Equal((day, 1m), (bar.BucketDate.Date, bar.Value));
                }
            }
            finally { await env.CleanAsync(); }
        }

        [SkippableFact]
        public async Task SeedlingBooking_AtMidnightIST_CountsOnItsIndiaDay_InDashboardAndDailyReport()
        {
            var env = await OpenAsync();
            try
            {
                // Own dates: 2020-01-20 is used by BookingRepositoryE2ETests.
                var day = new DateTime(2020, 2, 11);
                var after = new DateTime(2020, 2, 10, 20, 30, 0);    // 11 Feb 02:00 IST
                var before = new DateTime(2020, 2, 10, 18, 29, 0);   // 10 Feb 23:59 IST
                var (plant, s1, _) = await TwoVarietiesAsync(env);
                // BookingDate is SQL Server local time (GETDATE()): store each instant the way the
                // application does, using SQL Server's own offset.
                foreach (var (utc, qty) in new[] { (after, 30), (before, 7) })
                    await env.ExecAsync($@"
INSERT INTO dbo.Bookings (SpeciesId, Quantity, PlantId, BookingDate, AddedBy, Status, CustomerName)
VALUES (@S, @Q, @P, DATEADD(MINUTE, DATEPART(TZOFFSET, SYSDATETIMEOFFSET()), @Utc), N'{Marker}', N'Pending', N'{Marker}')",
                        ("@S", s1), ("@Q", qty), ("@P", plant), ("@Utc", utc));

                var bookingRepo = env.Services.GetRequiredService<BookingRepository>();
                Assert.Equal((1, 30m), await bookingRepo.GetBookingCountByBookingDateAsync(day, day.AddDays(1)));
                Assert.Equal((1, 7m), await bookingRepo.GetBookingCountByBookingDateAsync(day.AddDays(-1), day));

                var report = await env.Services.GetRequiredService<DailyReportService>().BuildReportForDateAsync(day);
                var page = await LoadPageAsync(env, DashboardPeriod.Custom, day, day);
                Assert.Equal(1, page.Overview.Seedlings.OrdersReceived);
                Assert.Equal(report.SeedlingBookingsCount, page.Overview.Seedlings.OrdersReceived);
                Assert.Equal(report.SeedlingBookingsQuantity, page.Overview.Seedlings.QuantityReceived);
            }
            finally { await env.CleanAsync(); }
        }

        [SkippableFact]
        public async Task RecentActivity_BookingTime_IsShownInIndiaTime()
        {
            var env = await OpenAsync();
            try
            {
                var (plant, s1, _) = await TwoVarietiesAsync(env);
                var id = await InsertLineAsync(env, plant, s1, 9, null, AppClock.Today);
                await env.ExecAsync("UPDATE dbo.Bookings SET OrderBookingId = Id WHERE Id = @Id", ("@Id", id));
                var item = Assert.Single((await NowAsync(env)).RecentActivity, a => a.Kind == "Booking" && a.Id == id);
                Assert.InRange(item.WhenIndia, AppClock.Now.AddMinutes(-5), AppClock.Now.AddMinutes(1));
                Assert.Equal($"Booking #{id} -- {Marker} -- 1 variety, 9 plants", item.Text);
            }
            finally { await env.CleanAsync(); }
        }

        // ---- current state vs period ----------------------------------------------------

        [SkippableFact]
        public async Task NowFigures_DoNotChangeWithThePeriod_PeriodFiguresDo()
        {
            var env = await OpenAsync();
            var pages = new[]
            {
                await LoadPageAsync(env, DashboardPeriod.Today), await LoadPageAsync(env, DashboardPeriod.Last7Days),
                await LoadPageAsync(env, DashboardPeriod.Last30Days), await LoadPageAsync(env, DashboardPeriod.ThisMonth),
                await LoadPageAsync(env, DashboardPeriod.Custom, new DateTime(2001, 1, 1), new DateTime(2001, 1, 31)),
            };
            static string Now(ManagementOverview o) => string.Join(";", new object[]
            {
                o.ReadyTotals.Physical, o.ReadyTotals.Reserved, o.ReadyTotals.Available, o.PottedTotals.Physical, o.PottedTotals.Reserved,
                o.Seedlings.OpenOrders, o.Seedlings.OpenLines, o.Seedlings.OpenRemainingQuantity, o.Seedlings.OverdueOrders,
                o.Maintenance.Active, o.Maintenance.Overdue, o.Store.PendingReview, o.Store.LowStockCount,
                o.Potted.PendingBookings, o.Potted.RemainingBookingQuantity, o.Labour.At9, o.Suggestions.ActiveProblems,
                string.Join("|", o.TopByReadyStock.Select(t => t.VarietyName + t.Quantity)),
                string.Join("|", o.TopByOpenBooking.Select(t => t.VarietyName + t.Quantity)),
                string.Join("|", o.Areas.Select(a => a.AreaName + a.ReadyPhysical)),
            });
            var first = Now(pages[0].Overview);
            Assert.All(pages, p => Assert.Equal(first, Now(p.Overview)));

            // A 2001 window holds no activity at all: every PERIOD figure is zero.
            var empty = pages[^1].Overview;
            Assert.Equal(0, empty.Seedlings.OrdersReceived);
            Assert.Equal(0, empty.Seedlings.Dispatches);
            Assert.Equal(0m, empty.Production.SownQuantity);
            Assert.Equal(0, empty.Potted.CompletedDispatchCountInRange);
            Assert.Equal(0, empty.Maintenance.ClosedInPeriod);
        }

        // ---- performance: no N+1 ---------------------------------------------------------

        private sealed class CommandCounter : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
        {
            private readonly List<IDisposable> _subs = new();
            private int _commands;
            public int Commands => _commands;
            public CommandCounter() => _subs.Add(DiagnosticListener.AllListeners.Subscribe(this));
            public void OnNext(DiagnosticListener l) { if (l.Name == "SqlClientDiagnosticListener") lock (_subs) _subs.Add(l.Subscribe(this)); }
            public void OnNext(KeyValuePair<string, object?> e) { if (e.Key.EndsWith("WriteCommandBefore")) Interlocked.Increment(ref _commands); }
            public void OnCompleted() { }
            public void OnError(Exception error) { }
            public void Dispose() { lock (_subs) _subs.ForEach(s => s.Dispose()); }
        }

        [SkippableFact]
        public async Task WholePage_RunsAFixedSmallNumberOfQueries_WhateverTheDataVolume()
        {
            var env = await OpenAsync();
            async Task<int> CountAsync(string range, DateTime? from = null, DateTime? to = null)
            {
                using var counter = new CommandCounter();
                await LoadPageAsync(env, range, from, to);
                return counter.Commands;
            }
            var empty = await CountAsync(DashboardPeriod.Custom, new DateTime(2001, 1, 1), new DateTime(2001, 1, 1));   // no activity
            var wide = await CountAsync(DashboardPeriod.Last30Days);                                                    // much more
            Assert.InRange(empty, 1, 60);
            Assert.Equal(empty, wide);   // the number of queries does not grow with the rows read
        }

        [SkippableFact]
        public async Task EmptyPeriod_GivesZeros_NotErrors()
        {
            var env = await OpenAsync();
            var data = await env.Repo.GetOverviewAsync(Day(new DateTime(2001, 1, 1)), AppClock.Today);
            Assert.Equal(0, data.Production.Sowings);
            Assert.Equal(0m, data.Production.SownQuantity);
            Assert.Equal(0m, data.Production.CuttingsProduced);
            Assert.Equal(0m, data.Production.PotsProduced);
            Assert.Equal(0m, data.Production.ReadyApprovedQuantity);
            Assert.Equal(0, data.MaintenanceClosedInPeriod);
        }

        [SkippableTheory]
        [InlineData(null, null)]
        [InlineData("today", null)]
        [InlineData("month", 2)]
        public async Task WholePage_LoadsEndToEnd_AndItsFiguresReconcile(string? range, int? detailAreaId)
        {
            var env = await OpenAsync();
            var page = ActivatorUtilities.CreateInstance<PlantStockManager.Pages.ManagementDashboard.IndexModel>(env.Services);
            page.Range = range;
            page.AreaId = detailAreaId;
            await page.OnGetAsync();

            var o = page.Overview;
            Assert.Equal(o.ReadyTotals.Physical - o.ReadyTotals.Reserved, o.ReadyTotals.Available);
            Assert.Equal(o.ReadyTotals.Physical, o.Areas.Sum(a => a.ReadyPhysical));
            Assert.True(o.TopByReadyStock.Count <= 3);
            Assert.Equal(o.TopByReadyStock.OrderByDescending(t => t.Quantity).Select(t => t.Quantity), o.TopByReadyStock.Select(t => t.Quantity));
            Assert.Equal(o.ReadyTotals.Physical, o.ReadyStockByVarietyChart.Sum(c => c.Value));
            Assert.True(o.Seedlings.OpenOrders <= o.Seedlings.OpenLines);
            Assert.True(o.Seedlings.OpenRemainingQuantity <= o.Seedlings.OpenBookedQuantity);
            Assert.True(o.RecentActivity.Count <= PlantStockManager.Pages.ManagementDashboard.IndexModel.RecentActivityItems);
            // the overview is company-wide even when the detail section is narrowed to one Area
            if (detailAreaId.HasValue)
                Assert.Equal(await env.ScalarAsync<decimal>("SELECT ISNULL(SUM(PhysicalQuantity), 0) FROM dbo.ReadyStock"), o.ReadyTotals.Physical);
        }

        [SkippableFact]
        public async Task RecentActivity_IsNewestFirst_AndBounded()
        {
            var env = await OpenAsync();
            var data = await NowAsync(env);
            var merged = ManagementDashboardRepository.MergeActivity(data.RecentActivity, 12);
            Assert.True(merged.Count <= 12);
            Assert.Equal(merged.OrderByDescending(a => a.WhenIndia).Select(a => a.WhenIndia), merged.Select(a => a.WhenIndia));
            Assert.All(data.RecentActivity.GroupBy(a => a.Kind), g => Assert.True(g.Count() <= ManagementDashboardRepository.RecentActivityPerSource));
        }
    }
}
