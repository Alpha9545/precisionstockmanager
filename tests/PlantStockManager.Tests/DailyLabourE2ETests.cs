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
using EntryPage = PlantStockManager.Pages.Production.Labour.EntryModel;
using IndexPage = PlantStockManager.Pages.Production.Labour.IndexModel;

namespace PlantStockManager.Tests
{
    // Daily Labour Count -- end-to-end: the REAL Entry / Index pages and DailyLabourCountRepository, with REAL login
    // claims (UserClaimsFactory -> UserRoles.AreaId), against a SCRATCH COPY of the test database that has
    // Database/Migrations/2026-10-04_DailyLabourCounts.sql applied (PSM_SCRATCH_CONNECTION; the database name must
    // start with PlantsIMS2_Scratch_). Every test writes only dates in 2001 (plus "today" rows it deletes by Id) and
    // removes them again.
    //
    // Users / Areas on the scratch copy:
    //   6   Akshay   Sowing Supervisor        -> Area 2   Main-Office-Areas (MainOffice; Polyhouses 2 Facility-5, 3 Facility-3, ...)
    //   110 Vasant   Mother Plant Supervisor  -> Area 127 Green Bless Nursery
    //   112 Sandeep  Outlet Sales             -> Area 124 Outlet (no Labour access: Outlet is excluded)
    //   15  Rahul    Booking Executive        -> Area 2   (no Labour permission)
    //   21  Prajwal  System Administrator     -> every Area
    [Collection("ScratchDb")]
    public class DailyLabourE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";
        private const int SowingSup = 6, MpSup = 110, OutletSales = 112, BookingExec = 15, Admin = 21;
        private const int MainOffice = 2, GreenBless = 127, Samarth = 123, Outlet = 124;
        private const int Facility5 = 2, Facility3 = 3;
        private static readonly DateTime D1 = new(2001, 3, 1), D2 = new(2001, 3, 2), D3 = new(2001, 3, 3);

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

            public Task CleanupAsync() => ExecAsync("DELETE FROM dbo.DailyLabourCounts WHERE LabourDate >= '2001-01-01' AND LabourDate < '2002-01-01'");

            public Task<int> CountAsync(DateTime date, int areaId, int? polyhouseId) => ScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.DailyLabourCounts WHERE LabourDate = @D AND AreaId = @A AND ((@P IS NULL AND PolyhouseId IS NULL) OR PolyhouseId = @P)",
                ("@D", date), ("@A", areaId), ("@P", polyhouseId));

            public async Task<ClaimsPrincipal> LoginAsync(int userId)
            {
                var factory = Get<UserClaimsFactory>();
                var user = await factory.GetActiveUserAsync(userId);
                Assert.NotNull(user);
                return await factory.CreatePrincipalAsync(user!);
            }

            public T Page<T>(ClaimsPrincipal principal, string method = "POST") where T : PageModel
            {
                var http = new DefaultHttpContext { User = principal };
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
            services.AddSingleton<IOptions<SecurityOptions>>(Options.Create(new SecurityOptions { FullAccessRoleNames = new[] { "System Administrator" } }));
            services.AddScoped<UserClaimsFactory>();
            foreach (var t in typeof(DatabaseHelper).Assembly.GetTypes().Where(t => t.Namespace == "PlantStockManager.Data" && t.IsClass && t.Name.EndsWith("Repository")))
                services.AddScoped(t);
            var env = new Env { ConnectionString = cs!, Provider = services.BuildServiceProvider() };
            Skip.If(await env.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID('dbo.DailyLabourCounts') IS NULL THEN 0 ELSE 1 END") == 0,
                "The scratch copy does not have 2026-10-04_DailyLabourCounts.sql applied.");
            await env.CleanupAsync();
            return env;
        }

        private static async Task<(IActionResult Result, EntryPage Page)> PostAsync(Env env, int userId, DateTime? date, int? areaId, int? polyhouseId,
            int? mf, int? mh, int? ff, int? fh, int? id = null, Action<EntryPage>? before = null, string? remarks = null)
        {
            var page = env.Page<EntryPage>(await env.LoginAsync(userId));
            page.Id = id; page.LabourDate = date; page.AreaId = areaId; page.PolyhouseId = polyhouseId; page.Remarks = remarks;
            page.MaleFullDay = mf; page.MaleHalfDay = mh; page.FemaleFullDay = ff; page.FemaleHalfDay = fh;
            before?.Invoke(page);
            return (await page.OnPostAsync(), page);
        }

        private static string[] Errors(PageModel page) => page.ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).ToArray();

        private static void AssertSaved(IActionResult result) =>
            Assert.Equal("/Production/Labour/Index", Assert.IsType<RedirectToPageResult>(result).PageName);

        // ---- permissions -------------------------------------------------------------------------------

        [SkippableFact]
        public async Task RolePermissions_OnlySowingAndMotherPlantSupervisorsAndAdmin()
        {
            var env = await OpenAsync();
            foreach (var u in new[] { SowingSup, MpSup })
            {
                var p = await env.LoginAsync(u);
                Assert.True(p.HasPermission("Labour.Enter"), $"user {u} Labour.Enter");
                Assert.True(p.HasPermission("Labour.View"), $"user {u} Labour.View");
            }
            foreach (var u in new[] { BookingExec, OutletSales })
            {
                var p = await env.LoginAsync(u);
                Assert.False(p.HasPermission("Labour.Enter"), $"user {u} Labour.Enter");
                Assert.False(p.HasPermission("Labour.View"), $"user {u} Labour.View");
            }
            Assert.True((await env.LoginAsync(Admin)).IsFullAccess());
            // exactly two roles hold Labour permissions
            Assert.Equal(2, await env.ScalarAsync<int>(@"SELECT COUNT(DISTINCT rp.RoleId) FROM dbo.RolePermissions rp JOIN dbo.Permissions p ON p.Id = rp.PermissionId WHERE p.Code LIKE 'Labour.%'"));
        }

        // ---- location rule -----------------------------------------------------------------------------

        [SkippableFact]
        public async Task MainOffice_RequiresPolyhouse_AndSavesPolyhouseWise()
        {
            var env = await OpenAsync();
            try
            {
                var (r, page) = await PostAsync(env, SowingSup, D1, MainOffice, null, 10, 2, 8, 1);
                Assert.IsType<PageResult>(r);
                Assert.Contains(DailyLabourRules.PolyhouseRequiredMessage, Errors(page));
                Assert.Equal(0, await env.CountAsync(D1, MainOffice, null));

                (r, _) = await PostAsync(env, SowingSup, D1, MainOffice, Facility5, 10, 2, 8, 1);
                AssertSaved(r);
                var row = await env.Get<DailyLabourCountRepository>().GetByIdAsync((await env.Get<DailyLabourCountRepository>().FindIdAsync(D1, MainOffice, Facility5))!.Value);
                Assert.Equal((10, 2, 8, 1, 21, 19.5m, SowingSup), (row!.MaleFullDay, row.MaleHalfDay, row.FemaleFullDay, row.FemaleHalfDay, row.TotalWorkers, row.LabourDays, row.CreatedById));
                Assert.Equal("Facility-5", row.PolyhouseName);
            }
            finally { await env.CleanupAsync(); }
        }

        [SkippableFact]
        public async Task PolyhouseDropdown_OnlyThatMainOfficeAreasPolyhouses_NoneForOtherAreas()
        {
            var env = await OpenAsync();
            var admin = await env.LoginAsync(Admin);
            var page = env.Page<EntryPage>(admin, "GET");
            var json = await page.OnGetPolyhousesAsync(MainOffice);
            var ids = ((IEnumerable<object>)json.Value!).Select(o => (int)o.GetType().GetProperty("id")!.GetValue(o)!).OrderBy(i => i).ToArray();
            var oracle = new List<int>();
            using (var conn = new SqlConnection(env.ConnectionString))
            {
                await conn.OpenAsync();
                using var cmd = new SqlCommand("SELECT p.Id FROM dbo.Polyhouses p JOIN dbo.Area a ON a.Id = p.AreaId WHERE p.AreaId = 2 AND a.IsActive = 1 ORDER BY p.Id", conn);
                using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync()) oracle.Add(rd.GetInt32(0));
            }
            Assert.Equal(oracle.ToArray(), ids);
            Assert.Contains(Facility5, ids);
            Assert.Empty((IEnumerable<object>)(await page.OnGetPolyhousesAsync(GreenBless)).Value!);   // Area-wise: no Polyhouse

            // A supervisor gets nothing for an Area they are not assigned to.
            var mp = env.Page<EntryPage>(await env.LoginAsync(MpSup), "GET");
            Assert.Empty((IEnumerable<object>)(await mp.OnGetPolyhousesAsync(MainOffice)).Value!);
        }

        [SkippableFact]
        public async Task OtherArea_IsAreaWise_PolyhouseRejected()
        {
            var env = await OpenAsync();
            try
            {
                var gbPolyhouse = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.Polyhouses WHERE AreaId = 127");
                var (r, page) = await PostAsync(env, MpSup, D1, GreenBless, gbPolyhouse, 15, 3, 12, 2);
                Assert.IsType<PageResult>(r);
                Assert.Contains(DailyLabourRules.PolyhouseNotAllowedMessage, Errors(page));

                (r, _) = await PostAsync(env, MpSup, D1, GreenBless, null, 15, 3, 12, 2);
                AssertSaved(r);
                var id = await env.Get<DailyLabourCountRepository>().FindIdAsync(D1, GreenBless, null);
                var row = await env.Get<DailyLabourCountRepository>().GetByIdAsync(id!.Value);
                Assert.Null(row!.PolyhouseId);
                Assert.Equal((18, 14, 32, 29.5m), (row.TotalMale, row.TotalFemale, row.TotalWorkers, row.LabourDays));
            }
            finally { await env.CleanupAsync(); }
        }

        // ---- authorization -----------------------------------------------------------------------------

        [SkippableFact]
        public async Task TamperedAreaOrPolyhouse_IsRejected()
        {
            var env = await OpenAsync();
            try
            {
                // Sowing Supervisor (Main Office only) -> Green Bless
                var (r, page) = await PostAsync(env, SowingSup, D1, GreenBless, null, 1, 0, 0, 0);
                Assert.IsType<PageResult>(r);
                Assert.Contains(DailyLabourRules.NotAuthorizedMessage, Errors(page));
                // Mother Plant Supervisor (Green Bless only) -> Main Office + Facility-5
                (r, page) = await PostAsync(env, MpSup, D1, MainOffice, Facility5, 1, 0, 0, 0);
                Assert.Contains(DailyLabourRules.NotAuthorizedMessage, Errors(page));
                // Main Office + a Polyhouse of another Area
                var gbPolyhouse = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.Polyhouses WHERE AreaId = 127");
                (r, page) = await PostAsync(env, SowingSup, D1, MainOffice, gbPolyhouse, 1, 0, 0, 0);
                Assert.Contains(DailyLabourRules.PolyhouseNotInAreaMessage, Errors(page));
                // Non-existent ids
                (r, page) = await PostAsync(env, Admin, D1, 999999, null, 1, 0, 0, 0);
                Assert.IsType<PageResult>(r);
                (r, page) = await PostAsync(env, SowingSup, D1, MainOffice, 999999, 1, 0, 0, 0);
                Assert.IsType<PageResult>(r);

                Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.DailyLabourCounts WHERE LabourDate = @D", ("@D", D1)));
            }
            finally { await env.CleanupAsync(); }
        }

        [SkippableFact]
        public async Task Outlet_IsExcluded_EvenForAnAdministrator_AndByTheDatabase()
        {
            var env = await OpenAsync();
            try
            {
                var (r, page) = await PostAsync(env, Admin, D1, Outlet, null, 2, 0, 3, 1);
                Assert.IsType<PageResult>(r);
                Assert.Contains(DailyLabourRules.OutletNotAllowedMessage, Errors(page));

                var get = env.Page<EntryPage>(await env.LoginAsync(Admin), "GET");
                await get.OnGetAsync();
                Assert.DoesNotContain(get.Areas, a => a.AreaType == OutletRules.AreaType);
                Assert.Contains(get.Areas, a => a.Id == MainOffice);
                Assert.Contains(get.Areas, a => a.Id == GreenBless);
                var find = (await get.OnGetFindAsync(D1, Outlet, null)).Value!;
                Assert.Null(find.GetType().GetProperty("id")!.GetValue(find));

                var list = env.Page<IndexPage>(await env.LoginAsync(Admin), "GET");
                list.AreaId = Outlet;
                await list.OnGetAsync();
                Assert.Null(list.AreaId);
                Assert.DoesNotContain(list.AreaOptions, a => a.AreaType == OutletRules.AreaType);

                // The trigger is the final guard.
                var ex = await Assert.ThrowsAsync<SqlException>(() => env.Get<DailyLabourCountRepository>().CreateAsync(
                    new DailyLabourCount { LabourDate = D1, AreaId = Outlet, MaleFullDay = 1 }, Admin));
                Assert.Equal(51714, ex.Number);
                Assert.Equal(0, await env.CountAsync(D1, Outlet, null));
            }
            finally { await env.CleanupAsync(); }
        }

        [SkippableFact]
        public async Task Remark_IsOptional_SavedEditedAndLimitedTo500()
        {
            var env = await OpenAsync();
            try
            {
                var repo = env.Get<DailyLabourCountRepository>();
                var (r, page) = await PostAsync(env, MpSup, D1, GreenBless, null, 1, 0, 0, 0, remarks: new string('x', 501));
                Assert.Contains("Remark cannot be longer than 500 characters.", Errors(page));
                Assert.Equal(0, await env.CountAsync(D1, GreenBless, null));

                AssertSaved((await PostAsync(env, MpSup, D1, GreenBless, null, 15, 3, 12, 2, remarks: "  Weeding and potting  ")).Result);
                var id = (await repo.FindIdAsync(D1, GreenBless, null))!.Value;
                Assert.Equal("Weeding and potting", (await repo.GetByIdAsync(id))!.Remarks);

                var get = env.Page<EntryPage>(await env.LoginAsync(MpSup), "GET");
                get.Id = id;
                await get.OnGetAsync();
                Assert.Equal("Weeding and potting", get.Remarks);

                AssertSaved((await PostAsync(env, MpSup, null, null, null, 15, 3, 12, 2, id: id, remarks: "Rain -- half day")).Result);
                Assert.Equal("Rain -- half day", (await repo.GetByIdAsync(id))!.Remarks);
                AssertSaved((await PostAsync(env, MpSup, null, null, null, 15, 3, 12, 2, id: id, remarks: "   ")).Result);
                Assert.Null((await repo.GetByIdAsync(id))!.Remarks);   // blank = no remark

                AssertSaved((await PostAsync(env, SowingSup, D1, MainOffice, Facility5, 10, 2, 8, 1)).Result);   // remark optional
                Assert.Null((await repo.GetByIdAsync((await repo.FindIdAsync(D1, MainOffice, Facility5))!.Value))!.Remarks);

                var list = env.Page<IndexPage>(await env.LoginAsync(MpSup), "GET");
                list.FromDate = D1; list.ToDate = D1;
                await list.OnGetAsync();
                Assert.Single(list.Records);
            }
            finally { await env.CleanupAsync(); }
        }

        [SkippableFact]
        public async Task Edit_OfAnotherAreasRecord_IsForbidden_AndPostedLocationIsIgnored()
        {
            var env = await OpenAsync();
            try
            {
                var repo = env.Get<DailyLabourCountRepository>();
                var (_, id) = await repo.CreateAsync(new DailyLabourCount { LabourDate = D1, AreaId = MainOffice, PolyhouseId = Facility5, MaleFullDay = 10, MaleHalfDay = 2, FemaleFullDay = 8, FemaleHalfDay = 1 }, SowingSup);

                var mpGet = env.Page<EntryPage>(await env.LoginAsync(MpSup), "GET");
                mpGet.Id = id;
                Assert.IsType<ForbidResult>(await mpGet.OnGetAsync());
                var (r, _) = await PostAsync(env, MpSup, D1, GreenBless, null, 99, 0, 0, 0, id: id);
                Assert.IsType<ForbidResult>(r);
                Assert.Equal(10, (await repo.GetByIdAsync(id))!.MaleFullDay);

                // Authorized edit: counts change, the record keeps its own date/location even if others are posted.
                var get = env.Page<EntryPage>(await env.LoginAsync(SowingSup), "GET");
                get.Id = id;
                Assert.IsType<PageResult>(await get.OnGetAsync());
                Assert.Equal((10, 2, 8, 1), (get.MaleFullDay!.Value, get.MaleHalfDay!.Value, get.FemaleFullDay!.Value, get.FemaleHalfDay!.Value));

                (r, _) = await PostAsync(env, SowingSup, D2, MainOffice, Facility3, 12, 2, 8, 1, id: id);
                AssertSaved(r);
                var row = await repo.GetByIdAsync(id);
                Assert.Equal((D1, MainOffice, (int?)Facility5, 12, SowingSup), (row!.LabourDate, row.AreaId, row.PolyhouseId, row.MaleFullDay, row.ModifiedById!.Value));
                Assert.NotNull(row.ModifiedDate);
                Assert.Equal(0, await env.CountAsync(D2, MainOffice, Facility3));
            }
            finally { await env.CleanupAsync(); }
        }

        // ---- validation --------------------------------------------------------------------------------

        [SkippableFact]
        public async Task Dates_FutureRejected_TodayAndPastAccepted()
        {
            var env = await OpenAsync();
            int? todayId = null;
            try
            {
                var (r, page) = await PostAsync(env, MpSup, DateTime.Today.AddDays(1), GreenBless, null, 1, 0, 0, 0);
                Assert.Contains(DailyLabourRules.FutureDateMessage, Errors(page));
                (r, page) = await PostAsync(env, MpSup, null, GreenBless, null, 1, 0, 0, 0);
                Assert.IsType<PageResult>(r);

                (r, _) = await PostAsync(env, MpSup, D1, GreenBless, null, 1, 0, 0, 0);   // past
                AssertSaved(r);

                todayId = await env.Get<DailyLabourCountRepository>().FindIdAsync(DateTime.Today, GreenBless, null);
                Skip.If(todayId.HasValue, "Green Bless already has a record for today on this scratch copy.");
                (r, _) = await PostAsync(env, MpSup, DateTime.Today, GreenBless, null, 1, 0, 0, 0);   // today
                AssertSaved(r);
                todayId = await env.Get<DailyLabourCountRepository>().FindIdAsync(DateTime.Today, GreenBless, null);
                Assert.NotNull(todayId);
            }
            finally
            {
                if (todayId.HasValue) await env.ExecAsync("DELETE FROM dbo.DailyLabourCounts WHERE Id = @I AND CreatedById = @U", ("@I", todayId.Value), ("@U", MpSup));
                await env.CleanupAsync();
            }
        }

        [SkippableFact]
        public async Task Counts_NegativeOverMaxMissingAndDecimal_Rejected()
        {
            var env = await OpenAsync();
            var (r, page) = await PostAsync(env, MpSup, D1, GreenBless, null, -1, 0, 0, 0);
            Assert.Contains("Male Full Day cannot be negative.", Errors(page));
            (r, page) = await PostAsync(env, MpSup, D1, GreenBless, null, 0, 501, 0, 0);
            Assert.Contains("Male Half Day cannot be more than 500.", Errors(page));
            (r, page) = await PostAsync(env, MpSup, D1, GreenBless, null, 0, 0, null, 0);
            Assert.Contains("Female Full Day is required (enter 0 if none).", Errors(page));
            // "10.5" fails int model binding; the page replaces the framework message with the business one.
            (r, page) = await PostAsync(env, MpSup, D1, GreenBless, null, null, 0, 0, 0,
                before: p => p.ModelState.AddModelError("MaleFullDay", "The value '10.5' is not valid for MaleFullDay."));
            Assert.Equal(new[] { "Male Full Day: " + DailyLabourRules.WholeNumberMessage }, Errors(page));
            Assert.Equal(0, await env.CountAsync(D1, GreenBless, null));
        }

        // ---- duplicates --------------------------------------------------------------------------------

        [SkippableFact]
        public async Task SameDateAndLocation_OpensTheExistingRecord_NeverASecondRow()
        {
            var env = await OpenAsync();
            try
            {
                AssertSaved((await PostAsync(env, SowingSup, D1, MainOffice, Facility5, 10, 2, 8, 1)).Result);
                var (r, _) = await PostAsync(env, SowingSup, D1, MainOffice, Facility5, 1, 1, 1, 1);
                var redirect = Assert.IsType<RedirectToPageResult>(r);
                Assert.Null(redirect.PageName);   // back to the Entry page, in edit mode
                var id = await env.Get<DailyLabourCountRepository>().FindIdAsync(D1, MainOffice, Facility5);
                Assert.Equal(id, (int)redirect.RouteValues!["id"]!);
                Assert.Equal(true, redirect.RouteValues["loaded"]);
                Assert.Equal(1, await env.CountAsync(D1, MainOffice, Facility5));
                Assert.Equal(10, (await env.Get<DailyLabourCountRepository>().GetByIdAsync(id!.Value))!.MaleFullDay);   // not overwritten

                var loaded = env.Page<EntryPage>(await env.LoginAsync(SowingSup), "GET");
                loaded.Id = id;
                await loaded.OnGetAsync(loaded: true);
                Assert.Equal(DailyLabourRules.AlreadyExistsMessage, loaded.ViewData["Info"]);

                // Area-wise
                AssertSaved((await PostAsync(env, MpSup, D1, GreenBless, null, 15, 3, 12, 2)).Result);
                Assert.IsType<RedirectToPageResult>((await PostAsync(env, MpSup, D1, GreenBless, null, 1, 1, 1, 1)).Result);
                Assert.Equal(1, await env.CountAsync(D1, GreenBless, null));

                // The Find handler used by the page
                var find = env.Page<EntryPage>(await env.LoginAsync(SowingSup), "GET");
                var found = (await find.OnGetFindAsync(D1, MainOffice, Facility5)).Value!;
                Assert.Equal(id, (int?)found.GetType().GetProperty("id")!.GetValue(found));
                var notMine = (await find.OnGetFindAsync(D1, GreenBless, null)).Value!;
                Assert.Null(notMine.GetType().GetProperty("id")!.GetValue(notMine));   // not authorized: nothing revealed
            }
            finally { await env.CleanupAsync(); }
        }

        [SkippableFact]
        public async Task DifferentPolyhousesAndDifferentAreas_StaySeparate()
        {
            var env = await OpenAsync();
            try
            {
                AssertSaved((await PostAsync(env, SowingSup, D1, MainOffice, Facility5, 10, 2, 8, 1)).Result);
                AssertSaved((await PostAsync(env, SowingSup, D1, MainOffice, Facility3, 7, 1, 5, 2)).Result);
                AssertSaved((await PostAsync(env, Admin, D1, GreenBless, null, 15, 3, 12, 2)).Result);
                AssertSaved((await PostAsync(env, Admin, D1, Samarth, null, 4, 0, 3, 1)).Result);
                Assert.Equal(1, await env.CountAsync(D1, MainOffice, Facility5));
                Assert.Equal(1, await env.CountAsync(D1, MainOffice, Facility3));
                Assert.Equal(0, await env.CountAsync(D1, MainOffice, null));   // never combined into one Main Office row
                Assert.Equal(1, await env.CountAsync(D1, GreenBless, null));
                Assert.Equal(1, await env.CountAsync(D1, Samarth, null));
            }
            finally { await env.CleanupAsync(); }
        }

        [SkippableFact]
        public async Task SimultaneousSubmits_CreateExactlyOneRecord()
        {
            var env = await OpenAsync();
            try
            {
                var repo = env.Get<DailyLabourCountRepository>();
                foreach (var (area, polyhouse) in new[] { (MainOffice, (int?)Facility5), (GreenBless, (int?)null) })
                {
                    var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(() => repo.CreateAsync(new DailyLabourCount
                    {
                        LabourDate = D3, AreaId = area, PolyhouseId = polyhouse, MaleFullDay = i, MaleHalfDay = 0, FemaleFullDay = 0, FemaleHalfDay = 0
                    }, Admin))).ToArray();
                    var results = await Task.WhenAll(tasks);
                    Assert.Single(results, x => x.Outcome == DailyLabourCountRepository.CreateOutcome.Created);
                    Assert.Equal(19, results.Count(x => x.Outcome == DailyLabourCountRepository.CreateOutcome.AlreadyExists));
                    Assert.Single(results.Select(x => x.Id).Distinct());
                    Assert.Equal(1, await env.CountAsync(D3, area, polyhouse));
                }
            }
            finally { await env.CleanupAsync(); }
        }

        // ---- history / filters / summary --------------------------------------------------------------

        [SkippableFact]
        public async Task History_FiltersSummaryAndAreaScope()
        {
            var env = await OpenAsync();
            try
            {
                var repo = env.Get<DailyLabourCountRepository>();
                async Task Add(DateTime d, int a, int? p, int mf, int mh, int ff, int fh) =>
                    await repo.CreateAsync(new DailyLabourCount { LabourDate = d, AreaId = a, PolyhouseId = p, MaleFullDay = mf, MaleHalfDay = mh, FemaleFullDay = ff, FemaleHalfDay = fh }, Admin);
                await Add(D1, MainOffice, Facility5, 10, 2, 8, 1);
                await Add(D1, MainOffice, Facility3, 7, 1, 5, 2);
                await Add(D2, MainOffice, Facility5, 20, 3, 15, 2);
                await Add(D1, GreenBless, null, 15, 3, 12, 2);
                await Add(D2, Samarth, null, 4, 0, 3, 1);

                async Task<IndexPage> List(int user, DateTime? from, DateTime? to, int? area = null, int? polyhouse = null)
                {
                    var page = env.Page<IndexPage>(await env.LoginAsync(user), "GET");
                    page.FromDate = from; page.ToDate = to; page.AreaId = area; page.PolyhouseId = polyhouse;
                    await page.OnGetAsync();
                    return page;
                }

                var all = await List(Admin, D1, D2);
                Assert.Equal(5, all.Records.Count);
                Assert.Equal((56, 9, 43, 8), (all.Summary.MaleFullDay, all.Summary.MaleHalfDay, all.Summary.FemaleFullDay, all.Summary.FemaleHalfDay));
                Assert.Equal(116, all.Summary.TotalWorkers);
                Assert.Equal(107.5m, all.Summary.LabourDays);
                Assert.Equal(all.Records.Sum(r => r.TotalWorkers), all.Summary.TotalWorkers);
                Assert.Equal(all.Records.Sum(r => r.LabourDays), all.Summary.LabourDays);

                var day1 = await List(Admin, D1, D1);
                Assert.Equal(3, day1.Records.Count);
                var mo = await List(Admin, D1, D2, MainOffice);
                Assert.True(mo.ShowPolyhouseFilter);
                Assert.Equal(3, mo.Records.Count);
                var f5 = await List(Admin, D1, D2, MainOffice, Facility5);
                Assert.Equal(2, f5.Records.Count);
                Assert.Equal(40 + 21, f5.Summary.TotalWorkers);
                Assert.Equal(37.5m + 19.5m, f5.Summary.LabourDays);
                var gb = await List(Admin, D1, D2, GreenBless, Facility5);   // Polyhouse filter does not apply to an Area-wise Area
                Assert.False(gb.ShowPolyhouseFilter);
                Assert.Null(gb.PolyhouseId);
                Assert.Single(gb.Records);

                // Area scope: a Mother Plant Supervisor sees only Green Bless, even when asking for Main Office.
                var mp = await List(MpSup, D1, D2);
                Assert.All(mp.Records, r => Assert.Equal(GreenBless, r.AreaId));
                Assert.Single(mp.Records);
                Assert.Equal(32, mp.Summary.TotalWorkers);
                var tampered = await List(MpSup, D1, D2, MainOffice, Facility5);
                Assert.Null(tampered.AreaId);
                Assert.All(tampered.Records, r => Assert.Equal(GreenBless, r.AreaId));
                Assert.DoesNotContain(tampered.AreaOptions, a => a.Id == MainOffice);
                var sowing = await List(SowingSup, D1, D2);
                Assert.All(sowing.Records, r => Assert.Equal(MainOffice, r.AreaId));
                Assert.Equal(3, sowing.Records.Count);
            }
            finally { await env.CleanupAsync(); }
        }
    }
}
