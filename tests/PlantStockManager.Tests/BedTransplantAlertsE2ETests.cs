using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.RazorPages.Infrastructure;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using AlertsPage = PlantStockManager.Pages.Production.BedSowing.AlertsModel;
using DashboardPage = PlantStockManager.Pages.ManagementDashboard.IndexModel;

namespace PlantStockManager.Tests
{
    // 2026-11-07 Bed Sowing Alerts end-to-end against a SCRATCH COPY (PlantsIMS2_Scratch_* only; SKIPPED without
    // PSM_SCRATCH_CONNECTION, 2026-10-12_BedSowing.sql or 2026-11-07_BedTransplantDays.sql). Every test sows its OWN
    // ZZTEST varieties and beds through BedSowingRepository and only asserts on those, so other beds in the copy do not
    // matter. Covers: the four lists, Area scope, closed beds, a changed schedule, the dashboard counts = the page lists,
    // the Plant Master field, and Ready Alerts (tray batches) unchanged.
    [Collection("ScratchDb")]
    public class BedTransplantAlertsE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";
        private static bool Any(int _) => true;

        private sealed class StaticMonitor<T> : IOptionsMonitor<T>
        {
            public StaticMonitor(T value) => CurrentValue = value;
            public T CurrentValue { get; }
            public T Get(string? name) => CurrentValue;
            public IDisposable? OnChange(Action<T, string?> listener) => null;
        }

        private sealed class NullTempData : ITempDataProvider
        {
            public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
            public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
        }

        private sealed class Env
        {
            public string Cs = "";
            public IServiceProvider Services = null!;
            public int AreaId, PolyhouseId, OtherAreaId, UserId;
            public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

            public async Task<T> ScalarAsync<T>(string sql, params (string Name, object? Value)[] args)
            {
                using var conn = new SqlConnection(Cs);
                await conn.OpenAsync();
                using var cmd = new SqlCommand("SET QUOTED_IDENTIFIER ON; " + sql, conn);
                foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
                var r = await cmd.ExecuteScalarAsync();
                return r == null || r == DBNull.Value ? default! : (T)Convert.ChangeType(r, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
            }

            public Task ExecAsync(string sql, params (string Name, object? Value)[] args) => ScalarAsync<int>(sql + "; SELECT 1", args);

            public T Page<T>(ClaimsPrincipal user) where T : PageModel
            {
                var http = new DefaultHttpContext { User = user };
                var page = ActivatorUtilities.CreateInstance<T>(Services);
                page.PageContext = new PageContext(new ActionContext(http, new Microsoft.AspNetCore.Routing.RouteData(), new CompiledPageActionDescriptor()))
                {
                    ViewData = new ViewDataDictionary(new Microsoft.AspNetCore.Mvc.ModelBinding.EmptyModelMetadataProvider(), new Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary())
                };
                page.TempData = new TempDataDictionary(http, new NullTempData());
                return page;
            }
        }

        private sealed record Variety(int SpeciesId, int SeedStockId);

        internal static string ScratchConnectionOrSkip()
        {
            var cs = Environment.GetEnvironmentVariable(ConnectionVariable);
            Skip.If(string.IsNullOrWhiteSpace(cs), $"Set {ConnectionVariable} to a {RequiredPrefix}* database connection string to run these tests.");
            return cs!;
        }

        private static async Task<Env> OpenAsync()
        {
            var cs = ScratchConnectionOrSkip();
            using (var conn = new SqlConnection(cs))
            {
                await conn.OpenAsync();
                using var cmd = new SqlCommand("SELECT DB_NAME()", conn);
                var name = (string)(await cmd.ExecuteScalarAsync())!;
                if (!name.StartsWith(RequiredPrefix, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Refusing to run these tests against '{name}'. Only databases named {RequiredPrefix}* are allowed.");
                using var bed = new SqlCommand(BedSowingSchema.ProbeSql, conn);
                Skip.If((int)(await bed.ExecuteScalarAsync())! != 1, "2026-10-12_BedSowing.sql is not applied to this scratch database.");
                using var days = new SqlCommand(BedTransplantScheduleSchema.ProbeSql, conn);
                Skip.If((int)(await days.ExecuteScalarAsync())! != 1, "2026-11-07_BedTransplantDays.sql is not applied to this scratch database.");
            }

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs }).Build());
            services.AddSingleton<DatabaseHelper>();
            services.AddSingleton<AreaAccessService>();
            services.AddSingleton<IOptions<SecurityOptions>>(Options.Create(new SecurityOptions()));
            services.AddSingleton<IOptionsMonitor<SeedlingWorkflowOptions>>(new StaticMonitor<SeedlingWorkflowOptions>(new SeedlingWorkflowOptions()));   // Area scope ON
            services.AddSingleton<SeedlingAreaScope>();
            foreach (var t in typeof(DatabaseHelper).Assembly.GetTypes().Where(t => t.Namespace == "PlantStockManager.Data" && t.IsClass && t.Name.EndsWith("Repository")))
                services.AddScoped(t);
            var env = new Env { Cs = cs, Services = services.BuildServiceProvider().CreateScope().ServiceProvider };
            env.PolyhouseId = await env.ScalarAsync<int>("SELECT TOP 1 PolyhouseId FROM dbo.BedSowingLocations WHERE IsActive = 1 ORDER BY PolyhouseId");
            env.AreaId = await env.ScalarAsync<int>("SELECT AreaId FROM dbo.BedSowingLocations WHERE PolyhouseId = @P", ("@P", env.PolyhouseId));
            env.OtherAreaId = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.Area WHERE Id <> @A ORDER BY Id", ("@A", env.AreaId));
            env.UserId = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.IMSUsers WHERE IsActive = 1 ORDER BY Id");
            Skip.If(env.PolyhouseId == 0 || env.OtherAreaId == 0 || env.UserId == 0, "The scratch copy has no Bed Sowing location, second Area or active user.");
            return env;
        }

        // A fresh ZZTEST variety (Ready Days 30, the given Bed Transplant Days) with its own Main Office seed lot.
        private static async Task<Variety> NewVarietyAsync(Env env, int? transplantDays)
        {
            var name = "ZZTEST BedAlert " + Guid.NewGuid().ToString("N")[..10];
            var speciesId = await env.ScalarAsync<int>(@"INSERT INTO dbo.PlantSpecies (PlantTypeId, Name, ReadyStockDays, BedTransplantDays)
VALUES ((SELECT MIN(Id) FROM dbo.PlantTypes), @N, 30, @D); SELECT CAST(SCOPE_IDENTITY() AS INT)", ("@N", name), ("@D", transplantDays));
            var lot = await env.ScalarAsync<int>(@"INSERT INTO dbo.SeedStock (SpeciesId, AreaId, BatchNo, Unit, PhysicalQuantity, CreatedBy)
VALUES (@S, @A, @B, N'Seeds', 100000, N'bed-alert-test'); SELECT CAST(SCOPE_IDENTITY() AS INT)", ("@S", speciesId), ("@A", env.AreaId), ("@B", "LOT-" + name[^10..]));
            return new Variety(speciesId, lot);
        }

        private static async Task<int> SowAsync(Env env, Variety v, int daysAgo, decimal qty = 1000)
        {
            var bed = new BedSowing
            {
                SpeciesId = v.SpeciesId, SeedStockId = v.SeedStockId, PolyhouseId = env.PolyhouseId, Quantity = qty,
                SowingDate = AppClock.Today.AddDays(-daysAgo), Remarks = "bed alert test", SubmissionToken = Guid.NewGuid()
            };
            var (ok, msg, _) = await env.Get<BedSowingRepository>().CreateAsync(bed, new BedSowingRepository.Actor(env.UserId, "bed-alert-test", false), Any);
            Assert.True(ok, msg);
            return bed.Id;
        }

        private static ClaimsPrincipal User(int? areaId = null, bool full = false, params string[] permissions)
        {
            var claims = new List<Claim> { new("UserId", "999999"), new(ClaimTypes.Name, "bed-alert-viewer") };
            if (areaId.HasValue) claims.Add(new Claim(AreaAccessService.AreaAccessClaimType, areaId.Value.ToString()));
            if (full) claims.Add(new Claim(ClaimsPrincipalSecurityExtensions.FullAccessClaimType, "true"));
            claims.AddRange(permissions.Select(p => new Claim(MinimumAuthorizationLevelHandler.PermissionClaimType, p)));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        }

        private static async Task<AlertsPage> AlertsAsync(Env env, ClaimsPrincipal user, int window = 0)
        {
            var page = env.Page<AlertsPage>(user);
            page.WindowDays = window;
            await page.OnGetAsync();
            return page;
        }

        private static List<int> Ids(IEnumerable<BedTransplantAlert> list, IEnumerable<int> mine) => list.Select(a => a.BedSowingId).Where(mine.Contains).ToList();

        [SkippableFact]
        public async Task Page_ListsOwnBeds_InTheRightCategories()
        {
            var env = await OpenAsync();
            var scheduled = await NewVarietyAsync(env, 20);
            var unscheduled = await NewVarietyAsync(env, null);
            var overdue = await SowAsync(env, scheduled, 25);      // due 5 days ago
            var today = await SowAsync(env, scheduled, 20);        // due today
            var soon = await SowAsync(env, scheduled, 18);         // due in 2 days
            var later = await SowAsync(env, scheduled, 10);        // due in 10 days: not listed (window 3)
            var none = await SowAsync(env, unscheduled, 40);       // no schedule
            var mine = new[] { overdue, today, soon, later, none };

            var page = await AlertsAsync(env, User(env.AreaId, permissions: "Sowing.View"));
            Assert.True(page.SchemaReady);
            Assert.Equal(new[] { overdue }, Ids(page.Alerts.Overdue, mine));
            Assert.Equal(new[] { today }, Ids(page.Alerts.DueToday, mine));
            Assert.Equal(new[] { soon }, Ids(page.Alerts.DueSoon, mine));
            Assert.Equal(new[] { none }, Ids(page.Alerts.NoSchedule, mine));
            var o = page.Alerts.Overdue.Single(a => a.BedSowingId == overdue);
            Assert.Equal(AppClock.Today.AddDays(-5), o.DueDate);
            Assert.Equal(20, o.BedTransplantDays);
            Assert.Equal(-5, page.DaysFromToday(o));
            Assert.Null(page.Alerts.NoSchedule.Single(a => a.BedSowingId == none).DueDate);

            // a wider window brings the later bed in as Due Soon
            var wide = await AlertsAsync(env, User(env.AreaId, permissions: "Sowing.View"), window: 10);
            Assert.Equal(new[] { soon, later }, Ids(wide.Alerts.DueSoon, mine));
        }

        [SkippableFact]
        public async Task Page_IsAreaScoped()
        {
            var env = await OpenAsync();
            var v = await NewVarietyAsync(env, 5);
            var bed = await SowAsync(env, v, 30);

            var own = await AlertsAsync(env, User(env.AreaId, permissions: "Sowing.View"));
            Assert.Contains(own.Alerts.Overdue, a => a.BedSowingId == bed);

            var other = await AlertsAsync(env, User(env.OtherAreaId, permissions: "Sowing.View"));
            Assert.DoesNotContain(other.Alerts.Overdue.Concat(other.Alerts.DueToday).Concat(other.Alerts.DueSoon).Concat(other.Alerts.NoSchedule),
                a => a.AreaId == env.AreaId);

            var admin = await AlertsAsync(env, User(full: true));
            Assert.Contains(admin.Alerts.Overdue, a => a.BedSowingId == bed);
            Assert.False(admin.CanTransplant(admin.Alerts.Overdue.Single(a => a.BedSowingId == bed)));   // admins never transplant
        }

        [SkippableFact]
        public async Task CancelledBed_DropsOff_AndAScheduleChangeAppliesAtOnce()
        {
            var env = await OpenAsync();
            var v = await NewVarietyAsync(env, 30);
            var bed = await SowAsync(env, v, 20);                                                    // due in 10 days
            var user = User(env.AreaId, permissions: "Sowing.View");
            Assert.DoesNotContain((await AlertsAsync(env, user)).Alerts.Overdue, a => a.BedSowingId == bed);

            // the owner shortens the schedule: the open bed is overdue on the next request (days are read live)
            await env.ExecAsync("UPDATE dbo.PlantSpecies SET BedTransplantDays = 15 WHERE Id = @S", ("@S", v.SpeciesId));
            Assert.Contains((await AlertsAsync(env, user)).Alerts.Overdue, a => a.BedSowingId == bed);

            // schedule removed: listed under No schedule, never dated
            await env.ExecAsync("UPDATE dbo.PlantSpecies SET BedTransplantDays = NULL WHERE Id = @S", ("@S", v.SpeciesId));
            Assert.Contains((await AlertsAsync(env, user)).Alerts.NoSchedule, a => a.BedSowingId == bed);

            var (ok, msg) = await env.Get<BedSowingRepository>().CancelAsync(bed, "bed alert test", new BedSowingRepository.Actor(env.UserId, "bed-alert-test", false), Any);
            Assert.True(ok, msg);
            var after = await AlertsAsync(env, user);
            Assert.DoesNotContain(after.Alerts.Overdue.Concat(after.Alerts.DueToday).Concat(after.Alerts.DueSoon).Concat(after.Alerts.NoSchedule),
                a => a.BedSowingId == bed);
        }

        [SkippableFact]
        public async Task Dashboard_BedCounts_EqualThePageLists_AndReadyAlertsUnchanged()
        {
            var env = await OpenAsync();
            var v = await NewVarietyAsync(env, 20);
            await SowAsync(env, v, 30, 500);   // overdue
            await SowAsync(env, v, 26, 700);   // overdue
            await SowAsync(env, v, 20, 300);   // due today
            await SowAsync(env, v, 19, 200);   // due soon
            await SowAsync(env, v, 2, 100);    // not yet

            var dashboard = ActivatorUtilities.CreateInstance<DashboardPage>(env.Services);
            dashboard.SpeciesId = v.SpeciesId;
            await dashboard.OnGetAsync();
            var bed = dashboard.Dashboard.BedTransplantAlerts;
            Assert.True(dashboard.Dashboard.BedTransplantAlertsReady);
            Assert.Equal((2, 1, 1, 0), (bed.Overdue.Count, bed.DueToday.Count, bed.DueSoon.Count, bed.NoSchedule.Count));
            Assert.Equal((1200m, 300m, 200m), (bed.OverdueQuantity, bed.DueTodayQuantity, bed.DueSoonQuantity));

            // the same figures as the Bed Sowing Alerts page (administrator: every Area) for this variety
            var page = await AlertsAsync(env, User(full: true));
            Assert.Equal(page.Alerts.Overdue.Count(a => a.SpeciesId == v.SpeciesId), bed.Overdue.Count);
            Assert.Equal(page.Alerts.DueToday.Count(a => a.SpeciesId == v.SpeciesId), bed.DueToday.Count);
            Assert.Equal(page.Alerts.DueSoon.Count(a => a.SpeciesId == v.SpeciesId), bed.DueSoon.Count);

            // Ready Alerts (tray batches): beds are not sowings, so this variety has none, and the unfiltered tray-batch
            // counts are exactly ClassifyReadyAlert over GetAlertCandidatesAsync, as before.
            Assert.Equal((0, 0, 0), (dashboard.Dashboard.ReadyStock.OverdueCount, dashboard.Dashboard.ReadyStock.ReadyTodayCount, dashboard.Dashboard.ReadyStock.ReadySoonCount));
            var day = AppClock.Today;
            var all = await env.Get<ManagementDashboardRepository>().GetReadyStockSummaryAsync(new ManagementDashboardFilters(), day);
            var candidates = await env.Get<SeedSowingRepository>().GetAlertCandidatesAsync(day.AddDays(SeedSowingRepository.DefaultReadySoonWindowDays));
            var classes = candidates.Select(s => SeedSowingRepository.ClassifyReadyAlert(s.Status, s.ExpectedReadyDate, day, SeedSowingRepository.DefaultReadySoonWindowDays)).ToList();
            Assert.Equal(classes.Count(c => c == "Overdue"), all.OverdueCount);
            Assert.Equal(classes.Count(c => c == "ReadyToday"), all.ReadyTodayCount);
            Assert.Equal(classes.Count(c => c == "ReadySoon"), all.ReadySoonCount);
        }

        [SkippableFact]
        public async Task PlantMaster_SavesBedTransplantDays_OnlyWhenTheFieldWasShown()
        {
            var env = await OpenAsync();
            var v = await NewVarietyAsync(env, 20);
            var name = await env.ScalarAsync<string>("SELECT Name FROM dbo.PlantSpecies WHERE Id = @S", ("@S", v.SpeciesId));
            Task<int?> Days() => env.ScalarAsync<int?>("SELECT BedTransplantDays FROM dbo.PlantSpecies WHERE Id = @S", ("@S", v.SpeciesId));

            async Task<string?> EditAsync(int? days, bool shown)
            {
                var page = env.Page<PlantStockManager.Pages.Admin.PlantModel>(User(full: true));
                page.EditSpeciesId = v.SpeciesId; page.EditSpeciesName = name; page.EditSpeciesScientificName = "";
                page.EditSpeciesReadyStockDays = 30; page.EditSpeciesColor = null;
                page.EditSpeciesBedTransplantDays = days; page.EditSpeciesBedTransplantDaysShown = shown;
                await page.OnPostEditSpeciesAsync();
                return page.TempData["Error"] as string;
            }

            Assert.Null(await EditAsync(28, shown: true));
            Assert.Equal(28, await Days());
            Assert.Null(await EditAsync(null, shown: false));            // a form without the field never clears it
            Assert.Equal(28, await Days());
            Assert.Equal(BedTransplantAlertRules.DaysRangeMessage, await EditAsync(0, shown: true));
            Assert.Equal(BedTransplantAlertRules.DaysRangeMessage, await EditAsync(400, shown: true));
            Assert.Equal(28, await Days());
            Assert.Null(await EditAsync(null, shown: true));             // emptied on purpose = no schedule
            Assert.Null(await Days());

            var listed = await env.Get<PlantSpeciesRepository>().GetSpeciesByPlantType(
                await env.ScalarAsync<int>("SELECT PlantTypeId FROM dbo.PlantSpecies WHERE Id = @S", ("@S", v.SpeciesId)), includeInactive: true);
            Assert.Null(listed.Single(s => s.Id == v.SpeciesId).BedTransplantDays);
            await env.ExecAsync("UPDATE dbo.PlantSpecies SET BedTransplantDays = 12 WHERE Id = @S", ("@S", v.SpeciesId));
            listed = await env.Get<PlantSpeciesRepository>().GetSpeciesByPlantType(
                await env.ScalarAsync<int>("SELECT PlantTypeId FROM dbo.PlantSpecies WHERE Id = @S", ("@S", v.SpeciesId)), includeInactive: true);
            Assert.Equal(12, listed.Single(s => s.Id == v.SpeciesId).BedTransplantDays);
        }
    }
}
