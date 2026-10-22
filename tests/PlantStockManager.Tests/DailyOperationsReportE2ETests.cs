using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using R = PlantStockManager.Services.DailyOperationsReportRules;

namespace PlantStockManager.Tests
{
    // Daily Operations Report (Data/DailyOperationsReportRepository.cs + the rules) against a
    // REAL scratch copy of the database (PSM_SCRATCH_CONNECTION, name must start with
    // PlantsIMS2_Scratch_). Two kinds of check:
    //   * fixture rows dated March 2017 (nothing real, and no other test, uses that month) placed on both sides of
    //     India midnight, to prove exact-day selection, IST boundaries, order grouping, units
    //     and the employee categories; removed again afterwards;
    //   * the scratch copy's own 2026 data, compared with INDEPENDENT queries (no double
    //     counting, Ready Stock rebuilt backwards from today's table, range == single days).
    [Collection("ScratchDb")]
    public class DailyOperationsReportE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";
        private const string Marker = "E2E_DAILY_OPS";
        private static readonly DateTime D20 = new(2017, 3, 20), D21 = new(2017, 3, 21), D22 = new(2017, 3, 22);

        private sealed class Env
        {
            public required string Cs { get; init; }
            public required DailyOperationsReportRepository Repo { get; init; }

            public async Task<T> ScalarAsync<T>(string sql, params (string, object)[] ps)
            {
                using var conn = new SqlConnection(Cs);
                await conn.OpenAsync();
                using var cmd = new SqlCommand(sql, conn);
                foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
                return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync())!, typeof(T));
            }

            public async Task<List<object[]>> RowsAsync(string sql, params (string, object)[] ps)
            {
                var rows = new List<object[]>();
                using var conn = new SqlConnection(Cs);
                await conn.OpenAsync();
                using var cmd = new SqlCommand(sql, conn);
                foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    var row = new object[r.FieldCount];
                    r.GetValues(row);
                    rows.Add(row);
                }
                return rows;
            }

            public Task<int> ExecAsync(string sql, params (string, object)[] ps) => ScalarAsync<int>(sql + "; SELECT @@ROWCOUNT", ps);

            public async Task CleanAsync()
            {
                await ExecAsync($"UPDATE dbo.Bookings SET OrderBookingId = NULL, ParentBookingId = NULL WHERE AddedBy = N'{Marker}'");
                await ExecAsync($"DELETE FROM dbo.Bookings WHERE AddedBy = N'{Marker}'");
                await ExecAsync($"DELETE FROM dbo.PottedPlantStockTransactions WHERE Remarks = N'{Marker}'");
                await ExecAsync($"DELETE FROM dbo.SeedStockTransactions WHERE Remarks = N'{Marker}'");
                await ExecAsync($"DELETE FROM dbo.FertilizerUsage WHERE Remarks = N'{Marker}'");
                await ExecAsync("DELETE FROM dbo.UserLoginHistory WHERE LoginAt < '2018-01-01'");
            }
        }

        internal static string? ScratchConnectionOrSkip()
        {
            var cs = Environment.GetEnvironmentVariable(ConnectionVariable);
            Skip.If(string.IsNullOrWhiteSpace(cs), $"Set {ConnectionVariable} to a {RequiredPrefix}* database connection string to run the end-to-end tests.");
            using var conn = new SqlConnection(cs);
            conn.Open();
            using var cmd = new SqlCommand("SELECT DB_NAME()", conn);
            var name = (string)cmd.ExecuteScalar()!;
            if (!name.StartsWith(RequiredPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException($"Refusing to run end-to-end tests against '{name}'. Only databases named {RequiredPrefix}* are allowed.");
            return cs;
        }

        private static async Task<Env> OpenAsync()
        {
            var cs = ScratchConnectionOrSkip()!;
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs }).Build();
            var env = new Env { Cs = cs, Repo = new DailyOperationsReportRepository(new DatabaseHelper(config)) };
            await env.CleanAsync();
            return env;
        }

        private static async Task<DailyOperationsReport> ReportAsync(Env env, DateTime day)
            => R.Build(await env.Repo.GetAsync(day, day), day, AppClock.Today, AppClock.Now);

        // An India wall-clock time as the UTC value a SYSUTCDATETIME() column would hold.
        private static DateTime Utc(DateTime india) => AppClock.IndiaToUtc(india);

        // ---- Fixture: 20-22 March 2017, both sides of India midnight ---------------

        private sealed record Fixture(int U1, string N1, int U2, string N2, int U3, string N3, string SeedUnit, string UnitA, string UnitB, int OrderId);

        private static async Task<Fixture> SeedFixtureAsync(Env env)
        {
            var users = await env.RowsAsync("SELECT TOP 3 Id, Name FROM dbo.IMSUsers WHERE IsActive = 1 ORDER BY Id");
            Skip.If(users.Count < 3, "Needs three active users.");
            int U(int i) => (int)users[i][0];
            string N(int i) => ((string)users[i][1]).Trim();

            var potted = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.PottedPlantStock ORDER BY Id");
            var seed = await env.RowsAsync("SELECT TOP 1 Id, LTRIM(RTRIM(Unit)) FROM dbo.SeedStock ORDER BY Id");
            var fert = await env.RowsAsync(@"
SELECT TOP 2 s.StockId, LTRIM(RTRIM(u.UnitName)) FROM dbo.FertilizerStock s INNER JOIN dbo.UnitMaster u ON u.UnitId = s.UnitId
WHERE s.StockId IN (SELECT MIN(StockId) FROM dbo.FertilizerStock GROUP BY UnitId) ORDER BY s.StockId");
            Skip.If(fert.Count < 2, "Needs fertilizer stock in two different units.");
            var (plant, species) = (await env.RowsAsync("SELECT TOP 1 PlantTypeId, Id FROM dbo.PlantSpecies WHERE PlantTypeId IS NOT NULL ORDER BY Id"))
                .Select(r => ((int)r[0], (int)r[1])).Single();
            var serverOffset = await env.ScalarAsync<int>("SELECT DATEPART(TZOFFSET, SYSDATETIMEOFFSET())");
            DateTime ServerLocal(DateTime india) => Utc(india).AddMinutes(serverOffset); // dbo.Bookings.BookingDate is GETDATE()

            // Logins: U1 at 23:59:59 IST on the 20th and 15:30 IST on the 21st; U2 at 00:00:00 IST on the 21st.
            foreach (var (u, at) in new[] { (U(0), D20.AddHours(23).AddMinutes(59).AddSeconds(59)), (U(0), D21.AddHours(15).AddMinutes(30)), (U(1), D21) })
                await env.ExecAsync("INSERT INTO dbo.UserLoginHistory (UserId, LoginAt) VALUES (@U, @At)", ("@U", u), ("@At", Utc(at)));

            // Wastage + activity: potted wastage by U2 at 10:30 IST on the 21st (3), unattributed at 23:50 IST on the 20th (2);
            // seed-lot wastage by U3 at 00:15 IST on the 21st (4 seed units) -> U3 worked without logging in that day.
            // Recorded at that moment too (CreatedAt = TransactionDate): activity follows the RECORDED time (2026-10-10).
            async Task Ledger(string table, string idCol, int id, int? user, decimal qty, DateTime india) =>
                await env.ExecAsync($@"INSERT INTO dbo.{table} ({idCol}, TransactionDate, TransactionType, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)
VALUES (@Id, @At, N'Wastage', @Q, 10, @U, N'{Marker}', @At)", ("@Id", id), ("@At", Utc(india)), ("@Q", -qty), ("@U", (object?)user ?? DBNull.Value));
            await Ledger("PottedPlantStockTransactions", "PottedPlantStockId", potted, U(1), 3, D21.AddHours(10).AddMinutes(30));
            await Ledger("PottedPlantStockTransactions", "PottedPlantStockId", potted, null, 2, D20.AddHours(23).AddMinutes(50));
            await Ledger("SeedStockTransactions", "SeedStockId", (int)seed[0][0], U(2), 4, D21.AddMinutes(15));

            // Fertilizer on the 21st in two units (not attributed to anyone).
            foreach (var (stock, qty) in new[] { ((int)fert[0][0], 2.5m), ((int)fert[1][0], 3m), ((int)fert[0][0], 1.25m) })
                await env.ExecAsync($"INSERT INTO dbo.FertilizerUsage (StockId, UsedQuantity, IssueDate, ReceivedBy, Remarks) VALUES (@S, @Q, @D, N'{Marker}', N'{Marker}')",
                    ("@S", stock), ("@Q", qty), ("@D", D21));

            // Seedling bookings: order B (one line) at 23:55 IST on the 20th; order A with two lines on the 21st
            // (00:05 and 09:00 IST) plus a revision line added to it on the 22nd.
            async Task<int> Line(DateTime india, int? order, int? parent) => await env.ScalarAsync<int>($@"
INSERT INTO dbo.Bookings (SpeciesId, Quantity, PlantId, BookingDate, AddedBy, Status, CustomerName, OrderBookingId, ParentBookingId)
VALUES (@S, 100, @P, @B, N'{Marker}', N'Pending', N'{Marker}', @O, @Par); SELECT CAST(SCOPE_IDENTITY() AS INT);",
                ("@S", species), ("@P", plant), ("@B", ServerLocal(india)), ("@O", (object?)order ?? DBNull.Value), ("@Par", (object?)parent ?? DBNull.Value));
            await Line(D20.AddHours(23).AddMinutes(55), null, null);
            var orderA = await Line(D21.AddMinutes(5), null, null);
            await env.ExecAsync("UPDATE dbo.Bookings SET OrderBookingId = Id WHERE Id = @Id", ("@Id", orderA));
            await Line(D21.AddHours(9), orderA, null);
            await Line(D22.AddHours(10), orderA, orderA);

            return new Fixture(U(0), N(0), U(1), N(1), U(2), N(2), (string)seed[0][1], (string)fert[0][1], (string)fert[1][1], orderA);
        }

        [SkippableFact]
        public async Task SelectedDate_GetsExactlyThatIndiaDay_OnBothSidesOfMidnight()
        {
            var env = await OpenAsync();
            try
            {
                var f = await SeedFixtureAsync(env);
                var r20 = await ReportAsync(env, D20);
                var r21 = await ReportAsync(env, D21);
                var r22 = await ReportAsync(env, D22);

                // Wastage (UTC ledger timestamps): 23:50 IST belongs to the 20th, 10:30 IST to the 21st.
                Assert.Equal(2m, r20.Wastage.Quantity);
                Assert.Equal(3m, r21.Wastage.Quantity);
                Assert.Equal(new[] { new UnitQuantityLine(f.SeedUnit, 4) }, r21.SeedLotWastage);
                Assert.True(r22.Wastage.Available);
                Assert.Equal(0m, r22.Wastage.Quantity);

                // Logins (UTC): 23:59:59 IST on the 20th vs 00:00:00 IST on the 21st.
                Assert.Equal(new[] { f.N1 }, r20.LoggedIn);
                Assert.Equal(new[] { f.N1, f.N2 }.OrderBy(n => n, StringComparer.Ordinal), r21.LoggedIn.OrderBy(n => n, StringComparer.Ordinal));
                Assert.Equal(1, r20.LoginEvents);
                Assert.Equal(2, r21.LoginEvents); // U1 at 15:30 + U2 at 00:00 on the 21st
            }
            finally { await env.CleanAsync(); }
        }

        [SkippableFact]
        public async Task SeedlingBookings_CountOrdersOnTheirFirstLinesDay_RevisionsNeverRecount()
        {
            var env = await OpenAsync();
            try
            {
                await SeedFixtureAsync(env);
                var r20 = await ReportAsync(env, D20);
                var r21 = await ReportAsync(env, D21);
                var r22 = await ReportAsync(env, D22);

                Assert.Equal((1, 1, 100m), (r20.SeedlingBookings.Count, r20.SeedlingBookings.Lines, r20.SeedlingBookings.Quantity));
                Assert.Equal((1, 2, 200m), (r21.SeedlingBookings.Count, r21.SeedlingBookings.Lines, r21.SeedlingBookings.Quantity));
                Assert.True(r22.SeedlingBookings.Available);
                Assert.Equal(0, r22.SeedlingBookings.Count);
            }
            finally { await env.CleanAsync(); }
        }

        [SkippableFact]
        public async Task Fertilizer_UsesIssueDate_AndKeepsEachUnitSeparate()
        {
            var env = await OpenAsync();
            try
            {
                var f = await SeedFixtureAsync(env);
                var r21 = await ReportAsync(env, D21);
                Assert.Equal(3, r21.Fertilizer.Count);
                Assert.Equal(3.75m, r21.FertilizerByUnit.Single(u => u.Unit == f.UnitA).Quantity);
                Assert.Equal(3m, r21.FertilizerByUnit.Single(u => u.Unit == f.UnitB).Quantity);
                Assert.Equal(0, (await ReportAsync(env, D22)).Fertilizer.Count);
            }
            finally { await env.CleanAsync(); }
        }

        [SkippableFact]
        public async Task EmployeeCategories_FollowTheRecordedLoginAndActivityEvidence()
        {
            var env = await OpenAsync();
            try
            {
                var f = await SeedFixtureAsync(env);
                var r21 = await ReportAsync(env, D21);
                var employees = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.IMSUsers WHERE IsActive = 1");

                Assert.True(r21.LoginDataAvailable);
                Assert.Equal(employees, r21.TotalEmployees);
                Assert.Equal(new[] { f.N2 }, r21.ActiveUsers);              // logged in + wastage entry
                Assert.Equal(new[] { f.N1 }, r21.NoActivityRecorded);       // logged in, nothing recorded
                Assert.Contains(f.N3, r21.NotLoggedIn);
                Assert.Equal(new[] { f.N3 }, r21.ActivityWithoutLogin);    // recorded work, no login that day
                Assert.Equal(employees - 2, r21.NotLoggedIn.Count);
                Assert.Equal(r21.TotalEmployees, r21.LoggedIn.Count + r21.NotLoggedIn.Count);

                // Login tracking now starts at the 20th's 23:59:59 IST fixture: the 19th has no evidence.
                var r19 = await ReportAsync(env, D20.AddDays(-1));
                Assert.False(r19.LoginDataAvailable);
                Assert.Empty(r19.NotLoggedIn);
                Assert.NotNull((await ReportAsync(env, D20)).LoginPartialDayNote);
            }
            finally { await env.CleanAsync(); }
        }

        [SkippableFact]
        public async Task NoTransactionsDay_IsZero_WhileNoRecordsYet_IsNotAvailable()
        {
            var env = await OpenAsync();
            try
            {
                await SeedFixtureAsync(env);
                var r22 = await ReportAsync(env, D22);
                Assert.True(r22.Fertilizer.Available);          // fertilizer records begin on the 21st
                Assert.Equal("0", R.FertilizerText(r22));
                Assert.False(r22.Sowing.Available);             // real sowing records only begin in 2026
                Assert.False(r22.OutletSales.Available);
                Assert.False(r22.ReadyStockAvailable);          // the Ready Stock ledger begins in 2026 too
                var text = R.ToPlainText(r22);
                Assert.Contains("Sowing: Not available\n", text);
                Assert.Contains("Fertilizer Usage: 0\n", text);

                var r19 = await ReportAsync(env, D20.AddDays(-1));
                Assert.False(r19.SeedlingBookings.Available);   // bookings begin with the 20th's fixture
                Assert.False(r19.Wastage.Available);
            }
            finally { await env.CleanAsync(); }
        }

        // ---- The scratch copy's own data, against independent queries ----------

        private static async Task<(DateTime From, DateTime To)> RealRangeAsync(Env env)
        {
            var first = await env.ScalarAsync<DateTime>("SELECT ISNULL(CAST(MIN(SowingDate) AS date), CAST(GETDATE() AS date)) FROM dbo.SeedSowings WHERE SowingDate >= '2021-01-01'");
            var from = first.AddDays(-2);
            var to = AppClock.Today;
            if ((to - from).Days >= R.MaxHistoryDays) from = to.AddDays(-(R.MaxHistoryDays - 1));
            return (from, to);
        }

        [SkippableFact]
        public async Task Sowing_EveryNonCancelledSowingOnce_SeedAndCuttingTogether()
        {
            var env = await OpenAsync();
            var (from, to) = await RealRangeAsync(env);
            var reports = R.BuildAll(await env.Repo.GetAsync(from, to), AppClock.Today, AppClock.Now);
            // 2026-10-12: with Bed Sowing, a bed counts once on its own sowing date and the tray batches
            // transplanted from it (SourceType BedSowing) are not sowings again -- nor (2026-10-13) the tray batches transplanted
            // from received Ready Stock (SourceType Transplant): BedSowingSchema.SowingActivitySql, the report's own rule.
            var bedReady = await env.ScalarAsync<int>(BedSowingSchema.ProbeSql) == 1;
            var expected = (await env.RowsAsync(@"
SELECT CAST(SowingDate AS date), COUNT(*), SUM(QuantitySown), SUM(CASE WHEN SourceType = N'Cutting' THEN QuantitySown ELSE 0 END)
FROM (SELECT SowingDate, SourceType, QuantitySown FROM dbo.SeedSowings WHERE Status <> 'Cancelled' AND SourceType <> N'BedSowing'"
                + (bedReady ? " AND SourceType <> N'Transplant'" : "")
                + (bedReady ? " UNION ALL SELECT CAST(SowingDate AS datetime2), N'BedSowing', Quantity FROM dbo.BedSowings WHERE Status <> N'Cancelled'" : "") + @") s
WHERE SowingDate >= @F AND SowingDate < @T GROUP BY CAST(SowingDate AS date)",
                ("@F", from), ("@T", to.AddDays(1)))).ToDictionary(x => (DateTime)x[0]);
            var cancelled = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings WHERE Status = 'Cancelled' AND SowingDate >= @F AND SowingDate < @T", ("@F", from), ("@T", to.AddDays(1)));

            foreach (var r in reports.Where(r => r.Sowing.Available))
            {
                var e = expected.TryGetValue(r.ReportDate, out var row) ? row : null;
                Assert.Equal(e == null ? 0 : (int)e[1], r.Sowing.Count);
                Assert.Equal(e == null ? 0m : (decimal)e[2], r.Sowing.Quantity);
                Assert.Equal(e == null ? 0m : (decimal)e[3], r.CuttingSowingQuantity);
                Assert.Equal(r.Sowing.Quantity, r.SeedSowingQuantity + r.CuttingSowingQuantity);
            }
            Assert.Equal(expected.Values.Sum(x => (decimal)x[2]), reports.Sum(r => r.Sowing.Quantity));
            _ = cancelled; // informational: cancelled sowings exist on this copy and are excluded above
        }

        [SkippableFact]
        public async Task Dispatch_CountsEventsNotLines_AndOnlyCompletedPottedDispatches()
        {
            var env = await OpenAsync();
            var (from, to) = await RealRangeAsync(env);
            var reports = R.BuildAll(await env.Repo.GetAsync(from, to), AppClock.Today, AppClock.Now);

            var seedling = (await env.RowsAsync(@"
SELECT d.DispatchDate, COUNT(DISTINCT d.Id), COUNT(l.Id), ISNULL(SUM(l.Quantity), 0)
FROM dbo.SeedlingDispatches d LEFT JOIN dbo.SeedlingDispatchLines l ON l.SeedlingDispatchId = d.Id
WHERE d.Status = 'Completed' AND d.DispatchDate >= @F AND d.DispatchDate < @T GROUP BY d.DispatchDate",
                ("@F", from), ("@T", to.AddDays(1)))).ToDictionary(x => (DateTime)x[0]);
            var potted = (await env.RowsAsync(@"
SELECT CAST(DATEADD(MINUTE, 330, DispatchDate) AS date), COUNT(*), SUM(Quantity) FROM dbo.Dispatches
WHERE Status = 'Completed' AND DispatchDate >= @F AND DispatchDate < @T GROUP BY CAST(DATEADD(MINUTE, 330, DispatchDate) AS date)",
                ("@F", Utc(from)), ("@T", Utc(to.AddDays(1))))).ToDictionary(x => (DateTime)x[0]);

            foreach (var r in reports)
            {
                if (r.SeedlingDispatch.Available)
                {
                    var e = seedling.TryGetValue(r.ReportDate, out var row) ? row : null;
                    Assert.Equal(e == null ? 0 : (int)e[1], r.SeedlingDispatch.Count);
                    Assert.Equal(e == null ? 0m : (decimal)e[3], r.SeedlingDispatch.Quantity);
                }
                if (r.PottedDispatch.Available)
                    Assert.Equal(potted.TryGetValue(r.ReportDate, out var p) ? (int)p[1] : 0, r.PottedDispatch.Count);
            }
        }

        // Rebuilt BACKWARDS: today's ReadyStock table minus every physical movement after the end
        // of each day -- a different computation from the report's forward ledger sum, and equal to
        // it only if the ledger explains every balance. Also: a past date is never today's figure
        // when stock moved since.
        [SkippableFact]
        public async Task ReadyStock_HistoricalDates_MatchTheTableRolledBack_NotTodaysStock()
        {
            var env = await OpenAsync();
            var (from, to) = await RealRangeAsync(env);
            var data = await env.Repo.GetAsync(from, to);
            var unreconciled = await env.ScalarAsync<int>(@"
SELECT COUNT(*) FROM dbo.ReadyStock rs
WHERE rs.Quantity - rs.DispatchedQuantity <> ISNULL((SELECT SUM(t.Quantity) FROM dbo.ReadyStockTransactions t
      WHERE t.ReadyStockId = rs.Id AND t.TransactionType NOT IN ('Reservation', 'ReservationRelease', 'TransferHold', 'TransferHoldRelease')), 0)
   OR rs.ReservedQuantity <> ISNULL((SELECT SUM(t.Quantity) FROM dbo.ReadyStockTransactions t
      WHERE t.ReadyStockId = rs.Id AND t.TransactionType IN ('Reservation', 'ReservationRelease')), 0)
   OR rs.InTransitQuantity <> ISNULL((SELECT SUM(t.Quantity) FROM dbo.ReadyStockTransactions t
      WHERE t.ReadyStockId = rs.Id AND t.TransactionType IN ('TransferHold', 'TransferHoldRelease')), 0)");
            Assert.Equal(unreconciled, data.Coverage.ReadyStockUnreconciledBatches);
            Skip.If(unreconciled > 0, "This copy's Ready Stock ledger does not reconcile; past dates are (correctly) not available.");

            var current = (await env.RowsAsync(@"
SELECT pt.Name, SUM(rs.Quantity - rs.DispatchedQuantity) FROM dbo.ReadyStock rs
INNER JOIN dbo.PlantSpecies ps ON ps.Id = rs.SpeciesId INNER JOIN dbo.PlantTypes pt ON pt.Id = ps.PlantTypeId GROUP BY pt.Name"))
                .ToDictionary(x => ((string)x[0]).Trim(), x => (decimal)x[1]);

            var currentNonZero = current.Where(c => c.Value != 0).OrderBy(c => c.Key).ToList();
            var sawDifferentFromToday = false;
            foreach (var r in R.BuildAll(data, AppClock.Today, AppClock.Now))
            {
                if (!r.ReadyStockAvailable)
                {
                    Assert.True(data.Coverage.ReadyStockFrom == null || r.ReportDate < data.Coverage.ReadyStockFrom);
                    continue;
                }
                var after = (await env.RowsAsync(@"
SELECT pt.Name, SUM(t.Quantity) FROM dbo.ReadyStockTransactions t
INNER JOIN dbo.ReadyStock rs ON rs.Id = t.ReadyStockId INNER JOIN dbo.PlantSpecies ps ON ps.Id = rs.SpeciesId
INNER JOIN dbo.PlantTypes pt ON pt.Id = ps.PlantTypeId
WHERE t.TransactionType NOT IN ('Reservation', 'ReservationRelease', 'TransferHold', 'TransferHoldRelease') AND t.TransactionDate >= @EndUtc GROUP BY pt.Name",
                    ("@EndUtc", Utc(r.ReportDate.AddDays(1))))).ToDictionary(x => ((string)x[0]).Trim(), x => (decimal)x[1]);

                var expected = current.Select(c => (c.Key, Qty: c.Value - (after.TryGetValue(c.Key, out var a) ? a : 0m)))
                    .Where(x => x.Qty != 0).ToDictionary(x => x.Key, x => x.Qty);
                Assert.Equal(expected.OrderBy(x => x.Key).ToList(), r.ReadyStock.ToDictionary(l => l.PlantTypeName.Trim(), l => l.Owned).OrderBy(x => x.Key).ToList());

                if (!r.IsToday && !r.ReadyStock.ToDictionary(l => l.PlantTypeName.Trim(), l => l.Owned).OrderBy(x => x.Key).SequenceEqual(currentNonZero))
                    sawDifferentFromToday = true;
            }
            Assert.True(sawDifferentFromToday, "Expected at least one past date whose Ready Stock differs from today's.");
        }

        [SkippableFact]
        public async Task HistoryRange_GivesTheSameReportsAsSingleDays()
        {
            var env = await OpenAsync();
            var (from, to) = await RealRangeAsync(env);
            var now = AppClock.Now;
            var range = R.BuildAll(await env.Repo.GetAsync(from, to), AppClock.Today, now);
            foreach (var r in range)
            {
                var single = R.Build(await env.Repo.GetAsync(r.ReportDate, r.ReportDate), r.ReportDate, AppClock.Today, now);
                Assert.Equal(R.ToPlainText(single), R.ToPlainText(r));
            }
        }

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
        public async Task AnyRange_IsOneDatabaseCommand_NoPerDayOrPerEmployeeQueries()
        {
            var env = await OpenAsync();
            await env.Repo.GetAsync(AppClock.Today, AppClock.Today); // warm up
            using var counter = new CommandCounter();
            await env.Repo.GetAsync(AppClock.Today.AddDays(-(R.MaxHistoryDays - 1)), AppClock.Today);
            Assert.Equal(1, counter.Commands);
        }
    }
}
