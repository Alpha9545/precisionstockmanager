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
using DetailsPage = PlantStockManager.Pages.Store.RequestDetailsModel;
using ItemsPage = PlantStockManager.Pages.Store.ItemsModel;
using NewRequestPage = PlantStockManager.Pages.Store.NewRequestModel;
using ReportsPage = PlantStockManager.Pages.Store.ReportsModel;
using StockEntryPage = PlantStockManager.Pages.Store.StockEntryModel;
using TransfersPage = PlantStockManager.Pages.Store.TransfersModel;

namespace PlantStockManager.Tests
{
    // Inventory Store -- end-to-end: the REAL Store pages, StoreRepository and StoreAuthorization
    // with REAL login claims and SubmissionTokenGuard, against a SCRATCH COPY that has
    // Database/Migrations/2026-10-06_StoreInventory.sql applied (PSM_SCRATCH_CONNECTION; the name
    // must start with PlantsIMS2_Scratch_). Test data is ZZST-coded items / ZZST purposes, removed
    // again; removing ledger rows works ONLY on a scratch copy and only for a session that asks for
    // it (TR_StoreTransactions_Ledger).
    //
    //   11  Mahadev  Main Office Store Keeper (+ Fertilizer Supervisor), Area 2 only
    //   110 Vasant   Mother Plant Supervisor   Area 127 (Green Bless; Polyhouse 14 P-1)
    //   6   Akshay   Sowing Supervisor         Area 2  (Polyhouse 2 Facility-5)
    //   4   Kanchan  Office Coordinator        Area 2
    //   15  Rahul    Booking Executive         Area 2 (no Store permission)
    //   21  Prajwal  System Administrator
    [Collection("ScratchDb")]
    public class StoreE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";
        private const int StoreKeeper = 11, MpSup = 110, OfficeCoordinator = 4, BookingExec = 15, Admin = 21;
        // 2026-10-10: looked up per run (FixtureUsers) -- user 6 is no longer a Sowing Supervisor in the local data
        private static int SowingSup = 6;
        private const int MainOffice = 2, GreenBless = 127, GreenBlessP1 = 14, Facility5 = 2, Nos = 9;

        private sealed class NullTempData : ITempDataProvider
        {
            public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
            public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
        }

        private sealed class Env
        {
            public string ConnectionString { get; init; } = "";
            public ServiceProvider Provider { get; init; } = null!;
            public T Get<T>() where T : notnull => Provider.GetRequiredService<T>();
            public StoreRepository Repo => Get<StoreRepository>();
            public int Category;

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

            public Task CleanupAsync() => ExecAsync(@"
EXEC sp_set_session_context N'StoreTestCleanup', 1;
DELETE t FROM dbo.StoreTransactions t JOIN dbo.StoreItems i ON i.Id = t.ItemId WHERE i.ItemCode LIKE 'ZZST-%';
DELETE l FROM dbo.StoreRequestLines l JOIN dbo.StoreRequests r ON r.Id = l.RequestId WHERE r.Purpose LIKE 'ZZST%';
DELETE FROM dbo.StoreRequests WHERE Purpose LIKE 'ZZST%';
DELETE FROM dbo.StoreItems WHERE ItemCode LIKE 'ZZST-%';
DELETE FROM dbo.StoreItemCategories WHERE Name LIKE 'ZZST%';
EXEC sp_set_session_context N'StoreTestCleanup', 0");

            public async Task<int> ItemAsync(string code, string type, decimal stock = 0, decimal minimum = 0)
            {
                var id = (await Repo.CreateItemAsync(new StoreItem { ItemCode = code, Name = $"Test {code}", CategoryId = Category, UnitId = Nos, ItemType = type, MinimumStock = minimum }, StoreKeeper))!.Value;
                if (stock > 0)
                    await Repo.RecordMovementAsync(new StoreRepository.StockMove(id, stock, StoreRules.Purchase, AppClock.Today), StoreKeeper);
                return id;
            }

            public async Task<decimal> StockAsync(int itemId) => await ScalarAsync<decimal>("SELECT AvailableQuantity FROM dbo.StoreItems WHERE Id = @I", ("@I", itemId));
            public async Task<decimal> LedgerSumAsync(int itemId) => await ScalarAsync<decimal>("SELECT ISNULL(SUM(Quantity), 0) FROM dbo.StoreTransactions WHERE ItemId = @I", ("@I", itemId));

            public async Task<ClaimsPrincipal> LoginAsync(int userId)
            {
                var factory = Get<UserClaimsFactory>();
                var user = await factory.GetActiveUserAsync(userId);
                Assert.NotNull(user);
                return await factory.CreatePrincipalAsync(user!);
            }

            public T Page<T>(ClaimsPrincipal principal, string method = "POST") where T : PageModel
            {
                var http = new DefaultHttpContext { User = principal, RequestServices = Provider };
                http.Request.Method = method;
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
            services.AddSingleton<SubmissionTokenGuard>();
            services.AddScoped<StoreAuthorization>();
            services.AddSingleton<IOptions<SecurityOptions>>(Options.Create(new SecurityOptions { FullAccessRoleNames = new[] { "System Administrator" } }));
            services.AddScoped<UserClaimsFactory>();
            foreach (var t in typeof(DatabaseHelper).Assembly.GetTypes().Where(t => t.Namespace == "PlantStockManager.Data" && t.IsClass && t.Name.EndsWith("Repository")))
                services.AddScoped(t);
            var env = new Env { ConnectionString = cs!, Provider = services.BuildServiceProvider() };
            Skip.If(await env.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID('dbo.StoreItems') IS NULL THEN 0 ELSE 1 END") == 0,
                "The scratch copy does not have 2026-10-06_StoreInventory.sql applied.");
            await env.CleanupAsync();
            env.Category = (await env.Repo.CreateCategoryAsync("ZZST Electrical", Admin))!.Value;
            SowingSup = await env.ScalarAsync<int>(FixtureUsers.MainOfficeSowingSupervisorSql);
            Skip.If(SowingSup == 0, "No active Sowing Supervisor is assigned to the Main Office Area in this copy.");
            return env;
        }

        private static string[] Errors(PageModel page) => page.ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).ToArray();

        private static async Task<(IActionResult Result, NewRequestPage Page)> RequestAsync(Env env, int user, int area, int? polyhouse, params (int Item, decimal Qty)[] lines)
        {
            var page = env.Page<NewRequestPage>(await env.LoginAsync(user));
            page.AreaId = area; page.PolyhouseId = polyhouse; page.Purpose = "ZZST polyhouse maintenance";
            page.LineItemIds = lines.Select(l => (int?)l.Item).ToList();
            page.LineQuantities = lines.Select(l => (decimal?)l.Qty).ToList();
            page.SubmitToken = SubmissionTokenGuard.NewToken();
            return (await page.OnPostAsync(), page);
        }

        private static int RequestId(IActionResult r)
        {
            var redirect = Assert.IsType<RedirectToPageResult>(r);
            Assert.Equal("/Store/RequestDetails", redirect.PageName);
            return (int)redirect.RouteValues!["id"]!;
        }

        private static async Task<(IActionResult Result, DetailsPage Page)> StepAsync(Env env, int user, int id, Func<DetailsPage, Task<IActionResult>> handler, Action<DetailsPage>? set = null, string? token = null)
        {
            var page = env.Page<DetailsPage>(await env.LoginAsync(user));
            page.Id = id;
            page.SubmitToken = token ?? SubmissionTokenGuard.NewToken();
            set?.Invoke(page);
            return (await handler(page), page);
        }

        private static string Done((IActionResult Result, DetailsPage Page) step)
        {
            Assert.IsType<RedirectToPageResult>(step.Result);
            Assert.True(step.Page.TempData.ContainsKey("Success"), step.Page.TempData["Error"] as string ?? string.Join("; ", Errors(step.Page)));
            return (string)step.Page.TempData["Success"]!;
        }

        private static string Refused((IActionResult Result, DetailsPage Page) step)
        {
            Assert.IsType<RedirectToPageResult>(step.Result);
            Assert.False(step.Page.TempData.ContainsKey("Success"));
            return (string)step.Page.TempData["Error"]!;
        }

        private static Action<DetailsPage> Lines(StoreRequest r, params (int Item, decimal Qty)[] q) => p =>
        {
            p.LineIds = r.Lines.Select(l => l.Id).ToList();
            p.Quantities = r.Lines.Select(l => (decimal?)(q.FirstOrDefault(x => x.Item == l.ItemId).Qty)).ToList();
        };

        // ---- item master and stock entry ------------------------------------------------------------

        [SkippableFact]
        public async Task ItemMaster_Create_DuplicateCode_InactiveItem()
        {
            var env = await OpenAsync();
            try
            {
                var page = env.Page<ItemsPage>(await env.LoginAsync(StoreKeeper));
                page.ItemCode = "zzst-cable"; page.Name = "Electric Cable"; page.CategoryId = env.Category; page.UnitId = Nos; page.ItemType = StoreRules.Consumable; page.MinimumStock = 50; page.IsActive = true;
                Assert.IsType<RedirectToPageResult>(await page.OnPostSaveAsync());
                var item = (await env.Repo.GetItemsAsync()).Single(i => i.ItemCode == "ZZST-CABLE");   // stored upper-case
                Assert.Equal((0m, StoreRules.Consumable, true), (item.AvailableQuantity, item.ItemType, item.IsLow));

                page = env.Page<ItemsPage>(await env.LoginAsync(StoreKeeper));
                page.ItemCode = "ZZST-Cable"; page.Name = "Dup"; page.CategoryId = env.Category; page.UnitId = Nos; page.ItemType = StoreRules.Consumable;
                Assert.IsType<PageResult>(await page.OnPostSaveAsync());
                Assert.Contains("Item code ZZST-CABLE already exists.", Errors(page));

                // A new item can never be created with stock (the database refuses it).
                var ex = await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync(
                    "INSERT INTO dbo.StoreItems (ItemCode, Name, CategoryId, UnitId, ItemType, AvailableQuantity) VALUES ('ZZST-X', 'x', @C, 9, 'Consumable', 5)", ("@C", env.Category)));
                Assert.Equal(51816, ex.Number);

                // Inactive items cannot be requested.
                var old = await env.ItemAsync("ZZST-OLD", StoreRules.Consumable, 10);
                await env.ExecAsync("UPDATE dbo.StoreItems SET IsActive = 0 WHERE Id = @I", ("@I", old));
                var (r, req) = await RequestAsync(env, MpSup, GreenBless, null, (old, 1));
                Assert.Contains("Line 1: choose an item.", Errors(req));
                await Assert.ThrowsAsync<StoreStockException>(() => env.Repo.CreateRequestAsync(GreenBless, null, "ZZST direct", new[] { new StoreRequestLineInput(old, 1) }, MpSup));

                // Only the Store Keeper may change the master (page rule's Write policy).
                Assert.True((await env.LoginAsync(StoreKeeper)).HasPermission("Store.Manage"));
                Assert.False((await env.LoginAsync(MpSup)).HasPermission("Store.Manage"));
            }
            finally { await env.CleanupAsync(); }
        }

        [SkippableFact]
        public async Task Purchase_And_Adjustment_AreLedgerTransactions_WithBalances()
        {
            var env = await OpenAsync();
            try
            {
                var cable = await env.ItemAsync("ZZST-CABLE", StoreRules.Consumable);
                async Task<StockEntryPage> Entry(string type, decimal qty, bool decrease = false, string? remarks = null, string? token = null)
                {
                    var p = env.Page<StockEntryPage>(await env.LoginAsync(StoreKeeper));
                    p.ItemId = cable; p.EntryType = type; p.Quantity = qty; p.Decrease = decrease; p.EntryDate = AppClock.Today; p.Remarks = remarks;
                    p.Reference = "BILL-1"; p.UnitCost = type == StoreRules.Purchase ? 12.5m : null;
                    p.SubmitToken = token ?? SubmissionTokenGuard.NewToken();
                    await p.OnPostAsync();
                    return p;
                }
                var purchase = await Entry(StoreRules.Purchase, 100);
                Assert.Equal("Purchase of 100 Nos ZZST-CABLE -- Test ZZST-CABLE recorded: stock 0 → 100.", purchase.TempData["Success"]);
                Assert.Contains("An adjustment needs a reason in Remarks.", Errors(await Entry(StoreRules.Adjustment, 5, decrease: true)));
                await Entry(StoreRules.Adjustment, 5, decrease: true, remarks: "Count correction");
                var tooMuch = await Entry(StoreRules.Adjustment, 96, decrease: true, remarks: "x");
                Assert.Contains("Not enough stock of ZZST-CABLE Test ZZST-CABLE: available 95, needed 96.", Errors(tooMuch));
                // The same form twice: one entry only.
                var token = SubmissionTokenGuard.NewToken();
                await Entry(StoreRules.Purchase, 10, token: token);
                await Entry(StoreRules.Purchase, 10, token: token);

                Assert.Equal(105m, await env.StockAsync(cable));
                var ledger = (await env.Repo.SearchTransactionsAsync(new StoreTransactionFilter { ItemId = cable }, null)).OrderBy(t => t.Id).ToList();
                Assert.Equal(new[] { (StoreRules.Purchase, 100m, 0m, 100m), (StoreRules.Adjustment, -5m, 100m, 95m), (StoreRules.Purchase, 10m, 95m, 105m) },
                    ledger.Select(t => (t.TransactionType, t.Quantity, t.BalanceBefore, t.BalanceAfter)));
                Assert.Equal(12.5m, ledger[0].UnitCost);
                Assert.Equal(105m, await env.LedgerSumAsync(cable));
            }
            finally { await env.CleanupAsync(); }
        }

        // ---- the complete multi-item workflow -------------------------------------------------------

        [SkippableFact]
        public async Task MultiItemRequest_PartialApproval_Issue_Consumed_Returned_Kept_Closed()
        {
            var env = await OpenAsync();
            try
            {
                var cable = await env.ItemAsync("ZZST-CABLE", StoreRules.Consumable, 200);
                var drill = await env.ItemAsync("ZZST-DRILL", StoreRules.Returnable, 5);
                var pump = await env.ItemAsync("ZZST-PUMP", StoreRules.Returnable, 3);
                var ties = await env.ItemAsync("ZZST-TIES", StoreRules.Consumable, 500);

                var (r, page) = await RequestAsync(env, MpSup, GreenBless, GreenBlessP1, (cable, 100), (drill, 1), (pump, 1), (ties, 50));
                var id = RequestId(r);
                var req = (await env.Repo.GetRequestAsync(id))!;
                Assert.Matches("^IR-\\d{6}$", req.RequestNo);
                Assert.Equal((StoreRules.Pending, 4, GreenBless, (int?)GreenBlessP1, MpSup), (req.Status, req.Lines.Count, req.AreaId, req.PolyhouseId, req.RequestedById));

                // The Store Keeper (Area 2 only) sees and reviews a Green Bless request: cable 60 of 100, ties rejected.
                Assert.IsType<PageResult>(await ((Func<Task<IActionResult>>)(async () => { var p = env.Page<DetailsPage>(await env.LoginAsync(StoreKeeper), "GET"); p.Id = id; return await p.OnGetAsync(); }))());
                var review = await StepAsync(env, StoreKeeper, id, p => p.OnPostReviewAsync(), Lines(req, (cable, 101), (drill, 1), (pump, 1), (ties, 0)));
                Assert.Contains("Test ZZST-CABLE: at most the 100 requested can be approved.", Errors(review.Page));
                Assert.Equal("Request partially approved.", Done(await StepAsync(env, StoreKeeper, id, p => p.OnPostReviewAsync(), Lines(req, (cable, 60), (drill, 1), (pump, 1), (ties, 0)))));
                req = (await env.Repo.GetRequestAsync(id))!;
                Assert.Equal(StoreRules.Approved, req.Status);
                Assert.Equal((60m, 0m), (req.Lines.Single(l => l.ItemId == cable).ApprovedQty!.Value, req.Lines.Single(l => l.ItemId == ties).ApprovedQty!.Value));

                // Issue: never more than approved.
                var issue = await StepAsync(env, StoreKeeper, id, p => p.OnPostIssueAsync(), Lines(req, (cable, 61), (drill, 1), (pump, 1)));
                Assert.Contains("Test ZZST-CABLE: at most the 60 approved can be issued.", Errors(issue.Page));
                Done(await StepAsync(env, StoreKeeper, id, p => p.OnPostIssueAsync(), Lines(req, (cable, 60), (drill, 1), (pump, 1))));
                Assert.Equal((140m, 4m, 2m, 500m), (await env.StockAsync(cable), await env.StockAsync(drill), await env.StockAsync(pump), await env.StockAsync(ties)));
                req = (await env.Repo.GetRequestAsync(id))!;
                Assert.Equal(StoreRules.Issued, req.Status);
                var holdings = await env.Repo.GetHoldingsAsync(null, GreenBless, GreenBlessP1);
                Assert.Equal(60m, holdings.Single(h => h.ItemId == cable).OutOnIssue);

                // Settlement by the Area: cable 36 consumed + 24 returned; drill returned; pump kept.
                var lineOf = (int item) => req.Lines.Single(l => l.ItemId == item).Id;
                var bad = await StepAsync(env, MpSup, id, p => p.OnPostSettleAsync(), p => { p.LineId = lineOf(drill); p.Consumed = 1; });
                Assert.Contains("Test ZZST-DRILL: a returnable item cannot be consumed -- it is either returned or kept.", Errors(bad.Page));
                bad = await StepAsync(env, MpSup, id, p => p.OnPostSettleAsync(), p => { p.LineId = lineOf(cable); p.Consumed = 36; p.Returned = 20; });
                Assert.Contains("Test ZZST-CABLE: consumed + kept + returned must equal the 60 issued (now 56).", Errors(bad.Page));
                Assert.IsType<ForbidResult>((await StepAsync(env, SowingSup, id, p => p.OnPostSettleAsync(), p => { p.LineId = lineOf(cable); p.Consumed = 60; })).Result);   // another Area
                Done(await StepAsync(env, MpSup, id, p => p.OnPostSettleAsync(), p => { p.LineId = lineOf(cable); p.Consumed = 36; p.Returned = 24; }));
                Done(await StepAsync(env, MpSup, id, p => p.OnPostSettleAsync(), p => { p.LineId = lineOf(drill); p.Returned = 1; }));
                Done(await StepAsync(env, MpSup, id, p => p.OnPostSettleAsync(), p => { p.LineId = lineOf(pump); p.Kept = 1; }));
                // Settling the same line again: refused (nothing changes).
                Assert.Equal(StoreRepository.StepResult.Changed, await env.Repo.SettleLineAsync(id, lineOf(pump), 0, 0, 1, null, MpSup));
                // Returned units are NOT back in stock until confirmed.
                Assert.Equal((140m, 4m), (await env.StockAsync(cable), await env.StockAsync(drill)));
                Assert.Equal(StoreRules.Issued, (await env.Repo.GetRequestAsync(id))!.Status);

                // The Store Keeper confirms the physical returns.
                Assert.IsType<ForbidResult>((await StepAsync(env, MpSup, id, p => p.OnPostConfirmReturnAsync(), p => { p.LineId = lineOf(cable); p.ReturnCondition = "Good"; })).Result);
                Done(await StepAsync(env, StoreKeeper, id, p => p.OnPostConfirmReturnAsync(), p => { p.LineId = lineOf(cable); p.ReturnCondition = "Good"; }));
                Assert.Equal(StoreRepository.StepResult.Changed, await env.Repo.ConfirmReturnAsync(id, lineOf(cable), "Good", null, StoreKeeper));   // not twice
                Done(await StepAsync(env, StoreKeeper, id, p => p.OnPostConfirmReturnAsync(), p => { p.LineId = lineOf(drill); p.ReturnCondition = "Damaged"; p.Remarks = "Chuck loose"; }));

                Assert.Equal((164m, 5m, 2m, 500m), (await env.StockAsync(cable), await env.StockAsync(drill), await env.StockAsync(pump), await env.StockAsync(ties)));
                foreach (var item in new[] { cable, drill, pump, ties })
                    Assert.Equal(await env.StockAsync(item), await env.LedgerSumAsync(item));
                req = (await env.Repo.GetRequestAsync(id))!;
                Assert.Equal(StoreRules.Closed, req.Status);
                Assert.NotNull(req.ClosedAt);
                var c = req.Lines.Single(l => l.ItemId == cable);
                Assert.Equal((60m, 36m, 0m, 0m, 24m, "Good"), (c.IssuedQty, c.ConsumedQty, c.KeptQty, c.ReturnDeclaredQty, c.ReturnedQty, c.ReturnCondition));
                Assert.Equal(("Damaged", "Chuck loose"), (req.Lines.Single(l => l.ItemId == drill).ReturnCondition, req.Lines.Single(l => l.ItemId == drill).ConfirmRemarks));

                // Where things are now: the pump stays assigned to Green Bless / P-1; 36 m cable consumed there.
                holdings = await env.Repo.GetHoldingsAsync(null, GreenBless, GreenBlessP1);
                Assert.Equal((1m, 1m), (holdings.Single(h => h.ItemId == pump).Kept, holdings.Single(h => h.ItemId == pump).AtLocation));
                Assert.Equal((36m, 0m), (holdings.Single(h => h.ItemId == cable).Consumed, holdings.Single(h => h.ItemId == cable).AtLocation));
                Assert.Equal(0m, holdings.Single(h => h.ItemId == drill).AtLocation);

                // Ledger: issue and return movements with balances, the kept / consumed parts never return.
                var moves = (await env.Repo.SearchTransactionsAsync(new StoreTransactionFilter { RequestId = id }, null)).OrderBy(t => t.Id).ToList();
                Assert.Equal(new[] { "ISSUE", "ISSUE", "ISSUE", "RETURN", "RETURN" }, moves.Select(t => t.TransactionType));
                Assert.All(moves, t => Assert.Equal((GreenBless, (int?)GreenBlessP1), (t.AreaId!.Value, t.PolyhouseId)));
                // Audit: who did what.
                var keeperName = await env.ScalarAsync<string>("SELECT Name FROM dbo.IMSUsers WHERE Id = @U", ("@U", StoreKeeper));
                var mpName = await env.ScalarAsync<string>("SELECT Name FROM dbo.IMSUsers WHERE Id = @U", ("@U", MpSup));
                Assert.Equal((keeperName, keeperName), (req.ReviewedByName, req.IssuedByName));
                Assert.All(req.Lines.Where(l => l.IssuedQty > 0), l => Assert.Equal(mpName, l.SettledByName));
                Assert.All(req.Lines.Where(l => l.ReturnedQty > 0), l => Assert.Equal(keeperName, l.ReturnConfirmedByName));
                Assert.NotNull(req.ReviewedAt);
                Assert.NotNull(req.IssuedAt);
                Assert.All(req.Lines.Where(l => l.IssuedQty > 0), l => Assert.NotNull(l.SettledAt));
            }
            finally { await env.CleanupAsync(); }
        }

        // ---- rejection, cancellation, area authorization --------------------------------------------

        [SkippableFact]
        public async Task Rejection_Cancellation_AndAreaAuthorization()
        {
            var env = await OpenAsync();
            try
            {
                var cable = await env.ItemAsync("ZZST-CABLE", StoreRules.Consumable, 100);
                // Tampered Area / Polyhouse; duplicate line; no lines.
                var (r, page) = await RequestAsync(env, MpSup, MainOffice, Facility5, (cable, 5));
                Assert.Contains(StoreRules.NotAuthorizedAreaMessage, Errors(page));
                (r, page) = await RequestAsync(env, MpSup, GreenBless, Facility5, (cable, 5));
                Assert.Contains(StoreRules.PolyhouseNotInAreaMessage, Errors(page));
                (r, page) = await RequestAsync(env, MpSup, GreenBless, null, (cable, 5), (cable, 2));
                Assert.Contains("Test ZZST-CABLE is listed twice -- one line per item.", Errors(page));
                Assert.False((await env.LoginAsync(BookingExec)).HasPermission("Store.Request|Store.View|Store.Manage"));
                foreach (var u in new[] { MpSup, SowingSup, OfficeCoordinator, StoreKeeper /* also Fertilizer Supervisor */ })
                    Assert.True((await env.LoginAsync(u)).HasPermission("Store.Request"), $"user {u}");
                Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.StoreRequests WHERE Purpose LIKE 'ZZST%'"));
                var dbEx = await Assert.ThrowsAsync<SqlException>(() => env.Repo.CreateRequestAsync(MainOffice, GreenBlessP1, "ZZST tamper", new[] { new StoreRequestLineInput(cable, 1) }, Admin));
                Assert.Equal(51713, dbEx.Number);   // the database is the final guard

                // Rejected: nothing approved.
                var rejected = RequestId((await RequestAsync(env, MpSup, GreenBless, null, (cable, 5))).Result);
                var rq = (await env.Repo.GetRequestAsync(rejected))!;
                Assert.Equal("Request rejected (nothing approved).", Done(await StepAsync(env, StoreKeeper, rejected, p => p.OnPostReviewAsync(), Lines(rq, (cable, 0)))));
                Assert.Equal(StoreRules.Rejected, (await env.Repo.GetRequestAsync(rejected))!.Status);

                // Another Area's supervisor can neither see nor act; same-Area non-requester cannot cancel.
                var id = RequestId((await RequestAsync(env, SowingSup, MainOffice, Facility5, (cable, 5))).Result);
                var view = env.Page<DetailsPage>(await env.LoginAsync(MpSup), "GET"); view.Id = id;
                Assert.IsType<ForbidResult>(await view.OnGetAsync());
                Assert.IsType<ForbidResult>((await StepAsync(env, OfficeCoordinator, id, p => p.OnPostCancelAsync(), p => p.Reason = "x")).Result);
                Assert.IsType<ForbidResult>((await StepAsync(env, SowingSup, id, p => p.OnPostReviewAsync(), Lines((await env.Repo.GetRequestAsync(id))!, (cable, 5)))).Result);
                var cancel = await StepAsync(env, SowingSup, id, p => p.OnPostCancelAsync(), p => p.Reason = " ");
                Assert.Contains("A reason is required.", Errors(cancel.Page));
                Done(await StepAsync(env, SowingSup, id, p => p.OnPostCancelAsync(), p => p.Reason = "Not needed any more"));
                var cancelled = (await env.Repo.GetRequestAsync(id))!;
                Assert.Equal((StoreRules.Cancelled, "Not needed any more"), (cancelled.Status, cancelled.CancelReason));
                // Too late: the Store Keeper tries to review the cancelled request -> told, nothing happens.
                Assert.Equal(StoreRules.ChangedMessage, Refused(await StepAsync(env, StoreKeeper, id, p => p.OnPostReviewAsync(), Lines(cancelled, (cable, 5)))));
                Assert.Equal(100m, await env.StockAsync(cable));
            }
            finally { await env.CleanupAsync(); }
        }

        // ---- stock limits and concurrency ----------------------------------------------------------

        [SkippableFact]
        public async Task Issue_CannotExceedAvailableStock_AllOrNothing()
        {
            var env = await OpenAsync();
            try
            {
                var cable = await env.ItemAsync("ZZST-CABLE", StoreRules.Consumable, 10);
                var ties = await env.ItemAsync("ZZST-TIES", StoreRules.Consumable, 100);
                var id = RequestId((await RequestAsync(env, MpSup, GreenBless, null, (cable, 15), (ties, 20))).Result);
                var req = (await env.Repo.GetRequestAsync(id))!;
                Done(await StepAsync(env, StoreKeeper, id, p => p.OnPostReviewAsync(), Lines(req, (cable, 15), (ties, 20))));
                req = (await env.Repo.GetRequestAsync(id))!;
                var page = await StepAsync(env, StoreKeeper, id, p => p.OnPostIssueAsync(), Lines(req, (cable, 15), (ties, 20)));
                Assert.Contains("Test ZZST-CABLE: only 10 in stock.", Errors(page.Page));
                // Even bypassing the page, the repository refuses and rolls the WHOLE issue back.
                await Assert.ThrowsAsync<StoreStockException>(() => env.Repo.IssueAsync(id, req.Lines.ToDictionary(l => l.Id, l => l.ItemId == cable ? 15m : 20m), StoreKeeper));
                Assert.Equal((10m, 100m, StoreRules.Approved), (await env.StockAsync(cable), await env.StockAsync(ties), (await env.Repo.GetRequestAsync(id))!.Status));
                // And the database itself never lets stock go negative.
                var ex = await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync("UPDATE dbo.StoreItems SET AvailableQuantity = -1 WHERE Id = @I", ("@I", cable)));
                Assert.Contains(ex.Number, new[] { 547, 51817 });
            }
            finally { await env.CleanupAsync(); }
        }

        [SkippableFact]
        public async Task ConcurrentIssues_Available10_Issue7AndIssue6_ExactlyOneSucceeds_NeverNegative()
        {
            var env = await OpenAsync();
            try
            {
                for (var round = 0; round < 5; round++)
                {
                    var item = await env.ItemAsync($"ZZST-RACE{round}", StoreRules.Consumable, 10);
                    var a = RequestId((await RequestAsync(env, MpSup, GreenBless, null, (item, 7))).Result);
                    var b = RequestId((await RequestAsync(env, SowingSup, MainOffice, null, (item, 6))).Result);
                    foreach (var (id, qty) in new[] { (a, 7m), (b, 6m) })
                    {
                        var req = (await env.Repo.GetRequestAsync(id))!;
                        Assert.Equal(StoreRepository.StepResult.Done, await env.Repo.ReviewAsync(id, req.Lines.ToDictionary(l => l.Id, _ => (qty, (string?)null)), null, StoreKeeper));
                    }
                    var ra = (await env.Repo.GetRequestAsync(a))!; var rb = (await env.Repo.GetRequestAsync(b))!;
                    var results = await Task.WhenAll(
                        Task.Run(async () => { try { await env.Repo.IssueAsync(a, new Dictionary<int, decimal> { [ra.Lines[0].Id] = 7 }, StoreKeeper); return true; } catch (StoreStockException) { return false; } }),
                        Task.Run(async () => { try { await env.Repo.IssueAsync(b, new Dictionary<int, decimal> { [rb.Lines[0].Id] = 6 }, StoreKeeper); return true; } catch (StoreStockException) { return false; } }));
                    Assert.Single(results, x => x);
                    var stock = await env.StockAsync(item);
                    Assert.Contains(stock, new[] { 3m, 4m });
                    Assert.Equal(stock, await env.LedgerSumAsync(item));
                    Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.StoreTransactions WHERE ItemId = @I AND TransactionType = 'ISSUE'", ("@I", item)));
                }
            }
            finally { await env.CleanupAsync(); }
        }

        [SkippableFact]
        public async Task ConcurrentTransfers_TwentyTimesOneFromTen_ExactlyTenSucceed()
        {
            var env = await OpenAsync();
            try
            {
                var pump = await env.ItemAsync("ZZST-PUMP", StoreRules.Returnable, 10);
                var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
                {
                    try
                    {
                        await env.Repo.RecordMovementAsync(new StoreRepository.StockMove(pump, -1, StoreRules.PermanentTransfer, AppClock.Today,
                            AreaId: i % 2 == 0 ? GreenBless : MainOffice, PolyhouseId: i % 2 == 0 ? GreenBlessP1 : Facility5, Remarks: "ZZST race"), StoreKeeper);
                        return true;
                    }
                    catch (StoreStockException) { return false; }
                })));
                Assert.Equal(10, results.Count(x => x));
                Assert.Equal(0m, await env.StockAsync(pump));
                Assert.Equal(0m, await env.LedgerSumAsync(pump));
                var balances = (await env.Repo.SearchTransactionsAsync(new StoreTransactionFilter { ItemId = pump }, null)).OrderBy(t => t.Id).Select(t => t.BalanceAfter).ToList();
                Assert.Equal(new[] { 10m, 9m, 8m, 7m, 6m, 5m, 4m, 3m, 2m, 1m, 0m }, balances);   // a clean chain, no lost update
                var holdings = await env.Repo.GetHoldingsAsync(null, itemId: pump);
                Assert.Equal(10m, holdings.Sum(h => h.Transferred));
                Assert.Equal(new[] { MainOffice, GreenBless }.OrderBy(x => x), holdings.Select(h => h.AreaId).OrderBy(x => x));
            }
            finally { await env.CleanupAsync(); }
        }

        // ---- transfers, reports, store keeper scope, immutability -----------------------------------

        [SkippableFact]
        public async Task DirectTransfer_TamperedPolyhouseRejected_AndReportsAreAreaScoped()
        {
            var env = await OpenAsync();
            try
            {
                var pump = await env.ItemAsync("ZZST-PUMP", StoreRules.Returnable, 5);
                var cable = await env.ItemAsync("ZZST-CABLE", StoreRules.Consumable, 100);
                async Task<TransfersPage> Transfer(int user, int item, decimal qty, int area, int? polyhouse)
                {
                    var p = env.Page<TransfersPage>(await env.LoginAsync(user));
                    p.ItemId = item; p.Quantity = qty; p.AreaId = area; p.PolyhouseId = polyhouse; p.TransferDate = AppClock.Today; p.Remarks = "ZZST assign";
                    p.SubmitToken = SubmissionTokenGuard.NewToken();
                    await p.OnPostAsync();
                    return p;
                }
                Assert.Contains(StoreRules.PolyhouseNotInAreaMessage, Errors(await Transfer(StoreKeeper, pump, 1, MainOffice, GreenBlessP1)));
                Assert.Contains("Quantity: a returnable item is counted in whole units.", Errors(await Transfer(StoreKeeper, pump, 1.5m, GreenBless, null)));
                var ok = await Transfer(StoreKeeper, pump, 2, GreenBless, GreenBlessP1);
                Assert.StartsWith("2 Nos ZZST-PUMP -- Test ZZST-PUMP permanently transferred to Green Bless Nursery / P-1", (string)ok.TempData["Success"]!);
                await Transfer(StoreKeeper, cable, 30, MainOffice, Facility5);
                Assert.Equal((3m, 70m), (await env.StockAsync(pump), await env.StockAsync(cable)));

                async Task<ReportsPage> Report(int user, string report, int? area = null, int? polyhouse = null)
                {
                    var p = env.Page<ReportsPage>(await env.LoginAsync(user), "GET");
                    p.Report = report; p.AreaId = area; p.PolyhouseId = polyhouse;
                    await p.OnGetAsync();
                    return p;
                }
                // Store Keeper: every Area; Polyhouse filter.
                var all = await Report(StoreKeeper, "PolyhouseWise");
                Assert.Contains(all.Holdings, h => h.ItemId == pump && h.PolyhouseId == GreenBlessP1 && h.Transferred == 2);
                Assert.Contains(all.Holdings, h => h.ItemId == cable && h.PolyhouseId == Facility5 && h.Transferred == 30);
                var p1 = await Report(StoreKeeper, "PolyhouseWise", GreenBless, GreenBlessP1);
                Assert.All(p1.Holdings, h => Assert.Equal(GreenBlessP1, h.PolyhouseId));
                Assert.Contains((await Report(StoreKeeper, "Purchase")).Transactions, t => t.ItemId == cable && t.TransactionType == StoreRules.Purchase);
                var stock = await Report(StoreKeeper, "Stock");
                Assert.Equal(stock.LedgerSums[pump], stock.Items.Single(i => i.Id == pump).AvailableQuantity);
                // A Green Bless supervisor: only Green Bless, never Main Office, no Main Office purchases.
                var mp = await Report(MpSup, "AreaWise", MainOffice);
                Assert.Null(mp.AreaId);   // another Area's id is ignored
                Assert.All(mp.Holdings, h => Assert.Equal(GreenBless, h.AreaId));
                Assert.Contains(mp.Holdings, h => h.ItemId == pump);
                Assert.DoesNotContain((await Report(MpSup, "Purchase")).Transactions, t => t.ItemId == cable);
                Assert.All((await Report(MpSup, "Transfer")).Transactions, t => Assert.Equal(GreenBless, t.AreaId));
                // A supervisor cannot post a transfer (page rule Write = Store.Manage; the handler checks too).
                var forbidden = env.Page<TransfersPage>(await env.LoginAsync(MpSup));
                forbidden.ItemId = pump; forbidden.Quantity = 1; forbidden.AreaId = GreenBless; forbidden.TransferDate = AppClock.Today; forbidden.Remarks = "x"; forbidden.SubmitToken = SubmissionTokenGuard.NewToken();
                Assert.IsType<ForbidResult>(await forbidden.OnPostAsync());
            }
            finally { await env.CleanupAsync(); }
        }

        [SkippableFact]
        public async Task StoreKeeper_GetsOnlyStoreManage_AndLedgerHistoryIsImmutable()
        {
            var env = await OpenAsync();
            try
            {
                // The Store Keeper ROLE gained exactly Store.Manage; its Area assignment is unchanged.
                var role = await env.ScalarAsync<string>(@"
SELECT STRING_AGG(p.Code, ',') WITHIN GROUP (ORDER BY p.Code) FROM dbo.RolePermissions rp JOIN dbo.Permissions p ON p.Id = rp.PermissionId
JOIN dbo.Roles r ON r.Id = rp.RoleId WHERE r.Name = 'Main Office Store Keeper' AND p.Code LIKE 'Store.%'");
                Assert.Equal("Store.Manage", role);
                var keeper = await env.LoginAsync(StoreKeeper);
                var access = env.Get<AreaAccessService>();
                Assert.Equal(new[] { MainOffice }, access.GetAccessibleAreaIds(keeper));
                Assert.False(access.CanAccessArea(keeper, GreenBless));
                Assert.False(keeper.HasPermission("Labour.Enter|Booking.Enter|MotherPlant.Enter|Admin.ManageUsers"));
                Assert.False(keeper.IsFullAccess());

                var cable = await env.ItemAsync("ZZST-CABLE", StoreRules.Consumable, 50);
                var txId = await env.ScalarAsync<int>("SELECT MAX(Id) FROM dbo.StoreTransactions WHERE ItemId = @I", ("@I", cable));
                var ex = await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync("UPDATE dbo.StoreTransactions SET Remarks = 'edited' WHERE Id = @T", ("@T", txId)));
                Assert.Equal(51811, ex.Number);
                ex = await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync("DELETE FROM dbo.StoreTransactions WHERE Id = @T", ("@T", txId)));
                Assert.Equal(51812, ex.Number);
                ex = await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync("UPDATE dbo.StoreItems SET AvailableQuantity = 999 WHERE Id = @I", ("@I", cable)));
                Assert.Equal(51817, ex.Number);
                Assert.Equal(50m, await env.StockAsync(cable));
            }
            finally { await env.CleanupAsync(); }
        }

        [SkippableFact]
        public async Task DuplicateSubmissions_AreStopped()
        {
            var env = await OpenAsync();
            try
            {
                var cable = await env.ItemAsync("ZZST-CABLE", StoreRules.Consumable, 100);
                var token = SubmissionTokenGuard.NewToken();
                for (var i = 0; i < 2; i++)
                {
                    var page = env.Page<NewRequestPage>(await env.LoginAsync(MpSup));
                    page.AreaId = GreenBless; page.Purpose = "ZZST dup"; page.LineItemIds = new() { cable }; page.LineQuantities = new() { 5 }; page.SubmitToken = token;
                    await page.OnPostAsync();
                }
                Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.StoreRequests WHERE Purpose = 'ZZST dup'"));

                var id = await env.ScalarAsync<int>("SELECT Id FROM dbo.StoreRequests WHERE Purpose = 'ZZST dup'");
                var req = (await env.Repo.GetRequestAsync(id))!;
                var stepToken = SubmissionTokenGuard.NewToken();
                Done(await StepAsync(env, StoreKeeper, id, p => p.OnPostReviewAsync(), Lines(req, (cable, 5)), stepToken));
                Assert.Equal(StoreRules.AlreadySubmittedMessage, Refused(await StepAsync(env, StoreKeeper, id, p => p.OnPostIssueAsync(), Lines(req, (cable, 5)), stepToken)));
                Assert.Equal(100m, await env.StockAsync(cable));
            }
            finally { await env.CleanupAsync(); }
        }
    }
}
