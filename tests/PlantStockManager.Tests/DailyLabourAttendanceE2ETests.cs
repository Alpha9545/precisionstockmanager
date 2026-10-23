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
using AttendancePage = PlantStockManager.Pages.Production.Labour.AttendanceModel;
using HistoryPage = PlantStockManager.Pages.Production.Labour.AttendanceHistoryModel;

namespace PlantStockManager.Tests
{
    // Daily Labour Attendance (9:00 AM / 2:00 PM) -- end-to-end: the REAL Attendance / Attendance
    // History pages and DailyLabourAttendanceRepository, with REAL login claims (UserClaimsFactory ->
    // UserRoles.AreaId), against a SCRATCH COPY of the test database that has
    // Database/Migrations/2026-10-06_DailyLabourAttendance.sql applied (PSM_SCRATCH_CONNECTION; the
    // database name must start with PlantsIMS2_Scratch_). Every test writes only dates in 2001 (plus
    // "today" rows it removes again) and cleans up after itself.
    //
    // Users / Areas on the scratch copy:
    //   6   Akshay   Sowing Supervisor        -> Area 2   Main-Office-Areas (MainOffice; Polyhouses 2 Facility-5, 3 Facility-3)
    //   110 Vasant   Mother Plant Supervisor  -> Area 127 Green Bless Nursery (Area-wise; has Polyhouses 14, 15, 16)
    //   112 Sandeep  Outlet Sales             -> Area 124 Outlet (no Labour access)
    //   15  Rahul    Booking Executive        -> Area 2   (no Labour permission)
    //   21  Prajwal  System Administrator     -> every Area
    [Collection("ScratchDb")]
    public class DailyLabourAttendanceE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";
        private const int MpSup = 110, OutletSales = 112, BookingExec = 15, Admin = 21;
        // 2026-10-10: looked up per run (FixtureUsers) -- user 6 is no longer a Sowing Supervisor in the local data
        private static int SowingSup = 6;
        private const int MainOffice = 2, GreenBless = 127, Samarth = 123, Outlet = 124;
        private const int Facility5 = 2, Facility3 = 3, GreenBlessP1 = 14;
        private static readonly DateTime D1 = new(2001, 3, 1), D2 = new(2001, 3, 2), D3 = new(2001, 3, 3);
        private const string Morning = DailyLabourRules.Morning, Afternoon = DailyLabourRules.Afternoon;

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
            public DailyLabourAttendanceRepository Repo => Get<DailyLabourAttendanceRepository>();

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

            // Attendance first (its audit trigger records the deletion), then the audit rows
            // (allowed once their attendance row is gone), then the manual records.
            public async Task CleanupAsync(DateTime? alsoDate = null, int? alsoArea = null)
            {
                const string range = "LabourDate >= '2001-01-01' AND LabourDate < '2002-01-01'";
                await ExecAsync($"DELETE FROM dbo.DailyLabourAttendance WHERE {range}");
                await ExecAsync($"DELETE FROM dbo.DailyLabourAttendanceHistory WHERE {range}");
                await ExecAsync($"DELETE FROM dbo.DailyLabourCounts WHERE {range}");
                if (alsoDate.HasValue && alsoArea.HasValue)
                {
                    await ExecAsync("DELETE FROM dbo.DailyLabourAttendance WHERE LabourDate = @D AND AreaId = @A", ("@D", alsoDate.Value.Date), ("@A", alsoArea.Value));
                    await ExecAsync("DELETE FROM dbo.DailyLabourAttendanceHistory WHERE LabourDate = @D AND AreaId = @A", ("@D", alsoDate.Value.Date), ("@A", alsoArea.Value));
                }
            }

            public Task<int> RowsAsync(DateTime date, int areaId, int? polyhouseId, string? time = null) => ScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.DailyLabourAttendance WHERE LabourDate = @D AND AreaId = @A AND ((@P IS NULL AND PolyhouseId IS NULL) OR PolyhouseId = @P) AND (@T IS NULL OR AttendanceTime = @T)",
                ("@D", date), ("@A", areaId), ("@P", polyhouseId), ("@T", time));

            public Task<int> HistoryRowsAsync(DateTime date, int areaId, string action) => ScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.DailyLabourAttendanceHistory WHERE LabourDate = @D AND AreaId = @A AND Action = @X",
                ("@D", date), ("@A", areaId), ("@X", action));

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
            Skip.If(await env.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID('dbo.DailyLabourAttendance') IS NULL THEN 0 ELSE 1 END") == 0,
                "The scratch copy does not have 2026-10-06_DailyLabourAttendance.sql applied.");
            await env.CleanupAsync();
            SowingSup = await env.ScalarAsync<int>(FixtureUsers.MainOfficeSowingSupervisorSql);
            Skip.If(SowingSup == 0, "No active Sowing Supervisor is assigned to the Main Office Area in this copy.");
            return env;
        }

        // Posts the Attendance form exactly as the page does (null = left blank).
        private static async Task<(IActionResult Result, AttendancePage Page)> PostAsync(Env env, int userId, DateTime? date, int? areaId, int? polyhouseId,
            int? m9, int? f9, int? m2, int? f2, string? r9 = null, string? r2 = null, Action<AttendancePage>? before = null)
        {
            var page = env.Page<AttendancePage>(await env.LoginAsync(userId));
            page.LabourDate = date; page.AreaId = areaId; page.PolyhouseId = polyhouseId;
            page.Male9 = m9; page.Female9 = f9; page.Male2 = m2; page.Female2 = f2; page.Remarks9 = r9; page.Remarks2 = r2;
            before?.Invoke(page);
            return (await page.OnPostAsync(), page);
        }

        private static async Task<AttendancePage> GetAsync(Env env, int userId, DateTime? date, int? areaId, int? polyhouseId)
        {
            var page = env.Page<AttendancePage>(await env.LoginAsync(userId), "GET");
            page.LabourDate = date; page.AreaId = areaId; page.PolyhouseId = polyhouseId;
            await page.OnGetAsync();
            return page;
        }

        private static async Task<HistoryPage> HistoryAsync(Env env, int userId, DateTime? from, DateTime? to, int? area = null, int? polyhouse = null)
        {
            var page = env.Page<HistoryPage>(await env.LoginAsync(userId), "GET");
            page.FromDate = from; page.ToDate = to; page.AreaId = area; page.PolyhouseId = polyhouse;
            await page.OnGetAsync();
            return page;
        }

        private static string[] Errors(PageModel page) => page.ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).ToArray();

        private static string AssertSaved(IActionResult result, PageModel page)
        {
            var redirect = Assert.IsType<RedirectToPageResult>(result);
            Assert.Null(redirect.PageName);   // back to the same Attendance page, same day and location
            return (string)page.TempData["Success"]!;
        }

        // ---- the two attendance times and the estimate ------------------------------------------------

        [SkippableFact]
        public async Task NineThenTwo_SavedSeparately_GivesTheApprovedEstimate()
        {
            var env = await OpenAsync();
            try
            {
                // 9:00 AM only -> Incomplete, not in the totals.
                var (r, page) = await PostAsync(env, MpSup, D1, GreenBless, null, 20, 15, null, null, r9: "  Weeding  ");
                Assert.Equal("9:00 AM attendance saved for 01-03-2001, Green Bless Nursery.", AssertSaved(r, page));
                var history = await HistoryAsync(env, MpSup, D1, D1);
                var day = Assert.Single(history.Days);
                Assert.False(day.IsComplete);
                Assert.Equal((20, 15, (int?)null, (int?)null), (day.Male9!.Value, day.Female9!.Value, day.Male2, day.Female2));
                Assert.Equal((1, 0, 1, 0, 0), (history.Summary.Days, history.Summary.CompleteDays, history.Summary.IncompleteDays, history.Summary.TotalFullDay, history.Summary.TotalHalfDay));

                // The page reloads the recorded 9:00 AM values; the supervisor adds 2:00 PM later.
                var get = await GetAsync(env, MpSup, D1, GreenBless, null);
                Assert.True(get.LocationReady);
                Assert.Equal((20, 15, "Weeding", (int?)null), (get.Male9!.Value, get.Female9!.Value, get.Remarks9, get.Male2));
                (r, page) = await PostAsync(env, MpSup, D1, GreenBless, null, get.Male9, get.Female9, 17, 12, r9: get.Remarks9);
                Assert.Equal("2:00 PM attendance saved for 01-03-2001, Green Bless Nursery.", AssertSaved(r, page));   // 9:00 AM unchanged

                history = await HistoryAsync(env, MpSup, D1, D1);
                day = Assert.Single(history.Days);
                Assert.True(day.IsComplete);
                Assert.Equal((17, 3, 12, 3, 29, 6), (day.MaleFullDay!.Value, day.MaleHalfDay!.Value, day.FemaleFullDay!.Value, day.FemaleHalfDay!.Value, day.TotalFullDay!.Value, day.TotalHalfDay!.Value));
                Assert.Equal((17, 3, 12, 3, 29, 6), (history.Summary.MaleFullDay, history.Summary.MaleHalfDay, history.Summary.FemaleFullDay, history.Summary.FemaleHalfDay, history.Summary.TotalFullDay, history.Summary.TotalHalfDay));
                Assert.Equal(2, await env.RowsAsync(D1, GreenBless, null));
                Assert.Equal(2, await env.HistoryRowsAsync(D1, GreenBless, "Created"));
                Assert.Equal(0, await env.HistoryRowsAsync(D1, GreenBless, "Corrected"));
                var rows = await env.Repo.GetEntriesAsync(D1, GreenBless, null);
                Assert.All(rows, e => Assert.Equal(MpSup, e.CreatedById));
                Assert.Equal("Weeding", rows.Single(e => e.AttendanceTime == Morning).Remarks);
            }
            finally { await env.CleanupAsync(); }
        }

        [SkippableFact]
        public async Task BothTimesInOnePost_MainOfficeIsPolyhouseWise_LocationsStaySeparate()
        {
            var env = await OpenAsync();
            try
            {
                var (r, page) = await PostAsync(env, SowingSup, D1, MainOffice, Facility5, 10, 8, 9, 8);
                Assert.Equal("9:00 AM and 2:00 PM attendance saved for 01-03-2001, Main-Office-Areas, Facility-5.", AssertSaved(r, page));
                (r, page) = await PostAsync(env, SowingSup, D1, MainOffice, Facility3, 7, 5, 7, 6);
                AssertSaved(r, page);
                (r, page) = await PostAsync(env, Admin, D1, Samarth, null, 4, 3, 2, 3);
                AssertSaved(r, page);

                Assert.Equal(2, await env.RowsAsync(D1, MainOffice, Facility5));
                Assert.Equal(2, await env.RowsAsync(D1, MainOffice, Facility3));
                Assert.Equal(0, await env.RowsAsync(D1, MainOffice, null));   // never one combined Main Office row
                Assert.Equal(2, await env.RowsAsync(D1, Samarth, null));

                var all = await HistoryAsync(env, Admin, D1, D1);
                Assert.Equal(3, all.Days.Count);
                // F5 9/1 8/0, F3 7/0 5/1, Samarth 2/2 3/0  ->  male 18/3, female 16/1
                Assert.Equal((18, 3, 16, 1), (all.Summary.MaleFullDay, all.Summary.MaleHalfDay, all.Summary.FemaleFullDay, all.Summary.FemaleHalfDay));
                var (date, _, total) = Assert.Single(all.ByDate);
                Assert.Equal((D1, 34, 4), (date, total.TotalFullDay, total.TotalHalfDay));   // the daily total row
            }
            finally { await env.CleanupAsync(); }
        }

        // ---- corrections and the audit history ------------------------------------------------------

        [SkippableFact]
        public async Task Correction_IsAudited_WithOldAndNewValues_UnchangedSaveIsNot()
        {
            var env = await OpenAsync();
            try
            {
                var (first, firstPage) = await PostAsync(env, SowingSup, D1, MainOffice, Facility5, 20, 15, 17, 12);
                Assert.StartsWith("9:00 AM and 2:00 PM attendance saved", AssertSaved(first, firstPage));

                // An administrator corrects the 2:00 PM male count (a previous date).
                var (r, page) = await PostAsync(env, Admin, D1, MainOffice, Facility5, 20, 15, 18, 12, r2: "Recount");
                Assert.Equal("2:00 PM attendance corrected for 01-03-2001, Main-Office-Areas, Facility-5.", AssertSaved(r, page));
                var pm = (await env.Repo.GetEntriesAsync(D1, MainOffice, Facility5)).Single(e => e.AttendanceTime == Afternoon);
                Assert.Equal((18, 12, "Recount", (int?)Admin, SowingSup), (pm.MaleCount, pm.FemaleCount, pm.Remarks, pm.ModifiedById, pm.CreatedById!.Value));
                Assert.NotNull(pm.ModifiedDate);

                var changes = await env.Repo.GetChangesAsync(D1, MainOffice, Facility5);
                Assert.Equal(new[] { "Created", "Created", "Corrected" }, changes.Select(c => c.Action));
                var corrected = changes.Last();
                Assert.Equal((Afternoon, 17, 18, 12, 12, (string?)null, "Recount"), (corrected.AttendanceTime, corrected.OldMaleCount!.Value, corrected.NewMaleCount!.Value, corrected.OldFemaleCount!.Value, corrected.NewFemaleCount!.Value, corrected.OldRemarks, corrected.NewRemarks));
                Assert.Equal("Prajwal", await env.ScalarAsync<string>("SELECT u.Username FROM dbo.DailyLabourAttendanceHistory h JOIN dbo.IMSUsers u ON u.Id = h.ChangedById WHERE h.Id = @I", ("@I", corrected.Id)));

                // The page shows the history; the day is flagged Corrected in Attendance History.
                var get = await GetAsync(env, SowingSup, D1, MainOffice, Facility5);
                Assert.Equal(3, get.Changes.Count);
                Assert.True(Assert.Single((await HistoryAsync(env, Admin, D1, D1)).Days).Corrected);

                // Saving the same values again: nothing changes, no audit row.
                (r, page) = await PostAsync(env, SowingSup, D1, MainOffice, Facility5, 20, 15, 18, 12, r2: "Recount");
                Assert.StartsWith("No changes", AssertSaved(r, page));
                Assert.Equal(3, (await env.Repo.GetChangesAsync(D1, MainOffice, Facility5)).Count);

                // A recorded time cannot be removed by leaving it blank.
                (r, page) = await PostAsync(env, SowingSup, D1, MainOffice, Facility5, 20, 15, null, null);
                Assert.IsType<PageResult>(r);
                Assert.Contains(Errors(page), e => e.StartsWith("2:00 PM attendance is already recorded"));
                Assert.Equal(2, await env.RowsAsync(D1, MainOffice, Facility5));

                // The audit history itself cannot be edited or removed while the attendance exists.
                var ex = await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync("UPDATE dbo.DailyLabourAttendanceHistory SET NewMaleCount = 1 WHERE Id = @I", ("@I", corrected.Id)));
                Assert.Equal(51726, ex.Number);
                ex = await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync("DELETE FROM dbo.DailyLabourAttendanceHistory WHERE Id = @I", ("@I", corrected.Id)));
                Assert.Equal(51727, ex.Number);
            }
            finally { await env.CleanupAsync(); }
        }

        // ---- location rule ---------------------------------------------------------------------------

        [SkippableFact]
        public async Task LocationRule_PolyhouseRequiredForMainOffice_RejectedElsewhere_OutletExcluded()
        {
            var env = await OpenAsync();
            // 2026-10-23: with 2026-10-26_LabourAttendanceOutlet.sql Outlet takes attendance (OutletAttendance_* below)
            var outletAllowed = await env.ScalarAsync<int>(LabourAttendanceOutletSchema.ProbeSql) == 1;
            try
            {
                var (r, page) = await PostAsync(env, SowingSup, D1, MainOffice, null, 1, 1, 1, 1);
                Assert.IsType<PageResult>(r);
                Assert.Contains(DailyLabourRules.PolyhouseRequiredMessage, Errors(page));
                (r, page) = await PostAsync(env, MpSup, D1, GreenBless, GreenBlessP1, 1, 1, 1, 1);
                Assert.Contains(DailyLabourRules.PolyhouseNotAllowedMessage, Errors(page));
                (r, page) = await PostAsync(env, SowingSup, D1, MainOffice, GreenBlessP1, 1, 1, 1, 1);
                Assert.Contains(DailyLabourRules.PolyhouseNotInAreaMessage, Errors(page));
                if (!outletAllowed)
                {
                    (r, page) = await PostAsync(env, Admin, D1, Outlet, null, 1, 1, 1, 1);
                    Assert.Contains(DailyLabourRules.OutletNotAllowedMessage, Errors(page));
                }
                (r, page) = await PostAsync(env, Admin, D1, 999999, null, 1, 1, 1, 1);
                Assert.IsType<PageResult>(r);
                (r, page) = await PostAsync(env, SowingSup, D1, MainOffice, 999999, 1, 1, 1, 1);
                Assert.IsType<PageResult>(r);
                Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.DailyLabourAttendance WHERE LabourDate = @D", ("@D", D1)));

                // Dropdowns: Outlet never offered; Polyhouses only for a Main Office Area.
                var get = await GetAsync(env, Admin, D1, null, null);
                Assert.Equal(outletAllowed, get.Areas.Any(a => a.AreaType == OutletRules.AreaType));
                Assert.Contains(get.Areas, a => a.Id == MainOffice);
                var ids = ((IEnumerable<object>)(await get.OnGetPolyhousesAsync(MainOffice)).Value!).Select(o => (int)o.GetType().GetProperty("id")!.GetValue(o)!).ToList();
                Assert.Contains(Facility5, ids);
                Assert.Empty((IEnumerable<object>)(await get.OnGetPolyhousesAsync(GreenBless)).Value!);

                // The trigger is the final guard, whatever the caller.
                if (!outletAllowed)
                {
                    var outletEx = await Assert.ThrowsAsync<SqlException>(() => env.Repo.SaveAsync(D1, Outlet, null, new[] { new DailyLabourAttendanceInput(Morning, 1, 1, null) }, Admin));
                    Assert.Equal(51714, outletEx.Number);
                }
                var ex = await Assert.ThrowsAsync<SqlException>(() => env.Repo.SaveAsync(D1, MainOffice, null, new[] { new DailyLabourAttendanceInput(Morning, 1, 1, null) }, Admin));
                Assert.Equal(51711, ex.Number);
            }
            finally { await env.CleanupAsync(); }
        }

        // ---- authorization ---------------------------------------------------------------------------

        [SkippableFact]
        public async Task AreaAuthorization_TamperedIdsRejected_OtherAreasNeverShown()
        {
            var env = await OpenAsync();
            try
            {
                // Posting another Area's id.
                var (r, page) = await PostAsync(env, SowingSup, D1, GreenBless, null, 1, 1, 1, 1);
                Assert.IsType<PageResult>(r);
                Assert.Contains(DailyLabourRules.NotAuthorizedMessage, Errors(page));
                (r, page) = await PostAsync(env, MpSup, D1, MainOffice, Facility5, 1, 1, 1, 1);
                Assert.Contains(DailyLabourRules.NotAuthorizedMessage, Errors(page));
                Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.DailyLabourAttendance WHERE LabourDate = @D", ("@D", D1)));

                // A hand-typed URL for another Area shows nothing of it.
                await env.Repo.SaveAsync(D1, MainOffice, Facility5, new[] { new DailyLabourAttendanceInput(Morning, 9, 9, "secret") }, SowingSup);
                var get = await GetAsync(env, MpSup, D1, MainOffice, Facility5);
                Assert.False(get.LocationReady);
                Assert.Null(get.AreaId);
                Assert.Empty(get.Entries);
                Assert.Empty(get.Changes);
                Assert.Null(get.Male9);
                Assert.DoesNotContain(get.Areas, a => a.Id == MainOffice);
                Assert.Empty((IEnumerable<object>)(await get.OnGetPolyhousesAsync(MainOffice)).Value!);

                // History is limited to the user's own Areas, even when another Area is asked for.
                var mp = await HistoryAsync(env, MpSup, D1, D1, MainOffice, Facility5);
                Assert.Null(mp.AreaId);
                Assert.Empty(mp.Days);
                Assert.DoesNotContain(mp.AreaOptions, a => a.Id == MainOffice);
                Assert.Single((await HistoryAsync(env, SowingSup, D1, D1)).Days);
                Assert.Single((await HistoryAsync(env, Admin, D1, D1)).Days);

                // Permissions: the existing Labour permissions decide who may open the pages.
                foreach (var u in new[] { SowingSup, MpSup })
                    Assert.True((await env.LoginAsync(u)).HasPermission("Labour.Enter"));
                foreach (var u in new[] { BookingExec, OutletSales })
                {
                    var p = await env.LoginAsync(u);
                    Assert.False(p.HasPermission("Labour.Enter"));
                    Assert.False(p.HasPermission("Labour.View"));
                }
                Assert.True((await env.LoginAsync(Admin)).IsFullAccess());
            }
            finally { await env.CleanupAsync(); }
        }

        // ---- Outlet attendance (2026-10-26_LabourAttendanceOutlet.sql) --------------------------------

        [SkippableFact]
        public async Task OutletAttendance_OutletSalesRecordTheirOwnOutlet_AreaWise_AttendanceOnly()
        {
            var env = await OpenAsync();
            Skip.If(await env.ScalarAsync<int>(LabourAttendanceOutletSchema.ProbeSql) == 0, "2026-10-26_LabourAttendanceOutlet.sql is not applied to this scratch copy.");
            try
            {
                // the permission: attendance only -- not overtime, labour counts or any history page
                var outletUser = await env.LoginAsync(OutletSales);
                Assert.True(outletUser.HasPermission("Labour.Attendance"));
                Assert.True(outletUser.HasPermission(FeatureAuthorizationConventions.Rules["/Production/Labour/Attendance"].Read));
                foreach (var other in new[] { "/Production/Labour/Overtime", "/Production/Labour/MyOvertime", "/Production/Labour/Entry", "/Production/Labour/Index", "/Production/Labour/OvertimeDetails" })
                    Assert.False(outletUser.HasPermission(FeatureAuthorizationConventions.Rules[other].Read), other);
                Assert.False(outletUser.HasPermission("Labour.Enter"));

                // the page offers exactly their own Outlet, Area-wise (no Polyhouse)
                var get = await GetAsync(env, OutletSales, D1, null, null);
                Assert.Equal(new[] { Outlet }, get.Areas.Select(a => a.Id));
                Assert.Empty((IEnumerable<object>)(await get.OnGetPolyhousesAsync(Outlet)).Value!);

                // saving: both times, audited like every other Area
                var (r, page) = await PostAsync(env, OutletSales, D1, Outlet, null, 3, 2, 3, 1);
                Assert.IsType<RedirectToPageResult>(r);
                Assert.Equal(2, await env.RowsAsync(D1, Outlet, null));
                Assert.Equal(2, await env.HistoryRowsAsync(D1, Outlet, "Created"));

                // a Polyhouse is refused (Area-wise), another Area is refused (their Area only), and the database repeats it
                var outletPolyhouse = await env.ScalarAsync<int>("SELECT MIN(Id) FROM dbo.Polyhouses WHERE AreaId = @A", ("@A", Outlet));
                if (outletPolyhouse > 0)
                {
                    (r, page) = await PostAsync(env, OutletSales, D2, Outlet, outletPolyhouse, 1, 1, 1, 1);
                    Assert.Contains(DailyLabourRules.PolyhouseNotAllowedMessage, Errors(page));
                    var ex = await Assert.ThrowsAsync<SqlException>(() => env.Repo.SaveAsync(D2, Outlet, outletPolyhouse, new[] { new DailyLabourAttendanceInput(Morning, 1, 1, null) }, Admin));
                    Assert.Equal(51712, ex.Number);
                }
                (r, page) = await PostAsync(env, OutletSales, D2, MainOffice, Facility5, 1, 1, 1, 1);
                Assert.Contains(DailyLabourRules.NotAuthorizedMessage, Errors(page));
                Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.DailyLabourAttendance WHERE LabourDate = @D", ("@D", D2)));

                // the administrator's Attendance History lists the Outlet day
                var history = await HistoryAsync(env, Admin, D1, D1, Outlet);
                Assert.Equal(Outlet, history.AreaId);
                Assert.Single(history.Days);
                Assert.Contains(history.AreaOptions, a => a.Id == Outlet);

                // labour counts and overtime still exclude Outlet in the database
                Assert.Equal(1, await env.ScalarAsync<int>("SELECT CASE WHEN OBJECT_DEFINITION(OBJECT_ID('dbo.TR_DailyLabourCounts_Location')) LIKE N'%51714%' AND OBJECT_DEFINITION(OBJECT_ID('dbo.TR_LabourOvertime_Location')) LIKE N'%51714%' THEN 1 ELSE 0 END"));
            }
            finally { await env.CleanupAsync(); }
        }

        // ---- dates and counts ------------------------------------------------------------------------

        [SkippableFact]
        public async Task Dates_FutureRejected_TodayAndPastAccepted_InIndiaTime()
        {
            var env = await OpenAsync();
            var today = AppClock.Today;
            // Checked BEFORE the try: the cleanup below removes today's Green Bless rows.
            Skip.If(await env.RowsAsync(today, GreenBless, null) > 0, "Green Bless already has attendance for today on this scratch copy.");
            try
            {
                var (r, page) = await PostAsync(env, MpSup, today.AddDays(1), GreenBless, null, 1, 1, 1, 1);
                Assert.Contains(DailyLabourRules.FutureDateMessage, Errors(page));
                (r, page) = await PostAsync(env, MpSup, null, GreenBless, null, 1, 1, 1, 1);
                Assert.Contains("Date is required.", Errors(page));
                Assert.Equal(0, await env.RowsAsync(today.AddDays(1), GreenBless, null));

                (r, page) = await PostAsync(env, MpSup, D2, GreenBless, null, 5, 5, null, null);      // past
                AssertSaved(r, page);
                (r, page) = await PostAsync(env, MpSup, today, GreenBless, null, 5, 5, null, null);   // today
                AssertSaved(r, page);
                Assert.Equal(1, await env.RowsAsync(today, GreenBless, null, Morning));

                // The page defaults to India's today and never offers a later date.
                var get = await GetAsync(env, MpSup, null, null, null);
                Assert.Equal(today, get.LabourDate);
                Assert.Equal(today, get.Today);
            }
            finally { await env.CleanupAsync(today, GreenBless); }
        }

        [SkippableFact]
        public async Task Counts_MissingNegativeOverMaxDecimalAndNothingEntered_AreRejected()
        {
            var env = await OpenAsync();
            var (r, page) = await PostAsync(env, MpSup, D1, GreenBless, null, 20, null, null, null);
            Assert.Contains("9:00 AM Female is required (enter 0 if none).", Errors(page));
            (r, page) = await PostAsync(env, MpSup, D1, GreenBless, null, -1, 0, null, null);
            Assert.Contains("9:00 AM Male cannot be negative.", Errors(page));
            (r, page) = await PostAsync(env, MpSup, D1, GreenBless, null, null, null, 0, 501);
            Assert.Contains("2:00 PM Female cannot be more than 500.", Errors(page));
            (r, page) = await PostAsync(env, MpSup, D1, GreenBless, null, null, null, null, null);
            Assert.Equal(new[] { DailyLabourRules.AttendanceRequiredMessage }, Errors(page));
            (r, page) = await PostAsync(env, MpSup, D1, GreenBless, null, 1, 1, null, null, r9: new string('x', 501));
            Assert.Contains("Remark cannot be longer than 500 characters.", Errors(page));
            // "10.5" fails int model binding; the page replaces the framework message with the business one.
            (r, page) = await PostAsync(env, MpSup, D1, GreenBless, null, null, 0, null, null,
                before: p => p.ModelState.AddModelError("Male9", "The value '10.5' is not valid for Male9."));
            Assert.Equal(new[] { "9:00 AM Male: " + DailyLabourRules.WholeNumberMessage }, Errors(page));
            Assert.Equal(0, await env.RowsAsync(D1, GreenBless, null));
        }

        // ---- duplicates ------------------------------------------------------------------------------

        [SkippableFact]
        public async Task SimultaneousSaves_NeverCreateADuplicateTime()
        {
            var env = await OpenAsync();
            try
            {
                foreach (var (area, polyhouse) in new[] { (MainOffice, (int?)Facility5), (GreenBless, (int?)null) })
                {
                    var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(() => env.Repo.SaveAsync(D3, area, polyhouse,
                        new[] { new DailyLabourAttendanceInput(Morning, i, 1, null), new DailyLabourAttendanceInput(Afternoon, i, 1, null) }, Admin))).ToArray();
                    var results = await Task.WhenAll(tasks);
                    Assert.All(results, x => Assert.Equal(DailyLabourAttendanceRepository.SaveOutcome.Saved, x.Outcome));
                    Assert.Equal(1, results.Count(x => x.Created.Contains(Morning)));
                    Assert.Equal(1, results.Count(x => x.Created.Contains(Afternoon)));
                    Assert.Equal(1, await env.RowsAsync(D3, area, polyhouse, Morning));
                    Assert.Equal(1, await env.RowsAsync(D3, area, polyhouse, Afternoon));
                    // Every saved value is in the audit history: 2 Created + one Corrected per real change.
                    var corrections = results.Sum(x => x.Corrected.Count);
                    Assert.Equal(2 + corrections, (await env.Repo.GetChangesAsync(D3, area, polyhouse)).Count);
                }
            }
            finally { await env.CleanupAsync(); }
        }

        // ---- historical manual records ---------------------------------------------------------------

        [SkippableFact]
        public async Task ManualRecords_StayReadable_CountedInTotals_AndBlockAttendanceForTheirDay()
        {
            var env = await OpenAsync();
            try
            {
                await env.Get<DailyLabourCountRepository>().CreateAsync(new DailyLabourCount { LabourDate = D2, AreaId = GreenBless, MaleFullDay = 15, MaleHalfDay = 3, FemaleFullDay = 12, FemaleHalfDay = 2, Remarks = "old" }, MpSup);
                await env.Repo.SaveAsync(D1, GreenBless, null, new[] { new DailyLabourAttendanceInput(Morning, 20, 15, null), new DailyLabourAttendanceInput(Afternoon, 17, 12, null) }, MpSup);

                // Attendance cannot be added where a manual record exists -- page, repository and database.
                var get = await GetAsync(env, MpSup, D2, GreenBless, null);
                Assert.True(get.ManualRecordExists);
                var (r, page) = await PostAsync(env, MpSup, D2, GreenBless, null, 1, 1, 1, 1);
                Assert.IsType<PageResult>(r);
                Assert.Contains(DailyLabourRules.LegacyRecordExistsMessage, Errors(page));
                Assert.Equal(DailyLabourAttendanceRepository.SaveOutcome.ManualRecordExists,
                    (await env.Repo.SaveAsync(D2, GreenBless, null, new[] { new DailyLabourAttendanceInput(Morning, 1, 1, null) }, Admin)).Outcome);
                var ex = await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync(
                    "INSERT INTO dbo.DailyLabourAttendance (LabourDate, AreaId, AttendanceTime, MaleCount, FemaleCount) VALUES (@D, @A, 'AM09', 1, 1)", ("@D", D2), ("@A", GreenBless)));
                Assert.Equal(51725, ex.Number);
                Assert.Equal(0, await env.RowsAsync(D2, GreenBless, null));

                // History: the manual record keeps its entered counts and is counted in the totals.
                var history = await HistoryAsync(env, MpSup, D1, D2);
                Assert.Equal(2, history.Days.Count);
                var manual = history.Days.Single(d => d.IsManual);
                Assert.Equal((D2, 15, 3, 12, 2), (manual.LabourDate, manual.MaleFullDay!.Value, manual.MaleHalfDay!.Value, manual.FemaleFullDay!.Value, manual.FemaleHalfDay!.Value));
                Assert.Null(manual.Male9);
                Assert.Equal((2, 2, 0, 1), (history.Summary.Days, history.Summary.CompleteDays, history.Summary.IncompleteDays, history.Summary.ManualDays));
                Assert.Equal((17 + 15, 3 + 3, 12 + 12, 3 + 2), (history.Summary.MaleFullDay, history.Summary.MaleHalfDay, history.Summary.FemaleFullDay, history.Summary.FemaleHalfDay));
                Assert.Equal(new[] { D2, D1 }, history.ByDate.Select(g => g.Date));   // newest first, one total per date
            }
            finally { await env.CleanupAsync(); }
        }

        [SkippableFact]
        public async Task History_FiltersByDateAreaAndPolyhouse()
        {
            var env = await OpenAsync();
            try
            {
                async Task Save(DateTime d, int a, int? p, int m9, int f9, int? m2 = null, int? f2 = null)
                {
                    var inputs = new List<DailyLabourAttendanceInput> { new(Morning, m9, f9, null) };
                    if (m2.HasValue) inputs.Add(new(Afternoon, m2.Value, f2!.Value, null));
                    await env.Repo.SaveAsync(d, a, p, inputs, Admin);
                }
                await Save(D1, MainOffice, Facility5, 10, 8, 9, 8);
                await Save(D1, MainOffice, Facility3, 7, 5, 7, 6);
                await Save(D2, MainOffice, Facility5, 20, 15, 17, 12);
                await Save(D1, GreenBless, null, 15, 12, 15, 12);
                await Save(D2, Samarth, null, 4, 3);   // incomplete

                var all = await HistoryAsync(env, Admin, D1, D2);
                Assert.Equal((5, 4, 1), (all.Summary.Days, all.Summary.CompleteDays, all.Summary.IncompleteDays));
                Assert.Equal(all.Days.Where(d => d.IsComplete).Sum(d => d.TotalFullDay!.Value), all.Summary.TotalFullDay);
                Assert.Equal(all.Days.Where(d => d.IsComplete).Sum(d => d.TotalHalfDay!.Value), all.Summary.TotalHalfDay);

                Assert.Equal(3, (await HistoryAsync(env, Admin, D1, D1)).Days.Count);
                var mo = await HistoryAsync(env, Admin, D1, D2, MainOffice);
                Assert.True(mo.ShowPolyhouseFilter);
                Assert.Equal(3, mo.Days.Count);
                var f5 = await HistoryAsync(env, Admin, D1, D2, MainOffice, Facility5);
                Assert.Equal(2, f5.Days.Count);
                Assert.All(f5.Days, d => Assert.Equal(Facility5, d.PolyhouseId));
                var gb = await HistoryAsync(env, Admin, D1, D2, GreenBless, Facility5);   // Polyhouse filter does not apply to an Area-wise Area
                Assert.False(gb.ShowPolyhouseFilter);
                Assert.Null(gb.PolyhouseId);
                Assert.Single(gb.Days);
                var reversed = await HistoryAsync(env, Admin, D2, D1);   // From > To is swapped
                Assert.Equal(5, reversed.Days.Count);
            }
            finally { await env.CleanupAsync(); }
        }
    }
}
