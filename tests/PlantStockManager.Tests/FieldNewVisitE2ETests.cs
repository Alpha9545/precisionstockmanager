using System.Security.Claims;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Services;
using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.Tests
{
    // Field Work follow-up (2026-11-03): the one-step New Visit and the vehicle-less Start Day, end-to-end against a SCRATCH
    // COPY (PSM_SCRATCH_CONNECTION, PlantsIMS2_Scratch_*; SKIPPED otherwise). Real services, repositories, CHECKs and triggers.
    // A and B are field employees (role given in the scratch copy), M a manager. Each test starts from no Field Work rows for them.
    [Collection("ScratchDb")]
    public class FieldNewVisitE2ETests
    {
        private sealed record Env(string Cs, FieldDayService Days, FieldCustomerService Customers, FieldVisitService Visits, string PhotoRoot, int A, int B, int M);

        private static ClaimsPrincipal P(int id, string perm)
            => new(new ClaimsIdentity(new[] { new Claim("UserId", id.ToString()), new Claim(MinimumAuthorizationLevelHandler.PermissionClaimType, perm) }, "T"));

        // vehicleMigration: null = runs either way; true / false = only with / without 2026-11-03_FieldWorkVehicleOptional.sql
        private static async Task<Env> OpenAsync(bool? vehicleMigration = null)
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
                using (var probe = new SqlCommand(FieldDayVehicleSchema.ProbeSql, conn))
                {
                    var migrated = (int)(await probe.ExecuteScalarAsync())! == 1;
                    Skip.If(vehicleMigration == true && !migrated, "2026-11-03_FieldWorkVehicleOptional.sql is not applied to this scratch copy.");
                    Skip.If(vehicleMigration == false && migrated, "2026-11-03_FieldWorkVehicleOptional.sql is applied to this scratch copy.");
                }
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
            var root = Path.Combine(Path.GetTempPath(), "psm-fw-nv-" + Guid.NewGuid().ToString("N")[..8]);
            var options = new FieldWorkOptions { PhotoRoot = root, MaxPhotosPerVisit = 3 };
            var photos = new FieldPhotoStore(options);
            var customers = new FieldCustomerService(new FieldCustomerRepository(db), photos, options);
            return new Env(cs!, new FieldDayService(new FieldDayRepository(db), photos, options), customers,
                new FieldVisitService(new FieldVisitRepository(db), customers, photos, options), root, users[0], users[1], users[2]);
        }

        private static byte[] Jpeg(int size = 2048)
        {
            var b = new byte[size];
            Random.Shared.NextBytes(b);
            b[0] = 0xFF; b[1] = 0xD8; b[2] = 0xFF; b[3] = 0xE0; b[^2] = 0xFF; b[^1] = 0xD9;
            return b;
        }

        private static string Phone() => "9" + Random.Shared.NextInt64(100_000_000, 999_999_999);

        private static async Task<int> StartDayAsync(Env env, int user)
        {
            await env.Days.RecordConsentAsync(user);
            var r = await env.Days.StartDayAsync(user, "OwnBike", new FieldDayService.ReadingRequest(100m, 12.97m, 77.59m, 10m, null, Jpeg(), Guid.NewGuid().ToString()));
            Assert.True(r.Ok, string.Join(" ", r.Errors));
            return r.DayId!.Value;
        }

        private static FieldVisitService.VisitRequest Visit(string? token = null, int photos = 0, string? status = null, DateTime? next = null,
            string? nextNote = null, decimal? lat = 12.98m, string? reason = null, string[]? items = null, string[]? qty = null)
            => new(0, lat, lat.HasValue ? 77.60m : null, lat.HasValue ? 12m : null, reason, "Discussed new gerbera range", "Notes", items ?? Array.Empty<string>(),
                qty ?? Array.Empty<string>(), Enumerable.Range(0, photos).Select(_ => Jpeg()).ToList(), photos > 0 ? 1 : null, status, next, nextNote,
                token ?? Guid.NewGuid().ToString());

        private static FieldVisitService.NewVisitRequest NewCustomer(FieldVisitService.VisitRequest v, string name, string phone, bool confirm = false)
            => new(FieldVisitService.ModeNew, null, new FieldCustomerRules.CustomerInput(name, "Ravi", phone, null, "Nursery", "Hoskote", "Bengaluru Rural", null), confirm, v);

        private static FieldVisitService.NewVisitRequest Existing(FieldVisitService.VisitRequest v, int? customerId)
            => new(FieldVisitService.ModeExisting, customerId, null, false, v);

        private static async Task<T> Scalar<T>(Env env, string sql, params (string, object)[] p)
        {
            using var conn = new SqlConnection(env.Cs);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(sql, conn);
            foreach (var (k, v) in p) cmd.Parameters.AddWithValue(k, v);
            return (T)(await cmd.ExecuteScalarAsync())!;
        }

        private static async Task Exec(Env env, string sql)
        {
            using var conn = new SqlConnection(env.Cs);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }

        private static int Files(Env env) => Directory.Exists(env.PhotoRoot) ? Directory.GetFiles(env.PhotoRoot).Length : 0;

        // ---- new customer ----------------------------------------------------------------------------------------------

        [SkippableFact]
        public async Task NewCustomerVisit_SavesCustomerVisitPhotosProfilePhotoFollowUpAndEvents_InOneGo()
        {
            var env = await OpenAsync();
            var a = P(env.A, FieldDayRules.DayPermission);
            var dayId = await StartDayAsync(env, env.A);
            var filesBefore = Files(env);
            var next = AppClock.Today.AddDays(5);
            var r = await env.Visits.SaveVisitAsync(a, NewCustomer(Visit(photos: 2, status: "Interested", next: next, nextNote: "Bring price list",
                items: new[] { "Rose sample" }, qty: new[] { "10" }), "ZZTEST NV Nursery", Phone()));
            Assert.True(r.Ok, string.Join(" ", r.Errors));
            var cid = r.CustomerId!.Value;
            var customer = (await new FieldCustomerRepository(new DatabaseHelper(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = env.Cs }).Build())).GetAsync(cid))!;
            Assert.Equal(env.A, customer.AssignedUserId);
            Assert.Equal(env.A, customer.CreatedByUserId);
            Assert.Equal("Interested", customer.LeadStatus);
            Assert.Equal(12.98m, customer.Latitude);   // the visit's GPS reading is the customer location
            Assert.NotNull(customer.PhotoId);            // the first visit photo (a copy) became the profile photo
            Assert.Equal("CustomerPhoto", await Scalar<string>(env, "SELECT Purpose FROM dbo.FieldPhotos WHERE Id = @P", ("@P", customer.PhotoId!.Value)));
            Assert.Equal(dayId, await Scalar<int>(env, "SELECT FieldDayId FROM dbo.FieldVisits WHERE Id = @V", ("@V", r.Id!.Value)));
            Assert.Equal(2, await Scalar<int>(env, "SELECT COUNT(*) FROM dbo.FieldVisitPhotos WHERE VisitId = @V", ("@V", r.Id!.Value)));
            Assert.Equal(1, await Scalar<int>(env, "SELECT COUNT(*) FROM dbo.FieldVisitItems WHERE VisitId = @V", ("@V", r.Id!.Value)));
            Assert.Equal(3, Files(env) - filesBefore);   // 2 visit photos + the profile copy
            var t = await env.Customers.GetTimelineAsync(cid);
            Assert.Equal(next, t.FollowUps.Single(f => f.Status == "Pending").DueDate);
            Assert.Contains(t.Events, e => e.EventType == "Created" && e.ToStatus == "New");
            Assert.Contains(t.Events, e => e.EventType == "StatusChanged" && e.FromStatus == "New" && e.ToStatus == "Interested");
            Assert.Contains(t.Events, e => e.EventType == "Updated" && e.Details!.Contains("Profile photo"));
            Assert.Contains(t.Events, e => e.EventType == "FollowUpSet");
        }

        [SkippableFact]
        public async Task NewCustomerVisit_WithNoPhoto_SavesWithoutAProfilePhotoAndWithoutError()
        {
            var env = await OpenAsync();
            var a = P(env.A, FieldDayRules.DayPermission);
            await StartDayAsync(env, env.A);
            var filesBefore = Files(env);
            var r = await env.Visits.SaveVisitAsync(a, NewCustomer(Visit(photos: 0), "ZZTEST NV NoPhoto", Phone()));
            Assert.True(r.Ok, string.Join(" ", r.Errors));
            Assert.True(await Scalar<int>(env, "SELECT CASE WHEN PhotoId IS NULL THEN 1 ELSE 0 END FROM dbo.FieldCustomers WHERE Id = @C", ("@C", r.CustomerId!.Value)) == 1);
            Assert.Equal(0, await Scalar<int>(env, "SELECT COUNT(*) FROM dbo.FieldVisitPhotos WHERE VisitId = @V", ("@V", r.Id!.Value)));
            Assert.Equal(filesBefore, Files(env));
            Assert.Equal("New", await Scalar<string>(env, "SELECT LeadStatus FROM dbo.FieldCustomers WHERE Id = @C", ("@C", r.CustomerId!.Value)));
        }

        [SkippableFact]
        public async Task NewCustomerVisit_FailingPartWay_SavesNothing_NoRowsNoFiles_AndTheSameFormCanBeSavedAfter()
        {
            var env = await OpenAsync();
            var a = P(env.A, FieldDayRules.DayPermission);
            await StartDayAsync(env, env.A);
            var name = "ZZTEST NV Rollback " + Guid.NewGuid().ToString("N")[..6];
            var token = Guid.NewGuid().ToString();
            var photoRowsBefore = await Scalar<int>(env, "SELECT COUNT(*) FROM dbo.FieldPhotos WHERE OwnerUserId = @U", ("@U", env.A));
            var filesBefore = Files(env);
            // the follow-up insert -- after the customer, visit, items, photos, profile photo and status change -- is made to fail
            await Exec(env, "ALTER TABLE dbo.FieldFollowUps WITH NOCHECK ADD CONSTRAINT CK_ZZTEST_FailFollowUp CHECK (Note IS NULL OR Note <> N'ZZTEST-FAIL')");
            try
            {
                var failed = await env.Visits.SaveVisitAsync(a, NewCustomer(Visit(token: token, photos: 2, status: "Interested", next: AppClock.Today.AddDays(2),
                    nextNote: "ZZTEST-FAIL", items: new[] { "Rose sample" }, qty: new[] { "5" }), name, Phone()));
                Assert.False(failed.Ok);
            }
            finally
            {
                await Exec(env, "ALTER TABLE dbo.FieldFollowUps DROP CONSTRAINT CK_ZZTEST_FailFollowUp");
            }
            Assert.Equal(0, await Scalar<int>(env, "SELECT COUNT(*) FROM dbo.FieldCustomers WHERE Name = @N", ("@N", name)));
            Assert.Equal(0, await Scalar<int>(env, "SELECT COUNT(*) FROM dbo.FieldVisits WHERE SubmissionToken = @T", ("@T", Guid.Parse(token))));
            Assert.Equal(photoRowsBefore, await Scalar<int>(env, "SELECT COUNT(*) FROM dbo.FieldPhotos WHERE OwnerUserId = @U", ("@U", env.A)));
            Assert.Equal(filesBefore, Files(env));   // the files written before the transaction were deleted again

            var retry = await env.Visits.SaveVisitAsync(a, NewCustomer(Visit(token: token, photos: 1, next: AppClock.Today.AddDays(2), nextNote: "ok"), name, Phone()));
            Assert.True(retry.Ok, string.Join(" ", retry.Errors));
            Assert.Equal(1, await Scalar<int>(env, "SELECT COUNT(*) FROM dbo.FieldCustomers WHERE Name = @N", ("@N", name)));
        }

        [SkippableFact]
        public async Task DuplicatePhone_OwnCustomerCanBeUsedInstead_OthersOnlyWarned_ConfirmSavesNew()
        {
            var env = await OpenAsync();
            var a = P(env.A, FieldDayRules.DayPermission);
            var b = P(env.B, FieldDayRules.DayPermission);
            await StartDayAsync(env, env.A);
            await StartDayAsync(env, env.B);
            var mine = Phone();
            var theirs = Phone();
            Assert.True((await env.Visits.SaveVisitAsync(a, NewCustomer(Visit(), "ZZTEST NV Mine", mine))).Ok);
            Assert.True((await env.Visits.SaveVisitAsync(b, NewCustomer(Visit(), "ZZTEST NV Theirs", theirs))).Ok);

            var own = await env.Visits.SaveVisitAsync(a, NewCustomer(Visit(), "ZZTEST NV Mine again", "+91 " + mine));
            Assert.True(own.NeedsDuplicateConfirmation);
            var match = Assert.Single(own.Duplicates!);
            Assert.True(FieldCustomerRules.DuplicateUsable(match, env.A));   // -> "Use this existing customer instead"
            // ... which is the Existing-customer path with that id
            var used = await env.Visits.SaveVisitAsync(a, Existing(Visit(), match.CustomerId));
            Assert.True(used.Ok, string.Join(" ", used.Errors));
            Assert.Equal(match.CustomerId, used.CustomerId);

            var other = await env.Visits.SaveVisitAsync(a, NewCustomer(Visit(), "ZZTEST NV Theirs again", theirs));
            Assert.True(other.NeedsDuplicateConfirmation);
            Assert.False(FieldCustomerRules.DuplicateUsable(Assert.Single(other.Duplicates!), env.A));   // B's customer: warning only
            Assert.Contains("assigned to", other.Errors[0]);
            var confirmed = await env.Visits.SaveVisitAsync(a, NewCustomer(Visit(), "ZZTEST NV Theirs again", theirs, confirm: true));
            Assert.True(confirmed.Ok, string.Join(" ", confirmed.Errors));
        }

        [SkippableFact]
        public async Task DoubleSubmit_SequentialAndConcurrent_SaveOneCustomerAndOneVisit()
        {
            var env = await OpenAsync();
            var a = P(env.A, FieldDayRules.DayPermission);
            await StartDayAsync(env, env.A);
            var token = Guid.NewGuid().ToString();
            var phone = Phone();
            var first = await env.Visits.SaveVisitAsync(a, NewCustomer(Visit(token: token, photos: 1), "ZZTEST NV Twice", phone));
            Assert.True(first.Ok);
            var again = await env.Visits.SaveVisitAsync(a, NewCustomer(Visit(token: token, photos: 1), "ZZTEST NV Twice", phone));
            Assert.True(again.Ok && again.Replay && again.Id == first.Id && again.CustomerId == first.CustomerId);

            var token2 = Guid.NewGuid().ToString();
            var phone2 = Phone();
            var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(() =>
                env.Visits.SaveVisitAsync(a, NewCustomer(Visit(token: token2, photos: 1), "ZZTEST NV Concurrent", phone2)))));
            Assert.Contains(results, r => r.Ok);
            Assert.Equal(1, await Scalar<int>(env, "SELECT COUNT(*) FROM dbo.FieldCustomers WHERE Name = N'ZZTEST NV Concurrent'"));
            Assert.Equal(1, await Scalar<int>(env, "SELECT COUNT(*) FROM dbo.FieldVisits WHERE SubmissionToken = @T", ("@T", Guid.Parse(token2))));
            Assert.Equal(2, await Scalar<int>(env, "SELECT COUNT(*) FROM dbo.FieldCustomers WHERE Name LIKE N'ZZTEST NV %' AND AssignedUserId = @U", ("@U", env.A)));
        }

        // ---- existing customer ------------------------------------------------------------------------------------------

        [SkippableFact]
        public async Task ExistingCustomerVisit_CompletesFollowUp_SetsAMissingProfilePhotoOnce_RefusesOthersCustomers()
        {
            var env = await OpenAsync();
            var a = P(env.A, FieldDayRules.DayPermission);
            var b = P(env.B, FieldDayRules.DayPermission);
            await StartDayAsync(env, env.A);
            await StartDayAsync(env, env.B);
            var created = await env.Visits.SaveVisitAsync(a, NewCustomer(Visit(next: AppClock.Today), "ZZTEST NV Existing", Phone()));   // no photo
            var cid = created.CustomerId!.Value;
            Assert.Equal(1, (await env.Customers.CountDueAsync(a)).DueToday);

            Assert.False((await env.Visits.SaveVisitAsync(a, Existing(Visit(), null))).Ok);    // no customer chosen
            var filesBefore = Files(env);
            var v1 = await env.Visits.SaveVisitAsync(a, Existing(Visit(photos: 1, status: "SampleGiven"), cid));
            Assert.True(v1.Ok, string.Join(" ", v1.Errors));
            Assert.True(v1.AskNextDate);   // the follow-up was completed and no new date was set
            Assert.Equal(0, (await env.Customers.CountDueAsync(a)).DueToday);
            var profile = await Scalar<int>(env, "SELECT PhotoId FROM dbo.FieldCustomers WHERE Id = @C", ("@C", cid));
            var v2 = await env.Visits.SaveVisitAsync(a, Existing(Visit(photos: 1), cid));
            Assert.True(v2.Ok);
            Assert.Equal(profile, await Scalar<int>(env, "SELECT PhotoId FROM dbo.FieldCustomers WHERE Id = @C", ("@C", cid)));   // not replaced
            Assert.Equal(1, await Scalar<int>(env, "SELECT COUNT(*) FROM dbo.FieldCustomerEvents WHERE CustomerId = @C AND EventType = 'Updated'", ("@C", cid)));
            Assert.Equal(3, Files(env) - filesBefore);   // v1: photo + profile copy; v2: its photo only (its unused copy is deleted)

            Assert.False((await env.Visits.SaveVisitAsync(b, Existing(Visit(), cid))).Ok);    // another employee's customer
            Assert.Empty((await env.Visits.SearchMyCustomersAsync(b, "ZZTEST NV Existing")));
            Assert.Single(await env.Visits.SearchMyCustomersAsync(a, "ZZTEST NV Existing"));
        }

        [SkippableFact]
        public async Task GpsReason_FlagsCustomerAndVisit_AndNoOpenDay_CreatesNoCustomer()
        {
            var env = await OpenAsync();
            var a = P(env.A, FieldDayRules.DayPermission);
            var name = "ZZTEST NV NoDay " + Guid.NewGuid().ToString("N")[..6];
            var noDay = await env.Visits.SaveVisitAsync(a, NewCustomer(Visit(), name, Phone()));
            Assert.False(noDay.Ok);
            Assert.Equal(FieldVisitService.NoOpenDayMessage, noDay.Errors.Single());
            Assert.Equal(0, await Scalar<int>(env, "SELECT COUNT(*) FROM dbo.FieldCustomers WHERE Name = @N", ("@N", name)));

            await StartDayAsync(env, env.A);
            Assert.False((await env.Visits.SaveVisitAsync(a, NewCustomer(Visit(lat: null), "ZZTEST NV NoGps", Phone()))).Ok);   // no GPS, no reason
            var r = await env.Visits.SaveVisitAsync(a, NewCustomer(Visit(lat: null, reason: "Inside cold store"), "ZZTEST NV NoGps", Phone()));
            Assert.True(r.Ok, string.Join(" ", r.Errors));
            Assert.Contains("no GPS", await Scalar<string>(env, "SELECT FlagReasons FROM dbo.FieldCustomers WHERE Id = @C", ("@C", r.CustomerId!.Value)));
            Assert.Contains("no GPS", await Scalar<string>(env, "SELECT FlagReasons FROM dbo.FieldVisits WHERE Id = @V", ("@V", r.Id!.Value)));
            Assert.Equal("Inside cold store", await Scalar<string>(env, "SELECT GpsExceptionReason FROM dbo.FieldCustomers WHERE Id = @C", ("@C", r.CustomerId!.Value)));
        }

        // ---- Start Day without a vehicle ---------------------------------------------------------------------------------

        [SkippableFact]
        public async Task StartDay_WithTheMigration_DoesNotAskForAVehicle_AndStoresNone()
        {
            var env = await OpenAsync(vehicleMigration: true);
            Assert.False(await env.Days.VehicleRequiredAsync());
            await env.Days.RecordConsentAsync(env.A);
            var r = await env.Days.StartDayAsync(env.A, "OwnCar", new FieldDayService.ReadingRequest(10m, 12.97m, 77.59m, 10m, null, Jpeg(), Guid.NewGuid().ToString()));
            Assert.True(r.Ok, string.Join(" ", r.Errors));
            Assert.Equal(1, await Scalar<int>(env, "SELECT CASE WHEN VehicleType IS NULL THEN 1 ELSE 0 END FROM dbo.FieldDays WHERE Id = @D", ("@D", r.DayId!.Value)));
            var day = await env.Days.GetTodayAsync(env.A);
            Assert.Null(day!.VehicleType);
            Assert.Equal("", FieldDayRules.VehicleLabel(day.VehicleType));
        }

        [SkippableFact]
        public async Task StartDay_WithoutTheMigration_StillRequiresTheVehicle()
        {
            var env = await OpenAsync(vehicleMigration: false);
            Assert.True(await env.Days.VehicleRequiredAsync());
            await env.Days.RecordConsentAsync(env.A);
            var refused = await env.Days.StartDayAsync(env.A, null, new FieldDayService.ReadingRequest(10m, 12.97m, 77.59m, 10m, null, Jpeg(), Guid.NewGuid().ToString()));
            Assert.False(refused.Ok);
            Assert.Contains("Select the vehicle type.", refused.Errors);
            var ok = await env.Days.StartDayAsync(env.A, "OwnBike", new FieldDayService.ReadingRequest(10m, 12.97m, 77.59m, 10m, null, Jpeg(), Guid.NewGuid().ToString()));
            Assert.True(ok.Ok, string.Join(" ", ok.Errors));
            Assert.Equal("OwnBike", (await env.Days.GetTodayAsync(env.A))!.VehicleType);
        }
    }
}
