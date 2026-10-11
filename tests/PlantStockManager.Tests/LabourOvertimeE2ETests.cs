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
using DetailsPage = PlantStockManager.Pages.Production.Labour.OvertimeDetailsModel;
using HistoryPage = PlantStockManager.Pages.Production.Labour.OvertimeHistoryModel;
using OvertimePage = PlantStockManager.Pages.Production.Labour.OvertimeModel;

namespace PlantStockManager.Tests
{
    // Labour Overtime -- end-to-end: the REAL Overtime / Overtime Details / Overtime History pages
    // and LabourOvertimeRepository, with REAL login claims (UserClaimsFactory -> UserRoles.AreaId)
    // and the REAL SubmissionTokenGuard, against a SCRATCH COPY of the test database that has
    // Database/Migrations/2026-10-06_LabourOvertime.sql applied (PSM_SCRATCH_CONNECTION; the
    // database name must start with PlantsIMS2_Scratch_). Every test writes only dates in 2001
    // (plus "today" rows it removes again) and cleans up after itself.
    //
    // Users / Areas on the scratch copy:
    //   6   Akshay   Sowing Supervisor        -> Area 2   Main-Office-Areas (MainOffice; Polyhouses 2 Facility-5, 3 Facility-3)
    //   110 Vasant   Mother Plant Supervisor  -> Area 127 Green Bless Nursery (Area-wise; has Polyhouse 14)
    //   15  Rahul    Booking Executive        -> Area 2   (no Labour permission)
    //   21  Prajwal  System Administrator     -> every Area
    [Collection("ScratchDb")]
    public class LabourOvertimeE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";
        private const int MpSup = 110, BookingExec = 15, Admin = 21;
        // 2026-10-10: looked up per run (FixtureUsers) -- user 6 is no longer a Sowing Supervisor in the local data
        private static int SowingSup = 6;
        private const int MainOffice = 2, GreenBless = 127, Samarth = 123, Outlet = 124;
        private const int Facility5 = 2, Facility3 = 3, GreenBlessP1 = 14;
        private static readonly DateTime D1 = new(2001, 3, 1), D2 = new(2001, 3, 2), D3 = new(2001, 4, 10);

        private static TimeOnly T(string hhmm) => TimeOnly.ParseExact(hhmm, "HH:mm");

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
            public LabourOvertimeRepository Repo => Get<LabourOvertimeRepository>();

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

            // Entries first (the audit trigger records the deletion), then their audit rows
            // (allowed once the entry is gone).
            public async Task CleanupAsync(DateTime? alsoDate = null)
            {
                const string range = "LabourDate >= '2001-01-01' AND LabourDate < '2002-01-01'";
                await ExecAsync($"DELETE FROM dbo.LabourOvertime WHERE {range}");
                await ExecAsync($"DELETE FROM dbo.LabourOvertimeHistory WHERE {range}");
                if (alsoDate.HasValue)
                {
                    await ExecAsync("DELETE FROM dbo.LabourOvertime WHERE LabourDate = @D AND WorkDescription LIKE 'ZZTEST%'", ("@D", alsoDate.Value.Date));
                    await ExecAsync("DELETE h FROM dbo.LabourOvertimeHistory h WHERE h.LabourDate = @D AND NOT EXISTS (SELECT 1 FROM dbo.LabourOvertime o WHERE o.Id = h.OvertimeId)", ("@D", alsoDate.Value.Date));
                }
            }

            public Task<int> RowsAsync(DateTime date, int? areaId = null) => ScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.LabourOvertime WHERE LabourDate = @D AND (@A IS NULL OR AreaId = @A)", ("@D", date), ("@A", areaId));

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
            services.AddSingleton<SubmissionTokenGuard>();
            services.AddSingleton<IOptions<SecurityOptions>>(Options.Create(new SecurityOptions { FullAccessRoleNames = new[] { "System Administrator" } }));
            services.AddScoped<UserClaimsFactory>();
            foreach (var t in typeof(DatabaseHelper).Assembly.GetTypes().Where(t => t.Namespace == "PlantStockManager.Data" && t.IsClass && t.Name.EndsWith("Repository")))
                services.AddScoped(t);
            var env = new Env { ConnectionString = cs!, Provider = services.BuildServiceProvider() };
            Skip.If(await env.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID('dbo.LabourOvertime') IS NULL THEN 0 ELSE 1 END") == 0,
                "The scratch copy does not have 2026-10-06_LabourOvertime.sql applied.");
            await env.CleanupAsync();
            SowingSup = await env.ScalarAsync<int>(FixtureUsers.MainOfficeSowingSupervisorSql);
            Skip.If(SowingSup == 0, "No active Sowing Supervisor is assigned to the Main Office Area in this copy.");
            return env;
        }

        // Posts the Add form exactly as the page does, with a fresh token from a GET (unless given).
        private static async Task<(IActionResult Result, OvertimePage Page)> AddAsync(Env env, int userId, DateTime? date, int? areaId, int? polyhouseId,
            int? male, int? female, string? start, string? end, string? work = "Polyhouse cleaning and maintenance", string? remarks = null, string? token = null)
        {
            var page = env.Page<OvertimePage>(await env.LoginAsync(userId));
            page.LabourDate = date; page.AreaId = areaId; page.PolyhouseId = polyhouseId;
            page.MaleWorkers = male; page.FemaleWorkers = female;
            page.StartTime = start == null ? null : T(start); page.EndTime = end == null ? null : T(end);
            page.WorkDescription = work; page.Remarks = remarks;
            page.SubmitToken = token ?? SubmissionTokenGuard.NewToken();
            return (await page.OnPostAsync(), page);
        }

        private static async Task<(IActionResult Result, OvertimePage Page)> EditAsync(Env env, int userId, int id,
            int? male, int? female, string? start, string? end, string? work, string? remarks = null, Action<OvertimePage>? before = null)
        {
            var page = env.Page<OvertimePage>(await env.LoginAsync(userId));
            page.Id = id;
            page.MaleWorkers = male; page.FemaleWorkers = female;
            page.StartTime = start == null ? null : T(start); page.EndTime = end == null ? null : T(end);
            page.WorkDescription = work; page.Remarks = remarks;
            page.SubmitToken = SubmissionTokenGuard.NewToken();
            before?.Invoke(page);
            return (await page.OnPostAsync(), page);
        }

        private static async Task<HistoryPage> HistoryAsync(Env env, int userId, DateTime? from, DateTime? to, int? area = null, int? polyhouse = null, string? groupBy = null, DateTime? date = null)
        {
            var page = env.Page<HistoryPage>(await env.LoginAsync(userId), "GET");
            page.FromDate = from; page.ToDate = to; page.AreaId = area; page.PolyhouseId = polyhouse; page.GroupBy = groupBy; page.Date = date;
            await page.OnGetAsync();
            return page;
        }

        private static string[] Errors(PageModel page) => page.ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).ToArray();

        private static int SavedId(IActionResult result)
        {
            var redirect = Assert.IsType<RedirectToPageResult>(result);
            Assert.Equal("/Production/Labour/OvertimeDetails", redirect.PageName);
            return (int)redirect.RouteValues!["id"]!;
        }

        // ---- the approved example ---------------------------------------------------------------------

        [SkippableFact]
        public async Task ApprovedExample_DurationAndWorkerHours_AreComputedByTheDatabase()
        {
            var env = await OpenAsync();
            try
            {
                var (r, page) = await AddAsync(env, SowingSup, D1, MainOffice, Facility5, 4, 2, "17:30", "20:00", remarks: "  after rain  ");
                var id = SavedId(r);
                Assert.Equal("Overtime saved: 2h 30m, 15 worker-hours (01-03-2001, Main-Office-Areas / Facility-5).", page.TempData["Success"]);

                var e = (await env.Repo.GetByIdAsync(id))!;
                Assert.Equal((150, 600, 300), (e.DurationMinutes, e.MaleWorkerMinutes, e.FemaleWorkerMinutes));
                Assert.Equal(("2h 30m", 10m, 5m, 15m), (e.Duration, e.MaleWorkerHours, e.FemaleWorkerHours, e.TotalWorkerHours));
                Assert.Equal((T("17:30"), T("20:00"), "Polyhouse cleaning and maintenance", "after rain", (int?)SowingSup), (e.StartTime, e.EndTime, e.WorkDescription, e.Remarks, e.CreatedById));
                Assert.Null(e.ModifiedDate);

                var details = env.Page<DetailsPage>(await env.LoginAsync(SowingSup), "GET");
                details.Id = id;
                Assert.IsType<PageResult>(await details.OnGetAsync());
                Assert.Equal(id, details.Entry.Id);
                Assert.Equal("Created", Assert.Single(details.Changes).Action);
            }
            finally { await env.CleanupAsync(); }
        }

        // ---- separate entries, overlapping allowed, never merged ------------------------------------

        [SkippableFact]
        public async Task SeveralEntries_SameDateAndLocation_IncludingOverlapping_StaySeparate()
        {
            var env = await OpenAsync();
            try
            {
                var a = SavedId((await AddAsync(env, MpSup, D1, GreenBless, null, 4, 2, "17:30", "20:00", "Polyhouse cleaning")).Result);
                var b = SavedId((await AddAsync(env, MpSup, D1, GreenBless, null, 3, 0, "18:00", "19:00", "Watering -- other group")).Result);   // overlaps a
                var c = SavedId((await AddAsync(env, MpSup, D1, GreenBless, null, 4, 2, "17:30", "20:00", "Polyhouse cleaning")).Result);       // same as a, new form: a separate entry
                Assert.Equal(3, new[] { a, b, c }.Distinct().Count());
                Assert.Equal(3, await env.RowsAsync(D1, GreenBless));

                var history = await HistoryAsync(env, MpSup, D1, D1);
                Assert.Equal(3, history.Total.Entries);
                Assert.Equal(150 + 60 + 150, history.Total.DurationMinutes);   // durations are added up, never merged
                Assert.Equal((23m, 10m, 33m), (history.Total.MaleWorkerHours, history.Total.FemaleWorkerHours, history.Total.TotalWorkerHours));
            }
            finally { await env.CleanupAsync(); }
        }

        [SkippableFact]
        public async Task DuplicateSubmission_OfTheSameForm_IsStoppedByTheToken()
        {
            var env = await OpenAsync();
            try
            {
                var token = SubmissionTokenGuard.NewToken();
                SavedId((await AddAsync(env, SowingSup, D1, MainOffice, Facility5, 4, 2, "17:30", "20:00", token: token)).Result);
                var (r, page) = await AddAsync(env, SowingSup, D1, MainOffice, Facility5, 4, 2, "17:30", "20:00", token: token);   // double click / retry
                Assert.Equal("/Production/Labour/OvertimeHistory", Assert.IsType<RedirectToPageResult>(r).PageName);
                Assert.Equal(LabourOvertimeRules.AlreadySubmittedMessage, page.TempData["Error"]);
                Assert.Equal(1, await env.RowsAsync(D1));

                // Simultaneous submits of one form: exactly one entry.
                var token2 = SubmissionTokenGuard.NewToken();
                var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => AddAsync(env, SowingSup, D2, MainOffice, Facility5, 1, 1, "18:00", "19:00", token: token2))));
                Assert.Equal(1, results.Count(x => x.Result is RedirectToPageResult rp && rp.PageName == "/Production/Labour/OvertimeDetails"));
                Assert.Equal(1, await env.RowsAsync(D2));

                // A rejected form gets a fresh token, so the corrected form can still be sent once.
                var (bad, badPage) = await AddAsync(env, SowingSup, D2, MainOffice, Facility5, 1, 1, "19:00", "18:00");
                Assert.IsType<PageResult>(bad);
                Assert.False(string.IsNullOrEmpty(badPage.SubmitToken));
            }
            finally { await env.CleanupAsync(); }
        }

        // ---- validation --------------------------------------------------------------------------------

        [SkippableFact]
        public async Task InvalidOvertime_IsRejected_NothingSaved()
        {
            var env = await OpenAsync();
            var (r, page) = await AddAsync(env, MpSup, D1, GreenBless, null, 4, 2, "20:00", "17:30");
            Assert.Contains(LabourOvertimeRules.EndAfterStartMessage, Errors(page));
            (r, page) = await AddAsync(env, MpSup, D1, GreenBless, null, 4, 2, "17:30", "17:30");
            Assert.Contains(LabourOvertimeRules.EndAfterStartMessage, Errors(page));
            (r, page) = await AddAsync(env, MpSup, D1, GreenBless, null, 0, 0, "17:30", "20:00");
            Assert.Contains(LabourOvertimeRules.AtLeastOneWorkerMessage, Errors(page));
            (r, page) = await AddAsync(env, MpSup, D1, GreenBless, null, -1, 2, "17:30", "20:00");
            Assert.Contains("Male Workers cannot be negative.", Errors(page));
            (r, page) = await AddAsync(env, MpSup, D1, GreenBless, null, 1, 501, "17:30", "20:00");
            Assert.Contains("Female Workers cannot be more than 500.", Errors(page));
            (r, page) = await AddAsync(env, MpSup, AppClock.Today.AddDays(1), GreenBless, null, 4, 2, "17:30", "20:00");
            Assert.Contains(DailyLabourRules.FutureDateMessage, Errors(page));
            (r, page) = await AddAsync(env, MpSup, D1, GreenBless, null, 4, 2, "17:30", "20:00", work: "  ");
            Assert.Contains(LabourOvertimeRules.DescriptionRequiredMessage, Errors(page));
            (r, page) = await AddAsync(env, MpSup, D1, GreenBless, null, 4, 2, null, "20:00");
            Assert.Contains(LabourOvertimeRules.TimesRequiredMessage, Errors(page));
            Assert.IsType<PageResult>(r);
            Assert.Equal(0, await env.RowsAsync(D1));
            Assert.Equal(0, await env.RowsAsync(AppClock.Today.AddDays(1)));

            // The database is the final guard for every value rule.
            var ex = await Assert.ThrowsAsync<SqlException>(() => env.Repo.CreateAsync(D1, GreenBless, null, new LabourOvertimeInput(4, 2, T("20:00"), T("17:30"), "x", null), Admin));
            Assert.Equal(547, ex.Number);
            ex = await Assert.ThrowsAsync<SqlException>(() => env.Repo.CreateAsync(D1, GreenBless, null, new LabourOvertimeInput(0, 0, T("17:30"), T("20:00"), "x", null), Admin));
            Assert.Equal(547, ex.Number);
            ex = await Assert.ThrowsAsync<SqlException>(() => env.Repo.CreateAsync(AppClock.Today.AddDays(2), GreenBless, null, new LabourOvertimeInput(1, 0, T("17:30"), T("20:00"), "x", null), Admin));
            Assert.Equal(547, ex.Number);
            Assert.Equal(0, await env.RowsAsync(D1));
        }

        [SkippableFact]
        public async Task TodayIsAllowed_InIndiaTime()
        {
            var env = await OpenAsync();
            var today = AppClock.Today;
            try
            {
                var id = SavedId((await AddAsync(env, MpSup, today, GreenBless, null, 1, 1, "17:30", "18:30", work: "ZZTEST today")).Result);
                Assert.Equal(today, (await env.Repo.GetByIdAsync(id))!.LabourDate);
                var get = env.Page<OvertimePage>(await env.LoginAsync(MpSup), "GET");
                await get.OnGetAsync();
                Assert.Equal(today, get.LabourDate);   // the form defaults to India's today
            }
            finally { await env.CleanupAsync(today); }
        }

        // ---- location rule and authorization ---------------------------------------------------------

        [SkippableFact]
        public async Task Location_AreaAndPolyhouseAuthorization_TamperingRejected()
        {
            var env = await OpenAsync();
            try
            {
                // Location rule.
                var (r, page) = await AddAsync(env, SowingSup, D1, MainOffice, null, 1, 1, "17:30", "20:00");
                Assert.Contains(DailyLabourRules.PolyhouseRequiredMessage, Errors(page));
                (r, page) = await AddAsync(env, MpSup, D1, GreenBless, GreenBlessP1, 1, 1, "17:30", "20:00");
                Assert.Contains(DailyLabourRules.PolyhouseNotAllowedMessage, Errors(page));
                (r, page) = await AddAsync(env, Admin, D1, Outlet, null, 1, 1, "17:30", "20:00");
                Assert.Contains(DailyLabourRules.OutletNotAllowedMessage, Errors(page));
                // Tampered Area: another Area's id.
                (r, page) = await AddAsync(env, MpSup, D1, MainOffice, Facility5, 1, 1, "17:30", "20:00");
                Assert.Contains(DailyLabourRules.NotAuthorizedMessage, Errors(page));
                (r, page) = await AddAsync(env, SowingSup, D1, GreenBless, null, 1, 1, "17:30", "20:00");
                Assert.Contains(DailyLabourRules.NotAuthorizedMessage, Errors(page));
                // Tampered Polyhouse: a Polyhouse of another Area / one that does not exist.
                (r, page) = await AddAsync(env, SowingSup, D1, MainOffice, GreenBlessP1, 1, 1, "17:30", "20:00");
                Assert.Contains(DailyLabourRules.PolyhouseNotInAreaMessage, Errors(page));
                (r, page) = await AddAsync(env, SowingSup, D1, MainOffice, 999999, 1, 1, "17:30", "20:00");
                Assert.IsType<PageResult>(r);
                Assert.Equal(0, await env.RowsAsync(D1));

                // The trigger is the final guard, whatever the caller.
                var ex = await Assert.ThrowsAsync<SqlException>(() => env.Repo.CreateAsync(D1, MainOffice, GreenBlessP1, new LabourOvertimeInput(1, 1, T("17:30"), T("20:00"), "x", null), Admin));
                Assert.Equal(51713, ex.Number);
                ex = await Assert.ThrowsAsync<SqlException>(() => env.Repo.CreateAsync(D1, Outlet, null, new LabourOvertimeInput(1, 1, T("17:30"), T("20:00"), "x", null), Admin));
                Assert.Equal(51714, ex.Number);

                // Another Area's entry: no details, no edit (GET or POST), not in the history.
                var mainOfficeEntry = SavedId((await AddAsync(env, SowingSup, D1, MainOffice, Facility5, 4, 2, "17:30", "20:00")).Result);
                var details = env.Page<DetailsPage>(await env.LoginAsync(MpSup), "GET");
                details.Id = mainOfficeEntry;
                Assert.IsType<ForbidResult>(await details.OnGetAsync());
                var edit = env.Page<OvertimePage>(await env.LoginAsync(MpSup), "GET");
                edit.Id = mainOfficeEntry;
                Assert.IsType<ForbidResult>(await edit.OnGetAsync());
                (r, _) = await EditAsync(env, MpSup, mainOfficeEntry, 99, 0, "17:30", "20:00", "hijack");
                Assert.IsType<ForbidResult>(r);
                Assert.Equal(4, (await env.Repo.GetByIdAsync(mainOfficeEntry))!.MaleWorkers);
                var mpHistory = await HistoryAsync(env, MpSup, D1, D1, MainOffice, Facility5);
                Assert.Null(mpHistory.AreaId);
                Assert.Empty(mpHistory.Entries);
                Assert.DoesNotContain(mpHistory.AreaOptions, a => a.Id == MainOffice);
                Assert.Single((await HistoryAsync(env, Admin, D1, D1)).Entries);

                // Permissions: the existing Labour permissions decide who may open the pages.
                var booking = await env.LoginAsync(BookingExec);
                Assert.False(booking.HasPermission("Labour.Enter"));
                Assert.False(booking.HasPermission("Labour.View"));
                Assert.True((await env.LoginAsync(SowingSup)).HasPermission("Labour.Enter"));
            }
            finally { await env.CleanupAsync(); }
        }

        // ---- editing and the audit history -----------------------------------------------------------

        [SkippableFact]
        public async Task Edit_RecalculatesInTheDatabase_IsAudited_PostedLocationIgnored()
        {
            var env = await OpenAsync();
            try
            {
                var id = SavedId((await AddAsync(env, SowingSup, D1, MainOffice, Facility5, 4, 2, "17:30", "20:00")).Result);

                // The page loads the entry; posted date/location are ignored (identity of the entry).
                var get = env.Page<OvertimePage>(await env.LoginAsync(SowingSup), "GET");
                get.Id = id;
                Assert.IsType<PageResult>(await get.OnGetAsync());
                Assert.Equal((4, 2, T("17:30"), T("20:00")), (get.MaleWorkers!.Value, get.FemaleWorkers!.Value, get.StartTime!.Value, get.EndTime!.Value));

                var (r, page) = await EditAsync(env, Admin, id, 4, 2, "17:30", "20:30", "Polyhouse cleaning and maintenance", "extended",
                    before: p => { p.LabourDate = D2; p.AreaId = MainOffice; p.PolyhouseId = Facility3; });
                Assert.Equal(id, SavedId(r));
                Assert.StartsWith("Overtime corrected", (string)page.TempData["Success"]!);
                var e = (await env.Repo.GetByIdAsync(id))!;
                Assert.Equal((D1, (int?)Facility5, 180, 720, 360, (int?)Admin), (e.LabourDate, e.PolyhouseId, e.DurationMinutes, e.MaleWorkerMinutes, e.FemaleWorkerMinutes, e.ModifiedById));
                Assert.Equal((12m, 6m), (e.MaleWorkerHours, e.FemaleWorkerHours));
                Assert.Equal(0, await env.RowsAsync(D2));

                var changes = await env.Repo.GetChangesAsync(id);
                Assert.Equal(new[] { "Created", "Corrected" }, changes.Select(c => c.Action));
                var c = changes[1];
                Assert.Equal((T("20:00"), T("20:30"), (string?)null, "extended"), (c.OldEndTime!.Value, c.NewEndTime!.Value, c.OldRemarks, c.NewRemarks));

                // Same values again: nothing written, no audit row.
                (r, page) = await EditAsync(env, SowingSup, id, 4, 2, "17:30", "20:30", "Polyhouse cleaning and maintenance", "extended");
                Assert.StartsWith("No changes", (string)page.TempData["Success"]!);
                Assert.Equal(2, (await env.Repo.GetChangesAsync(id)).Count);

                // Invalid corrections change nothing.
                (r, page) = await EditAsync(env, SowingSup, id, 0, 0, "17:30", "20:30", "x");
                Assert.Contains(LabourOvertimeRules.AtLeastOneWorkerMessage, Errors(page));
                (r, page) = await EditAsync(env, SowingSup, id, 4, 2, "21:00", "20:30", "x");
                Assert.Contains(LabourOvertimeRules.EndAfterStartMessage, Errors(page));
                Assert.Equal(180, (await env.Repo.GetByIdAsync(id))!.DurationMinutes);

                // The audit history itself cannot be edited or removed while the entry exists.
                var ex = await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync("UPDATE dbo.LabourOvertimeHistory SET NewMaleWorkers = 1 WHERE Id = @I", ("@I", c.Id)));
                Assert.Equal(51746, ex.Number);
                ex = await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync("DELETE FROM dbo.LabourOvertimeHistory WHERE Id = @I", ("@I", c.Id)));
                Assert.Equal(51747, ex.Number);
            }
            finally { await env.CleanupAsync(); }
        }

        // ---- history: filters and summaries ----------------------------------------------------------

        [SkippableFact]
        public async Task History_DailyMonthlyAreaPolyhouseSummaries_AndFilters()
        {
            var env = await OpenAsync();
            try
            {
                async Task Add(DateTime d, int a, int? p, int m, int f, string s, string e) =>
                    await env.Repo.CreateAsync(d, a, p, new LabourOvertimeInput(m, f, T(s), T(e), "work", null), Admin);
                await Add(D1, MainOffice, Facility5, 4, 2, "17:30", "20:00");   // 150: 10 / 5
                await Add(D1, MainOffice, Facility3, 2, 0, "18:00", "19:00");   // 60:  2 / 0
                await Add(D2, MainOffice, Facility5, 1, 1, "17:00", "17:45");   // 45:  0.75 / 0.75
                await Add(D1, GreenBless, null, 3, 3, "17:00", "19:00");        // 120: 6 / 6
                await Add(D3, Samarth, null, 5, 0, "18:00", "18:30");           // 30 (April): 2.5 / 0

                var all = await HistoryAsync(env, Admin, D1, D3);
                Assert.Equal((5, 405, "6h 45m"), (all.Total.Entries, all.Total.DurationMinutes, all.Total.Duration));
                Assert.Equal((21.25m, 11.75m, 33m), (all.Total.MaleWorkerHours, all.Total.FemaleWorkerHours, all.Total.TotalWorkerHours));
                Assert.Equal((15, 6), (all.Total.MaleWorkers, all.Total.FemaleWorkers));

                var daily = all.Summary.ToDictionary(s => s.Key);   // default: by Date
                Assert.Equal(new[] { "10-04-2001", "02-03-2001", "01-03-2001" }, all.Summary.Select(s => s.Key));
                Assert.Equal((3, 330, 18m, 11m), (daily["01-03-2001"].Entries, daily["01-03-2001"].DurationMinutes, daily["01-03-2001"].MaleWorkerHours, daily["01-03-2001"].FemaleWorkerHours));

                var monthly = (await HistoryAsync(env, Admin, D1, D3, groupBy: "Month")).Summary.ToDictionary(s => s.Key);
                Assert.Equal((4, 375, 18.75m, 11.75m), (monthly["March 2001"].Entries, monthly["March 2001"].DurationMinutes, monthly["March 2001"].MaleWorkerHours, monthly["March 2001"].FemaleWorkerHours));
                Assert.Equal((1, 2.5m), (monthly["April 2001"].Entries, monthly["April 2001"].TotalWorkerHours));

                var byArea = (await HistoryAsync(env, Admin, D1, D3, groupBy: "Area")).Summary.ToDictionary(s => s.Key);
                Assert.Equal((3, 18.5m), (byArea["Main-Office-Areas"].Entries, byArea["Main-Office-Areas"].TotalWorkerHours));
                var byPolyhouse = (await HistoryAsync(env, Admin, D1, D3, groupBy: "Polyhouse")).Summary.ToDictionary(s => s.Key);
                Assert.Equal((2, 16.5m), (byPolyhouse["Main-Office-Areas / Facility-5"].Entries, byPolyhouse["Main-Office-Areas / Facility-5"].TotalWorkerHours));
                Assert.Equal(12m, byPolyhouse["Green Bless Nursery (Area-wise)"].TotalWorkerHours);
                Assert.Equal("Date", (await HistoryAsync(env, Admin, D1, D3, groupBy: "Nonsense")).GroupBy);

                // Filters: one date, a range, an Area, a Polyhouse.
                var oneDay = await HistoryAsync(env, Admin, D1, D3, date: D2);
                Assert.Equal((D2, D2, 1), (oneDay.FromDate!.Value, oneDay.ToDate!.Value, oneDay.Total.Entries));
                Assert.Equal(4, (await HistoryAsync(env, Admin, D1, D2)).Total.Entries);
                var mo = await HistoryAsync(env, Admin, D1, D3, MainOffice);
                Assert.True(mo.ShowPolyhouseFilter);
                Assert.Equal(3, mo.Total.Entries);
                Assert.Equal(2, (await HistoryAsync(env, Admin, D1, D3, MainOffice, Facility5)).Total.Entries);
                var gb = await HistoryAsync(env, Admin, D1, D3, GreenBless, Facility5);   // Polyhouse filter does not apply to an Area-wise Area
                Assert.Null(gb.PolyhouseId);
                Assert.Equal(1, gb.Total.Entries);
                Assert.Equal(5, (await HistoryAsync(env, Admin, D3, D1)).Total.Entries);   // From > To is swapped

                // Area scope: a supervisor's totals cover only their own Areas.
                var sowing = await HistoryAsync(env, SowingSup, D1, D3);
                Assert.Equal(3, sowing.Total.Entries);
                Assert.All(sowing.Entries, e => Assert.Equal(MainOffice, e.AreaId));
                var mp = await HistoryAsync(env, MpSup, D1, D3);
                Assert.Equal((1, 12m), (mp.Total.Entries, mp.Total.TotalWorkerHours));
            }
            finally { await env.CleanupAsync(); }
        }
    }
}
