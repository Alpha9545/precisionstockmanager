using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using DispatchModel = PlantStockManager.Models.Dispatch;

namespace PlantStockManager.Tests
{
    // CORRECTION #4 -- end-to-end: the REAL dispatch (fulfilment) code, repositories and page against a SCRATCH COPY of
    // the test database (same safety rules as the other E2E classes: PSM_SCRATCH_CONNECTION, database name must start
    // with PlantsIMS2_Scratch_, otherwise refused / skipped).
    //
    // Data used (real rows of the copy): Potted Plant Stock 193 (Salvia Salsa, Outlet Area 124) is the pool the tests
    // reset; 195 (Ping Pong Purple, Area 124) is the "other stock" that must never be touched. Users: 112 Sandeep Pawar =
    // Outlet Sales (Area 124); 3 Sarika = Booking Executive and 2 Reshma = Dispatch Executive (Main Office, Area 2).
    [Collection("ScratchDb")]
    public class BookingFulfilmentE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";
        private const int Pool = 193, OtherPool = 195, OutletArea = 124, OutletUser = 112;

        private sealed class NullTempData : ITempDataProvider
        {
            public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
            public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
        }

        private sealed class StaticMonitor<T> : IOptionsMonitor<T>
        {
            public StaticMonitor(T value) => CurrentValue = value;
            public T CurrentValue { get; }
            public T Get(string? name) => CurrentValue;
            public IDisposable? OnChange(Action<T, string?> listener) => null;
        }

        private sealed class Env
        {
            public string ConnectionString { get; init; } = "";
            public ServiceProvider Provider { get; init; } = null!;
            public T Get<T>() where T : notnull => Provider.GetRequiredService<T>();

            public async Task<T> ScalarAsync<T>(string sql, params (string Name, object? Value)[] args)
            {
                using var conn = new SqlConnection(ConnectionString);
                await conn.OpenAsync();
                using var cmd = new SqlCommand(sql, conn);
                foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
                var r = await cmd.ExecuteScalarAsync();
                return r == null || r == DBNull.Value ? default! : (T)Convert.ChangeType(r, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
            }

            public Task ExecAsync(string sql, params (string Name, object? Value)[] args) => ScalarAsync<int>(sql + "; SELECT 1", args);

            public async Task<ClaimsPrincipal> LoginAsync(int userId)
            {
                var factory = Get<UserClaimsFactory>();
                var user = await factory.GetActiveUserAsync(userId);
                Assert.NotNull(user);
                return await factory.CreatePrincipalAsync(user!);
            }

            public T Page<T>(ClaimsPrincipal principal) where T : PageModel
            {
                var http = new DefaultHttpContext { User = principal };
                var page = ActivatorUtilities.CreateInstance<T>(Provider);
                page.PageContext = new PageContext(new ActionContext(http, new Microsoft.AspNetCore.Routing.RouteData(), new CompiledPageActionDescriptor()));
                page.TempData = new TempDataDictionary(http, new NullTempData());
                return page;
            }

            // the pool with a clean state: `physical` plants, nothing reserved (scratch copy only)
            public Task ResetPoolAsync(decimal physical, int pool = Pool)
                => ExecAsync("UPDATE dbo.PottedPlantStock SET ReservedQuantity = 0 WHERE Id = @S; UPDATE dbo.PottedPlantStock SET PhysicalQuantity = @P WHERE Id = @S", ("@S", pool), ("@P", physical));

            public async Task<(decimal Physical, decimal Reserved, decimal Available, decimal Sold)> StockAsync(int pool = Pool)
            {
                using var conn = new SqlConnection(ConnectionString);
                await conn.OpenAsync();
                using var cmd = new SqlCommand("SELECT PhysicalQuantity, ReservedQuantity, AvailableQuantity, SoldDispatchedQuantity FROM dbo.PottedPlantStock WHERE Id = @S", conn);
                cmd.Parameters.AddWithValue("@S", pool);
                using var r = await cmd.ExecuteReaderAsync();
                await r.ReadAsync();
                return (r.GetDecimal(0), r.GetDecimal(1), r.GetDecimal(2), r.GetDecimal(3));
            }

            public async Task<(string Status, decimal Booked, decimal Dispatched)> BookingAsync(int id)
            {
                using var conn = new SqlConnection(ConnectionString);
                await conn.OpenAsync();
                using var cmd = new SqlCommand("SELECT Status, Quantity, DispatchedQuantity FROM dbo.PottedPlantBookings WHERE Id = @I", conn);
                cmd.Parameters.AddWithValue("@I", id);
                using var r = await cmd.ExecuteReaderAsync();
                await r.ReadAsync();
                return (r.GetString(0), r.GetDecimal(1), r.GetDecimal(2));
            }

            public Task<int> DispatchCountAsync(int bookingId) => ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Dispatches WHERE PottedPlantBookingId = @B AND Status = N'Completed'", ("@B", bookingId));

            // everything a refused dispatch must leave untouched
            public Task<string> StateAsync() => ScalarAsync<string>(@"SELECT CONCAT(
                (SELECT CONCAT(COUNT(*), '/', ISNULL(SUM(PhysicalQuantity), 0), '/', ISNULL(SUM(ReservedQuantity), 0), '/', ISNULL(SUM(SoldDispatchedQuantity), 0)) FROM dbo.PottedPlantStock), '|',
                (SELECT COUNT(*) FROM dbo.PottedPlantStockTransactions), '|',
                (SELECT CONCAT(COUNT(*), '/', ISNULL(SUM(DispatchedQuantity), 0)) FROM dbo.PottedPlantBookings), '|',
                (SELECT COUNT(*) FROM dbo.Dispatches))");
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

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs }).Build());
            services.AddSingleton<DatabaseHelper>();
            services.AddSingleton<AreaAccessService>();
            services.AddSingleton<IOptions<SecurityOptions>>(Options.Create(new SecurityOptions { FullAccessRoleNames = new[] { "System Administrator" } }));
            services.AddSingleton<IOptionsMonitor<SeedlingWorkflowOptions>>(new StaticMonitor<SeedlingWorkflowOptions>(new SeedlingWorkflowOptions()));
            services.AddSingleton<SeedlingAreaScope>();
            services.AddScoped<UserClaimsFactory>();
            foreach (var t in typeof(DatabaseHelper).Assembly.GetTypes().Where(t => t.Namespace == "PlantStockManager.Data" && t.IsClass && t.Name.EndsWith("Repository")))
                services.AddScoped(t);
            return new Env { ConnectionString = cs!, Provider = services.BuildServiceProvider() };
        }

        // a booking of `quantity` against the pool (reserves the stock, exactly like the Potted Plant Booking page)
        private static async Task<int> BookAsync(Env env, decimal quantity, int pool = Pool)
        {
            var (ok, message, id) = await env.Get<PottedPlantBookingRepository>().InsertAsync(new PottedPlantBooking
            {
                PottedPlantStockId = pool, Quantity = quantity, CustomerName = "e2e customer", Contact = "9999999999",
                BookingDate = DateTime.Today, DeliveryDate = DateTime.Today, BookedById = OutletUser, CreatedBy = "e2e"
            }, OutletUser);
            Assert.True(ok, message);
            return id;
        }

        private static Task<(bool Success, string? Message, int Id)> DispatchAsync(Env env, int bookingId, decimal quantity,
            Func<int?, bool>? canAccessArea = null, decimal? expected = null, int? stockIdFromCaller = null)
            => env.Get<DispatchRepository>().InsertAsync(new DispatchModel
            {
                PottedPlantBookingId = bookingId, Quantity = quantity, DispatchDate = DateTime.Today, CreatedBy = "e2e",
                PottedPlantStockId = stockIdFromCaller ?? 0
            }, OutletUser, canAccessArea, expected);

        // ---- the defect: stock that is entirely reserved could not be dispatched ----

        [SkippableFact]
        public async Task FullFulfilment_WhenTheWholeStockIsReserved_NowWorks_ReservationReleasedFirst_StockAndBookingReconcile()
        {
            var env = await OpenAsync();
            await env.ResetPoolAsync(100);
            var booking = await BookAsync(env, 100);                                   // Physical 100, Reserved 100, Available 0
            Assert.Equal((100m, 100m, 0m), (await env.StockAsync()) is var s ? (s.Physical, s.Reserved, s.Available) : default);
            var soldBefore = (await env.StockAsync()).Sold;

            var (ok, message, dispatchId) = await DispatchAsync(env, booking, 100);
            Assert.True(ok, message);                                                  // before the fix: "...Physical Quantity (0) below what is already Reserved (100)"

            var after = await env.StockAsync();
            Assert.Equal((0m, 0m, 0m), (after.Physical, after.Reserved, after.Available));
            Assert.Equal(soldBefore + 100, after.Sold);
            Assert.Equal(("Dispatched", 100m, 100m), await env.BookingAsync(booking));
            Assert.Equal(1, await env.DispatchCountAsync(booking));
            // ledger: release first, then the physical deduction, both -100 and both tied to this dispatch
            var order = await env.ScalarAsync<string>(@"SELECT STRING_AGG(TransactionType + ':' + CAST(CAST(Quantity AS INT) AS VARCHAR(12)), ',') WITHIN GROUP (ORDER BY Id)
                FROM dbo.PottedPlantStockTransactions WHERE PottedPlantStockId = @S AND ReferenceType = N'Dispatch' AND ReferenceId = @D", ("@S", Pool), ("@D", dispatchId));
            Assert.Equal("ReservationRelease:-100,Dispatch:-100", order);
        }

        [SkippableFact]
        public async Task PartialFulfilment_Booked100_Fulfil30_Then40_Then31Rejected_Then30Completes()
        {
            var env = await OpenAsync();
            await env.ResetPoolAsync(100);
            var booking = await BookAsync(env, 100);

            async Task Check(string status, decimal fulfilled)
            {
                Assert.Equal((status, 100m, fulfilled), await env.BookingAsync(booking));
                var s = await env.StockAsync();
                Assert.Equal(100 - fulfilled, s.Physical);                              // physical = start - fulfilled
                Assert.Equal(100 - fulfilled, s.Reserved);                              // reserved = remaining
                Assert.Equal(0m, s.Available);                                          // nothing else was ever free
            }

            Assert.True((await DispatchAsync(env, booking, 30)).Success);   await Check("PartiallyDispatched", 30);   // remaining 70
            Assert.True((await DispatchAsync(env, booking, 40)).Success);   await Check("PartiallyDispatched", 70);   // remaining 30
            var before = await env.StateAsync();
            var (over, message, _) = await DispatchAsync(env, booking, 31);
            Assert.False(over);
            Assert.Contains("exceeds this Booking's remaining quantity (30)", message);
            Assert.Equal(before, await env.StateAsync());                                                              // nothing changed
            Assert.True((await DispatchAsync(env, booking, 30)).Success);   await Check("Dispatched", 100);
            Assert.Equal(3, await env.DispatchCountAsync(booking));
        }

        // ---- refusals leave everything exactly as it was ----

        [SkippableTheory]
        [InlineData(0)]
        [InlineData(-5)]
        [InlineData(2.5)]
        [InlineData(101)]
        public async Task InvalidQuantities_AreRefused_AndNothingChanges(double quantity)
        {
            var env = await OpenAsync();
            await env.ResetPoolAsync(100);
            var booking = await BookAsync(env, 100);
            var before = await env.StateAsync();
            var (ok, message, _) = await DispatchAsync(env, booking, (decimal)quantity);
            Assert.False(ok);
            Assert.NotNull(message);
            Assert.Equal(before, await env.StateAsync());
            Assert.Equal(("Pending", 100m, 0m), await env.BookingAsync(booking));
        }

        [SkippableFact]
        public async Task CancelledAndAlreadyDispatchedBookings_CannotBeFulfilled()
        {
            var env = await OpenAsync();
            await env.ResetPoolAsync(100);
            var cancelled = await BookAsync(env, 40);
            Assert.True((await env.Get<PottedPlantBookingRepository>().CancelAsync(cancelled, "e2e", OutletUser)).Success);
            Assert.Equal(0m, (await env.StockAsync()).Reserved);                        // the reservation went back
            var before = await env.StateAsync();
            var (okCancelled, cancelledMessage, _) = await DispatchAsync(env, cancelled, 10);
            Assert.False(okCancelled);
            Assert.Contains("'Cancelled'", cancelledMessage);

            var done = await BookAsync(env, 20);
            Assert.True((await DispatchAsync(env, done, 20)).Success);
            var afterDone = await env.StateAsync();
            var (okDone, doneMessage, _) = await DispatchAsync(env, done, 1);
            Assert.False(okDone);
            Assert.Contains("'Dispatched'", doneMessage);
            Assert.Equal(afterDone, await env.StateAsync());
            Assert.NotEqual(before, afterDone);
        }

        // ---- Area and stock source ----

        [SkippableFact]
        public async Task AreaRule_IsEnforcedByTheRepository_ForRealUsers()
        {
            var env = await OpenAsync();
            await env.ResetPoolAsync(100);
            var booking = await BookAsync(env, 50);
            var access = env.Get<AreaAccessService>();
            var sarika = await env.LoginAsync(3);       // Booking Executive, Main Office (Area 2)
            var reshma = await env.LoginAsync(2);       // Dispatch Executive, Main Office (Area 2)
            var sandeep = await env.LoginAsync(OutletUser);
            Assert.False(access.CanAccessArea(sarika, OutletArea));
            var before = await env.StateAsync();

            foreach (var stranger in new[] { sarika, reshma })
            {
                var (ok, message, _) = await DispatchAsync(env, booking, 10, a => access.CanAccessArea(stranger, a));
                Assert.False(ok);
                Assert.Contains("not authorized", message);
            }
            Assert.Equal(before, await env.StateAsync());

            var (allowed, allowedMessage, _) = await DispatchAsync(env, booking, 10, a => access.CanAccessArea(sandeep, a));
            Assert.True(allowed, allowedMessage);                                        // the Outlet user of that Area may
            Assert.Equal(("PartiallyDispatched", 50m, 10m), await env.BookingAsync(booking));
        }

        [SkippableFact]
        public async Task TheStockSource_ComesFromTheBooking_NeverFromTheCaller()
        {
            var env = await OpenAsync();
            await env.ResetPoolAsync(100);
            var booking = await BookAsync(env, 30);
            var other = await env.StockAsync(OtherPool);
            var (ok, message, _) = await DispatchAsync(env, booking, 30, stockIdFromCaller: OtherPool);   // a forged stock id
            Assert.True(ok, message);
            Assert.Equal(other, await env.StockAsync(OtherPool));                        // the other pool is untouched
            Assert.Equal((70m, 0m), ((await env.StockAsync()).Physical, (await env.StockAsync()).Reserved));
        }

        // ---- double submission and concurrency ----

        [SkippableFact]
        public async Task TheSameFormSubmittedTwice_DispatchesOnce()
        {
            var env = await OpenAsync();
            await env.ResetPoolAsync(100);
            var booking = await BookAsync(env, 100);
            // both submissions carry the value the form was opened with (0 dispatched)
            var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => DispatchAsync(env, booking, 10, expected: 0))));
            Assert.Equal(1, results.Count(r => r.Success));
            var refused = results.Single(r => !r.Success);
            Assert.Contains("Nothing was dispatched", refused.Message);
            Assert.Equal(("PartiallyDispatched", 100m, 10m), await env.BookingAsync(booking));
            Assert.Equal((90m, 90m), ((await env.StockAsync()).Physical, (await env.StockAsync()).Reserved));
            Assert.Equal(1, await env.DispatchCountAsync(booking));
        }

        [SkippableFact]
        public async Task TwoSimultaneousDispatchesOfTheSameBooking_CannotExceedTheRemainder()
        {
            var env = await OpenAsync();
            await env.ResetPoolAsync(100);
            var booking = await BookAsync(env, 50);
            var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => DispatchAsync(env, booking, 30))));   // 30 + 30 > 50
            Assert.Equal(1, results.Count(r => r.Success));
            Assert.Contains("remaining quantity (20)", results.Single(r => !r.Success).Message);
            Assert.Equal(("PartiallyDispatched", 50m, 30m), await env.BookingAsync(booking));
            var s = await env.StockAsync();
            Assert.Equal((70m, 20m, 50m), (s.Physical, s.Reserved, s.Available));
        }

        [SkippableFact]
        public async Task TwoBookingsOnTheSameStock_DispatchedAtOnce_NeverOversell()
        {
            var env = await OpenAsync();
            await env.ResetPoolAsync(100);
            var a = await BookAsync(env, 60);
            var b = await BookAsync(env, 40);                                            // together exactly the stock
            var results = await Task.WhenAll(new[] { a, b }.Select((id, i) => Task.Run(() => DispatchAsync(env, id, i == 0 ? 60 : 40))));
            Assert.All(results, r => Assert.True(r.Success, r.Message));
            var s = await env.StockAsync();
            Assert.Equal((0m, 0m, 0m), (s.Physical, s.Reserved, s.Available));
            Assert.Equal("Dispatched", (await env.BookingAsync(a)).Status);
            Assert.Equal("Dispatched", (await env.BookingAsync(b)).Status);
        }

        // ---- a failure changes nothing at all ----

        [SkippableFact]
        public async Task WhenTheStockCannotCoverTheDispatch_EverythingRollsBack()
        {
            var env = await OpenAsync();
            await env.ResetPoolAsync(100);
            var booking = await BookAsync(env, 50);
            // data problem on the scratch copy: the stock now reserves less than the booking still needs
            await env.ExecAsync("UPDATE dbo.PottedPlantStock SET ReservedQuantity = 5 WHERE Id = @S", ("@S", Pool));
            var before = await env.StateAsync();
            var (ok, message, _) = await DispatchAsync(env, booking, 20);
            Assert.False(ok);
            Assert.Contains("cannot cover", message);
            Assert.Equal(before, await env.StateAsync());                                // no Dispatch row, no ledger row, no totals moved
            Assert.Equal(("Pending", 50m, 0m), await env.BookingAsync(booking));
            Assert.Equal(0, await env.DispatchCountAsync(booking));
        }

        // ---- regressions ----

        [SkippableFact]
        public async Task CancellingADispatch_StillRestoresStockAndTheBooking()
        {
            var env = await OpenAsync();
            await env.ResetPoolAsync(100);
            var booking = await BookAsync(env, 100);
            var (ok, message, id) = await DispatchAsync(env, booking, 100);
            Assert.True(ok, message);
            Assert.True((await env.Get<DispatchRepository>().CancelAsync(id, "e2e", OutletUser)).Success);
            var s = await env.StockAsync();
            Assert.Equal((100m, 100m), (s.Physical, s.Reserved));
            Assert.Equal(("Pending", 100m, 0m), await env.BookingAsync(booking));
        }

        [SkippableFact]
        public async Task DirectSale_IsUnchanged()
        {
            var env = await OpenAsync();
            await env.ResetPoolAsync(100);
            var (ok, message, code) = await env.Get<DispatchRepository>().DirectSaleAsync(Pool, 10, "walk-in", null, null, OutletUser, "e2e");
            Assert.True(ok, message);
            Assert.NotNull(code);
            var s = await env.StockAsync();
            Assert.Equal((90m, 0m), (s.Physical, s.Reserved));
        }

        [SkippableFact]
        public async Task HistoricalBookings_AreUntouched()
        {
            var env = await OpenAsync();
            var history = await env.ScalarAsync<string>("SELECT CONCAT((SELECT CHECKSUM_AGG(CHECKSUM(*)) FROM dbo.PottedPlantBookings WHERE Id IN (79, 80)), '/', (SELECT CHECKSUM_AGG(CHECKSUM(*)) FROM dbo.Dispatches WHERE Id IN (79, 80)), '/', (SELECT CHECKSUM_AGG(CHECKSUM(*)) FROM dbo.Bookings WHERE Id < 5043))");
            await env.ResetPoolAsync(100);
            var booking = await BookAsync(env, 10);
            Assert.True((await DispatchAsync(env, booking, 10)).Success);
            Assert.Equal(history, await env.ScalarAsync<string>("SELECT CONCAT((SELECT CHECKSUM_AGG(CHECKSUM(*)) FROM dbo.PottedPlantBookings WHERE Id IN (79, 80)), '/', (SELECT CHECKSUM_AGG(CHECKSUM(*)) FROM dbo.Dispatches WHERE Id IN (79, 80)), '/', (SELECT CHECKSUM_AGG(CHECKSUM(*)) FROM dbo.Bookings WHERE Id < 5043))"));
            Assert.Equal(("Dispatched", 20m, 20m), await env.BookingAsync(80));
        }

        [SkippableFact]
        public async Task SeedlingBookings_AndOutletBookings_KeepWorking()
        {
            var env = await OpenAsync();
            // seedling booking 5045 (Antirrhinum Twinny, 40): reserve 40, dispatch 30, 11 refused, 10 completes -- as before
            var seedling = env.Get<SeedlingFulfilmentRepository>();
            var actor = new SeedlingFulfilmentRepository.Actor("e2e", 3);
            Skip.IfNot(await env.ScalarAsync<string>("SELECT Status FROM dbo.Bookings WHERE Id = 5045") == "Pending", "booking 5045 is not open in this copy");
            Assert.True((await seedling.ReserveAsync(5045, 40, actor)).Success);
            var allocation = await env.ScalarAsync<int>("SELECT Id FROM dbo.BookingBatchAllocations WHERE BookingId = 5045 AND Status = N'Active'");
            var (d1, m1) = await seedling.DispatchAsync(5045, new[] { (allocation, 30m) }, DateTime.Today, null, null, actor, _ => true);
            Assert.True(d1, m1);
            Assert.False((await seedling.DispatchAsync(5045, new[] { (allocation, 11m) }, DateTime.Today, null, null, actor, _ => true)).Success);
            Assert.True((await seedling.DispatchAsync(5045, new[] { (allocation, 10m) }, DateTime.Today, null, null, actor, _ => true)).Success);
            Assert.Equal("Completed", await env.ScalarAsync<string>("SELECT Status FROM dbo.Bookings WHERE Id = 5045"));

            // outlet booking: book 40, collect 15, 26 refused, 25 completes
            await env.ResetPoolAsync(100);
            var outlet = env.Get<OutletBookingRepository>();
            var (created, createMessage, bookingId) = await outlet.InsertAsync(
                new OutletBooking { CustomerName = "e2e outlet", BookingDate = DateTime.Today, CreatedBy = "e2e" },
                new[] { new OutletBookingRepository.BookingItemInput(OutletStockType.Potted, Pool, 40) }, OutletArea, OutletUser);
            Assert.True(created, createMessage);
            var item = await env.ScalarAsync<int>("SELECT Id FROM dbo.OutletBookingItems WHERE BookingId = @B", ("@B", bookingId));
            Assert.True((await outlet.CollectItemAsync(item, 15, OutletUser, "e2e")).Success);
            Assert.False((await outlet.CollectItemAsync(item, 26, OutletUser, "e2e")).Success);
            Assert.True((await outlet.CollectItemAsync(item, 25, OutletUser, "e2e")).Success);
            Assert.Equal("Completed", await env.ScalarAsync<string>("SELECT Status FROM dbo.OutletBookings WHERE Id = @B", ("@B", bookingId)));
            var s = await env.StockAsync();
            Assert.Equal((60m, 0m), (s.Physical, s.Reserved));
        }

        // ---- the Dispatch page, with real users ----

        [SkippableFact]
        public async Task DispatchPage_EndToEnd_ShowsTheFigures_RefusesAStaleForm_AndCompletesTheBooking()
        {
            var env = await OpenAsync();
            await env.ResetPoolAsync(100);
            var booking = await BookAsync(env, 100);
            var sandeep = await env.LoginAsync(OutletUser);

            var form = env.Page<PlantStockManager.Pages.Production.Dispatch.CreateModel>(sandeep);
            await form.OnGetAsync();
            var row = form.DispatchableBookings.Single(b => b.Id == booking);
            Assert.Equal((100m, 0m, 100m), (row.Quantity, row.DispatchedQuantity, row.RemainingQuantity));            // Booked / Fulfilled / Remaining
            Assert.Equal((100m, 100m, 0m), (row.StockPhysicalQuantity, row.StockReservedQuantity, row.StockAvailableQuantity));   // stock figures

            PlantStockManager.Pages.Production.Dispatch.CreateModel Submit(decimal qty, decimal? expected, ClaimsPrincipal? user = null)
            {
                var p = env.Page<PlantStockManager.Pages.Production.Dispatch.CreateModel>(user ?? sandeep);
                p.Dispatch = new DispatchModel { PottedPlantBookingId = booking, Quantity = qty, DispatchDate = DateTime.Today };
                p.ExpectedDispatched = expected;
                return p;
            }

            // a form without its opened-at value, and a fractional quantity: refused, nothing changes
            var before = await env.StateAsync();
            var noToken = Submit(10, null);
            Assert.IsType<PageResult>(await noToken.OnPostAsync());
            Assert.Contains(noToken.ModelState.Values.SelectMany(v => v.Errors), e => e.ErrorMessage.Contains("out of date"));
            var fractional = Submit(2.5m, 0);
            Assert.IsType<PageResult>(await fractional.OnPostAsync());
            Assert.Equal(before, await env.StateAsync());

            // 40 now (opened at 0 dispatched) -> fulfilled 40, remaining 60, message says so
            var first = Submit(40, 0);
            Assert.IsType<RedirectToPageResult>(await first.OnPostAsync());
            var message = (string)first.TempData["Success"]!;
            Assert.Contains("40 of 100 fulfilled, 60 remaining", message);
            Assert.Contains("PartiallyDispatched", message);

            // the same (now stale) form again: refused, still only 40
            var repeat = Submit(40, 0);
            Assert.IsType<PageResult>(await repeat.OnPostAsync());
            Assert.Contains(repeat.ModelState.Values.SelectMany(v => v.Errors), e => e.ErrorMessage.Contains("Nothing was dispatched"));
            Assert.Equal(("PartiallyDispatched", 100m, 40m), await env.BookingAsync(booking));

            // more than remains, with a current form: the message names the remainder
            var tooMany = Submit(61, 40);
            Assert.IsType<PageResult>(await tooMany.OnPostAsync());
            Assert.Contains(tooMany.ModelState.Values.SelectMany(v => v.Errors), e => e.ErrorMessage.Contains("remaining quantity (60)"));

            // a Main Office user cannot see or dispatch this Outlet's booking
            var sarika = await env.LoginAsync(3);
            var foreign = env.Page<PlantStockManager.Pages.Production.Dispatch.CreateModel>(sarika);
            await foreign.OnGetAsync();
            Assert.DoesNotContain(foreign.DispatchableBookings, b => b.Id == booking);
            var foreignPost = Submit(10, 40, sarika);
            Assert.IsType<PageResult>(await foreignPost.OnPostAsync());
            Assert.Contains(foreignPost.ModelState.Values.SelectMany(v => v.Errors), e => e.ErrorMessage.Contains("not authorized"));
            Assert.Equal(("PartiallyDispatched", 100m, 40m), await env.BookingAsync(booking));

            // the exact remainder completes it
            var last = Submit(60, 40);
            Assert.IsType<RedirectToPageResult>(await last.OnPostAsync());
            Assert.Contains("Booking status: Dispatched", (string)last.TempData["Success"]!);
            Assert.Equal(("Dispatched", 100m, 100m), await env.BookingAsync(booking));
            var s = await env.StockAsync();
            Assert.Equal((0m, 0m), (s.Physical, s.Reserved));
        }
    }
}
