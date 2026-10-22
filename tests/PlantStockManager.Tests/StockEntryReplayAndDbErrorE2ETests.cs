using System.Data.SqlTypes;
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using EmptyPotIssuePage = PlantStockManager.Pages.Production.EmptyPotInventory.IssueModel;
using EmptyPotPurchasePage = PlantStockManager.Pages.Production.EmptyPotInventory.PurchaseModel;
using FertilizerStockPage = PlantStockManager.Pages.Fertilizer.StockModel;
using OutletBookingDetailsPage = PlantStockManager.Pages.Production.OutletBooking.DetailsModel;
using OutletBookingPage = PlantStockManager.Pages.Production.OutletBooking.CreateModel;
using OutletPurchasePage = PlantStockManager.Pages.Production.OutletPurchase.CreateModel;
using OutletSalePage = PlantStockManager.Pages.Production.OutletSale.CreateModel;
using OutletWastagePage = PlantStockManager.Pages.Production.OutletWastage.CreateModel;
using PottedBookingPage = PlantStockManager.Pages.Production.PottedPlantBooking.CreateModel;
using PurchaseOrderReceivePage = PlantStockManager.Pages.Production.PurchaseOrder.ReceiveModel;

namespace PlantStockManager.Tests
{
    // 2026-10-21 security sweep (second round), against a SCRATCH COPY only (PSM_SCRATCH_CONNECTION, database name
    // PlantsIMS2_Scratch_*; SKIPPED otherwise):
    //   * Duplicate submission -- these stock-entry forms recorded the movement AGAIN for every repeat of the same form
    //     (double click, browser retry, replayed request, simultaneous posts). Each now takes a one-time
    //     SubmissionTokenGuard token: the same form twice / three times at once = ONE movement; a post without the
    //     form's token = none.
    //   * Database error disclosure -- SQL Server's own text ("Arithmetic overflow...", "SqlDateTime overflow...",
    //     constraint / index / table names) reached the page through `return (false, ex.Message)`. DbErrorMessage now
    //     returns a safe message for engine text and keeps the app's own THROW (>= 50000) business messages.
    [Collection("ScratchDb")]
    public class StockEntryReplayAndDbErrorE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";

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
            public string Cs = "";
            public ServiceProvider Provider = null!;
            public ClaimsPrincipal Admin = null!;
            public int AdminId, OutletId, MainOfficeId, ProductionAreaId, PlantTypeId;
            public string PotSize = "";
            public T Get<T>() where T : notnull => Provider.GetRequiredService<T>();

            public async Task<T> ScalarAsync<T>(string sql, params (string Name, object? Value)[] args)
            {
                using var conn = new SqlConnection(Cs);
                await conn.OpenAsync();
                using var cmd = new SqlCommand(sql, conn);
                foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
                var r = await cmd.ExecuteScalarAsync();
                return r == null || r == DBNull.Value ? default! : (T)Convert.ChangeType(r, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
            }

            public Task<int> CountAsync(string sql, string tag) => ScalarAsync<int>(sql, ("@T", tag));

            public T Page<T>() where T : PageModel
            {
                var http = new DefaultHttpContext { User = Admin, RequestServices = Provider };
                var page = ActivatorUtilities.CreateInstance<T>(Provider);
                page.PageContext = new PageContext(new ActionContext(http, new Microsoft.AspNetCore.Routing.RouteData(), new CompiledPageActionDescriptor()));
                page.TempData = new TempDataDictionary(http, new NullTempData());
                return page;
            }

            public async Task<int> NewSpeciesAsync(string tag) => await ScalarAsync<int>(
                "INSERT INTO dbo.PlantSpecies (PlantTypeId, Name) VALUES (@P, @N); SELECT CAST(SCOPE_IDENTITY() AS INT)",
                ("@P", PlantTypeId), ("@N", tag));
        }

        private static async Task<Env> OpenAsync()
        {
            var cs = Environment.GetEnvironmentVariable(ConnectionVariable);
            Skip.If(string.IsNullOrWhiteSpace(cs), $"Set {ConnectionVariable} to a {RequiredPrefix}* database connection string to run these tests.");
            using (var conn = new SqlConnection(cs))
            {
                await conn.OpenAsync();
                using var cmd = new SqlCommand("SELECT DB_NAME()", conn);
                var name = (string)(await cmd.ExecuteScalarAsync())!;
                if (!name.StartsWith(RequiredPrefix, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Refusing to run these tests against '{name}'. Only databases named {RequiredPrefix}* are allowed.");
            }
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs }).Build());
            services.AddLogging();
            services.AddSingleton<DatabaseHelper>();
            services.AddSingleton<AreaAccessService>();
            services.AddSingleton<IOptions<SecurityOptions>>(Options.Create(new SecurityOptions { FullAccessRoleNames = new[] { "System Administrator" } }));
            services.AddSingleton<IOptionsMonitor<SeedlingWorkflowOptions>>(new StaticMonitor<SeedlingWorkflowOptions>(new SeedlingWorkflowOptions()));
            services.AddSingleton<SeedlingAreaScope>();
            services.AddSingleton<SubmissionTokenGuard>();
            services.AddScoped<UserClaimsFactory>();
            services.AddSingleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();
            services.AddScoped<IAuthorizationHandler, MinimumAuthorizationLevelHandler>();
            services.AddAuthorization();
            foreach (var t in typeof(DatabaseHelper).Assembly.GetTypes().Where(t => t.Namespace == "PlantStockManager.Data" && t.IsClass && t.Name.EndsWith("Repository")))
                services.AddScoped(t);
            var env = new Env { Cs = cs!, Provider = services.BuildServiceProvider() };

            env.AdminId = await env.ScalarAsync<int>(@"SELECT TOP 1 ur.UserId FROM dbo.UserRoles ur JOIN dbo.Roles r ON r.Id = ur.RoleId
JOIN dbo.IMSUsers u ON u.Id = ur.UserId AND u.IsActive = 1 WHERE r.Name = N'System Administrator' ORDER BY ur.UserId");
            Skip.If(env.AdminId == 0, "No active System Administrator in this copy.");
            env.OutletId = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.Area WHERE AreaType = N'Outlet' AND IsActive = 1 ORDER BY Id");
            env.MainOfficeId = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.Area WHERE AreaType = N'MainOffice' AND IsActive = 1 ORDER BY Id");
            env.ProductionAreaId = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.Area WHERE AreaType = N'MotherPlant' AND IsActive = 1 ORDER BY Id");
            Skip.If(env.OutletId == 0 || env.MainOfficeId == 0 || env.ProductionAreaId == 0, "This copy lacks an active Outlet / Main Office / Mother Plant Area.");
            env.PlantTypeId = await env.ScalarAsync<int>("SELECT MIN(Id) FROM dbo.PlantTypes");
            env.PotSize = (await env.ScalarAsync<string>("SELECT TOP 1 Name FROM dbo.PotSizes WHERE IsActive = 1 ORDER BY SortOrder, Id")) ?? "";
            Skip.If(env.PotSize.Length == 0, "No active Pot Size in this copy.");

            using var scope = env.Provider.CreateScope();
            var factory = scope.ServiceProvider.GetRequiredService<UserClaimsFactory>();
            env.Admin = await factory.CreatePrincipalAsync((await factory.GetActiveUserAsync(env.AdminId))!);
            return env;
        }

        private static string Tag() => "ZZTEST Replay " + Guid.NewGuid().ToString("N")[..10];

        // The same form (one token) sent twice in a row, then a second form sent three times at once, then a post
        // without the form's token: exactly one movement per form, none without a token.
        private static async Task AssertOneMovementPerFormAsync(Func<string?, Task<IActionResult>> post, Func<Task<int>> count)
        {
            var start = await count();
            var token = SubmissionTokenGuard.NewToken();
            await post(token);
            await post(token);
            Assert.Equal(start + 1, await count());

            var second = SubmissionTokenGuard.NewToken();
            await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => post(second))));
            Assert.Equal(start + 2, await count());

            await post(null);
            Assert.Equal(start + 2, await count());
        }

        // Potted stock at the Outlet for a NEW variety, through the (token-protected) Outlet Purchase page.
        private static async Task<(int Species, int Pool)> OutletStockAsync(Env env, string tag, decimal quantity = 100m)
        {
            var species = await env.NewSpeciesAsync(tag);
            var page = env.Page<OutletPurchasePage>();
            (page.PurchaseDate, page.SupplierName, page.OutletAreaId, page.SpeciesId, page.PotSize, page.Quantity, page.Remarks, page.SubmitToken)
                = (DateTime.Today, tag, env.OutletId, species, env.PotSize, quantity, tag, SubmissionTokenGuard.NewToken());
            Assert.IsType<RedirectToPageResult>(await page.OnPostAsync());
            var pool = await env.ScalarAsync<int>("SELECT Id FROM dbo.PottedPlantStock WHERE SpeciesId = @S AND AreaId = @A", ("@S", species), ("@A", env.OutletId));
            Assert.True(pool > 0);
            return (species, pool);
        }

        private static async Task<decimal> PottedAsync(Env env, int pool, string column)
            => await env.ScalarAsync<decimal>($"SELECT {column} FROM dbo.PottedPlantStock WHERE Id = @P", ("@P", pool));

        [SkippableFact]
        public async Task OutletPurchase_OneMovementPerForm()
        {
            var env = await OpenAsync();
            var tag = Tag();
            var species = await env.NewSpeciesAsync(tag);
            await AssertOneMovementPerFormAsync(token =>
            {
                var p = env.Page<OutletPurchasePage>();
                (p.PurchaseDate, p.SupplierName, p.OutletAreaId, p.SpeciesId, p.PotSize, p.Quantity, p.Remarks, p.SubmitToken)
                    = (DateTime.Today, tag, env.OutletId, species, env.PotSize, 10m, tag, token);
                return p.OnPostAsync();
            }, () => env.CountAsync("SELECT COUNT(*) FROM dbo.OutletPurchases WHERE Remarks = @T", tag));
            var pool = await env.ScalarAsync<int>("SELECT Id FROM dbo.PottedPlantStock WHERE SpeciesId = @S", ("@S", species));
            Assert.Equal(20m, await PottedAsync(env, pool, "PhysicalQuantity"));
        }

        [SkippableFact]
        public async Task OutletSale_OneMovementPerForm()
        {
            var env = await OpenAsync();
            var tag = Tag();
            var (_, pool) = await OutletStockAsync(env, tag);
            await AssertOneMovementPerFormAsync(token =>
            {
                var p = env.Page<OutletSalePage>();
                (p.OutletAreaId, p.CustomerName, p.SaleDate, p.Remarks, p.SubmitToken) = (env.OutletId, tag, DateTime.Today, tag, token);
                p.Items = new() { new OutletSalePage.ItemLine { StockType = OutletStockType.Potted, StockId = pool, Quantity = 5 } };
                return p.OnPostAsync();
            }, () => env.CountAsync("SELECT COUNT(*) FROM dbo.OutletSales WHERE Remarks = @T", tag));
            Assert.Equal(90m, await PottedAsync(env, pool, "PhysicalQuantity"));
        }

        [SkippableFact]
        public async Task OutletWastage_OneMovementPerForm()
        {
            var env = await OpenAsync();
            var tag = Tag();
            var (_, pool) = await OutletStockAsync(env, tag);
            await AssertOneMovementPerFormAsync(token =>
            {
                var p = env.Page<OutletWastagePage>();
                (p.OutletAreaId, p.StockType, p.StockId, p.Quantity, p.Reason, p.WastageDate, p.Remarks, p.SubmitToken)
                    = (env.OutletId, OutletStockType.Potted, pool, 2m, "Damaged", DateTime.Today, tag, token);
                return p.OnPostAsync();
            }, () => env.CountAsync("SELECT COUNT(*) FROM dbo.OutletWastages WHERE Remarks = @T", tag));
            Assert.Equal(96m, await PottedAsync(env, pool, "PhysicalQuantity"));
        }

        [SkippableFact]
        public async Task OutletBooking_OneReservationPerForm_AndOneCollectionPerForm()
        {
            var env = await OpenAsync();
            var tag = Tag();
            var (_, pool) = await OutletStockAsync(env, tag);
            await AssertOneMovementPerFormAsync(token =>
            {
                var p = env.Page<OutletBookingPage>();
                (p.OutletAreaId, p.CustomerName, p.BookingDate, p.Remarks, p.SubmitToken) = (env.OutletId, tag, DateTime.Today, tag, token);
                p.Items = new() { new OutletBookingPage.ItemLine { StockType = OutletStockType.Potted, StockId = pool, Quantity = 10 } };
                return p.OnPostAsync();
            }, () => env.CountAsync("SELECT COUNT(*) FROM dbo.OutletBookings WHERE Remarks = @T", tag));
            Assert.Equal(20m, await PottedAsync(env, pool, "ReservedQuantity"));

            // Collect 1 plant at a time from the first booking (10 booked): one hand-over per form
            var booking = await env.ScalarAsync<int>("SELECT MIN(Id) FROM dbo.OutletBookings WHERE Remarks = @T", ("@T", tag));
            var item = await env.ScalarAsync<int>("SELECT Id FROM dbo.OutletBookingItems WHERE BookingId = @B", ("@B", booking));
            await AssertOneMovementPerFormAsync(token =>
            {
                var p = env.Page<OutletBookingDetailsPage>();
                (p.CollectItemId, p.CollectQuantity, p.SubmitToken) = (item, 1m, token);
                return p.OnPostCollectAsync(booking);
            }, async () => (int)await env.ScalarAsync<decimal>("SELECT CollectedQuantity FROM dbo.OutletBookingItems WHERE Id = @I", ("@I", item)));
        }

        [SkippableFact]
        public async Task PottedPlantBooking_OneReservationPerForm()
        {
            var env = await OpenAsync();
            var tag = Tag();
            var (_, pool) = await OutletStockAsync(env, tag);
            await AssertOneMovementPerFormAsync(token =>
            {
                var p = env.Page<PottedBookingPage>();
                p.Booking = new PottedPlantBooking
                {
                    PottedPlantStockId = pool, Quantity = 4, CustomerName = tag, Contact = "9000000000",
                    BookingDate = DateTime.Today, DeliveryDate = DateTime.Today.AddDays(3), Remarks = tag
                };
                p.SubmitToken = token;
                return p.OnPostAsync();
            }, () => env.CountAsync("SELECT COUNT(*) FROM dbo.PottedPlantBookings WHERE Remarks = @T", tag));
            Assert.Equal(8m, await PottedAsync(env, pool, "ReservedQuantity"));
        }

        [SkippableFact]
        public async Task FertilizerStockReceipt_OneBatchPerForm()
        {
            var env = await OpenAsync();
            var tag = Tag();
            var fertilizer = await env.ScalarAsync<int>("SELECT MIN(FertilizerId) FROM dbo.FertilizerMaster");
            var unit = await env.ScalarAsync<int>("SELECT MIN(UnitId) FROM dbo.UnitMaster");
            var source = await env.ScalarAsync<int>("SELECT MIN(SourceId) FROM dbo.FertilizerSource");
            Skip.If(fertilizer == 0 || unit == 0 || source == 0, "This copy lacks fertilizer masters.");
            await AssertOneMovementPerFormAsync(token =>
            {
                var p = env.Page<FertilizerStockPage>();
                p.Stock = new FertilizerStock
                {
                    FertilizerId = fertilizer, Quantity = 10, UnitId = unit, SourceId = source, BatchNumber = tag,
                    PurchaseDate = DateTime.Today, ExpiryDate = DateTime.Today.AddYears(1)
                };
                p.SubmitToken = token;
                return p.OnPostSaveAsync();
            }, () => env.CountAsync("SELECT COUNT(*) FROM dbo.FertilizerStock WHERE BatchNumber = @T", tag));
        }

        [SkippableFact]
        public async Task PurchaseOrderReceipt_OneReceiptPerForm()
        {
            var env = await OpenAsync();
            var tag = Tag();
            var (vOk, vMsg, vendor) = await env.Get<VendorRepository>().InsertAsync(new Vendor { Name = tag });
            Assert.True(vOk, vMsg);
            var order = new PlantStockManager.Models.PurchaseOrder
            {
                VendorId = vendor, OrderDate = DateTime.Today, Remarks = tag, CreatedBy = "replay-test",
                Items = new() { new PurchaseOrderItem { ItemCategory = "Other", ItemName = tag, OrderedQuantity = 100 } }
            };
            var (oOk, oMsg, orderId) = await env.Get<PurchaseOrderRepository>().InsertAsync(order, env.AdminId);
            Assert.True(oOk, oMsg);
            var item = await env.ScalarAsync<int>("SELECT Id FROM dbo.PurchaseOrderItems WHERE PurchaseOrderId = @O", ("@O", orderId));

            await AssertOneMovementPerFormAsync(token =>
            {
                var p = env.Page<PurchaseOrderReceivePage>();
                (p.ReceiptDate, p.ReceivedById, p.Remarks, p.SubmitToken) = (DateTime.Today, env.AdminId, tag, token);
                (p.ItemIds, p.ReceiveQuantities) = (new() { item }, new() { 10m });
                return p.OnPostAsync(orderId);
            }, () => env.CountAsync("SELECT COUNT(*) FROM dbo.PurchaseReceipts WHERE Remarks = @T", tag));
            Assert.Equal(20m, await env.ScalarAsync<decimal>("SELECT ReceivedQuantity FROM dbo.PurchaseOrderItems WHERE Id = @I", ("@I", item)));
        }

        [SkippableFact]
        public async Task EmptyPotPurchaseAndIssue_OneMovementPerForm()
        {
            var env = await OpenAsync();
            var tag = Tag();
            await AssertOneMovementPerFormAsync(token =>
            {
                var p = env.Page<EmptyPotPurchasePage>();
                (p.PurchaseDate, p.PotSize, p.Quantity, p.SupplierName, p.AreaId, p.Remarks, p.SubmitToken)
                    = (DateTime.Today, env.PotSize, 50m, tag, env.MainOfficeId, tag, token);
                return p.OnPostAsync();
            }, () => env.CountAsync("SELECT COUNT(*) FROM dbo.EmptyPotPurchases WHERE Remarks = @T", tag));

            var pool = await env.ScalarAsync<int>("SELECT TOP 1 EmptyPotInventoryId FROM dbo.EmptyPotPurchases WHERE Remarks = @T", ("@T", tag));
            await AssertOneMovementPerFormAsync(token =>
            {
                var p = env.Page<EmptyPotIssuePage>();
                (p.SourcePoolId, p.DestinationAreaId, p.Quantity, p.Remarks, p.SubmitToken) = (pool, env.ProductionAreaId, 5m, tag, token);
                return p.OnPostAsync();
            }, () => env.CountAsync("SELECT COUNT(*) FROM dbo.InternalTransfers WHERE Remarks = @T", tag));
        }

        // ---- database error disclosure -------------------------------------------------------------------------------

        private static async Task<SqlException> SqlErrorAsync(Env env, string sql)
        {
            using var conn = new SqlConnection(env.Cs);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(sql, conn);
            return await Assert.ThrowsAsync<SqlException>(() => cmd.ExecuteNonQueryAsync());
        }

        [SkippableFact]
        public async Task DbErrorMessage_HidesEngineText_KeepsTheAppsOwnRules()
        {
            var env = await OpenAsync();

            var rule = await SqlErrorAsync(env, "THROW 51999, N'Only 3 plants are available at this Outlet.', 1;");
            Assert.Equal("Only 3 plants are available at this Outlet.", DbErrorMessage.For(rule));

            var overflow = await SqlErrorAsync(env, "DECLARE @d DECIMAL(18,2) = 1e20;");
            Assert.Equal(DbErrorMessage.Generic, DbErrorMessage.For(overflow));
            Assert.Equal("Stock rule.", DbErrorMessage.For(overflow, "Stock rule."));

            var constraint = await SqlErrorAsync(env, "DECLARE @t TABLE (q INT CHECK (q > 0)); INSERT @t VALUES (-1);");
            Assert.DoesNotContain("CHECK", DbErrorMessage.For(constraint));

            Assert.Equal(DbErrorMessage.Generic, DbErrorMessage.For(new SqlTypeException("SqlDateTime overflow. Must be between 1/1/1753 ...")));

            // the app's own business exception, thrown by the app's code: shown as it is
            using (var conn = new SqlConnection(env.Cs))
            {
                await conn.OpenAsync();
                using var tx = conn.BeginTransaction();
                var mine = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    env.Get<TrayStockRepository>().GetOrCreateLockedAsync(conn, tx, env.MainOfficeId, null, "24 Cavity", "replay-test"));
                Assert.Equal(TrayStockRepository.PolyhouseRequiredMessage, DbErrorMessage.For(mine));
                tx.Rollback();
            }

            // the SAME exception type raised inside SqlClient (a command on a closed connection): hidden
            var client = Assert.Throws<InvalidOperationException>(() => new SqlCommand("SELECT 1", new SqlConnection(env.Cs)).ExecuteScalar());
            Assert.Equal(DbErrorMessage.Generic, DbErrorMessage.For(client));

            // a programming error, and an exception that was never thrown by the app: hidden
            var bug = Assert.Throws<NullReferenceException>(() => ((string)null!).Trim());
            Assert.Equal(DbErrorMessage.Generic, DbErrorMessage.For(bug));
            Assert.Equal(DbErrorMessage.Generic, DbErrorMessage.For(new InvalidOperationException("not thrown by the app")));
        }

        [SkippableFact]
        public async Task StockForms_ShowASafeMessage_ForAnOutOfRangeValue_AndSaveNothing()
        {
            var env = await OpenAsync();
            var tag = Tag();
            var species = await env.NewSpeciesAsync(tag);

            var purchase = env.Page<OutletPurchasePage>();
            (purchase.PurchaseDate, purchase.SupplierName, purchase.OutletAreaId, purchase.SpeciesId, purchase.PotSize, purchase.Quantity, purchase.Remarks, purchase.SubmitToken)
                = (DateTime.Today, tag, env.OutletId, species, env.PotSize, 100_000_000_000_000_000_000m, tag, SubmissionTokenGuard.NewToken());
            Assert.IsType<PageResult>(await purchase.OnPostAsync());

            var (_, pool) = await OutletStockAsync(env, tag + "b");
            var sale = env.Page<OutletSalePage>();
            (sale.OutletAreaId, sale.CustomerName, sale.SaleDate, sale.Remarks, sale.SubmitToken) = (env.OutletId, tag, new DateTime(1, 1, 1), tag, SubmissionTokenGuard.NewToken());
            sale.Items = new() { new OutletSalePage.ItemLine { StockType = OutletStockType.Potted, StockId = pool, Quantity = 1 } };
            Assert.IsType<PageResult>(await sale.OnPostAsync());

            var messages = purchase.ModelState.Values.Concat(sale.ModelState.Values).SelectMany(v => v.Errors).Select(e => e.ErrorMessage).ToList();
            Assert.Contains(DbErrorMessage.Generic, messages);
            Assert.DoesNotContain(messages, m => m.Contains("overflow", StringComparison.OrdinalIgnoreCase) || m.Contains("SqlDateTime", StringComparison.Ordinal));
            Assert.Equal(0, await env.CountAsync("SELECT COUNT(*) FROM dbo.OutletPurchases WHERE Remarks = @T", tag));
            Assert.Equal(0, await env.CountAsync("SELECT COUNT(*) FROM dbo.OutletSales WHERE Remarks = @T", tag));
        }
    }

    // Without a database: every stock-entry form fixed in this sweep carries a one-time token, each with its own scope.
    public class StockEntryReplayStructureTests
    {
        private sealed class ThrowingLoggerFactory : ILoggerFactory
        {
            private sealed class ThrowingLogger : ILogger
            {
                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
                public bool IsEnabled(LogLevel logLevel) => true;
                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                    => throw new ObjectDisposedException("EventLogInternal");
            }
            public ILogger CreateLogger(string categoryName) => new ThrowingLogger();
            public void AddProvider(ILoggerProvider provider) { }
            public void Dispose() { }
        }

        // Found by the full suite: a logger from a disposed host threw out of DbErrorMessage.For, turning a handled
        // database error into an unhandled one. Logging failures are swallowed; the safe message is still returned.
        [Fact]
        public void DbErrorMessage_ABrokenLogger_NeverBreaksTheCaller()
        {
            DbErrorMessage.UseLoggerFactory(new ThrowingLoggerFactory());
            try
            {
                Assert.Equal(DbErrorMessage.Generic, DbErrorMessage.For(new SqlTypeException("SqlDateTime overflow.")));
            }
            finally
            {
                DbErrorMessage.UseLoggerFactory(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
            }
        }

        public static readonly Type[] GuardedPages =
        {
            typeof(OutletPurchasePage), typeof(OutletSalePage), typeof(OutletWastagePage), typeof(OutletBookingPage),
            typeof(OutletBookingDetailsPage), typeof(PottedBookingPage), typeof(FertilizerStockPage), typeof(PurchaseOrderReceivePage),
            typeof(EmptyPotPurchasePage), typeof(EmptyPotIssuePage),
            typeof(PlantStockManager.Pages.Production.SeedStock.AddStockModel), typeof(PlantStockManager.Pages.Production.SeedStock.AddSeedStockModel),
            typeof(PlantStockManager.Pages.Production.PotBatch.DetailsModel),   // production entry (2026-10-21, third round)
        };

        [Fact]
        public void EveryFixedStockForm_BindsASubmitToken_AndTakesTheGuard()
        {
            foreach (var page in GuardedPages)
            {
                var token = page.GetProperty("SubmitToken");
                Assert.True(token?.GetCustomAttribute<BindPropertyAttribute>() != null, $"{page.FullName} has no bound SubmitToken.");
                Assert.True(page.GetConstructors().Single().GetParameters().Any(p => p.ParameterType == typeof(SubmissionTokenGuard)),
                    $"{page.FullName} does not take SubmissionTokenGuard.");
            }
        }

        [Fact]
        public void TokenScopes_AreDistinct()
        {
            var scopes = GuardedPages
                .SelectMany(p => p.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral && f.Name.EndsWith("TokenScope")))
                .Select(f => (string)f.GetRawConstantValue()!)
                .ToList();
            Assert.Equal(GuardedPages.Length, scopes.Count);
            Assert.Equal(scopes.Count, scopes.Distinct().Count());
        }
    }
}
