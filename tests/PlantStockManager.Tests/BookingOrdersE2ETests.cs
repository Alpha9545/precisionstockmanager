using System.Reflection;
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
using PlantStockManager.Services;
using RecordsPage = PlantStockManager.Pages.Data.BookingsRecordModel;
using EditRecordsPage = PlantStockManager.Pages.Bookings.BookingRecordsModel;
using DetailsPage = PlantStockManager.Pages.Bookings.BookingDetailsModel;
using RevisePage = PlantStockManager.Pages.Bookings.ReviseModel;
using DispatchPage = PlantStockManager.Pages.Bookings.SeedlingDispatchModel;
using FulfilmentPage = PlantStockManager.Pages.Bookings.FulfilmentModel;
using RegisterPage = PlantStockManager.Pages.Bookings.DispatchRegisterModel;
using BookPage = PlantStockManager.Pages.Bookings.BookModel;
using DashboardPage = PlantStockManager.Pages.IndexModel;
using PottedIndexPage = PlantStockManager.Pages.Production.PottedPlantBooking.IndexModel;

namespace PlantStockManager.Tests
{
    // Seedling booking ORDERS end-to-end, against a SCRATCH COPY of the test database that has
    // 2026-10-05_BookingOrderLink_Schema.sql (+ the reviewed backfill) applied
    // (PSM_SCRATCH_CONNECTION; the database name must start with PlantsIMS2_Scratch_).
    // Real pages and repositories: one order = several variety lines (own quantities, delivery
    // dates, statuses, reservations, allocations, dispatches) linked by OrderBookingId.
    [Collection("ScratchDb")]
    public class BookingOrdersE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";
        private const int Sandip = 14, Rahul = 15, Sarika = 3, Admin = 21;
        private static readonly DateTime Delivery = new(2026, 12, 20);

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

            public async Task<List<int>> IdsAsync(string sql, params (string Name, object? Value)[] args)
            {
                var list = new List<int>();
                using var conn = new SqlConnection(ConnectionString);
                await conn.OpenAsync();
                using var cmd = new SqlCommand(sql, conn);
                foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) list.Add(r.GetInt32(0));
                return list;
            }

            public async Task<T> PageAsync<T>(int userId) where T : PageModel
            {
                var factory = Get<UserClaimsFactory>();
                var user = await factory.GetActiveUserAsync(userId);
                Assert.NotNull(user);
                var http = new DefaultHttpContext { User = await factory.CreatePrincipalAsync(user!) };
                var page = ActivatorUtilities.CreateInstance<T>(Provider);
                page.PageContext = new PageContext(new ActionContext(http, new Microsoft.AspNetCore.Routing.RouteData(), new CompiledPageActionDescriptor()))
                {
                    ViewData = new ViewDataDictionary(new Microsoft.AspNetCore.Mvc.ModelBinding.EmptyModelMetadataProvider(), new Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary())
                };
                page.TempData = new TempDataDictionary(http, new NullTempData());
                return page;
            }
        }

        private static async Task<Env> OpenAsync()
        {
            var cs = Environment.GetEnvironmentVariable(ConnectionVariable);
            Skip.If(string.IsNullOrWhiteSpace(cs), $"Set {ConnectionVariable} to a {RequiredPrefix}* database connection string to run the end-to-end tests.");
            string name; bool hasColumn;
            using (var conn = new SqlConnection(cs))
            {
                await conn.OpenAsync();
                using var cmd = new SqlCommand("SELECT DB_NAME(), CASE WHEN COL_LENGTH('dbo.Bookings', 'OrderBookingId') IS NULL THEN 0 ELSE 1 END", conn);
                using var r = await cmd.ExecuteReaderAsync();
                await r.ReadAsync();
                name = r.GetString(0); hasColumn = r.GetInt32(1) == 1;
            }
            if (!name.StartsWith(RequiredPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException($"Refusing to run end-to-end tests against '{name}'. Only databases named {RequiredPrefix}* are allowed.");
            Skip.IfNot(hasColumn, "The scratch database has no Bookings.OrderBookingId -- run 2026-10-05_BookingOrderLink_Schema.sql on it first.");

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs }).Build());
            services.AddSingleton<DatabaseHelper>();
            services.AddSingleton<AreaAccessService>();
            services.AddSingleton<IOptions<SecurityOptions>>(Options.Create(new SecurityOptions
            {
                FullAccessRoleNames = new[] { "System Administrator" },
                OwnBookingsOnlyUserIds = new[] { Sandip, Rahul }
            }));
            services.AddSingleton<IOptionsMonitor<SeedlingWorkflowOptions>>(new StaticMonitor<SeedlingWorkflowOptions>(new SeedlingWorkflowOptions()));
            services.AddSingleton<SeedlingAreaScope>();
            services.AddSingleton<BookingOwnershipService>();
            services.AddSingleton<SubmissionTokenGuard>();
            services.AddScoped<UserClaimsFactory>();
            foreach (var t in typeof(DatabaseHelper).Assembly.GetTypes().Where(t => t.Namespace == "PlantStockManager.Data" && t.IsClass && t.Name.EndsWith("Repository")))
                services.AddScoped(t);
            return new Env { ConnectionString = cs!, Provider = services.BuildServiceProvider() };
        }

        // ---- helpers ----------------------------------------------------------------------------------------

        private sealed record Variety(int PlantId, int SpeciesId);

        // Three distinct varieties (with Ready Stock to reserve when `withStock`).
        private static async Task<List<Variety>> VarietiesAsync(Env env, bool withStock = false)
        {
            var sql = withStock
                ? @"SELECT TOP 3 ps.PlantTypeId, rs.SpeciesId FROM dbo.ReadyStock rs INNER JOIN dbo.PlantSpecies ps ON ps.Id = rs.SpeciesId
                    GROUP BY ps.PlantTypeId, rs.SpeciesId HAVING SUM(rs.Quantity - rs.ReservedQuantity - rs.DispatchedQuantity) >= 20
                    ORDER BY SUM(rs.Quantity - rs.ReservedQuantity - rs.DispatchedQuantity) DESC"
                : "SELECT TOP 3 PlantTypeId, Id FROM dbo.PlantSpecies WHERE PlantTypeId IS NOT NULL ORDER BY Id";
            var list = new List<Variety>();
            using var conn = new SqlConnection(env.ConnectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(sql, conn);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) list.Add(new Variety(r.GetInt32(0), r.GetInt32(1)));
            return list;
        }

        private static async Task<Booking> OrderHeaderAsync(Env env, int bookedBy, string customer)
        {
            var state = await env.ScalarAsync<int>("SELECT TOP 1 StateId FROM dbo.Bookings WHERE StateId IS NOT NULL AND DistrictId IS NOT NULL ORDER BY Id DESC");
            var district = await env.ScalarAsync<int>("SELECT TOP 1 DistrictId FROM dbo.Bookings WHERE StateId = @S AND DistrictId IS NOT NULL ORDER BY Id DESC", ("@S", state));
            return new Booking
            {
                CustomerName = customer, Contact = "9999999999", Address = "e2e", AddedBy = "e2e", BookedById = bookedBy,
                StateId = state, DistrictId = district, AdvanceTaken = true, AdvanceTakenAmount = 500, AdvanceTakenDetails = "e2e advance"
            };
        }

        private static string Tag(string what) => $"E2E-ORDER-{what}-{Guid.NewGuid().ToString("N")[..6]}";

        // A: a 3-variety order through the repository.
        private static async Task<(int OrderId, List<int> Lines, List<Variety> Varieties)> CreateOrderAsync(Env env, int bookedBy, string customer,
            bool withStock = false, int[]? quantities = null, DateTime[]? dates = null)
        {
            var v = await VarietiesAsync(env, withStock);
            Skip.If(v.Count < 3, "Not enough varieties (with Ready Stock) on this scratch copy.");
            quantities ??= new[] { 50, 30, 20 };
            dates ??= new[] { Delivery, Delivery, Delivery };
            var (ok, message, orderId, ids) = await env.Get<BookingRepository>().InsertOrderAsync(await OrderHeaderAsync(env, bookedBy, customer),
                v.Select((x, i) => new BookingOrders.NewLine(x.PlantId, x.SpeciesId, quantities[i], dates[i])).ToList());
            Assert.True(ok, message);
            return (orderId, ids, v);
        }

        private static Task<int> CountAsync(Env env, string customer)
            => env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Bookings WHERE CustomerName = @C", ("@C", customer));

        private static Task<decimal> ReservedAsync(Env env, int id) => env.ScalarAsync<decimal>("SELECT ReservedQuantity FROM dbo.Bookings WHERE Id = @I", ("@I", id));
        private static Task<decimal> DispatchedAsync(Env env, int id) => env.ScalarAsync<decimal>("SELECT DispatchedQuantity FROM dbo.Bookings WHERE Id = @I", ("@I", id));
        private static Task<string> StatusAsync(Env env, int id) => env.ScalarAsync<string>("SELECT ISNULL(Status, 'Pending') FROM dbo.Bookings WHERE Id = @I", ("@I", id));
        private static Task<int> AllocationsAsync(Env env, int id) => env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.BookingBatchAllocations WHERE BookingId = @I", ("@I", id));

        // =========================================================================================================

        [SkippableFact]
        public async Task A_NewOrder_ThreeVarieties_OneBookingNumber()
        {
            var env = await OpenAsync();
            var customer = Tag("A");
            var (orderId, lines, _) = await CreateOrderAsync(env, Sarika, customer);
            Assert.Equal(3, lines.Count);
            Assert.Equal(lines[0], orderId);
            Assert.Equal(new[] { orderId, orderId, orderId },
                await env.IdsAsync("SELECT OrderBookingId FROM dbo.Bookings WHERE CustomerName = @C ORDER BY Id", ("@C", customer)));
            // customer-level fields identical on every line; advance only once; a 'Booking' log row per line
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(DISTINCT CONCAT(Contact, '|', Address, '|', BookedById, '|', StateId, '|', DistrictId)) FROM dbo.Bookings WHERE CustomerName = @C", ("@C", customer)));
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Bookings WHERE CustomerName = @C AND AdvanceTaken = 1", ("@C", customer)));
            Assert.Equal(3, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Transactions t INNER JOIN dbo.Bookings b ON b.Id = t.BookingId WHERE b.CustomerName = @C AND t.TransactionType = 'Booking'", ("@C", customer)));
        }

        [SkippableFact]
        public async Task B_OneInvalidLine_NothingIsSaved()
        {
            var env = await OpenAsync();
            var v = await VarietiesAsync(env);
            var wrongSpecies = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.PlantSpecies WHERE PlantTypeId <> @P ORDER BY Id", ("@P", v[2].PlantId));
            foreach (var bad in new[] { new BookingOrders.NewLine(v[2].PlantId, wrongSpecies, 20, Delivery),   // variety of another species (checked in the DB, after 2 lines were inserted)
                                        new BookingOrders.NewLine(v[2].PlantId, v[2].SpeciesId, 0, Delivery),   // zero quantity
                                        new BookingOrders.NewLine(v[2].PlantId, v[2].SpeciesId, 5, null) })     // no delivery date
            {
                var customer = Tag("B");
                var before = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Bookings");
                var (ok, message, orderId, ids) = await env.Get<BookingRepository>().InsertOrderAsync(await OrderHeaderAsync(env, Sarika, customer),
                    new[] { new BookingOrders.NewLine(v[0].PlantId, v[0].SpeciesId, 50, Delivery), new BookingOrders.NewLine(v[1].PlantId, v[1].SpeciesId, 30, Delivery), bad });
                Assert.False(ok);
                Assert.False(string.IsNullOrWhiteSpace(message));
                Assert.Equal(0, orderId);
                Assert.Empty(ids);
                Assert.Equal(0, await CountAsync(env, customer));
                Assert.Equal(before, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Bookings"));
            }
        }

        [SkippableFact]
        public async Task C_ExistingSingleBooking_StillDisplaysNormally()
        {
            var env = await OpenAsync();
            var id = await env.ScalarAsync<int>(@"SELECT TOP 1 b.Id FROM dbo.Bookings b WHERE b.OrderBookingId IS NULL AND ISNULL(b.Status, 'Pending') = 'Pending'
                AND NOT EXISTS (SELECT 1 FROM dbo.Bookings x WHERE x.OrderBookingId = b.Id) ORDER BY b.Id DESC");
            Skip.If(id == 0, "No unlinked single booking on this scratch copy.");
            var details = await env.PageAsync<DetailsPage>(Admin); details.Id = id;
            Assert.IsType<PageResult>(await details.OnGetAsync());
            Assert.Equal(id, details.Booking!.OrderId);
            Assert.Single(details.OrderLines);

            var (y, m) = (await env.ScalarAsync<int>("SELECT YEAR(DeliveryDate) FROM dbo.Bookings WHERE Id = @I", ("@I", id)),
                          await env.ScalarAsync<int>("SELECT MONTH(DeliveryDate) FROM dbo.Bookings WHERE Id = @I", ("@I", id)));
            var records = await env.PageAsync<RecordsPage>(Admin);
            records.SelectedYear = y; records.SelectedMonth = m; records.SelectedStatus = "Pending";
            await records.OnGetAsync();
            var order = Assert.Single(records.Orders, o => o.OrderId == id);
            Assert.Equal(id, Assert.Single(order.Lines).Id);
        }

        [SkippableFact]
        public async Task D_HistoricalMultiVarietyOrder_DisplaysAsOneOrder_AfterBackfill()
        {
            var env = await OpenAsync();
            var orderId = await env.ScalarAsync<int>(@"SELECT TOP 1 OrderBookingId FROM dbo.Bookings
                WHERE OrderBookingId IS NOT NULL AND CustomerName NOT LIKE 'E2E-%' GROUP BY OrderBookingId HAVING COUNT(*) >= 3 ORDER BY OrderBookingId DESC");
            Skip.If(orderId == 0, "No backfilled multi-variety order on this scratch copy (run the reviewed backfill COMMIT on the scratch copy).");
            var lines = await env.IdsAsync("SELECT Id FROM dbo.Bookings WHERE OrderBookingId = @O ORDER BY Id", ("@O", orderId));
            foreach (var lineId in lines)
            {
                var details = await env.PageAsync<DetailsPage>(Admin); details.Id = lineId;
                Assert.IsType<PageResult>(await details.OnGetAsync());
                Assert.Equal(orderId, details.Booking!.OrderId);                                        // opening ANY line shows the order
                Assert.Equal(lines, details.OrderLines.Select(l => l.Id).ToList());
            }
            var fulfilment = await env.PageAsync<FulfilmentPage>(Admin);
            fulfilment.Status = "All"; fulfilment.Search = orderId.ToString();
            await fulfilment.OnGetAsync();
            var group = Assert.Single(fulfilment.Orders);
            Assert.Equal(orderId, group.OrderId);
            Assert.Equal(lines, group.Lines.Select(l => l.Id).ToList());
        }

        [SkippableFact]
        public async Task E_F_DeliveryDatesAndStatuses_StayPerVariety()
        {
            var env = await OpenAsync();
            var customer = Tag("EF");
            var dates = new[] { Delivery, Delivery.AddDays(5), Delivery.AddDays(9) };
            var (_, lines, _) = await CreateOrderAsync(env, Sarika, customer, dates: dates);
            for (var i = 0; i < 3; i++)
                Assert.Equal(dates[i], await env.ScalarAsync<DateTime>("SELECT DeliveryDate FROM dbo.Bookings WHERE Id = @I", ("@I", lines[i])));

            var cancel = await env.PageAsync<DetailsPage>(Sarika); cancel.Id = lines[1]; cancel.CancelReason = "E2E cancel one variety";
            await cancel.OnPostCancelAsync();
            Assert.Null(cancel.TempData["Error"]);
            Assert.Equal(new[] { "Pending", "Cancelled", "Pending" }, new[] { await StatusAsync(env, lines[0]), await StatusAsync(env, lines[1]), await StatusAsync(env, lines[2]) });
        }

        [SkippableFact]
        public async Task G_H_I_ReserveAllocateDispatch_OneVarietyEach_SiblingsUnchanged()
        {
            var env = await OpenAsync();
            var customer = Tag("GHI");
            var (orderId, lines, v) = await CreateOrderAsync(env, Sarika, customer, withStock: true, quantities: new[] { 10, 10, 10 });

            // G: reserve the first variety only
            var reserve = await env.PageAsync<DetailsPage>(Admin); reserve.Id = lines[0]; reserve.ReserveQuantity = 5;
            await reserve.OnPostReserveAsync();
            Assert.Null(reserve.TempData["Error"]);
            Assert.Equal(new[] { 5m, 0m, 0m }, new[] { await ReservedAsync(env, lines[0]), await ReservedAsync(env, lines[1]), await ReservedAsync(env, lines[2]) });

            // H: allocate a batch to the second variety only
            var batchB = await env.ScalarAsync<int>(@"SELECT TOP 1 Id FROM dbo.ReadyStock WHERE SpeciesId = @S AND Quantity - ReservedQuantity - DispatchedQuantity >= 5 ORDER BY Id", ("@S", v[1].SpeciesId));
            var allocate = await env.PageAsync<DispatchPage>(Admin);
            allocate.Id = lines[1]; allocate.AllocateReadyStockId = batchB; allocate.AllocateQuantity = 4; allocate.AllocateToken = SubmissionTokenGuard.NewToken();
            await allocate.OnPostAllocateAsync();
            Assert.Null(allocate.TempData["Error"]);
            Assert.Equal(1, await AllocationsAsync(env, lines[1]));
            var reservedLinesA = await AllocationsAsync(env, lines[0]);
            Assert.True(reservedLinesA >= 1);                               // the reservation of G (oldest batch first, may span batches)
            Assert.Equal(0, await AllocationsAsync(env, lines[2]));
            Assert.Equal(new[] { 5m, 4m, 0m }, new[] { await ReservedAsync(env, lines[0]), await ReservedAsync(env, lines[1]), await ReservedAsync(env, lines[2]) });

            // I: allocate + dispatch the third variety only
            var batchC = await env.ScalarAsync<int>(@"SELECT TOP 1 Id FROM dbo.ReadyStock WHERE SpeciesId = @S AND Quantity - ReservedQuantity - DispatchedQuantity >= 5 ORDER BY Id", ("@S", v[2].SpeciesId));
            var allocateC = await env.PageAsync<DispatchPage>(Admin);
            allocateC.Id = lines[2]; allocateC.AllocateReadyStockId = batchC; allocateC.AllocateQuantity = 3; allocateC.AllocateToken = SubmissionTokenGuard.NewToken();
            await allocateC.OnPostAllocateAsync();
            Assert.Null(allocateC.TempData["Error"]);
            var allocationC = await env.ScalarAsync<int>("SELECT Id FROM dbo.BookingBatchAllocations WHERE BookingId = @I", ("@I", lines[2]));
            var dispatch = await env.PageAsync<DispatchPage>(Admin);
            dispatch.Id = lines[2]; dispatch.DispatchDate = DateTime.Today;
            dispatch.Lines = new List<DispatchPage.DispatchLineInput> { new() { AllocationId = allocationC, Quantity = 3 } };
            await dispatch.OnPostDispatchAsync();
            Assert.Null(dispatch.TempData["Error"]);
            Assert.Equal(new[] { 0m, 0m, 3m }, new[] { await DispatchedAsync(env, lines[0]), await DispatchedAsync(env, lines[1]), await DispatchedAsync(env, lines[2]) });
            Assert.Equal(reservedLinesA, await AllocationsAsync(env, lines[0]));
            Assert.Equal(1, await AllocationsAsync(env, lines[1]));
            Assert.Equal(new[] { 5m, 4m, 0m }, new[] { await ReservedAsync(env, lines[0]), await ReservedAsync(env, lines[1]), await ReservedAsync(env, lines[2]) });

            // the Dispatch Register shows the line as "Booking #order – variety"
            var register = await env.PageAsync<RegisterPage>(Admin);
            register.From = DateTime.Today; register.To = DateTime.Today; register.Search = customer;
            await register.OnGetAsync();
            var line = Assert.Single(register.Lines);
            Assert.Equal(orderId, line.OrderId);
            Assert.Equal(lines[2], line.BookingId);
            Assert.StartsWith($"Booking #{orderId} – ", line.LineLabel);
        }

        [SkippableFact]
        public async Task J_Ownership_AppliesToTheWholeOrder()
        {
            var env = await OpenAsync();
            var customer = Tag("J");
            var (orderId, lines, _) = await CreateOrderAsync(env, Sandip, customer);
            // a line whose own BookedById disagrees with its order must still follow the ORDER's owner
            await env.ExecAsync("UPDATE dbo.Bookings SET BookedById = @R WHERE Id = @I", ("@R", Rahul), ("@I", lines[2]));

            foreach (var id in lines)
            {
                var mine = await env.PageAsync<DetailsPage>(Sandip); mine.Id = id;
                Assert.IsType<PageResult>(await mine.OnGetAsync());
                Assert.Equal(lines, mine.OrderLines.Select(l => l.Id).ToList());

                var other = await env.PageAsync<DetailsPage>(Rahul); other.Id = id;
                Assert.IsType<RedirectToPageResult>(await other.OnGetAsync());
                Assert.Null(other.Booking);
                var cancel = await env.PageAsync<DetailsPage>(Rahul); cancel.Id = id; cancel.CancelReason = "forged";
                await cancel.OnPostCancelAsync();
                Assert.Equal(BookingOwnershipService.NotFoundMessage, cancel.TempData["Error"]);
            }
            foreach (var id in lines)
                Assert.Equal("Pending", await StatusAsync(env, id));

            async Task<List<int>> RecordIds(int user)
            {
                var p = await env.PageAsync<RecordsPage>(user);
                p.SelectedYear = Delivery.Year; p.SelectedMonth = Delivery.Month; p.SelectedStatus = "Pending";
                await p.OnGetAsync();
                return p.Bookings.Select(b => b.Id).Where(lines.Contains).OrderBy(i => i).ToList();
            }
            Assert.Equal(lines, await RecordIds(Sandip));          // all lines of his order, including the inconsistent one
            Assert.Empty(await RecordIds(Rahul));                  // none of them -- not even the line with BookedById = Rahul
            Assert.Equal(lines, await RecordIds(Sarika));          // L: another Booking Executive sees everything
            Assert.Equal(lines, await RecordIds(Admin));           // K: administrator sees everything

            var fulfilment = await env.PageAsync<FulfilmentPage>(Rahul);
            fulfilment.Status = "All"; fulfilment.Search = orderId.ToString();
            await fulfilment.OnGetAsync();
            Assert.Empty(fulfilment.Bookings);

            // leave the shared scratch data consistent for the other test classes
            await env.ExecAsync("UPDATE dbo.Bookings SET BookedById = @S WHERE Id = @I", ("@S", Sandip), ("@I", lines[2]));
        }

        [SkippableFact]
        public async Task M_BookPage_AddAnotherSpecies_CreatesOneOrderAtomically()
        {
            var env = await OpenAsync();
            var v = await VarietiesAsync(env);
            var header = await OrderHeaderAsync(env, Sarika, Tag("M"));
            var page = await env.PageAsync<BookPage>(Sarika);
            page.Booking = header;
            page.Lines = v.Select((x, i) => new BookPage.OrderLineInput { PlantId = x.PlantId, SpeciesId = x.SpeciesId, Quantity = 100 * (i + 1), DeliveryDate = Delivery }).ToList();
            var result = Assert.IsType<JsonResult>(await page.OnPostAsync());
            Assert.Contains("success = True", result.Value!.ToString());
            var ids = await env.IdsAsync("SELECT Id FROM dbo.Bookings WHERE CustomerName = @C ORDER BY Id", ("@C", header.CustomerName));
            Assert.Equal(3, ids.Count);
            Assert.Equal(new[] { ids[0], ids[0], ids[0] }, await env.IdsAsync("SELECT OrderBookingId FROM dbo.Bookings WHERE CustomerName = @C ORDER BY Id", ("@C", header.CustomerName)));

            // one bad line -> nothing saved, error returned
            var bad = await OrderHeaderAsync(env, Sarika, Tag("M-bad"));
            var page2 = await env.PageAsync<BookPage>(Sarika);
            page2.Booking = bad;
            page2.Lines = new() { new() { PlantId = v[0].PlantId, SpeciesId = v[0].SpeciesId, Quantity = 10, DeliveryDate = Delivery },
                                  new() { PlantId = v[1].PlantId, SpeciesId = v[1].SpeciesId, Quantity = 0, DeliveryDate = Delivery } };
            await page2.OnPostAsync();
            Assert.Equal(0, await CountAsync(env, bad.CustomerName));
        }

        [SkippableFact]
        public async Task N_ReviseAddVariety_JoinsTheSameOrder_IncludingAHistoricalSingle()
        {
            var env = await OpenAsync();
            var v = await VarietiesAsync(env);
            var customer = Tag("N");
            var (orderId, lines, _) = await CreateOrderAsync(env, Sarika, customer);

            async Task ReviseAddAsync(int lineId)
            {
                var revise = await env.PageAsync<RevisePage>(Sarika);
                revise.Id = lineId;
                revise.NewPlantId = await env.ScalarAsync<int>("SELECT PlantId FROM dbo.Bookings WHERE Id = @I", ("@I", lineId));
                revise.NewSpeciesId = await env.ScalarAsync<int>("SELECT SpeciesId FROM dbo.Bookings WHERE Id = @I", ("@I", lineId));
                revise.NewQuantity = await env.ScalarAsync<int>("SELECT Quantity FROM dbo.Bookings WHERE Id = @I", ("@I", lineId));
                revise.Reason = "E2E customer adds a variety";
                revise.AddVariety = true; revise.SplitPlantId = v[0].PlantId; revise.SplitSpeciesId = v[0].SpeciesId; revise.SplitQuantity = 7;
                Assert.IsType<RedirectToPageResult>(await revise.OnPostAsync());
            }

            await ReviseAddAsync(lines[1]);   // revising a NON-first line still adds to the order
            var after = await env.IdsAsync("SELECT Id FROM dbo.Bookings WHERE CustomerName = @C ORDER BY Id", ("@C", customer));
            Assert.Equal(4, after.Count);
            Assert.Equal(orderId, await env.ScalarAsync<int>("SELECT OrderBookingId FROM dbo.Bookings WHERE Id = @I", ("@I", after[3])));
            Assert.Equal(lines[1], await env.ScalarAsync<int>("SELECT ParentBookingId FROM dbo.Bookings WHERE Id = @I", ("@I", after[3])));

            // a historical single-line booking (no order number, as before the link existed) becomes an order of 2
            var single = Tag("N-single");
            var (singleOk, singleMessage, singleId, _) = await env.Get<BookingRepository>().InsertOrderAsync(await OrderHeaderAsync(env, Sarika, single),
                new[] { new BookingOrders.NewLine(v[1].PlantId, v[1].SpeciesId, 15, Delivery) });
            Assert.True(singleOk, singleMessage);
            await env.ExecAsync("UPDATE dbo.Bookings SET OrderBookingId = NULL WHERE Id = @I", ("@I", singleId));   // scratch copy: make it historical
            await ReviseAddAsync(singleId);
            var pair = await env.IdsAsync("SELECT Id FROM dbo.Bookings WHERE CustomerName = @C ORDER BY Id", ("@C", single));
            Assert.Equal(2, pair.Count);
            Assert.Equal(new[] { singleId, singleId }, await env.IdsAsync("SELECT OrderBookingId FROM dbo.Bookings WHERE CustomerName = @C ORDER BY Id", ("@C", single)));
        }

        [SkippableFact]
        public async Task EditCustomerDetails_UpdatesTheWholeOrder_VarietyFieldsStayPerLine()
        {
            var env = await OpenAsync();
            var customer = Tag("EDIT");
            var (orderId, lines, v) = await CreateOrderAsync(env, Sarika, customer);
            var edit = await env.PageAsync<EditRecordsPage>(Sarika);
            edit.Booking = new Booking
            {
                Id = lines[1], PlantId = v[1].PlantId, SpeciesId = v[1].SpeciesId, Quantity = 35, DeliveryDate = Delivery.AddDays(2),
                CustomerName = customer + "-renamed", Contact = "8888888888", Address = "new address",
                StateId = await env.ScalarAsync<int>("SELECT StateId FROM dbo.Bookings WHERE Id = @I", ("@I", lines[0])),
                DistrictId = await env.ScalarAsync<int>("SELECT DistrictId FROM dbo.Bookings WHERE Id = @I", ("@I", lines[0])),
                BookedById = Rahul
            };
            edit.RevisionReason = "E2E customer details changed";
            var result = Assert.IsType<JsonResult>(await edit.OnPostEditBookingAsync());
            Assert.Contains("success = True", result.Value!.ToString());

            Assert.Equal(3, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Bookings WHERE OrderBookingId = @O AND CustomerName = @N AND Contact = '8888888888' AND Address = 'new address' AND BookedById = @R",
                ("@O", orderId), ("@N", customer + "-renamed"), ("@R", Rahul)));
            Assert.Equal(new[] { 50, 35, 20 }, await env.IdsAsync("SELECT Quantity FROM dbo.Bookings WHERE OrderBookingId = @O ORDER BY Id", ("@O", orderId)));
            Assert.Equal(Delivery, await env.ScalarAsync<DateTime>("SELECT DeliveryDate FROM dbo.Bookings WHERE Id = @I", ("@I", lines[0])));
            Assert.Equal(Delivery.AddDays(2), await env.ScalarAsync<DateTime>("SELECT DeliveryDate FROM dbo.Bookings WHERE Id = @I", ("@I", lines[1])));
        }

        [SkippableFact]
        public async Task O_Pdf_OneOrderHeaderWithItsVarieties()
        {
            var env = await OpenAsync();
            var customer = Tag("PDF");
            var (orderId, _, _) = await CreateOrderAsync(env, Sarika, customer);
            var page = await env.PageAsync<RecordsPage>(Admin);
            page.SelectedYear = Delivery.Year; page.SelectedMonth = Delivery.Month; page.SelectedStatus = "Pending";
            var file = Assert.IsType<FileContentResult>(await page.OnGetGeneratePdfAsync());
            var reader = new iTextSharp.text.pdf.PdfReader(file.FileContents);
            var text = string.Concat(Enumerable.Range(1, reader.NumberOfPages).Select(p => iTextSharp.text.pdf.parser.PdfTextExtractor.GetTextFromPage(reader, p)));
            Assert.Equal(1, CountOf(text, $"Booking #{orderId} "));        // the order header once
            Assert.Equal(1, CountOf(text, customer));                        // the customer once, not per variety
            Assert.Contains("3 varieties", text);
            var group = Assert.Single(page.Orders, o => o.OrderId == orderId);
            Assert.Equal(3, group.Lines.Count);
        }

        private static int CountOf(string text, string value)
        {
            var n = 0; var i = 0;
            while ((i = text.IndexOf(value, i, StringComparison.Ordinal)) >= 0) { n++; i += value.Length; }
            return n;
        }

        [SkippableFact]
        public async Task P_Dashboard_OneMultiVarietyOrderCountsAsOneBooking()
        {
            var env = await OpenAsync();
            var v = await VarietiesAsync(env);
            var plant = v[0].PlantId;
            var varieties = await env.IdsAsync("SELECT TOP 3 Id FROM dbo.PlantSpecies WHERE PlantTypeId = @P ORDER BY Id", ("@P", plant));
            Skip.If(varieties.Count < 3, "Need 3 varieties of one species.");
            var crop = await env.ScalarAsync<string>("SELECT Name FROM dbo.PlantTypes WHERE Id = @P", ("@P", plant));

            async Task<(int Count, decimal Qty)> PendingAsync()
            {
                var dashboard = new DashboardPage(env.Get<DatabaseHelper>(), null!, null!, null!, null!, null!, null!);
                await (Task)typeof(DashboardPage).GetMethod("LoadCropsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(dashboard, null)!;
                var row = dashboard.Crops.Single(c => c.Crop == crop);
                return (row.PendingBookings, row.PendingBookingQuantity);
            }

            var before = await PendingAsync();
            var (ok, message, _, _) = await env.Get<BookingRepository>().InsertOrderAsync(await OrderHeaderAsync(env, Sarika, Tag("P")),
                varieties.Select(s => new BookingOrders.NewLine(plant, s, 10, Delivery)).ToList());
            Assert.True(ok, message);
            var after = await PendingAsync();
            Assert.Equal(before.Count + 1, after.Count);        // ONE booking
            Assert.Equal(before.Qty + 30, after.Qty);           // all three varieties' quantities
        }

        [SkippableFact]
        public async Task Q_DailyReport_OneMultiVarietyOrderCountsAsOneBooking()
        {
            var env = await OpenAsync();
            var repo = env.Get<BookingRepository>();
            var before = await repo.GetBookingCountByBookingDateAsync(DateTime.Today, DateTime.Today.AddDays(1));
            await CreateOrderAsync(env, Sarika, Tag("Q"));
            var after = await repo.GetBookingCountByBookingDateAsync(DateTime.Today, DateTime.Today.AddDays(1));
            Assert.Equal(before.Count + 1, after.Count);
            Assert.Equal(before.Quantity + 100, after.Quantity);
        }

        [SkippableFact]
        public async Task R_PottedPlantBookings_AreUnchanged()
        {
            var env = await OpenAsync();
            async Task<List<int>> PottedIds()
            {
                var p = await env.PageAsync<PottedIndexPage>(Admin);
                await p.OnGetAsync();
                return p.Bookings.Select(b => b.Id).OrderBy(i => i).ToList();
            }
            var before = await PottedIds();
            await CreateOrderAsync(env, Sarika, Tag("R"));
            Assert.Equal(before, await PottedIds());
        }
    }
}
