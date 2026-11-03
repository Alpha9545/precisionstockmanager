using System.Security.Claims;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Services;
using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.Tests
{
    // Field Work Phase 1 (2026-11-03_FieldWorkDays.sql), end-to-end against a SCRATCH COPY only (PSM_SCRATCH_CONNECTION,
    // database name PlantsIMS2_Scratch_*; SKIPPED otherwise or when the migration is not applied). The real service,
    // repository, CHECK constraints and trigger. Each test first deletes the Field Work rows of the two real users it
    // acts as (scratch-only), so the tests can be re-run on the same copy.
    [Collection("ScratchDb")]
    public class FieldWorkE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";

        private sealed record Env(string Cs, FieldDayService Service, FieldDayRepository Repo, FieldPhotoStore Photos, int Employee, int Other, int Manager);

        private static async Task<Env> OpenAsync()
        {
            var cs = Environment.GetEnvironmentVariable(ConnectionVariable);
            Skip.If(string.IsNullOrWhiteSpace(cs), $"Set {ConnectionVariable} to a {RequiredPrefix}* database connection string to run these tests.");
            var users = new List<int>();
            using (var conn = new SqlConnection(cs))
            {
                await conn.OpenAsync();
                using (var cmd = new SqlCommand("SELECT DB_NAME()", conn))
                {
                    var name = (string)(await cmd.ExecuteScalarAsync())!;
                    if (!name.StartsWith(RequiredPrefix, StringComparison.Ordinal))
                        throw new InvalidOperationException($"Refusing to run these tests against '{name}'. Only databases named {RequiredPrefix}* are allowed.");
                }
                using (var probe = new SqlCommand(FieldWorkSchema.ProbeSql, conn))
                    Skip.If((int)(await probe.ExecuteScalarAsync())! != 1, "2026-11-03_FieldWorkDays.sql is not applied to this scratch copy.");
                using (var cmd = new SqlCommand("SELECT TOP 3 Id FROM dbo.IMSUsers WHERE IsActive = 1 ORDER BY Id", conn))
                using (var r = await cmd.ExecuteReaderAsync())
                    while (await r.ReadAsync())
                        users.Add(r.GetInt32(0));
                Skip.If(users.Count < 3, "The scratch copy needs 3 active users.");
                const string cleanup = FieldCustomerE2ETests.Phase2CleanupSql + @"
DELETE e FROM dbo.FieldDayEvents e JOIN dbo.FieldDays d ON d.Id = e.FieldDayId WHERE d.UserId IN (@A, @B, @C);
DELETE FROM dbo.FieldDays WHERE UserId IN (@A, @B, @C);
DELETE FROM dbo.FieldPhotos WHERE OwnerUserId IN (@A, @B, @C);
DELETE FROM dbo.FieldWorkConsents WHERE UserId IN (@A, @B, @C);";
                using (var cmd = new SqlCommand(cleanup, conn))
                {
                    cmd.Parameters.AddWithValue("@A", users[0]);
                    cmd.Parameters.AddWithValue("@B", users[1]);
                    cmd.Parameters.AddWithValue("@C", users[2]);
                    await cmd.ExecuteNonQueryAsync();
                }
            }
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs }).Build();
            var options = new FieldWorkOptions { PhotoRoot = Path.Combine(Path.GetTempPath(), "psm-fw-e2e"), DistanceMismatchSlackKm = 2m };
            var repo = new FieldDayRepository(new DatabaseHelper(config));
            var photos = new FieldPhotoStore(options);
            return new Env(cs!, new FieldDayService(repo, photos, options), repo, photos, users[0], users[1], users[2]);
        }

        private static ClaimsPrincipal User(int id, params string[] permissions)
        {
            var claims = new List<Claim> { new("UserId", id.ToString()) };
            claims.AddRange(permissions.Select(p => p == "FULL"
                ? new Claim(ClaimsPrincipalSecurityExtensions.FullAccessClaimType, "true")
                : new Claim(MinimumAuthorizationLevelHandler.PermissionClaimType, p)));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        }

        private static byte[] Jpeg(int size = 2048)
        {
            var b = new byte[size];
            Random.Shared.NextBytes(b);
            b[0] = 0xFF; b[1] = 0xD8; b[2] = 0xFF; b[3] = 0xE0;
            b[^2] = 0xFF; b[^1] = 0xD9;
            return b;
        }

        private static FieldDayService.ReadingRequest Reading(decimal km, decimal? lat = 12.9716m, decimal? lng = 77.5946m, decimal? acc = 12m,
            bool photo = true, string? reason = null, string? token = null)
            => new(km, lat, lng, acc, reason, photo ? Jpeg() : null, token ?? Guid.NewGuid().ToString());

        private static async Task<T> ScalarAsync<T>(Env env, string sql, int id)
        {
            using var conn = new SqlConnection(env.Cs);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", id);
            return (T)(await cmd.ExecuteScalarAsync())!;
        }

        [SkippableFact]
        public async Task StartAndEndDay_StoresBothReadings_RefusesDuplicates_AndRecognisesReplays()
        {
            var env = await OpenAsync();
            var s = env.Service;

            var noConsent = await s.StartDayAsync(env.Employee, "OwnBike", Reading(1000m));
            Assert.False(noConsent.Ok);
            await s.RecordConsentAsync(env.Employee);
            await s.RecordConsentAsync(env.Employee);   // twice is harmless

            // an unknown vehicle is refused only where Start Day still asks for one (2026-11-03_FieldWorkVehicleOptional.sql missing);
            // with the migration the vehicle is not asked and is ignored (FieldVehicleOptionalE2ETests)
            if (await s.VehicleRequiredAsync())
                Assert.False((await s.StartDayAsync(env.Employee, "Rocket", Reading(1000m))).Ok);
            Assert.False((await s.StartDayAsync(env.Employee, "OwnBike", Reading(1000m, acc: 500m))).Ok);   // poor GPS, no reason

            var token = Guid.NewGuid().ToString();
            var started = await s.StartDayAsync(env.Employee, "OwnBike", Reading(1000m, token: token));
            Assert.True(started.Ok, string.Join(" ", started.Errors));
            var replay = await s.StartDayAsync(env.Employee, "OwnBike", Reading(1000m, token: token));
            Assert.True(replay.Ok);
            Assert.Equal(started.DayId, replay.DayId);                                                      // same day, not a second one
            var second = await s.StartDayAsync(env.Employee, "OwnBike", Reading(1000m));
            Assert.False(second.Ok);                                                                        // one day per date
            Assert.Equal(FieldDayRules.StatusOpen, await s.GetTodayStatusAsync(env.Employee));

            Assert.False((await s.EndDayAsync(env.Employee, Reading(999.9m))).Ok);                           // below start km
            var endToken = Guid.NewGuid().ToString();
            var ended = await s.EndDayAsync(env.Employee, Reading(1042.5m, lat: 12.9800m, lng: 77.6000m, token: endToken));
            Assert.True(ended.Ok, string.Join(" ", ended.Errors));
            // a replay after the day closed: no open day any more -> refused politely, nothing written
            Assert.False((await s.EndDayAsync(env.Employee, Reading(1042.5m, token: endToken))).Ok);

            var day = (await env.Repo.GetDayAsync(started.DayId!.Value))!;
            Assert.Equal(FieldDayRules.StatusClosed, day.Status);
            Assert.Equal(1000m, day.StartKm);
            Assert.Equal(1042.5m, day.EndKm);
            Assert.Equal(42.5m, day.OdometerKm);
            Assert.NotNull(day.StartPhotoId);
            Assert.NotNull(day.EndPhotoId);
            Assert.InRange(day.GpsDistanceKm!.Value, 1.0m, 1.3m);
            Assert.Equal(FieldDayRules.ReviewPending, day.ReviewStatus);
            Assert.Null(day.FlagReasons);
            var events = await s.GetEventsAsync(day.Id);
            Assert.Equal(new[] { "Started", "Ended" }, events.Select(e => e.EventType));
            Assert.Equal(1, await ScalarAsync<int>(env, "SELECT COUNT(*) FROM dbo.FieldDays WHERE UserId = @Id", env.Employee));
        }

        [SkippableFact]
        public async Task MissingPhotoOrGps_WithReason_IsSavedAndFlagged_AndGpsMismatchIsFlagged()
        {
            var env = await OpenAsync();
            var s = env.Service;
            await s.RecordConsentAsync(env.Employee);
            var started = await s.StartDayAsync(env.Employee, "OwnCar", Reading(500m, lat: null, lng: null, acc: null, photo: false, reason: "Camera and GPS blocked"));
            Assert.True(started.Ok, string.Join(" ", started.Errors));
            var day = (await env.Repo.GetDayAsync(started.DayId!.Value))!;
            Assert.Equal(FieldDayRules.ReviewFlagged, day.ReviewStatus);
            Assert.Contains("no GPS", day.StartFlagReasons);
            Assert.Null(day.StartPhotoId);

            await s.RecordConsentAsync(env.Other);
            var o = await s.StartDayAsync(env.Other, "OwnBike", Reading(100m, lat: 12.9716m, lng: 77.5946m));
            Assert.True(o.Ok);
            // ~128 km straight line (Bengaluru -> Mysuru) but only 10 km on the odometer: impossible -> flagged
            var e = await s.EndDayAsync(env.Other, Reading(110m, lat: 12.3052m, lng: 76.6552m));
            Assert.True(e.Ok, string.Join(" ", e.Errors));
            var other = (await env.Repo.GetDayAsync(o.DayId!.Value))!;
            Assert.Equal(FieldDayRules.ReviewFlagged, other.ReviewStatus);
            Assert.Contains("GPS distance", other.EndFlagReasons);
        }

        [SkippableFact]
        public async Task EmployeesSeeOnlyTheirOwnDayAndPhotos_ManagersSeeAll()
        {
            var env = await OpenAsync();
            var s = env.Service;
            await s.RecordConsentAsync(env.Employee);
            var started = await s.StartDayAsync(env.Employee, "OwnBike", Reading(10m));
            Assert.True(started.Ok);
            var day = (await env.Repo.GetDayAsync(started.DayId!.Value))!;

            Assert.NotNull(await s.GetVisibleDayAsync(User(env.Employee, FieldDayRules.DayPermission), day.Id));
            Assert.Null(await s.GetVisibleDayAsync(User(env.Other, FieldDayRules.DayPermission), day.Id));
            Assert.NotNull(await s.GetVisibleDayAsync(User(env.Manager, FieldDayRules.ManagePermission), day.Id));

            Assert.NotNull(await s.GetDayPhotoAsync(User(env.Employee, FieldDayRules.DayPermission), day.Id, day.StartPhotoId!.Value));
            Assert.Null(await s.GetDayPhotoAsync(User(env.Other, FieldDayRules.DayPermission), day.Id, day.StartPhotoId!.Value));
            // a photo id that is not one of this day's photos is never served, even to a manager
            Assert.Null(await s.GetDayPhotoAsync(User(env.Manager, FieldDayRules.ManagePermission), day.Id, day.StartPhotoId!.Value + 100000));
        }

        [SkippableFact]
        public async Task ManagerApprovesFlagsAndReopens_EmployeeEndsAgain()
        {
            var env = await OpenAsync();
            var s = env.Service;
            var manager = User(env.Manager, FieldDayRules.ManagePermission);
            await s.RecordConsentAsync(env.Employee);
            var started = await s.StartDayAsync(env.Employee, "CompanyVehicle", Reading(2000m));
            Assert.True(started.Ok);
            var id = started.DayId!.Value;

            Assert.False((await s.ReviewAsync(manager, id, FieldDayRules.ReviewAction.Approve, null)).Ok);   // still open
            Assert.False((await s.ReviewAsync(User(env.Other, FieldDayRules.DayPermission), id, FieldDayRules.ReviewAction.Approve, null)).Ok);
            Assert.True((await s.EndDayAsync(env.Employee, Reading(2010m))).Ok);

            Assert.False((await s.ReviewAsync(manager, id, FieldDayRules.ReviewAction.Flag, " ")).Ok);       // needs a comment
            Assert.True((await s.ReviewAsync(manager, id, FieldDayRules.ReviewAction.Flag, "Meter not readable")).Ok);
            Assert.True((await s.ReviewAsync(manager, id, FieldDayRules.ReviewAction.Approve, "OK after call")).Ok);
            var approved = (await env.Repo.GetDayAsync(id))!;
            Assert.Equal(FieldDayRules.ReviewApproved, approved.ReviewStatus);
            Assert.Equal(env.Manager, approved.ReviewedByUserId);

            // the employee who is ALSO a manager may not review their own day
            Assert.False((await s.ReviewAsync(User(env.Employee, FieldDayRules.ManagePermission), id, FieldDayRules.ReviewAction.Flag, "x")).Ok);

            Assert.False((await s.ReviewAsync(manager, id, FieldDayRules.ReviewAction.Reopen, null)).Ok);   // needs a reason
            Assert.True((await s.ReviewAsync(manager, id, FieldDayRules.ReviewAction.Reopen, "Ended by mistake at lunch")).Ok);
            var reopened = (await env.Repo.GetDayAsync(id))!;
            Assert.Equal(FieldDayRules.StatusOpen, reopened.Status);
            Assert.Null(reopened.EndKm);
            Assert.Null(reopened.EndPhotoId);
            Assert.Equal(FieldDayRules.ReviewPending, reopened.ReviewStatus);
            var reopenEvent = (await s.GetEventsAsync(id)).Last();
            Assert.Equal("Reopened", reopenEvent.EventType);
            Assert.Contains("end 2010.0 km", reopenEvent.Details);   // the cleared end reading is kept in the history

            Assert.True((await s.EndDayAsync(env.Employee, Reading(2055m))).Ok);
            Assert.Equal(2055m, (await env.Repo.GetDayAsync(id))!.EndKm);
            Assert.Equal(new[] { "Started", "Ended", "Flagged", "Approved", "Reopened", "Ended" }, (await s.GetEventsAsync(id)).Select(e => e.EventType));
        }

        [SkippableFact]
        public async Task AutoClose_IsIdempotent_AndStartDayClosesAStaleOpenDayFirst()
        {
            var env = await OpenAsync();
            var s = env.Service;
            await s.RecordConsentAsync(env.Employee);
            // an open day of yesterday that the job has not closed yet
            var yesterday = AppClock.Today.AddDays(-1);
            var staleId = await env.Repo.StartDayAsync(new FieldDayRepository.StartDayCommand(env.Employee, yesterday, "OwnBike", 300m,
                new FieldDayRules.Position(12.9m, 77.5m, 10m), "test", null, null, Guid.NewGuid()));

            var started = await s.StartDayAsync(env.Employee, "OwnBike", Reading(350m));
            Assert.True(started.Ok, string.Join(" ", started.Errors));
            var stale = (await env.Repo.GetDayAsync(staleId))!;
            Assert.Equal(FieldDayRules.StatusAutoClosed, stale.Status);
            Assert.Equal(FieldDayRules.ReviewFlagged, stale.ReviewStatus);
            Assert.Equal(FieldDayRules.AutoClosedFlag, stale.EndFlagReasons);

            // the job "after midnight": today's open day is closed exactly once
            var tomorrow = AppClock.Today.AddDays(1);
            var first = await env.Repo.AutoCloseStaleAsync(tomorrow);
            Assert.True(first >= 1);
            Assert.Equal(0, await env.Repo.AutoCloseStaleAsync(tomorrow));
            Assert.Equal(0, await env.Repo.AutoCloseStaleAsync(tomorrow));
            Assert.Equal(FieldDayRules.StatusAutoClosed, (await env.Repo.GetDayAsync(started.DayId!.Value))!.Status);
            Assert.Equal(1, await ScalarAsync<int>(env,
                "SELECT COUNT(*) FROM dbo.FieldDayEvents WHERE FieldDayId = @Id AND EventType = 'AutoClosed'", started.DayId!.Value));
            // an auto-closed day refuses End Day
            Assert.False((await s.EndDayAsync(env.Employee, Reading(400m))).Ok);
        }

        [SkippableFact]
        public async Task ConcurrentStartDay_CreatesExactlyOneDay()
        {
            var env = await OpenAsync();
            await env.Service.RecordConsentAsync(env.Employee);
            var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
                Task.Run(() => env.Service.StartDayAsync(env.Employee, "OwnBike", Reading(700m)))));
            Assert.Equal(1, results.Count(r => r.Ok));
            Assert.Equal(1, await ScalarAsync<int>(env, "SELECT COUNT(*) FROM dbo.FieldDays WHERE UserId = @Id", env.Employee));
            // the photos of the refused submissions were removed again (only the accepted day's photo has a row)
            Assert.Equal(1, await ScalarAsync<int>(env, "SELECT COUNT(*) FROM dbo.FieldPhotos WHERE OwnerUserId = @Id", env.Employee));
        }

        [SkippableFact]
        public async Task Database_RefusesChangingTheStartReading_AndForeignPhotos()
        {
            var env = await OpenAsync();
            var s = env.Service;
            await s.RecordConsentAsync(env.Employee);
            await s.RecordConsentAsync(env.Other);
            var mine = await s.StartDayAsync(env.Employee, "OwnBike", Reading(100m));
            var theirs = await s.StartDayAsync(env.Other, "OwnBike", Reading(100m));
            Assert.True(mine.Ok && theirs.Ok);
            var theirPhoto = (await env.Repo.GetDayAsync(theirs.DayId!.Value))!.StartPhotoId!.Value;

            using var conn = new SqlConnection(env.Cs);
            await conn.OpenAsync();
            async Task<int> Fails(string sql)
            {
                using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@Id", mine.DayId!.Value);
                cmd.Parameters.AddWithValue("@P", theirPhoto);
                var ex = await Assert.ThrowsAsync<SqlException>(() => cmd.ExecuteNonQueryAsync());
                return ex.Number;
            }
            Assert.Equal(54151, await Fails("UPDATE dbo.FieldDays SET StartKm = StartKm - 50 WHERE Id = @Id"));
            Assert.Equal(54152, await Fails(
                "UPDATE dbo.FieldDays SET Status = 'Closed', EndedAtUtc = SYSUTCDATETIME(), EndKm = 120, EndPhotoId = @P, EndLatitude = 1, EndLongitude = 1, EndAccuracyM = 5, EndSubmissionToken = NEWID() WHERE Id = @Id"));
            await Assert.ThrowsAsync<SqlException>(async () =>
            {
                using var cmd = new SqlCommand("UPDATE dbo.FieldDays SET Status = 'Closed', EndedAtUtc = SYSUTCDATETIME(), EndKm = 50, EndExceptionReason = N'x', EndSubmissionToken = NEWID() WHERE Id = @Id", conn);
                cmd.Parameters.AddWithValue("@Id", mine.DayId!.Value);
                await cmd.ExecuteNonQueryAsync();   // end km below start km: CK_FieldDays_EndKm
            });
            Assert.Equal(FieldDayRules.StatusOpen, (await env.Repo.GetDayAsync(mine.DayId!.Value))!.Status);
        }
    }
}
