using System.Security.Claims;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Services;
using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.Tests
{
    // Field Work Phase 3 (2026-11-03): day routes end-to-end against a SCRATCH COPY with Phase 1 + 2 applied
    // (PSM_SCRATCH_CONNECTION, PlantsIMS2_Scratch_*; SKIPPED otherwise). Real services and repositories. Users: the first
    // three active users; A and B get the "Field Employee" role in the scratch copy (team view), M acts as manager.
    [Collection("ScratchDb")]
    public class FieldRouteE2ETests
    {
        private sealed record Env(string Cs, FieldDayService Days, FieldCustomerService Customers, FieldVisitService Visits, FieldRouteService Routes,
            int A, int B, int M);

        private static ClaimsPrincipal P(int id, string perm)
            => new(new ClaimsIdentity(new[] { new Claim("UserId", id.ToString()), new Claim(MinimumAuthorizationLevelHandler.PermissionClaimType, perm) }, "T"));

        // ~0.008993 degrees of latitude = 1 km
        private const decimal Lat0 = 12.9716m, Lng0 = 77.5946m, KmLat = 0.008993m;

        private static async Task<Env> OpenAsync()
        {
            var cs = Environment.GetEnvironmentVariable("PSM_SCRATCH_CONNECTION");
            Skip.If(string.IsNullOrWhiteSpace(cs), "Set PSM_SCRATCH_CONNECTION to a PlantsIMS2_Scratch_* database connection string to run these tests.");
            var users = new List<int>();
            using (var conn = new SqlConnection(cs))
            {
                await conn.OpenAsync();
                using (var cmd = new SqlCommand("SELECT DB_NAME()", conn))
                    if (!((string)(await cmd.ExecuteScalarAsync())!).StartsWith("PlantsIMS2_Scratch_", StringComparison.Ordinal))
                        throw new InvalidOperationException("Only PlantsIMS2_Scratch_* databases are allowed.");
                using (var probe = new SqlCommand(FieldCustomerSchema.ProbeSql, conn))
                    Skip.If((int)(await probe.ExecuteScalarAsync())! != 1, "2026-11-03_FieldWorkCustomers.sql is not applied to this scratch copy.");
                using (var cmd = new SqlCommand("SELECT TOP 3 Id FROM dbo.IMSUsers WHERE IsActive = 1 ORDER BY Id", conn))
                using (var r = await cmd.ExecuteReaderAsync())
                    while (await r.ReadAsync()) users.Add(r.GetInt32(0));
                using var setup = new SqlCommand(FieldCustomerE2ETests.Phase2CleanupSql + @"
DELETE e FROM dbo.FieldDayEvents e JOIN dbo.FieldDays d ON d.Id = e.FieldDayId WHERE d.UserId IN (@A, @B, @C);
DELETE FROM dbo.FieldDays WHERE UserId IN (@A, @B, @C);
DELETE FROM dbo.FieldPhotos WHERE OwnerUserId IN (@A, @B, @C);
INSERT INTO dbo.UserRoles (UserId, RoleId, CreatedDate)
SELECT u.Id, r.Id, SYSUTCDATETIME() FROM dbo.IMSUsers u CROSS JOIN dbo.Roles r
WHERE u.Id IN (@A, @B) AND r.Name = N'Field Employee' AND NOT EXISTS (SELECT 1 FROM dbo.UserRoles x WHERE x.UserId = u.Id AND x.RoleId = r.Id);", conn);
                setup.Parameters.AddWithValue("@A", users[0]);
                setup.Parameters.AddWithValue("@B", users[1]);
                setup.Parameters.AddWithValue("@C", users[2]);
                await setup.ExecuteNonQueryAsync();
            }
            var db = new DatabaseHelper(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs }).Build());
            var options = new FieldWorkOptions { PhotoRoot = Path.Combine(Path.GetTempPath(), "psm-fw-e2e") };
            var photos = new FieldPhotoStore(options);
            var dayRepo = new FieldDayRepository(db);
            var customers = new FieldCustomerService(new FieldCustomerRepository(db), photos, options);
            return new Env(cs!, new FieldDayService(dayRepo, photos, options), customers, new FieldVisitService(new FieldVisitRepository(db), customers, photos, options),
                new FieldRouteService(dayRepo, new FieldRouteRepository(db), options), users[0], users[1], users[2]);
        }

        private static byte[] Jpeg() { var b = new byte[512]; b[0] = 0xFF; b[1] = 0xD8; b[2] = 0xFF; b[^2] = 0xFF; b[^1] = 0xD9; return b; }

        private static FieldDayService.ReadingRequest Reading(decimal km, decimal? kmNorth)
            => new(km, kmNorth.HasValue ? Lat0 + kmNorth.Value * KmLat : null, kmNorth.HasValue ? Lng0 : null, kmNorth.HasValue ? 10m : null,
                kmNorth.HasValue ? null : "Phone GPS off", Jpeg(), Guid.NewGuid().ToString());

        private static async Task<int> CustomerAsync(Env env, int user, string name)
        {
            var r = await env.Customers.CreateAsync(P(user, FieldDayRules.DayPermission), new FieldCustomerService.CreateRequest(
                new FieldCustomerRules.CustomerInput(name, null, "9" + Random.Shared.NextInt64(100_000_000, 999_999_999), null, "Farmer", "Hoskote", null, null),
                null, Lat0, Lng0, 10m, null, null, null, true, Guid.NewGuid().ToString()));
            Assert.True(r.Ok, string.Join(" ", r.Errors));
            return r.Id!.Value;
        }

        private static async Task VisitAsync(Env env, int user, int customerId, decimal? kmNorth, string discussed)
        {
            var r = await env.Visits.CreateAsync(P(user, FieldDayRules.DayPermission), new FieldVisitService.VisitRequest(customerId,
                kmNorth.HasValue ? Lat0 + kmNorth.Value * KmLat : null, kmNorth.HasValue ? Lng0 : null, kmNorth.HasValue ? 12m : null,
                kmNorth.HasValue ? null : "Inside cold store", discussed, null, Array.Empty<string?>(), Array.Empty<string?>(), new List<byte[]>(), null,
                null, null, null, Guid.NewGuid().ToString()));
            Assert.True(r.Ok, string.Join(" ", r.Errors));
        }

        [SkippableFact]
        public async Task RoundTripWithFarVisits_IsFlaggedAtEndDay_RouteShowsOrderSkippedPointsAndSuspiciousLegs()
        {
            var env = await OpenAsync();
            await env.Days.RecordConsentAsync(env.A);
            var start = await env.Days.StartDayAsync(env.A, "OwnBike", Reading(1000m, 0m));
            Assert.True(start.Ok, string.Join(" ", start.Errors));
            var c1 = await CustomerAsync(env, env.A, "ZZTEST Route One");
            var c2 = await CustomerAsync(env, env.A, "ZZTEST Route Two");
            var c3 = await CustomerAsync(env, env.A, "ZZTEST Route Three");
            await VisitAsync(env, env.A, c1, 10m, "Showed roses");        // 10 km north, seconds after the start -> suspicious leg
            await VisitAsync(env, env.A, c2, null, "No GPS here");        // skipped
            await VisitAsync(env, env.A, c3, 12m, "Order discussed");

            // back at the start point with only 5 km on the odometer: the start -> end line is ~0 km (Phase 1 would not
            // flag), but the route start -> 10 -> 12 -> 0 is ~24 km -> flagged at End Day
            var end = await env.Days.EndDayAsync(env.A, Reading(1005m, 0m));
            Assert.True(end.Ok, string.Join(" ", end.Errors));
            var view = await env.Routes.GetRouteAsync(P(env.A, FieldDayRules.DayPermission), start.DayId!.Value);
            Assert.NotNull(view);
            Assert.Contains("GPS distance along the route", view!.Day.EndFlagReasons);
            Assert.Equal(FieldDayRules.ReviewFlagged, view.Day.ReviewStatus);
            Assert.True(view.Day.GpsDistanceKm < 0.1m);   // the stored straight start -> end line is unchanged (Phase 1)

            var r = view.Route;
            Assert.Equal(new[] { "Start", "Visit", "Visit", "End" }, r.Points.Select(p => p.Kind.ToString()));
            Assert.Equal(new[] { "ZZTEST Route One", "ZZTEST Route Three" }, r.Points.Where(p => p.Kind == FieldRouteRules.PointKind.Visit).Select(p => p.CustomerName));
            Assert.Equal("Showed roses", r.Points[1].Summary);
            Assert.Equal(3, r.VisitCount);
            Assert.Equal("ZZTEST Route Two", Assert.Single(r.Skipped).CustomerName);
            Assert.InRange(r.RouteKm, 23.9m, 24.1m);
            Assert.Equal(5m, r.OdometerKm);
            Assert.Contains(r.Flags, f => f.Contains("GPS distance along the route"));
            Assert.Contains(r.Legs, l => l.From == 1 && l.To == 2 && l.Suspicious);   // 10 km within seconds
        }

        [SkippableFact]
        public async Task Visibility_OwnDayOnly_ManagerSeesAll_TeamViewPutsNotStartedFirst()
        {
            var env = await OpenAsync();
            await env.Days.RecordConsentAsync(env.A);
            var start = await env.Days.StartDayAsync(env.A, "OwnCar", Reading(500m, 0m));
            Assert.True(start.Ok);
            var c = await CustomerAsync(env, env.A, "ZZTEST Team Customer");
            await VisitAsync(env, env.A, c, 3m, "Price list");
            var dayId = start.DayId!.Value;

            var a = P(env.A, FieldDayRules.DayPermission);
            var b = P(env.B, FieldDayRules.DayPermission);
            var m = P(env.M, FieldDayRules.ManagePermission);
            Assert.NotNull(await env.Routes.GetRouteAsync(a, dayId));
            Assert.Null(await env.Routes.GetRouteAsync(b, dayId));                       // another employee's day
            Assert.NotNull(await env.Routes.GetRouteAsync(m, dayId));
            Assert.False((await env.Routes.GetRouteForDateAsync(b, env.A, AppClock.Today)).Allowed);
            var own = await env.Routes.GetRouteForDateAsync(a, null, AppClock.Today);
            Assert.True(own.Allowed);
            Assert.Equal(dayId, own.View!.Day.Id);
            var managerView = await env.Routes.GetRouteForDateAsync(m, env.A, AppClock.Today);
            Assert.True(managerView.Allowed && managerView.View!.Day.Id == dayId);
            Assert.Null((await env.Routes.GetRouteForDateAsync(b, null, AppClock.Today)).View);   // B has no day: allowed, empty

            Assert.Empty(await env.Routes.GetTeamAsync(a, AppClock.Today));                // employees get nothing
            var team = await env.Routes.GetTeamAsync(m, AppClock.Today);
            var rowA = team.Single(t => t.UserId == env.A);
            var rowB = team.Single(t => t.UserId == env.B);
            Assert.True(rowB.NotStarted);
            Assert.Equal(FieldDayRules.StatusOpen, rowA.Status);
            Assert.Equal(1, rowA.Visits);
            Assert.InRange(rowA.RouteKm!.Value, 2.9m, 3.1m);
            Assert.Null(rowA.OdometerKm);
            // every "Not started" row comes before every started row
            var firstStarted = team.FindIndex(t => !t.NotStarted);
            Assert.True(firstStarted < 0 || team.Skip(firstStarted).All(t => !t.NotStarted));
            Assert.True(team.IndexOf(rowB) < team.IndexOf(rowA));
        }
    }
}
