using System.Security.Claims;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Services;
using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.Tests
{
    // Field Work Phase 2 (2026-11-03_FieldWorkCustomers.sql), end-to-end against a SCRATCH COPY only (PSM_SCRATCH_CONNECTION,
    // PlantsIMS2_Scratch_*; SKIPPED otherwise or when the migration is not applied). Real services, repositories, CHECKs and
    // triggers. Users: the first three active users -- A and B get the "Field Employee" role in the scratch copy (so they
    // are assignable), M acts as a manager through its claims. Each test deletes the Field Work rows of these users first.
    [Collection("ScratchDb")]
    public class FieldCustomerE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";

        // Phase 2 rows of the test users (@A, @B, @C); a no-op on a copy without the Phase 2 migration. Also used by
        // FieldWorkE2ETests before it deletes Field Days (visits reference them).
        public const string Phase2CleanupSql = @"
IF OBJECT_ID('dbo.FieldCustomers', 'U') IS NOT NULL
BEGIN
    DECLARE @Cs TABLE (Id INT PRIMARY KEY);
    INSERT INTO @Cs SELECT Id FROM dbo.FieldCustomers WHERE AssignedUserId IN (@A, @B, @C) OR CreatedByUserId IN (@A, @B, @C)
    UNION SELECT CustomerId FROM dbo.FieldVisits WHERE UserId IN (@A, @B, @C)
    UNION SELECT CustomerId FROM dbo.FieldCustomerNotes WHERE UserId IN (@A, @B, @C);
    DELETE FROM dbo.FieldCustomerEvents WHERE CustomerId IN (SELECT Id FROM @Cs);
    DELETE FROM dbo.FieldFollowUps WHERE CustomerId IN (SELECT Id FROM @Cs);
    DELETE FROM dbo.FieldCustomerNotes WHERE CustomerId IN (SELECT Id FROM @Cs);
    DELETE vp FROM dbo.FieldVisitPhotos vp JOIN dbo.FieldVisits v ON v.Id = vp.VisitId WHERE v.CustomerId IN (SELECT Id FROM @Cs);
    DELETE vi FROM dbo.FieldVisitItems vi JOIN dbo.FieldVisits v ON v.Id = vi.VisitId WHERE v.CustomerId IN (SELECT Id FROM @Cs);
    DELETE FROM dbo.FieldVisits WHERE CustomerId IN (SELECT Id FROM @Cs);
    DELETE FROM dbo.FieldCustomers WHERE Id IN (SELECT Id FROM @Cs);
    DELETE FROM dbo.FieldPhotos WHERE OwnerUserId IN (@A, @B, @C) AND Purpose IN ('CustomerPhoto', 'VisitPhoto');
END;";

        private sealed record Env(string Cs, FieldCustomerService Customers, FieldVisitService Visits, FieldDayService Days,
            FieldCustomerRepository Repo, FieldWorkOptions Options, int A, int B, int M)
        {
            public ClaimsPrincipal Employee(int id) => Principal(id, FieldDayRules.DayPermission);
            public ClaimsPrincipal Manager => Principal(M, FieldDayRules.ManagePermission);
        }

        private static ClaimsPrincipal Principal(int id, params string[] permissions)
        {
            var claims = new List<Claim> { new("UserId", id.ToString()) };
            claims.AddRange(permissions.Select(p => new Claim(MinimumAuthorizationLevelHandler.PermissionClaimType, p)));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        }

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
                using (var probe = new SqlCommand(FieldCustomerSchema.ProbeSql, conn))
                    Skip.If((int)(await probe.ExecuteScalarAsync())! != 1, "2026-11-03_FieldWorkCustomers.sql is not applied to this scratch copy.");
                using (var cmd = new SqlCommand("SELECT TOP 3 Id FROM dbo.IMSUsers WHERE IsActive = 1 ORDER BY Id", conn))
                using (var r = await cmd.ExecuteReaderAsync())
                    while (await r.ReadAsync())
                        users.Add(r.GetInt32(0));
                Skip.If(users.Count < 3, "The scratch copy needs 3 active users.");
                const string setup = Phase2CleanupSql + @"
DELETE e FROM dbo.FieldDayEvents e JOIN dbo.FieldDays d ON d.Id = e.FieldDayId WHERE d.UserId IN (@A, @B, @C);
DELETE FROM dbo.FieldDays WHERE UserId IN (@A, @B, @C);
DELETE FROM dbo.FieldPhotos WHERE OwnerUserId IN (@A, @B, @C);
DELETE FROM dbo.FieldWorkConsents WHERE UserId IN (@A, @B, @C);
-- A and B: assignable field employees in this scratch copy
INSERT INTO dbo.UserRoles (UserId, RoleId, CreatedDate)
SELECT u.Id, r.Id, SYSUTCDATETIME() FROM dbo.IMSUsers u CROSS JOIN dbo.Roles r
WHERE u.Id IN (@A, @B) AND r.Name = N'Field Employee'
  AND NOT EXISTS (SELECT 1 FROM dbo.UserRoles x WHERE x.UserId = u.Id AND x.RoleId = r.Id);";
                using (var cmd = new SqlCommand(setup, conn))
                {
                    cmd.Parameters.AddWithValue("@A", users[0]);
                    cmd.Parameters.AddWithValue("@B", users[1]);
                    cmd.Parameters.AddWithValue("@C", users[2]);
                    await cmd.ExecuteNonQueryAsync();
                }
            }
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs }).Build();
            var db = new DatabaseHelper(config);
            var options = new FieldWorkOptions { PhotoRoot = Path.Combine(Path.GetTempPath(), "psm-fw-e2e"), MaxPhotosPerVisit = 3 };
            var photos = new FieldPhotoStore(options);
            var repo = new FieldCustomerRepository(db);
            var customers = new FieldCustomerService(repo, photos, options);
            var visits = new FieldVisitService(new FieldVisitRepository(db), customers, photos, options);
            var days = new FieldDayService(new FieldDayRepository(db), photos, options);
            return new Env(cs!, customers, visits, days, repo, options, users[0], users[1], users[2]);
        }

        private static byte[] Jpeg(int size = 2048)
        {
            var b = new byte[size];
            Random.Shared.NextBytes(b);
            b[0] = 0xFF; b[1] = 0xD8; b[2] = 0xFF; b[3] = 0xE0;
            b[^2] = 0xFF; b[^1] = 0xD9;
            return b;
        }

        private static FieldCustomerService.CreateRequest NewCustomer(string phone, string name = "ZZTEST Rose Farm", string? token = null,
            decimal? lat = 12.9716m, string? reason = null, byte[]? photo = null, int? consent = null, bool confirm = false, int? assignTo = null,
            string? district = "Bengaluru Rural")
            => new(new Services.FieldWork.FieldCustomerRules.CustomerInput(name, "Ravi", phone, null, "Farmer", "Hoskote", district, null),
                assignTo, lat, lat.HasValue ? 77.5946m : null, lat.HasValue ? 15m : null, reason, photo, consent, confirm, token ?? Guid.NewGuid().ToString());

        private static async Task<int> StartDayAsync(Env env, int userId)
        {
            await env.Days.RecordConsentAsync(userId);
            var r = await env.Days.StartDayAsync(userId, "OwnBike",
                new FieldDayService.ReadingRequest(1000m, 12.97m, 77.59m, 10m, null, Jpeg(), Guid.NewGuid().ToString()));
            Assert.True(r.Ok, string.Join(" ", r.Errors));
            return r.DayId!.Value;
        }

        private static FieldVisitService.VisitRequest Visit(int customerId, string? token = null, decimal? lat = 12.98m, string? reason = null,
            int photos = 0, string? status = null, DateTime? next = null, string[]? items = null, string[]? qty = null)
            => new(customerId, lat, lat.HasValue ? 77.60m : null, lat.HasValue ? 12m : null, reason, "Discussed rose varieties", "Wants samples",
                items ?? Array.Empty<string>(), qty ?? Array.Empty<string>(), Enumerable.Range(0, photos).Select(_ => Jpeg()).ToList(),
                env_ConsentVersion, status, next, next.HasValue ? "Call about order" : null, token ?? Guid.NewGuid().ToString());

        private const int env_ConsentVersion = 1;

        private static async Task<T> ScalarAsync<T>(Env env, string sql, params (string, object)[] p)
        {
            using var conn = new SqlConnection(env.Cs);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(sql, conn);
            foreach (var (k, v) in p) cmd.Parameters.AddWithValue(k, v);
            return (T)(await cmd.ExecuteScalarAsync())!;
        }

        private static async Task ExecAsync(Env env, string sql, params (string, object)[] p)
        {
            using var conn = new SqlConnection(env.Cs);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(sql, conn);
            foreach (var (k, v) in p) cmd.Parameters.AddWithValue(k, v);
            await cmd.ExecuteNonQueryAsync();
        }

        private static string UniquePhone() => "9" + Random.Shared.NextInt64(100_000_000, 999_999_999).ToString();

        // ---------------------------------------------------------------------------------------------------------------

        [SkippableFact]
        public async Task CreateCustomer_DuplicatePhoneWarns_ConfirmSaves_ReplayReturnsSameRow_GpsAndConsentRules()
        {
            var env = await OpenAsync();
            var a = env.Employee(env.A);
            var phone = UniquePhone();

            var token = Guid.NewGuid().ToString();
            var first = await env.Customers.CreateAsync(a, NewCustomer(phone, token: token));
            Assert.True(first.Ok, string.Join(" ", first.Errors));
            var replay = await env.Customers.CreateAsync(a, NewCustomer(phone, token: token));
            Assert.True(replay.Ok && replay.Replay);
            Assert.Equal(first.Id, replay.Id);
            Assert.Equal(1, await ScalarAsync<int>(env, "SELECT COUNT(*) FROM dbo.FieldCustomers WHERE SubmissionToken = @T", ("@T", Guid.Parse(token))));

            // same number written differently, by another employee: warned (not blocked), owner shown
            var b = env.Employee(env.B);
            var dup = await env.Customers.CreateAsync(b, NewCustomer("+91 " + phone[..5] + "-" + phone[5..], name: "ZZTEST Other Farm"));
            Assert.False(dup.Ok);
            Assert.True(dup.NeedsDuplicateConfirmation);
            Assert.Contains("ZZTEST Rose Farm", dup.Errors[0]);
            var confirmed = await env.Customers.CreateAsync(b, NewCustomer(phone, name: "ZZTEST Other Farm", confirm: true));
            Assert.True(confirmed.Ok, string.Join(" ", confirmed.Errors));

            // a field employee needs GPS or a reason; a reason saves it flagged
            Assert.False((await env.Customers.CreateAsync(a, NewCustomer(UniquePhone(), lat: null))).Ok);
            var flagged = await env.Customers.CreateAsync(a, NewCustomer(UniquePhone(), lat: null, reason: "No signal in the shed"));
            Assert.True(flagged.Ok);
            Assert.Contains("no GPS", (await env.Repo.GetAsync(flagged.Id!.Value))!.FlagReasons);

            // a photo needs the current consent reminder version; then it is stored as a CustomerPhoto with that version
            Assert.False((await env.Customers.CreateAsync(a, NewCustomer(UniquePhone(), photo: Jpeg(), consent: 0))).Ok);
            var withPhoto = await env.Customers.CreateAsync(a, NewCustomer(UniquePhone(), photo: Jpeg(), consent: 1, district: "  bengaluru   RURAL "));
            Assert.True(withPhoto.Ok, string.Join(" ", withPhoto.Errors));
            var c = (await env.Repo.GetAsync(withPhoto.Id!.Value))!;
            Assert.NotNull(c.PhotoId);
            Assert.Equal("Bengaluru Rural", c.District);   // existing spelling reused (case / spaces ignored)
            Assert.Equal(1, await ScalarAsync<int>(env, "SELECT ConsentReminderVersion FROM dbo.FieldPhotos WHERE Id = @P AND Purpose = 'CustomerPhoto'", ("@P", c.PhotoId!.Value)));
            Assert.Equal(env.A, c.AssignedUserId);
            Assert.Equal("New", c.LeadStatus);
        }

        [SkippableFact]
        public async Task OtherEmployees_GetNotFound_CreatorAfterReassign_SeesMaskedPhoneAndCannotWork()
        {
            var env = await OpenAsync();
            var a = env.Employee(env.A);
            var b = env.Employee(env.B);
            var phone = UniquePhone();
            var created = await env.Customers.CreateAsync(a, NewCustomer(phone, photo: Jpeg(), consent: 1));
            Assert.True(created.Ok);
            var id = created.Id!.Value;
            var photoId = (await env.Repo.GetAsync(id))!.PhotoId!.Value;

            Assert.Null(await env.Customers.GetVisibleAsync(b, id));
            Assert.Null(await env.Customers.GetPhotoAsync(b, id, photoId));
            Assert.NotNull(await env.Customers.GetPhotoAsync(a, id, photoId));
            Assert.NotNull(await env.Customers.GetVisibleAsync(env.Manager, id));
            Assert.False((await env.Customers.AddNoteAsync(b, new FieldCustomerService.NoteRequest(id, "Call", "x", null, null, false, null, null, Guid.NewGuid().ToString()))).Ok);
            var (bRows, _) = await env.Customers.ListAsync(b, FieldCustomerService.NormalizeFilter(phone, null, null, null, null, null, false, false, null, 1, false, Array.Empty<string>(), Array.Empty<int>()));
            Assert.DoesNotContain(bRows, r => r.Id == id);

            // manager moves it to B: A (the creator) still sees it, but the phone is masked and A cannot log calls / visits
            Assert.True((await env.Customers.ReassignAsync(env.Manager, new[] { id }, null, env.B)).Ok);
            var seenByA = await env.Customers.GetVisibleAsync(a, id);
            Assert.NotNull(seenByA);
            Assert.False(FieldCustomerService.MaySeePhone(a, seenByA!));
            Assert.False(FieldCustomerService.MayWork(a, seenByA!));
            var (aRows, _) = await env.Customers.ListAsync(a, FieldCustomerService.NormalizeFilter("ZZTEST", null, null, null, null, null, false, false, null, 1, false, Array.Empty<string>(), Array.Empty<int>()));
            var row = aRows.Single(r => r.Id == id);
            Assert.Equal(FieldCustomerRules.MaskPhone(phone), row.PhoneShown);
            Assert.False((await env.Customers.AddNoteAsync(a, new FieldCustomerService.NoteRequest(id, "Call", "x", null, null, false, null, null, Guid.NewGuid().ToString()))).Ok);
            Assert.NotNull(await env.Customers.GetVisibleAsync(b, id));

            // archived: hidden from employees, still visible to the manager
            Assert.True((await env.Customers.ArchiveAsync(env.Manager, id, "Closed business")).Ok);
            Assert.Null(await env.Customers.GetVisibleAsync(b, id));
            Assert.NotNull(await env.Customers.GetVisibleAsync(env.Manager, id));
            Assert.False((await env.Customers.ArchiveAsync(b, id, "x")).Ok);   // employees cannot archive
            Assert.True((await env.Customers.RestoreAsync(env.Manager, id)).Ok);
        }

        [SkippableFact]
        public async Task Visit_NeedsOpenDay_ThenSavesItemsPhotosStatus_CompletesFollowUp_AndReplayIsOneVisit()
        {
            var env = await OpenAsync();
            var a = env.Employee(env.A);
            var created = await env.Customers.CreateAsync(a, NewCustomer(UniquePhone()));
            var id = created.Id!.Value;

            var noDay = await env.Visits.CreateAsync(a, Visit(id));
            Assert.False(noDay.Ok);
            Assert.Equal(FieldVisitService.NoOpenDayMessage, noDay.Errors.Single());

            var dayId = await StartDayAsync(env, env.A);
            // a call that sets a follow-up for today
            var call = await env.Customers.AddNoteAsync(a, new FieldCustomerService.NoteRequest(id, "Call", "Asked for price list", null, "Contacted", true,
                AppClock.Today, "Visit with samples", Guid.NewGuid().ToString()));
            Assert.True(call.Ok, string.Join(" ", call.Errors));
            Assert.Equal(new Models.FieldFollowUpCounts(1, 0), await env.Customers.CountDueAsync(a));

            Assert.False((await env.Visits.CreateAsync(a, Visit(id, photos: 4))).Ok);                 // limit 3
            Assert.False((await env.Visits.CreateAsync(a, Visit(id, lat: null))).Ok);                 // no GPS, no reason
            Assert.False((await env.Visits.CreateAsync(a, Visit(id, items: new[] { "Rose" }, qty: new[] { "-2" }))).Ok);

            var token = Guid.NewGuid().ToString();
            var next = AppClock.Today.AddDays(3);
            var v = await env.Visits.CreateAsync(a, Visit(id, token: token, photos: 2, status: "SampleGiven", next: next,
                items: new[] { "Rose sample", "Catalogue", "" }, qty: new[] { "10", "", "" }));
            Assert.True(v.Ok, string.Join(" ", v.Errors));
            Assert.False(v.AskNextDate);   // a next date was set
            var replay = await env.Visits.CreateAsync(a, Visit(id, token: token, photos: 2));
            Assert.True(replay.Ok && replay.Replay && replay.Id == v.Id);

            var visitId = v.Id!.Value;
            Assert.Equal(dayId, await ScalarAsync<int>(env, "SELECT FieldDayId FROM dbo.FieldVisits WHERE Id = @V", ("@V", visitId)));
            Assert.Equal(1, await ScalarAsync<int>(env, "SELECT COUNT(*) FROM dbo.FieldVisits WHERE CustomerId = @C", ("@C", id)));
            Assert.Equal(2, await ScalarAsync<int>(env, "SELECT COUNT(*) FROM dbo.FieldVisitPhotos WHERE VisitId = @V", ("@V", visitId)));
            Assert.Equal(2, await ScalarAsync<int>(env, "SELECT COUNT(*) FROM dbo.FieldVisitItems WHERE VisitId = @V", ("@V", visitId)));
            var timeline = await env.Customers.GetTimelineAsync(id);
            Assert.Equal("SampleGiven", (await env.Repo.GetAsync(id))!.LeadStatus);
            Assert.Contains(timeline.FollowUps, f => f.Status == "Done" && f.ClosedByVisitId == visitId);
            var pending = timeline.FollowUps.Single(f => f.Status == "Pending");
            Assert.Equal(next, pending.DueDate);
            Assert.Equal(new Models.FieldFollowUpCounts(0, 0), await env.Customers.CountDueAsync(a));
            Assert.Contains(timeline.Events, e => e.EventType == "StatusChanged" && e.FromStatus == "Contacted" && e.ToStatus == "SampleGiven" && e.VisitId == visitId);

            // a visit with poor GPS and a reason is saved flagged; with no next date it asks for one
            var poor = await env.Visits.CreateAsync(a, Visit(id, reason: "Inside cold store") with { AccuracyM = 400m });
            Assert.True(poor.Ok, string.Join(" ", poor.Errors));
            Assert.True(poor.AskNextDate);
            Assert.Contains("poor GPS", await ScalarAsync<string>(env, "SELECT FlagReasons FROM dbo.FieldVisits WHERE Id = @V", ("@V", poor.Id!.Value)));

            // the photos are served for this customer only
            var photoId = await ScalarAsync<int>(env, "SELECT TOP 1 PhotoId FROM dbo.FieldVisitPhotos WHERE VisitId = @V", ("@V", visitId));
            Assert.NotNull(await env.Customers.GetPhotoAsync(a, id, photoId));
            Assert.Null(await env.Customers.GetPhotoAsync(env.Employee(env.B), id, photoId));
            var other = await env.Customers.CreateAsync(a, NewCustomer(UniquePhone()));
            Assert.Null(await env.Customers.GetPhotoAsync(a, other.Id!.Value, photoId));   // right owner, wrong customer id
        }

        [SkippableFact]
        public async Task FollowUps_DueAndOverdue_CallCheckbox_AskNext_OnePendingPerCustomer_ManagerOverdueView()
        {
            var env = await OpenAsync();
            var a = env.Employee(env.A);
            var c1 = (await env.Customers.CreateAsync(a, NewCustomer(UniquePhone()))).Id!.Value;
            var c2 = (await env.Customers.CreateAsync(a, NewCustomer(UniquePhone()))).Id!.Value;

            Assert.True((await env.Customers.SetFollowUpAsync(a, c1, AppClock.Today, "Call back")).Ok);
            Assert.False((await env.Customers.SetFollowUpAsync(a, c1, AppClock.Today.AddDays(2), null)).Ok);   // one pending per customer
            Assert.False((await env.Customers.SetFollowUpAsync(a, c2, AppClock.Today.AddDays(-1), null)).Ok); // no past dates
            // an overdue one (inserted directly, as if set earlier)
            await ExecAsync(env, @"INSERT INTO dbo.FieldFollowUps (CustomerId, DueDate, Status, CreatedByUserId) VALUES (@C, DATEADD(DAY, -3, @T), 'Pending', @U)",
                ("@C", c2), ("@T", AppClock.Today), ("@U", env.A));
            Assert.Equal(new Models.FieldFollowUpCounts(1, 1), await env.Customers.CountDueAsync(a));

            var overdue = await env.Customers.ListFollowUpsAsync(env.Manager, env.A, overdueOnly: true);
            Assert.Single(overdue);
            Assert.Equal(c2, overdue[0].CustomerId);
            Assert.Equal(2, (await env.Customers.ListFollowUpsAsync(a, null, overdueOnly: true)).Count);   // employees always see all their pending

            // unticked "This completes the follow-up" + a next date: refused (one pending per customer)
            Assert.False((await env.Customers.AddNoteAsync(a, new FieldCustomerService.NoteRequest(c1, "Call", "No answer", null, null, false,
                AppClock.Today.AddDays(1), null, Guid.NewGuid().ToString()))).Ok);
            // unticked, no next date: saved, follow-up stays pending
            Assert.True((await env.Customers.AddNoteAsync(a, new FieldCustomerService.NoteRequest(c1, "Call", "No answer", null, null, false, null, null,
                Guid.NewGuid().ToString()))).Ok);
            Assert.Equal(new Models.FieldFollowUpCounts(1, 1), await env.Customers.CountDueAsync(a));
            // ticked, no next date: completed, and the page should ask for the next date
            var done = await env.Customers.AddNoteAsync(a, new FieldCustomerService.NoteRequest(c1, "Call", "Spoke, will order next month", null, "Interested",
                true, null, null, Guid.NewGuid().ToString()));
            Assert.True(done.Ok && done.AskNextDate);
            Assert.Equal(new Models.FieldFollowUpCounts(0, 1), await env.Customers.CountDueAsync(a));

            // "no follow-up set" filter: Interested without a pending follow-up
            var filter = FieldCustomerService.NormalizeFilter(null, null, null, null, null, null, true, false, null, 1, false, Array.Empty<string>(), Array.Empty<int>());
            var (rows, _) = await env.Customers.ListAsync(a, filter);
            Assert.Contains(rows, r => r.Id == c1);
            Assert.True((await env.Customers.SetFollowUpAsync(a, c1, AppClock.Today.AddDays(30), null)).Ok);
            (rows, _) = await env.Customers.ListAsync(a, filter);
            Assert.DoesNotContain(rows, r => r.Id == c1);

            // cancel needs a reason; an archived customer's follow-ups leave the counts
            var f2 = (await env.Customers.GetTimelineAsync(c2)).FollowUps.Single(f => f.Status == "Pending");
            Assert.False((await env.Customers.CancelFollowUpAsync(a, f2.Id, " ")).Ok);
            Assert.False((await env.Customers.CancelFollowUpAsync(env.Employee(env.B), f2.Id, "x")).Ok);
            Assert.True((await env.Customers.ArchiveAsync(env.Manager, c2, "Duplicate entry")).Ok);
            Assert.Equal(new Models.FieldFollowUpCounts(0, 0), await env.Customers.CountDueAsync(a));
        }

        [SkippableFact]
        public async Task Reassign_SingleAndBulk_MovesPendingFollowUps_IsAtomicAndRepeatable_NeedsReassigningForDeactivated()
        {
            var env = await OpenAsync();
            var a = env.Employee(env.A);
            var b = env.Employee(env.B);
            var ids = new List<int>();
            for (var i = 0; i < 3; i++)
            {
                var id = (await env.Customers.CreateAsync(a, NewCustomer(UniquePhone()))).Id!.Value;
                Assert.True((await env.Customers.SetFollowUpAsync(a, id, AppClock.Today, null)).Ok);
                ids.Add(id);
            }
            Assert.Equal(3, (await env.Customers.CountDueAsync(a)).DueToday);

            Assert.False((await env.Customers.ReassignAsync(a, ids, env.A, env.B)).Ok);           // employees cannot
            Assert.False((await env.Customers.ReassignAsync(env.Manager, ids, env.A, env.M)).Ok); // M is not a field employee
            var moved = await env.Customers.ReassignAsync(env.Manager, ids.Take(2).ToList(), env.A, env.B, "Territory change");
            Assert.True(moved.Ok && moved.Id == 2, string.Join(" ", moved.Errors));
            Assert.Equal(1, (await env.Customers.CountDueAsync(a)).DueToday);
            Assert.Equal(2, (await env.Customers.CountDueAsync(b)).DueToday);   // follow-ups moved with the customers
            var again = await env.Customers.ReassignAsync(env.Manager, ids.Take(2).ToList(), env.A, env.B);
            Assert.True(again.Ok && again.Id == 0);                             // repeated submit: nothing moved twice
            Assert.Equal(2, await ScalarAsync<int>(env, "SELECT COUNT(*) FROM dbo.FieldCustomerEvents WHERE EventType = 'Reassigned' AND ToUserId = @B AND CustomerId IN (@X, @Y)",
                ("@B", env.B), ("@X", ids[0]), ("@Y", ids[1])));
            // a selection that no longer belongs to the "from" employee (ids[2] is A's, not M's) moves NOTHING
            var stale = await env.Customers.ReassignAsync(env.Manager, ids, env.M, env.B);
            Assert.False(stale.Ok);
            Assert.Equal(env.A, (await env.Repo.GetAsync(ids[2]))!.AssignedUserId);
            Assert.Equal(env.B, (await env.Repo.GetAsync(ids[0]))!.AssignedUserId);

            // A deactivated: A's customers "need reassigning"; nobody can move customers TO A
            await ExecAsync(env, "UPDATE dbo.IMSUsers SET IsActive = 0 WHERE Id = @U", ("@U", env.A));
            try
            {
                Assert.True(await env.Customers.CountNeedingReassignAsync() >= 1);
                var filter = FieldCustomerService.NormalizeFilter(null, null, null, null, null, null, false, true, null, 1, true, Array.Empty<string>(), Array.Empty<int>());
                var (rows, _) = await env.Customers.ListAsync(env.Manager, filter);
                Assert.Contains(rows, r => r.Id == ids[2] && !r.AssignedUserActive);
                Assert.DoesNotContain(rows, r => r.Id == ids[0]);
                Assert.False((await env.Customers.ReassignAsync(env.Manager, new[] { ids[0] }, env.B, env.A)).Ok);
                Assert.True((await env.Customers.ReassignAsync(env.Manager, new[] { ids[2] }, env.A, env.B)).Ok);
            }
            finally
            {
                await ExecAsync(env, "UPDATE dbo.IMSUsers SET IsActive = 1 WHERE Id = @U", ("@U", env.A));
            }
        }

        [SkippableFact]
        public async Task VisitsAreImmutable_ExceptManagerTextEdit_Logged_EmployeeCorrectionIsANote_DatabaseGuards()
        {
            var env = await OpenAsync();
            var a = env.Employee(env.A);
            var id = (await env.Customers.CreateAsync(a, NewCustomer(UniquePhone()))).Id!.Value;
            await StartDayAsync(env, env.A);
            var visitId = (await env.Visits.CreateAsync(a, Visit(id, items: new[] { "Rose sample" }, qty: new[] { "5" }))).Id!.Value;

            Assert.False((await env.Visits.EditAsync(a, visitId, "changed", null, Array.Empty<string>(), Array.Empty<string>())).Ok);
            var edit = await env.Visits.EditAsync(env.Manager, visitId, "Discussed rose and gerbera", "Corrected by manager", new[] { "Rose sample" }, new[] { "8" });
            Assert.True(edit.Ok, string.Join(" ", edit.Errors));
            var ev = (await env.Customers.GetTimelineAsync(id)).Events.First(e => e.EventType == "VisitEdited");
            Assert.Contains("Discussed was: \"Discussed rose varieties\"", ev.Details);
            Assert.Contains("Rose sample × 5", ev.Details);

            var correction = await env.Customers.AddNoteAsync(a, new FieldCustomerService.NoteRequest(id, null, "It was 6 samples, not 5", visitId, "Won", true,
                AppClock.Today, null, Guid.NewGuid().ToString()));
            Assert.True(correction.Ok, string.Join(" ", correction.Errors));
            var t = await env.Customers.GetTimelineAsync(id);
            Assert.Contains(t.Notes, n => n.NoteType == "Correction" && n.VisitId == visitId);
            Assert.NotEqual("Won", (await env.Repo.GetAsync(id))!.LeadStatus);              // a correction never changes the status
            Assert.DoesNotContain(t.FollowUps, f => f.Status == "Pending");                  // ... nor sets follow-ups
            Assert.False((await env.Customers.AddNoteAsync(env.Employee(env.B), new FieldCustomerService.NoteRequest(id, null, "x", visitId, null, false, null, null,
                Guid.NewGuid().ToString()))).Ok);

            async Task<int> Fails(string sql)
            {
                var ex = await Assert.ThrowsAsync<SqlException>(() => ExecAsync(env, sql, ("@V", visitId), ("@C", id)));
                return ex.Number;
            }
            Assert.Equal(54252, await Fails("UPDATE dbo.FieldVisits SET CheckInAtUtc = DATEADD(HOUR, -5, CheckInAtUtc) WHERE Id = @V"));
            Assert.Equal(54252, await Fails("UPDATE dbo.FieldVisits SET Latitude = Latitude + 1 WHERE Id = @V"));
            Assert.Equal(54256, await Fails("UPDATE dbo.FieldCustomerNotes SET Text = N'x' WHERE CustomerId = @C"));
            Assert.Equal(54260, await Fails("UPDATE dbo.FieldCustomerEvents SET Details = N'x' WHERE CustomerId = @C"));
            // a visit on a closed day is refused by the database too
            await ExecAsync(env, "UPDATE dbo.FieldDays SET Status = 'AutoClosed', AutoClosedAtUtc = SYSUTCDATETIME(), ReviewStatus = 'Flagged' WHERE UserId = @U AND Status = 'Open'",
                ("@U", env.A));
            Assert.False((await env.Visits.CreateAsync(a, Visit(id))).Ok);
            Assert.Equal(54251, await Fails(@"INSERT INTO dbo.FieldVisits (CustomerId, UserId, FieldDayId, Latitude, Longitude, AccuracyM, Discussed, LeadStatusBefore, LeadStatusAfter, SubmissionToken)
SELECT @C, v.UserId, v.FieldDayId, 1, 1, 5, N'x', 'New', 'New', NEWID() FROM dbo.FieldVisits v WHERE v.Id = @V"));
        }

        [SkippableFact]
        public async Task ConcurrentDoubleSubmit_OfOneVisit_SavesOneVisit()
        {
            var env = await OpenAsync();
            var a = env.Employee(env.A);
            var id = (await env.Customers.CreateAsync(a, NewCustomer(UniquePhone()))).Id!.Value;
            await StartDayAsync(env, env.A);
            var token = Guid.NewGuid().ToString();
            var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(() => env.Visits.CreateAsync(a, Visit(id, token: token, photos: 1)))));
            Assert.All(results, r => Assert.True(r.Ok || r.Errors.Any(e => e.Contains("already saved")), string.Join(" ", r.Errors)));
            Assert.Equal(1, await ScalarAsync<int>(env, "SELECT COUNT(*) FROM dbo.FieldVisits WHERE CustomerId = @C", ("@C", id)));
            Assert.Equal(1, await ScalarAsync<int>(env, "SELECT COUNT(*) FROM dbo.FieldVisitPhotos vp JOIN dbo.FieldVisits v ON v.Id = vp.VisitId WHERE v.CustomerId = @C", ("@C", id)));
        }
    }
}
