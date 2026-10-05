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
using AddStockPage = PlantStockManager.Pages.Production.EmptyPotInventory.AddStockModel;
using EmptyPotIndexPage = PlantStockManager.Pages.Production.EmptyPotInventory.IndexModel;
using EmptyPotDetailsPage = PlantStockManager.Pages.Production.EmptyPotInventory.DetailsModel;
using IssuePage = PlantStockManager.Pages.Production.EmptyPotInventory.IssueModel;
using RecordsPage = PlantStockManager.Pages.Data.BookingsRecordModel;
using EditRecordsPage = PlantStockManager.Pages.Bookings.BookingRecordsModel;
using DetailsPage = PlantStockManager.Pages.Bookings.BookingDetailsModel;
using RevisePage = PlantStockManager.Pages.Bookings.ReviseModel;
using DispatchPage = PlantStockManager.Pages.Bookings.SeedlingDispatchModel;
using FulfilmentPage = PlantStockManager.Pages.Bookings.FulfilmentModel;
using RegisterPage = PlantStockManager.Pages.Bookings.DispatchRegisterModel;
using BookPage = PlantStockManager.Pages.Bookings.BookModel;
using PottedIndexPage = PlantStockManager.Pages.Production.PottedPlantBooking.IndexModel;

namespace PlantStockManager.Tests
{
    // End-to-end against a SCRATCH COPY of the test database (PSM_SCRATCH_CONNECTION; the database name must start with
    // PlantsIMS2_Scratch_), through the REAL pages and repositories:
    //   * Add Empty Pot Stock (Mother Plant Supervisor, own Area), forged / Main Office / invalid input rejected, double
    //     submit, pot-size separation, consumption, Internal Transfer reversal, concurrency, Purchase Order path retired;
    //   * "own bookings only" for Sandip Galande (14) and Rahul Thorve (15) -- configured by Id exactly as in
    //     appsettings (Authorization:OwnBookingsOnlyUserIds) -- across every list, filter, search, PDF, details and action.
    [Collection("ScratchDb")]
    public class EmptyPotAreaStockAndOwnBookingsE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";

        // users (verified in PlantsIMS2_Test)
        private const int Sandip = 14, Rahul = 15, Sarika = 3, Admin = 21, Vasant = 110, Mangesh = 8;
        // Areas / Empty Pot pools / pot batch
        private const int MainOfficeArea = 2, GreenBless = 127, Tilekarwadi = 128;
        private const int OfficePool4Inch = 254, GreenBlessPool4Inch = 260, PotBatchInGreenBless = 290;

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

            public async Task<int[]> IdsAsync(string sql, params (string Name, object? Value)[] args)
            {
                var list = new List<int>();
                using var conn = new SqlConnection(ConnectionString);
                await conn.OpenAsync();
                using var cmd = new SqlCommand(sql, conn);
                foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) list.Add(r.GetInt32(0));
                return list.OrderBy(i => i).ToArray();
            }

            public async Task<ClaimsPrincipal> LoginAsync(int userId)
            {
                var factory = Get<UserClaimsFactory>();
                var user = await factory.GetActiveUserAsync(userId);
                Assert.NotNull(user);
                return await factory.CreatePrincipalAsync(user!);
            }

            public async Task<T> PageAsync<T>(int userId) where T : PageModel => Page<T>(await LoginAsync(userId));

            public T Page<T>(ClaimsPrincipal principal) where T : PageModel
            {
                var http = new DefaultHttpContext { User = principal };
                var page = ActivatorUtilities.CreateInstance<T>(Provider);
                page.PageContext = new PageContext(new ActionContext(http, new Microsoft.AspNetCore.Routing.RouteData(), new CompiledPageActionDescriptor()))
                {
                    ViewData = new ViewDataDictionary(new Microsoft.AspNetCore.Mvc.ModelBinding.EmptyModelMetadataProvider(), new Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary())
                };
                page.TempData = new TempDataDictionary(http, new NullTempData());
                return page;
            }

            public Task<decimal> PoolQtyAsync(int poolId)
                => ScalarAsync<decimal>("SELECT PhysicalQuantity FROM dbo.EmptyPotInventory WHERE Id = @I", ("@I", poolId));

            public Task<decimal> PoolQtyAsync(string potSize, int areaId)
                => ScalarAsync<decimal>("SELECT ISNULL((SELECT PhysicalQuantity FROM dbo.EmptyPotInventory WHERE PotSize = @P AND AreaId = @A), 0)", ("@P", potSize), ("@A", areaId));
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
            services.AddSingleton<IOptions<SecurityOptions>>(Options.Create(new SecurityOptions
            {
                FullAccessRoleNames = new[] { "System Administrator" },
                OwnBookingsOnlyUserIds = new[] { Sandip, Rahul }       // as configured in appsettings
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

        // Ledger invariant: every pool's PhysicalQuantity equals the sum of its ledger rows, and nothing is negative.
        private static async Task AssertEmptyPotLedgerConsistentAsync(Env env)
        {
            Assert.Equal(0, await env.ScalarAsync<int>(@"SELECT COUNT(*) FROM dbo.EmptyPotInventory e
                OUTER APPLY (SELECT ISNULL(SUM(t.Quantity), 0) AS S FROM dbo.EmptyPotInventoryTransactions t WHERE t.EmptyPotInventoryId = e.Id) l
                WHERE e.PhysicalQuantity <> l.S OR e.PhysicalQuantity < 0"));
        }

        private static async Task<(bool Ok, string? Error, AddStockPage Page)> AddStockAsync(Env env, int user, int? areaId, string potSize, decimal qty,
            DateTime? date = null, string? token = null)
        {
            var page = await env.PageAsync<AddStockPage>(user);
            page.AreaId = areaId; page.PotSize = potSize; page.Quantity = qty; page.StockDate = date ?? DateTime.Today;
            page.Remarks = "E2E add stock"; page.SubmitToken = token ?? SubmissionTokenGuard.NewToken();
            var result = await page.OnPostAsync();
            var error = page.ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).FirstOrDefault()
                        ?? page.TempData["Error"] as string;
            return (result is RedirectToPageResult && error == null, error, page);
        }

        // =========================================================================================================
        // EMPTY POT
        // =========================================================================================================

        [SkippableFact]
        public async Task AddStock_Supervisor_SeesOnlyOwnArea_AndAddsToIt_WithLedgerRow()
        {
            var env = await OpenAsync();
            var get = await env.PageAsync<AddStockPage>(Vasant);
            await get.OnGetAsync();
            Assert.Equal(new[] { GreenBless }, get.Areas.Select(a => a.Id));          // only his own Area, no Main Office
            Assert.Equal(GreenBless, get.AreaId);

            var before = await env.PoolQtyAsync(GreenBlessPool4Inch);
            var other4 = await env.PoolQtyAsync("4 inch", Tilekarwadi);
            var office = await env.PoolQtyAsync(OfficePool4Inch);
            var date = DateTime.Today.AddDays(-2);
            var (ok, error, _) = await AddStockAsync(env, Vasant, GreenBless, "4 inch", 25, date);
            Assert.True(ok, error);
            Assert.Equal(before + 25, await env.PoolQtyAsync(GreenBlessPool4Inch));
            Assert.Equal(other4, await env.PoolQtyAsync("4 inch", Tilekarwadi));      // no other Area changed
            Assert.Equal(office, await env.PoolQtyAsync(OfficePool4Inch));           // Main Office untouched

            // the ledger row: StockIn / AreaStockEntry, the entered date, the supervisor
            var row = await env.ScalarAsync<int>(@"SELECT TOP 1 Id FROM dbo.EmptyPotInventoryTransactions
                WHERE EmptyPotInventoryId = @P AND TransactionType = 'StockIn' AND ReferenceType = 'AreaStockEntry' AND Quantity = 25
                  AND UserId = @U AND CAST(TransactionDate AS DATE) = @D AND BeforeQuantity = @B ORDER BY Id DESC",
                ("@P", GreenBlessPool4Inch), ("@U", Vasant), ("@D", date), ("@B", before));
            Assert.True(row > 0);
            await AssertEmptyPotLedgerConsistentAsync(env);
        }

        [SkippableFact]
        public async Task AddStock_ForgedUnauthorizedArea_IsRejected_NothingChanges()
        {
            var env = await OpenAsync();
            var other = await env.PoolQtyAsync("4 inch", Tilekarwadi);
            var count = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.EmptyPotInventoryTransactions");
            var (ok, error, _) = await AddStockAsync(env, Vasant, Tilekarwadi, "4 inch", 10);   // Mangesh's Area, posted by Vasant
            Assert.False(ok);
            Assert.Equal(EmptyPotStockRules.AreaNotAllowedMessage, error);
            Assert.Equal(other, await env.PoolQtyAsync("4 inch", Tilekarwadi));
            Assert.Equal(count, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.EmptyPotInventoryTransactions"));

            // a non-existent Area and no Area at all
            Assert.False((await AddStockAsync(env, Vasant, 999999, "4 inch", 10)).Ok);
            Assert.False((await AddStockAsync(env, Vasant, null, "4 inch", 10)).Ok);
            Assert.Equal(count, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.EmptyPotInventoryTransactions"));
        }

        [SkippableFact]
        public async Task AddStock_MainOfficeArea_IsRejected_EvenForTheAdministrator()
        {
            var env = await OpenAsync();
            var office = await env.PoolQtyAsync(OfficePool4Inch);
            var (ok, error, _) = await AddStockAsync(env, Admin, MainOfficeArea, "4 inch", 10);
            Assert.False(ok);
            Assert.Equal(EmptyPotStockRules.AreaNotAllowedMessage, error);
            Assert.Equal(office, await env.PoolQtyAsync(OfficePool4Inch));

            // and the repository itself refuses it even if called directly
            var (repoOk, repoMessage, _) = await env.Get<EmptyPotInventoryRepository>().AddAreaStockAsync(MainOfficeArea, "4 inch", 10, DateTime.Today, null, Admin, "e2e");
            Assert.False(repoOk);
            Assert.Equal(EmptyPotStockRules.AreaNotAllowedMessage, repoMessage);
            Assert.Equal(office, await env.PoolQtyAsync(OfficePool4Inch));
        }

        [SkippableFact]
        public async Task AddStock_Administrator_CanAddToAnyProductionArea()
        {
            var env = await OpenAsync();
            var get = await env.PageAsync<AddStockPage>(Admin);
            await get.OnGetAsync();
            Assert.Contains(get.Areas, a => a.Id == Tilekarwadi);
            Assert.DoesNotContain(get.Areas, a => a.AreaType == "MainOffice");
            var before = await env.PoolQtyAsync("4 inch", Tilekarwadi);
            Assert.True((await AddStockAsync(env, Admin, Tilekarwadi, "4 inch", 3)).Ok);
            Assert.Equal(before + 3, await env.PoolQtyAsync("4 inch", Tilekarwadi));
        }

        [SkippableTheory]
        [InlineData(0, 0, "4 inch")]
        [InlineData(-5, 0, "4 inch")]
        [InlineData(2.5, 0, "4 inch")]
        [InlineData(5, 1, "4 inch")]          // tomorrow
        [InlineData(5, 0, "99 inch")]         // not a pot size
        [InlineData(5, 0, "")]
        public async Task AddStock_InvalidInput_IsRejected(double qty, int daysAhead, string potSize)
        {
            var env = await OpenAsync();
            var count = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.EmptyPotInventoryTransactions");
            var (ok, error, _) = await AddStockAsync(env, Vasant, GreenBless, potSize, (decimal)qty, DateTime.Today.AddDays(daysAhead));
            Assert.False(ok);
            Assert.False(string.IsNullOrEmpty(error));
            Assert.Equal(count, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.EmptyPotInventoryTransactions"));
        }

        [SkippableFact]
        public async Task AddStock_SameFormSubmittedTwice_AddsOnlyOnce()
        {
            var env = await OpenAsync();
            var before = await env.PoolQtyAsync(GreenBlessPool4Inch);
            var token = SubmissionTokenGuard.NewToken();
            Assert.True((await AddStockAsync(env, Vasant, GreenBless, "4 inch", 4, token: token)).Ok);
            var (again, error, _) = await AddStockAsync(env, Vasant, GreenBless, "4 inch", 4, token: token);
            Assert.False(again);
            Assert.Contains("already submitted", error);
            Assert.Equal(before + 4, await env.PoolQtyAsync(GreenBlessPool4Inch));
        }

        [SkippableFact]
        public async Task AddStock_PotSizesStaySeparate()
        {
            var env = await OpenAsync();
            var four = await env.PoolQtyAsync("4 inch", GreenBless);
            var eight = await env.PoolQtyAsync("8 inch", GreenBless);
            Assert.True((await AddStockAsync(env, Vasant, GreenBless, "8 inch", 6)).Ok);
            Assert.Equal(four, await env.PoolQtyAsync("4 inch", GreenBless));
            Assert.Equal(eight + 6, await env.PoolQtyAsync("8 inch", GreenBless));
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.EmptyPotInventory WHERE PotSize = '8 inch' AND AreaId = @A", ("@A", GreenBless)));
        }

        [SkippableFact]
        public async Task EmptyPotList_AndHistory_RespectAreaAccess()
        {
            var env = await OpenAsync();
            var index = await env.PageAsync<EmptyPotIndexPage>(Vasant);
            await index.OnGetAsync();
            Assert.NotEmpty(index.EmptyPotInventories);
            Assert.All(index.EmptyPotInventories, e => Assert.True(e.AreaId == null || e.AreaId == GreenBless, $"pool {e.Id} in Area {e.AreaId}"));

            var foreignPool = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.EmptyPotInventory WHERE AreaId = @A", ("@A", Tilekarwadi));
            var details = await env.PageAsync<EmptyPotDetailsPage>(Vasant);
            Assert.IsType<RedirectToPageResult>(await details.OnGetAsync(foreignPool));
            Assert.Empty(details.Transactions);

            var own = await env.PageAsync<EmptyPotDetailsPage>(Vasant);
            Assert.IsType<PageResult>(await own.OnGetAsync(GreenBlessPool4Inch));
            Assert.NotEmpty(own.Transactions);

            var admin = await env.PageAsync<EmptyPotIndexPage>(Admin);
            await admin.OnGetAsync();
            Assert.Equal(await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.EmptyPotInventory"), admin.EmptyPotInventories.Count);
        }

        [SkippableFact]
        public async Task Consumption_StillTakesPotsFromTheBatchAreaPool()
        {
            var env = await OpenAsync();
            Assert.True((await AddStockAsync(env, Vasant, GreenBless, "4 inch", 5)).Ok);   // make sure there are pots
            var before = await env.PoolQtyAsync(GreenBlessPool4Inch);
            var (ok, message) = await env.Get<PotBatchRepository>().AddEntryAsync(PotBatchInGreenBless, DateTime.Today, 1, "E2E consumption", Vasant, "e2e");
            Assert.True(ok, message);
            Assert.Equal(before - 1, await env.PoolQtyAsync(GreenBlessPool4Inch));
            Assert.Equal(-1m, await env.ScalarAsync<decimal>(@"SELECT TOP 1 Quantity FROM dbo.EmptyPotInventoryTransactions
                WHERE EmptyPotInventoryId = @P AND TransactionType = 'Consumption' ORDER BY Id DESC", ("@P", GreenBlessPool4Inch)));
            await AssertEmptyPotLedgerConsistentAsync(env);
        }

        [SkippableFact]
        public async Task AdminIssue_FromOfficeStock_AndCancelReversal_ReturnPotsToTheRightPools()
        {
            var env = await OpenAsync();
            var office = await env.PoolQtyAsync(OfficePool4Inch);
            var area = await env.PoolQtyAsync(GreenBlessPool4Inch);
            Skip.If(office < 10, "Main Office 4 inch stock is used up on this scratch copy.");

            var issue = await env.PageAsync<IssuePage>(Admin);
            issue.SourcePoolId = OfficePool4Inch; issue.DestinationAreaId = GreenBless; issue.Quantity = 10; issue.Remarks = "E2E issue";
            Assert.IsType<RedirectToPageResult>(await issue.OnPostAsync());
            Assert.Equal(office - 10, await env.PoolQtyAsync(OfficePool4Inch));
            Assert.Equal(area + 10, await env.PoolQtyAsync(GreenBlessPool4Inch));

            var transferId = await env.ScalarAsync<int>("SELECT MAX(Id) FROM dbo.InternalTransfers WHERE StockType = 'EmptyPot' AND SourceEmptyPotInventoryId = @P", ("@P", OfficePool4Inch));
            var (ok, message) = await env.Get<InternalTransferRepository>().CancelAsync(transferId, "e2e", Admin);
            Assert.True(ok, message);
            Assert.Equal(office, await env.PoolQtyAsync(OfficePool4Inch));
            Assert.Equal(area, await env.PoolQtyAsync(GreenBlessPool4Inch));
            await AssertEmptyPotLedgerConsistentAsync(env);
        }

        [SkippableFact]
        public async Task ConcurrentConsumption_NeverTakesStockNegative()
        {
            var env = await OpenAsync();
            Assert.True((await AddStockAsync(env, Mangesh, Tilekarwadi, "6 inch", 20)).Ok);
            var poolId = await env.ScalarAsync<int>("SELECT Id FROM dbo.EmptyPotInventory WHERE PotSize = '6 inch' AND AreaId = @A", ("@A", Tilekarwadi));
            var start = await env.PoolQtyAsync(poolId);
            var chunk = Math.Max(1m, Math.Ceiling(start / 4m));
            var repo = env.Get<EmptyPotInventoryRepository>();

            async Task<bool> ConsumeAsync()
            {
                using var conn = new SqlConnection(env.ConnectionString);
                await conn.OpenAsync();
                using var tx = conn.BeginTransaction();
                var (ok, _) = await repo.RecordTransactionAsync(conn, tx, poolId, -chunk, "Consumption", "E2EConcurrency", null, Mangesh, "E2E concurrent consumption");
                if (ok) tx.Commit(); else tx.Rollback();
                return ok;
            }

            var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(ConsumeAsync)));
            var successes = results.Count(r => r);
            Assert.Equal((int)Math.Floor(start / chunk), successes);
            Assert.Equal(start - successes * chunk, await env.PoolQtyAsync(poolId));
            Assert.True(await env.PoolQtyAsync(poolId) >= 0);
            await AssertEmptyPotLedgerConsistentAsync(env);
        }

        [SkippableFact]
        public async Task ConcurrentAdds_AreAllCounted_AndCreateOnePool()
        {
            var env = await OpenAsync();
            var before = await env.PoolQtyAsync("7 inch", Tilekarwadi);
            var repo = env.Get<EmptyPotInventoryRepository>();
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
                repo.AddAreaStockAsync(Tilekarwadi, "7 inch", 7, DateTime.Today, "E2E concurrent add", Mangesh, "e2e"))));
            Assert.All(results, r => Assert.True(r.Success, r.Message));
            Assert.Equal(before + 56, await env.PoolQtyAsync("7 inch", Tilekarwadi));
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.EmptyPotInventory WHERE PotSize = '7 inch' AND AreaId = @A", ("@A", Tilekarwadi)));
            await AssertEmptyPotLedgerConsistentAsync(env);
        }

        [SkippableFact]
        public async Task PurchaseOrder_EmptyPotLines_CanNeitherBeOrderedNorReceived()
        {
            var env = await OpenAsync();
            var poRepo = env.Get<PurchaseOrderRepository>();
            var vendor = await env.ScalarAsync<int>(@"IF NOT EXISTS (SELECT 1 FROM dbo.Vendors WHERE Name = 'ZZTEST-E2E-Vendor')
                INSERT INTO dbo.Vendors (Name, IsActive) VALUES ('ZZTEST-E2E-Vendor', 1);
                SELECT Id FROM dbo.Vendors WHERE Name = 'ZZTEST-E2E-Vendor'");

            var (created, createMessage, _) = await poRepo.InsertAsync(new PurchaseOrder
            {
                VendorId = vendor, OrderDate = DateTime.Today, CreatedBy = "e2e",
                Items = new List<PurchaseOrderItem> { new() { ItemCategory = "EmptyPot", PotSize = "4 inch", AreaId = GreenBless, OrderedQuantity = 50 } }
            }, Admin);
            Assert.False(created);
            Assert.Equal(PurchaseOrderRepository.EmptyPotPurchaseOrderRetiredMessage, createMessage);

            // a (historical-style) EmptyPot line that already exists cannot be received into stock either
            var (ok, message, poId) = await poRepo.InsertAsync(new PurchaseOrder
            {
                VendorId = vendor, OrderDate = DateTime.Today, CreatedBy = "e2e",
                Items = new List<PurchaseOrderItem> { new() { ItemCategory = "Other", ItemName = "E2E line", OrderedQuantity = 50 } }
            }, Admin);
            Assert.True(ok, message);
            var itemId = await env.ScalarAsync<int>("SELECT Id FROM dbo.PurchaseOrderItems WHERE PurchaseOrderId = @P", ("@P", poId));
            await env.ExecAsync("UPDATE dbo.PurchaseOrderItems SET ItemCategory = 'EmptyPot', PotSize = '4 inch', AreaId = @A, ItemName = NULL WHERE Id = @I",
                ("@A", GreenBless), ("@I", itemId));
            var pool = await env.PoolQtyAsync(GreenBlessPool4Inch);
            var (received, receiveMessage, _) = await poRepo.InsertReceiptAsync(poId, new List<(int, decimal)> { (itemId, 50) }, DateTime.Today, null, null, "e2e", Admin);
            Assert.False(received);
            Assert.Equal(PurchaseOrderRepository.EmptyPotPurchaseOrderRetiredMessage, receiveMessage);
            Assert.Equal(pool, await env.PoolQtyAsync(GreenBlessPool4Inch));
            Assert.Equal(0m, await env.ScalarAsync<decimal>("SELECT ReceivedQuantity FROM dbo.PurchaseOrderItems WHERE Id = @I", ("@I", itemId)));
        }

        // =========================================================================================================
        // BOOKINGS -- own bookings only
        // =========================================================================================================

        private sealed class BookingSeed
        {
            public int S1, S2, R1, R2, O1;   // Sandip x2, Rahul x2, Sarika (another Booking Executive)
            public string Tag = "";
            public int[] All => new[] { S1, S2, R1, R2, O1 };
        }

        private const int PlantA = 4, SpeciesA = 3179, Year = 2026, Month = 12;

        private static async Task<int> InsertBookingAsync(Env env, int bookedBy, string customer)
        {
            var state = await env.ScalarAsync<int>("SELECT TOP 1 StateId FROM dbo.Bookings WHERE StateId IS NOT NULL AND DistrictId IS NOT NULL ORDER BY Id DESC");
            var district = await env.ScalarAsync<int>("SELECT TOP 1 DistrictId FROM dbo.Bookings WHERE StateId = @S AND DistrictId IS NOT NULL ORDER BY Id DESC", ("@S", state));
            var id = await env.Get<BookingRepository>().InsertBookingAsync(new Booking
            {
                PlantId = PlantA, SpeciesId = SpeciesA, CustomerName = customer, DeliveryDate = new DateTime(Year, Month, 20), Quantity = 10,
                AddedBy = "e2e", Address = "e2e", Contact = "9999999999", AdvanceTaken = false, StateId = state, DistrictId = district, BookedById = bookedBy
            });
            Assert.True(id > 0);
            return await env.ScalarAsync<int>("SELECT MAX(Id) FROM dbo.Bookings WHERE CustomerName = @C", ("@C", customer));
        }

        private static async Task<BookingSeed> SeedBookingsAsync(Env env)
        {
            var tag = "E2E-OWN-" + Guid.NewGuid().ToString("N")[..8];
            return new BookingSeed
            {
                Tag = tag,
                S1 = await InsertBookingAsync(env, Sandip, tag + "-S1"),
                S2 = await InsertBookingAsync(env, Sandip, tag + "-S2"),
                R1 = await InsertBookingAsync(env, Rahul, tag + "-R1"),
                R2 = await InsertBookingAsync(env, Rahul, tag + "-R2"),
                O1 = await InsertBookingAsync(env, Sarika, tag + "-O1"),
            };
        }

        private static async Task<List<Booking>> RecordsAsync(Env env, int user, string? bookedBy = null, bool pdf = false)
        {
            var page = await env.PageAsync<RecordsPage>(user);
            page.SelectedMonth = Month; page.SelectedYear = Year; page.SelectedStatus = "Pending"; page.SelectedBookedBy = bookedBy;
            if (pdf) await page.OnGetGeneratePdfAsync(); else await page.OnGetAsync();
            return page.Bookings;
        }

        private static async Task<List<SeedlingBookingSummary>> FulfilmentAsync(Env env, int user, string? search)
        {
            var page = await env.PageAsync<FulfilmentPage>(user);
            page.Status = "All"; page.Search = search;
            await page.OnGetAsync();
            return page.Bookings;
        }

        private static int[] Ids(IEnumerable<Booking> l) => l.Select(b => b.Id).OrderBy(i => i).ToArray();
        private static int[] Ids(IEnumerable<SeedlingBookingSummary> l) => l.Select(b => b.Id).OrderBy(i => i).ToArray();
        private static int[] Set(params int[] ids) => ids.OrderBy(i => i).ToArray();

        [SkippableFact]
        public async Task RestrictedUsers_SeeOnlyTheirOwnBookings_OthersSeeEverything()
        {
            var env = await OpenAsync();
            var d = await SeedBookingsAsync(env);

            var sandip = await RecordsAsync(env, Sandip);
            Assert.NotEmpty(sandip);
            Assert.All(sandip, b => Assert.Equal(Sandip, b.BookedById));
            Assert.Equal(Set(d.S1, d.S2), Ids(sandip).Intersect(d.All).ToArray());
            // exactly an independent SQL oracle of Sandip's own Pending December bookings
            Assert.Equal(await env.IdsAsync(@"SELECT b.Id FROM dbo.Bookings b INNER JOIN dbo.PlantTypes pt ON b.PlantId = pt.Id INNER JOIN dbo.PlantSpecies ps ON b.SpeciesId = ps.Id
                WHERE YEAR(b.DeliveryDate) = @Y AND MONTH(b.DeliveryDate) = @M AND b.Status = 'Pending' AND b.BookedById = @U", ("@Y", Year), ("@M", Month), ("@U", Sandip)), Ids(sandip));

            var rahul = await RecordsAsync(env, Rahul);
            Assert.All(rahul, b => Assert.Equal(Rahul, b.BookedById));
            Assert.Equal(Set(d.R1, d.R2), Ids(rahul).Intersect(d.All).ToArray());

            foreach (var everyone in new[] { Sarika, Admin })
            {
                var all = await RecordsAsync(env, everyone);
                Assert.Equal(Set(d.All), Ids(all).Intersect(d.All).ToArray());
                Assert.Equal(await env.ScalarAsync<int>(@"SELECT COUNT(*) FROM dbo.Bookings b INNER JOIN dbo.PlantTypes pt ON b.PlantId = pt.Id INNER JOIN dbo.PlantSpecies ps ON b.SpeciesId = ps.Id
                    WHERE YEAR(b.DeliveryDate) = @Y AND MONTH(b.DeliveryDate) = @M AND b.Status = 'Pending'", ("@Y", Year), ("@M", Month)), all.Count);
            }

            var page = await env.PageAsync<RecordsPage>(Sandip);
            page.SelectedMonth = Month; page.SelectedYear = Year;
            await page.OnGetAsync();
            Assert.True(page.ShowingOwnBookingsOnly);
            Assert.Equal(new[] { "U:14" }, page.BookedByOptions.Select(o => o.Value));      // the filter offers nobody else
        }

        [SkippableFact]
        public async Task FiltersSearchAndPdf_CannotExposeAnotherUsersBooking()
        {
            var env = await OpenAsync();
            var d = await SeedBookingsAsync(env);

            Assert.Empty(await RecordsAsync(env, Sandip, "U:15"));                        // forged "Booking By = Rahul"
            Assert.Empty(await RecordsAsync(env, Sandip, "U:3"));
            Assert.Empty((await RecordsAsync(env, Sandip, "X:none")).Where(b => b.BookedById != Sandip));
            Assert.All(await RecordsAsync(env, Sandip, pdf: true), b => Assert.Equal(Sandip, b.BookedById));
            Assert.Equal(Set(d.S1, d.S2), Ids(await RecordsAsync(env, Sandip, pdf: true)).Intersect(d.All).ToArray());

            // Fulfilment search: by customer text and by exact booking Id
            Assert.Equal(Set(d.S1, d.S2), Ids(await FulfilmentAsync(env, Sandip, d.Tag)));
            Assert.Equal(Set(d.R1, d.R2), Ids(await FulfilmentAsync(env, Rahul, d.Tag)));
            Assert.Empty(await FulfilmentAsync(env, Sandip, d.R1.ToString()));
            Assert.Empty(await FulfilmentAsync(env, Rahul, d.S1.ToString()));
            Assert.Equal(Set(d.All), Ids(await FulfilmentAsync(env, Sarika, d.Tag)));
            Assert.Equal(Set(d.All), Ids(await FulfilmentAsync(env, Admin, d.Tag)));
            Assert.All(await FulfilmentAsync(env, Sandip, null), b => Assert.Equal(Sandip, b.BookedById));   // no filter at all

            // Edit Booking Records list (and its Booked By dropdown) too
            var edit = await env.PageAsync<EditRecordsPage>(Rahul);
            edit.SelectedMonth = Month; edit.SelectedYear = Year; edit.SelectedBookedBy = "U:14";
            await edit.OnGetAsync();
            Assert.Empty(edit.Bookings);
            Assert.Equal(new[] { Rahul }, edit.Employees.Select(e => e.EmployeeID));
        }

        [SkippableFact]
        public async Task DispatchRegister_ShowsOnlyDispatchesOfOwnBookings()
        {
            var env = await OpenAsync();
            var from = new DateTime(2000, 1, 1);
            var to = DateTime.Today.AddYears(1);
            foreach (var user in new[] { Sandip, Rahul })
            {
                var page = await env.PageAsync<RegisterPage>(user);
                page.From = from; page.To = to;
                await page.OnGetAsync();
                Assert.True(page.ShowingOwnBookingsOnly);
                var expected = await env.ScalarAsync<int>(@"SELECT COUNT(*) FROM dbo.SeedlingDispatchLines l INNER JOIN dbo.SeedlingDispatches d ON d.Id = l.SeedlingDispatchId
                    INNER JOIN dbo.Bookings b ON b.Id = d.BookingId WHERE b.BookedById = @U AND d.DispatchDate BETWEEN @F AND @T", ("@U", user), ("@F", from), ("@T", to));
                Assert.Equal(expected, page.Lines.Count);
                foreach (var bookingId in page.Lines.Select(l => l.BookingId).Distinct())
                    Assert.Equal(user, await env.ScalarAsync<int>("SELECT BookedById FROM dbo.Bookings WHERE Id = @I", ("@I", bookingId)));
            }
            var all = await env.PageAsync<RegisterPage>(Sarika);
            all.From = from; all.To = to;
            await all.OnGetAsync();
            Assert.False(all.ShowingOwnBookingsOnly);
            Assert.Equal(await env.ScalarAsync<int>(@"SELECT COUNT(*) FROM dbo.SeedlingDispatchLines l INNER JOIN dbo.SeedlingDispatches d ON d.Id = l.SeedlingDispatchId
                WHERE d.DispatchDate BETWEEN @F AND @T", ("@F", from), ("@T", to)), all.Lines.Count);
        }

        [SkippableFact]
        public async Task OpeningAnotherUsersBookingById_IsRejected_EverywhereOwnAndAdminWork()
        {
            var env = await OpenAsync();
            var d = await SeedBookingsAsync(env);

            foreach (var (user, foreign, own) in new[] { (Sandip, d.R1, d.S1), (Rahul, d.S1, d.R1), (Sandip, d.O1, d.S2) })
            {
                var details = await env.PageAsync<DetailsPage>(user); details.Id = foreign;
                Assert.IsType<RedirectToPageResult>(await details.OnGetAsync());
                Assert.Null(details.Booking);
                Assert.Equal(BookingOwnershipService.NotFoundMessage, details.TempData["Error"]);

                var revise = await env.PageAsync<RevisePage>(user); revise.Id = foreign;
                Assert.IsType<RedirectToPageResult>(await revise.OnGetAsync());

                var dispatch = await env.PageAsync<DispatchPage>(user); dispatch.Id = foreign;
                Assert.IsType<RedirectToPageResult>(await dispatch.OnGetAsync());
                Assert.Null(dispatch.Booking);

                var mine = await env.PageAsync<DetailsPage>(user); mine.Id = own;
                Assert.IsType<PageResult>(await mine.OnGetAsync());
                Assert.Equal(own, mine.Booking!.Id);
            }

            foreach (var everyone in new[] { Sarika, Admin })
                foreach (var id in d.All)
                {
                    var p = await env.PageAsync<DetailsPage>(everyone); p.Id = id;
                    Assert.IsType<PageResult>(await p.OnGetAsync());
                    var s = await env.PageAsync<DispatchPage>(everyone); s.Id = id;
                    Assert.IsType<PageResult>(await s.OnGetAsync());
                }
        }

        private static Task<(string Status, decimal Reserved, int Revision, int Allocations, int Quantity, int? BookedBy)> StateAsync(Env env, int id)
            => Task.Run(async () => (
                await env.ScalarAsync<string>("SELECT ISNULL(Status, 'Pending') FROM dbo.Bookings WHERE Id = @I", ("@I", id)),
                await env.ScalarAsync<decimal>("SELECT ReservedQuantity FROM dbo.Bookings WHERE Id = @I", ("@I", id)),
                await env.ScalarAsync<int>("SELECT RevisionNo FROM dbo.Bookings WHERE Id = @I", ("@I", id)),
                await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.BookingBatchAllocations WHERE BookingId = @I", ("@I", id)),
                await env.ScalarAsync<int>("SELECT Quantity FROM dbo.Bookings WHERE Id = @I", ("@I", id)),
                (int?)await env.ScalarAsync<int>("SELECT BookedById FROM dbo.Bookings WHERE Id = @I", ("@I", id))));

        [SkippableFact]
        public async Task ForgedActionsOnAnotherUsersBooking_AreRejected_AndChangeNothing()
        {
            var env = await OpenAsync();
            var d = await SeedBookingsAsync(env);
            var anyReadyStock = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.ReadyStock ORDER BY Id DESC");

            foreach (var (user, foreign) in new[] { (Sandip, d.R1), (Rahul, d.S1), (Sandip, d.O1) })
            {
                var before = await StateAsync(env, foreign);
                var notFound = BookingOwnershipService.NotFoundMessage;

                var p1 = await env.PageAsync<DetailsPage>(user); p1.Id = foreign; p1.ReserveQuantity = 1;
                await p1.OnPostReserveAsync(); Assert.Equal(notFound, p1.TempData["Error"]);
                var p2 = await env.PageAsync<DetailsPage>(user); p2.Id = foreign;
                await p2.OnPostReleaseAllAsync(); Assert.Equal(notFound, p2.TempData["Error"]);
                var p3 = await env.PageAsync<DetailsPage>(user); p3.Id = foreign; p3.CancelReason = "forged";
                await p3.OnPostCancelAsync(); Assert.Equal(notFound, p3.TempData["Error"]);

                var a = await env.PageAsync<DispatchPage>(user); a.Id = foreign; a.AllocateReadyStockId = anyReadyStock; a.AllocateQuantity = 1;
                a.AllocateToken = SubmissionTokenGuard.NewToken();
                await a.OnPostAllocateAsync(); Assert.Equal(notFound, a.TempData["Error"]);
                var r = await env.PageAsync<DispatchPage>(user); r.Id = foreign; r.ReleaseAllocationId = 1; r.ReleaseQuantity = 1;
                await r.OnPostReleaseAsync(); Assert.Equal(notFound, r.TempData["Error"]);
                var x = await env.PageAsync<DispatchPage>(user); x.Id = foreign;
                x.Lines = new List<DispatchPage.DispatchLineInput> { new() { AllocationId = 1, Quantity = 1 } };
                await x.OnPostDispatchAsync(); Assert.Equal(notFound, x.TempData["Error"]);

                var rev = await env.PageAsync<RevisePage>(user); rev.Id = foreign; rev.NewPlantId = PlantA; rev.NewSpeciesId = SpeciesA; rev.NewQuantity = 5; rev.Reason = "forged";
                Assert.IsType<RedirectToPageResult>(await rev.OnPostAsync());
                Assert.Equal(notFound, rev.TempData["Error"]);

                var edit = await env.PageAsync<EditRecordsPage>(user);
                edit.Booking = new Booking { Id = foreign, PlantId = PlantA, SpeciesId = SpeciesA, Quantity = 3, CustomerName = "FORGED", DeliveryDate = new DateTime(Year, Month, 20),
                                             StateId = 1, DistrictId = 1, BookedById = user };
                var editResult = Assert.IsType<JsonResult>(await edit.OnPostEditBookingAsync());
                Assert.Contains("success = False", editResult.Value!.ToString());
                var del = await env.PageAsync<EditRecordsPage>(user);
                var delResult = Assert.IsType<JsonResult>(await del.OnPostDeleteBookingAsync(foreign, "forged"));
                Assert.Contains("success = False", delResult.Value!.ToString());

                Assert.Equal(before, await StateAsync(env, foreign));                       // nothing changed
                Assert.NotEqual("FORGED", await env.ScalarAsync<string>("SELECT CustomerName FROM dbo.Bookings WHERE Id = @I", ("@I", foreign)));
            }
        }

        [SkippableFact]
        public async Task OwnBookings_StillWork_AndStayOwned()
        {
            var env = await OpenAsync();
            var d = await SeedBookingsAsync(env);

            // edit own booking, trying to hand it to Sarika: saved, but still booked by Sandip
            var edit = await env.PageAsync<EditRecordsPage>(Sandip);
            var state = await env.ScalarAsync<int>("SELECT StateId FROM dbo.Bookings WHERE Id = @I", ("@I", d.S1));
            var district = await env.ScalarAsync<int>("SELECT DistrictId FROM dbo.Bookings WHERE Id = @I", ("@I", d.S1));
            edit.Booking = new Booking { Id = d.S1, PlantId = PlantA, SpeciesId = SpeciesA, Quantity = 12, CustomerName = d.Tag + "-S1", Address = "e2e", Contact = "9999999999",
                                         DeliveryDate = new DateTime(Year, Month, 20), AdvanceTaken = false, StateId = state, DistrictId = district, BookedById = Sarika };
            edit.RevisionReason = "E2E own edit";
            var result = Assert.IsType<JsonResult>(await edit.OnPostEditBookingAsync());
            Assert.Contains("success = True", result.Value!.ToString());
            Assert.Equal(12, await env.ScalarAsync<int>("SELECT Quantity FROM dbo.Bookings WHERE Id = @I", ("@I", d.S1)));
            Assert.Equal(Sandip, await env.ScalarAsync<int>("SELECT BookedById FROM dbo.Bookings WHERE Id = @I", ("@I", d.S1)));

            // cancel own booking through Booking Details
            var cancel = await env.PageAsync<DetailsPage>(Sandip); cancel.Id = d.S2; cancel.CancelReason = "E2E own cancel";
            await cancel.OnPostCancelAsync();
            Assert.Null(cancel.TempData["Error"]);
            Assert.Equal("Cancelled", await env.ScalarAsync<string>("SELECT Status FROM dbo.Bookings WHERE Id = @I", ("@I", d.S2)));

            // another Booking Executive can still act on anyone's booking
            var other = await env.PageAsync<DetailsPage>(Sarika); other.Id = d.R2; other.CancelReason = "E2E other exec cancel";
            await other.OnPostCancelAsync();
            Assert.Equal("Cancelled", await env.ScalarAsync<string>("SELECT Status FROM dbo.Bookings WHERE Id = @I", ("@I", d.R2)));
        }

        [SkippableFact]
        public async Task BookPage_RestrictedUser_AlwaysBooksAsThemselves_OthersChooseFreely()
        {
            var env = await OpenAsync();
            var tag = "E2E-OWN-BOOK-" + Guid.NewGuid().ToString("N")[..8];
            var state = await env.ScalarAsync<int>("SELECT TOP 1 StateId FROM dbo.Bookings WHERE StateId IS NOT NULL AND DistrictId IS NOT NULL ORDER BY Id DESC");
            var district = await env.ScalarAsync<int>("SELECT TOP 1 DistrictId FROM dbo.Bookings WHERE StateId = @S AND DistrictId IS NOT NULL ORDER BY Id DESC", ("@S", state));
            Booking NewBooking(string suffix, int bookedBy) => new()
            {
                PlantId = PlantA, SpeciesId = SpeciesA, CustomerName = tag + suffix, DeliveryDate = new DateTime(Year, Month, 21), Quantity = 5,
                Address = "e2e", Contact = "9999999999", AdvanceTaken = false, StateId = state, DistrictId = district, BookedById = bookedBy
            };

            var get = await env.PageAsync<BookPage>(Sandip);
            await get.OnGet();
            Assert.True(get.BookedBySelfOnly);
            Assert.Equal(new[] { Sandip }, get.Employees.Select(e => e.EmployeeID));

            var sandip = await env.PageAsync<BookPage>(Sandip);
            sandip.Booking = NewBooking("-forged", Sarika);                                  // forged "Booked By = Sarika"
            await sandip.OnPostAsync();
            Assert.Equal(Sandip, await env.ScalarAsync<int>("SELECT BookedById FROM dbo.Bookings WHERE CustomerName = @C", ("@C", tag + "-forged")));

            var sarika = await env.PageAsync<BookPage>(Sarika);
            sarika.Booking = NewBooking("-sarika", Rahul);                                    // unrestricted: books on behalf of Rahul
            await sarika.OnPostAsync();
            Assert.Equal(Rahul, await env.ScalarAsync<int>("SELECT BookedById FROM dbo.Bookings WHERE CustomerName = @C", ("@C", tag + "-sarika")));
            var free = await env.PageAsync<BookPage>(Sarika);
            await free.OnGet();
            Assert.False(free.BookedBySelfOnly);
            Assert.True(free.Employees.Count > 1);
        }

        [SkippableFact]
        public async Task Repository_OwnerFilter_IsAppliedInSql()
        {
            var env = await OpenAsync();
            var fulfilment = env.Get<SeedlingFulfilmentRepository>();
            foreach (var user in new[] { Sandip, Rahul })
            {
                var list = await fulfilment.ListBookingsAsync(null, null, null, null, null, user);
                Assert.All(list, b => Assert.Equal(user, b.BookedById));
                Assert.Equal(Math.Min(1000, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Bookings WHERE BookedById = @U", ("@U", user))), list.Count);
            }
            Assert.Equal(Math.Min(1000, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Bookings")),
                (await fulfilment.ListBookingsAsync(null, null, null, null, null, null)).Count);
            var options = await env.Get<BookingRepository>().GetBookedByOptionsAsync(Rahul);
            Assert.Equal(new[] { "U:15" }, options.Select(o => o.Value));
        }

        [SkippableFact]
        public async Task PottedPlantBookings_AreUnchangedForRestrictedUsers()
        {
            var env = await OpenAsync();
            var sandip = await env.PageAsync<PottedIndexPage>(Sandip);
            await sandip.OnGetAsync();
            var sarika = await env.PageAsync<PottedIndexPage>(Sarika);    // same role and Area, not restricted
            await sarika.OnGetAsync();
            Assert.Equal(sarika.Bookings.Select(b => b.Id).OrderBy(i => i), sandip.Bookings.Select(b => b.Id).OrderBy(i => i));
        }
    }
}
