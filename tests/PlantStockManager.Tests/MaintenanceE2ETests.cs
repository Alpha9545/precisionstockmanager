using System.IO;
using System.Security.Claims;
using System.Text;
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
using AssignedPage = PlantStockManager.Pages.Maintenance.AssignedModel;
using DetailsPage = PlantStockManager.Pages.Maintenance.DetailsModel;
using MyTicketsPage = PlantStockManager.Pages.Maintenance.MyTicketsModel;
using RaisePage = PlantStockManager.Pages.Maintenance.RaiseModel;
using ReportsPage = PlantStockManager.Pages.Maintenance.ReportsModel;

namespace PlantStockManager.Tests
{
    // Maintenance Tickets -- end-to-end: the REAL Raise / Details / My Tickets / Assigned / Reports
    // pages, MaintenanceRepository, MaintenanceAuthorization, SubmissionTokenGuard and
    // MaintenancePhotoStore (a temporary photo folder), with REAL login claims, against a SCRATCH
    // COPY that has Database/Migrations/2026-10-06_MaintenanceTickets.sql applied
    // (PSM_SCRATCH_CONNECTION; the name must start with PlantsIMS2_Scratch_). The worker logins of
    // Database/ZZTEST_MaintenanceUsers.sql are created on the scratch copy if missing. Every ticket
    // a test raises is titled "ZZTEST ..." and removed again (its events / photo rows cascade).
    //
    //   6   Akshay    Sowing Supervisor        Area 2 (Main-Office-Areas; Polyhouse 2 Facility-5)
    //   4   Kanchan   Office Coordinator       Area 2
    //   11  Mahadev   Fertilizer Supervisor    Area 2
    //   110 Vasant    Mother Plant Supervisor  Area 127 (Green Bless; Polyhouse 14)
    //   15  Rahul     Booking Executive        Area 2 (no Maintenance permission)
    //   21  Prajwal   System Administrator
    //   zztest.mt.electrician1 / electrician2 (Electrician), zztest.mt.plumber (Plumber)
    [Collection("ScratchDb")]
    public class MaintenanceE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";
        private const int OfficeCoordinator = 4, FertilizerSup = 11, MpSup = 110, BookingExec = 15, Admin = 21;
        // 2026-10-10: looked up per run (FixtureUsers) -- user 6 is no longer a Sowing Supervisor in the local data
        private static int SowingSup = 6;
        private const int MainOffice = 2, GreenBless = 127, Facility5 = 2, GreenBlessP1 = 14;

        private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52, 1, 2, 3, 4 };
        private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1, 5, 6, 7 };

        private sealed class NullTempData : ITempDataProvider
        {
            public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
            public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
        }

        private sealed class Env : IDisposable
        {
            public string ConnectionString { get; init; } = "";
            public ServiceProvider Provider { get; init; } = null!;
            public string PhotoRoot { get; init; } = "";
            public int Electrician1, Electrician2, Plumber, Electrical, Plumbing;
            public T Get<T>() where T : notnull => Provider.GetRequiredService<T>();
            public MaintenanceRepository Repo => Get<MaintenanceRepository>();

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

            public Task CleanupAsync() => ExecAsync("DELETE FROM dbo.MaintenanceTickets WHERE Title LIKE 'ZZTEST%'");

            public int PhotoFiles => Directory.Exists(PhotoRoot) ? Directory.GetFiles(PhotoRoot).Length : 0;

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

            public void Dispose()
            {
                try { if (Directory.Exists(PhotoRoot)) Directory.Delete(PhotoRoot, true); } catch (IOException) { }
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

            var photoRoot = Path.Combine(Path.GetTempPath(), "psm-mt-e2e-" + Guid.NewGuid().ToString("N"));
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs }).Build());
            services.AddSingleton<DatabaseHelper>();
            services.AddSingleton<AreaAccessService>();
            services.AddSingleton<SubmissionTokenGuard>();
            services.AddSingleton(new MaintenanceStorageOptions { PhotoRoot = photoRoot });
            services.AddSingleton<MaintenancePhotoStore>();
            services.AddScoped<MaintenanceAuthorization>();
            services.AddSingleton<IOptions<SecurityOptions>>(Options.Create(new SecurityOptions { FullAccessRoleNames = new[] { "System Administrator" } }));
            services.AddScoped<UserClaimsFactory>();
            foreach (var t in typeof(DatabaseHelper).Assembly.GetTypes().Where(t => t.Namespace == "PlantStockManager.Data" && t.IsClass && t.Name.EndsWith("Repository")))
                services.AddScoped(t);
            var env = new Env { ConnectionString = cs!, Provider = services.BuildServiceProvider(), PhotoRoot = photoRoot };
            Skip.If(await env.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID('dbo.MaintenanceTickets') IS NULL THEN 0 ELSE 1 END") == 0,
                "The scratch copy does not have 2026-10-06_MaintenanceTickets.sql applied.");

            // The worker logins (scratch copy only -- checked above).
            await env.ExecAsync(@"
DECLARE @users TABLE (Username NVARCHAR(100), Name NVARCHAR(100), RoleName NVARCHAR(100));
INSERT INTO @users VALUES (N'zztest.mt.electrician1', N'ZZTEST-MT-ELECTRICIAN-1', N'Electrician'),
                          (N'zztest.mt.electrician2', N'ZZTEST-MT-ELECTRICIAN-2', N'Electrician'),
                          (N'zztest.mt.plumber', N'ZZTEST-MT-PLUMBER', N'Plumber');
INSERT INTO dbo.IMSUsers (Username, Password, Name, IsActive)
SELECT u.Username, N'$2a$11$uyHITMZfN1Cit91MLQsY6umSRpOtcOJUyN9uHPNU.ZhebigEOdxUS', u.Name, 1 FROM @users u
WHERE NOT EXISTS (SELECT 1 FROM dbo.IMSUsers x WHERE x.Username = u.Username);
INSERT INTO dbo.UserRoles (UserId, RoleId, AreaId)
SELECT x.Id, r.Id, NULL FROM @users u JOIN dbo.IMSUsers x ON x.Username = u.Username
JOIN dbo.Roles r ON COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = u.RoleName
WHERE NOT EXISTS (SELECT 1 FROM dbo.UserRoles y WHERE y.UserId = x.Id AND y.RoleId = r.Id)");
            env.Electrician1 = await env.ScalarAsync<int>("SELECT Id FROM dbo.IMSUsers WHERE Username = 'zztest.mt.electrician1'");
            env.Electrician2 = await env.ScalarAsync<int>("SELECT Id FROM dbo.IMSUsers WHERE Username = 'zztest.mt.electrician2'");
            env.Plumber = await env.ScalarAsync<int>("SELECT Id FROM dbo.IMSUsers WHERE Username = 'zztest.mt.plumber'");
            env.Electrical = await env.ScalarAsync<int>("SELECT Id FROM dbo.MaintenanceCategories WHERE Name = 'Electrical'");
            env.Plumbing = await env.ScalarAsync<int>("SELECT Id FROM dbo.MaintenanceCategories WHERE Name = 'Plumbing'");
            await env.CleanupAsync();
            SowingSup = await env.ScalarAsync<int>(FixtureUsers.MainOfficeSowingSupervisorSql);
            Skip.If(SowingSup == 0, "No active Sowing Supervisor is assigned to the Main Office Area in this copy.");
            return env;
        }

        private static IFormFile Upload(byte[] content, string name)
            => new FormFile(new MemoryStream(content), 0, content.Length, "Photos", name) { Headers = new HeaderDictionary(), ContentType = "image/png" };

        private static async Task<(IActionResult Result, RaisePage Page)> RaiseAsync(Env env, int user, int area, int? polyhouse, int? category = null,
            string priority = "Urgent", string title = "ZZTEST fan motor is not working", List<IFormFile>? photos = null, string? token = null)
        {
            var page = env.Page<RaisePage>(await env.LoginAsync(user));
            page.CategoryId = category ?? env.Electrical; page.AreaId = area; page.PolyhouseId = polyhouse; page.Priority = priority;
            page.Title = title; page.Description = "Fan stopped working at approximately 11:30 AM.";
            page.Photos = photos ?? new List<IFormFile>();
            page.SubmitToken = token ?? SubmissionTokenGuard.NewToken();
            return (await page.OnPostAsync(), page);
        }

        private static int RaisedId(IActionResult r)
        {
            var redirect = Assert.IsType<RedirectToPageResult>(r);
            Assert.Equal("/Maintenance/Details", redirect.PageName);
            return (int)redirect.RouteValues!["id"]!;
        }

        // Runs one Details POST handler as the given user.
        private static async Task<(IActionResult Result, DetailsPage Page)> StepAsync(Env env, int user, int id, Func<DetailsPage, Task<IActionResult>> handler,
            Action<DetailsPage>? set = null, string? token = null)
        {
            var page = env.Page<DetailsPage>(await env.LoginAsync(user));
            page.Id = id;
            page.SubmitToken = token ?? SubmissionTokenGuard.NewToken();
            set?.Invoke(page);
            return (await handler(page), page);
        }

        private static string Done(IActionResult result, DetailsPage page)
        {
            var redirect = Assert.IsType<RedirectToPageResult>(result);
            Assert.Null(redirect.PageName);
            Assert.True(page.TempData.ContainsKey("Success"), page.TempData["Error"] as string ?? string.Join("; ", Errors(page)));
            return (string)page.TempData["Success"]!;
        }

        // A user who may take this step in principle, on a ticket that has already moved on:
        // told so, nothing happens (a user who may never take it gets 403 instead).
        private static void AssertTooLate((IActionResult Result, DetailsPage Page) step)
        {
            Assert.Null(Assert.IsType<RedirectToPageResult>(step.Result).PageName);
            Assert.Equal(MaintenanceRules.ChangedMessage, step.Page.TempData["Error"]);
            Assert.False(step.Page.TempData.ContainsKey("Success"));
        }

        private static string[] Errors(PageModel page) => page.ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).ToArray();
        private static DateTime Tomorrow => AppClock.Today.AddDays(1);

        // ---- creating -------------------------------------------------------------------------------

        [SkippableFact]
        public async Task Raise_WithPhotos_CreatesTicketEventsAndFilesOutsideWwwroot()
        {
            using var env = await OpenAsync();
            try
            {
                var (r, page) = await RaiseAsync(env, SowingSup, MainOffice, Facility5, photos: new() { Upload(Png, "fan.png"), Upload(Jpeg, "C:\\phone\\motor.jpg") });
                var id = RaisedId(r);
                var t = (await env.Repo.GetByIdAsync(id))!;
                Assert.Matches("^MT-\\d{6}$", t.TicketNo);
                Assert.Equal($"Ticket {t.TicketNo} raised (Electrical, Urgent).", page.TempData["Success"]);
                Assert.Equal((MaintenanceRules.Open, MainOffice, (int?)Facility5, SowingSup, (int?)null), (t.Status, t.AreaId, t.PolyhouseId, t.RequestedById, t.AssignedWorkerId));
                Assert.True((DateTime.UtcNow - t.RequestedAt).Duration() < TimeSpan.FromMinutes(5));   // server timestamp

                var photos = await env.Repo.GetPhotosAsync(id);
                Assert.Equal(new[] { "fan.png", "motor.jpg" }, photos.Select(p => p.OriginalFileName).OrderBy(x => x));
                Assert.All(photos, p => Assert.Matches("^[0-9a-f]{32}\\.(png|jpg)$", p.StoredFileName));
                Assert.Equal(new[] { "image/jpeg", "image/png" }, photos.Select(p => p.ContentType).OrderBy(x => x));
                Assert.Equal(2, env.PhotoFiles);
                Assert.DoesNotContain("wwwroot", env.Get<MaintenancePhotoStore>().Root);
                Assert.Equal(new[] { "Created", "PhotoAdded" }, (await env.Repo.GetEventsAsync(id)).Select(e => e.EventType));

                // Every approved supervisor role may raise; others may not.
                foreach (var u in new[] { SowingSup, OfficeCoordinator, FertilizerSup, MpSup })
                    Assert.True((await env.LoginAsync(u)).HasPermission("Maintenance.Raise"), $"user {u}");
                Assert.False((await env.LoginAsync(BookingExec)).HasPermission("Maintenance.Raise|Maintenance.View|Maintenance.Work"));
                Assert.False((await env.LoginAsync(env.Electrician1)).HasPermission("Maintenance.Raise"));
                RaisedId((await RaiseAsync(env, OfficeCoordinator, MainOffice, null, category: env.Plumbing, priority: "Normal", title: "ZZTEST tap leaking")).Result);
                RaisedId((await RaiseAsync(env, FertilizerSup, MainOffice, null, priority: "Critical", title: "ZZTEST pump")).Result);
            }
            finally { await env.CleanupAsync(); }
        }

        [SkippableFact]
        public async Task Raise_AreaAndPolyhouseTampering_AndBadInput_AreRejected_NothingSaved()
        {
            using var env = await OpenAsync();
            try
            {
                var (r, page) = await RaiseAsync(env, MpSup, MainOffice, Facility5);   // another Area
                Assert.IsType<PageResult>(r);
                Assert.Contains("You are not authorized to raise tickets for this Area.", Errors(page));
                (r, page) = await RaiseAsync(env, SowingSup, MainOffice, GreenBlessP1);   // a Polyhouse of another Area
                Assert.Contains("This Polyhouse does not belong to the selected Area.", Errors(page));
                (r, page) = await RaiseAsync(env, SowingSup, 999999, null);
                Assert.Contains("Choose an Area.", Errors(page));
                (r, page) = await RaiseAsync(env, SowingSup, MainOffice, null, category: 999999);
                Assert.Contains("Choose a category.", Errors(page));
                (r, page) = await RaiseAsync(env, SowingSup, MainOffice, null, priority: "Low");
                Assert.Contains("Choose a priority.", Errors(page));
                (r, page) = await RaiseAsync(env, SowingSup, MainOffice, null, title: "  ");
                Assert.Contains("Problem is required.", Errors(page));
                Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.MaintenanceTickets WHERE Title LIKE 'ZZTEST%'"));

                // The database is the final guard for the Polyhouse rule.
                var ex = await Assert.ThrowsAsync<SqlException>(() => env.Repo.CreateAsync(
                    new MaintenanceRepository.NewTicket(env.Electrical, MainOffice, GreenBlessP1, "ZZTEST x", "x", "Normal"), Admin, Array.Empty<MaintenancePhotoStore.SavedPhoto>()));
                Assert.Equal(51713, ex.Number);
            }
            finally { await env.CleanupAsync(); }
        }

        [SkippableFact]
        public async Task Photos_WrongContentOversizeOrTooMany_AreRejected_NoFilesLeft()
        {
            using var env = await OpenAsync();
            try
            {
                var exe = Encoding.ASCII.GetBytes("MZ\x90\0this is not a photo");
                var (r, page) = await RaiseAsync(env, SowingSup, MainOffice, null, photos: new() { Upload(exe, "fan.jpg") });
                Assert.Contains("fan.jpg: only JPEG, PNG or WebP photos are allowed.", Errors(page));
                var svg = Encoding.ASCII.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>");
                (r, page) = await RaiseAsync(env, SowingSup, MainOffice, null, photos: new() { Upload(svg, "fan.png") });
                Assert.Contains("fan.png: only JPEG, PNG or WebP photos are allowed.", Errors(page));
                var big = new byte[MaintenancePhotoStore.MaxBytes + 1];
                Png.CopyTo(big, 0);
                (r, page) = await RaiseAsync(env, SowingSup, MainOffice, null, photos: new() { Upload(big, "big.png") });
                Assert.Contains("big.png: a photo can be at most 5 MB.", Errors(page));
                (r, page) = await RaiseAsync(env, SowingSup, MainOffice, null, photos: Enumerable.Range(0, 6).Select(i => Upload(Png, $"p{i}.png")).ToList());
                Assert.Contains($"At most {MaintenanceRules.MaxPhotosPerStage} photos.", Errors(page));
                Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.MaintenanceTickets WHERE Title LIKE 'ZZTEST%'"));
                Assert.Equal(0, env.PhotoFiles);   // nothing written for rejected forms

                var id = RaisedId((await RaiseAsync(env, SowingSup, MainOffice, null, photos: Enumerable.Range(0, 5).Select(i => Upload(Png, $"p{i}.png")).ToList())).Result);
                Assert.Equal(5, (await env.Repo.GetPhotosAsync(id)).Count);
            }
            finally { await env.CleanupAsync(); }
        }

        [SkippableFact]
        public async Task Photo_Retrieval_IsAccessControlled()
        {
            using var env = await OpenAsync();
            try
            {
                var id = RaisedId((await RaiseAsync(env, SowingSup, MainOffice, Facility5, photos: new() { Upload(Png, "fan.png") })).Result);
                var other = RaisedId((await RaiseAsync(env, SowingSup, MainOffice, null, category: env.Plumbing, title: "ZZTEST other")).Result);
                var photo = (await env.Repo.GetPhotosAsync(id)).Single();

                async Task<IActionResult> Get(int user, int ticketId, int photoId)
                {
                    var page = env.Page<DetailsPage>(await env.LoginAsync(user), "GET");
                    page.Id = ticketId;
                    return await page.OnGetPhotoAsync(photoId);
                }
                var file = Assert.IsType<PhysicalFileResult>(await Get(SowingSup, id, photo.Id));
                Assert.Equal("image/png", file.ContentType);
                Assert.StartsWith(env.Get<MaintenancePhotoStore>().Root, file.FileName);
                Assert.IsType<PhysicalFileResult>(await Get(env.Electrician1, id, photo.Id));   // Open ticket of his trade
                Assert.IsType<ForbidResult>(await Get(env.Plumber, id, photo.Id));               // other trade
                Assert.IsType<ForbidResult>(await Get(MpSup, id, photo.Id));                     // other Area
                Assert.IsType<NotFoundResult>(await Get(SowingSup, other, photo.Id));            // photo id of another ticket
                Assert.IsType<NotFoundResult>(await Get(SowingSup, id, 999999));
            }
            finally { await env.CleanupAsync(); }
        }

        // ---- the complete workflow --------------------------------------------------------------------

        [SkippableFact]
        public async Task FullWorkflow_WithRejectedCompletion_ClosesWithEveryEventAndServerTimestamps()
        {
            using var env = await OpenAsync();
            try
            {
                var id = RaisedId((await RaiseAsync(env, SowingSup, MainOffice, Facility5)).Result);
                Func<DetailsPage, Task<IActionResult>> accept = p => p.OnPostAcceptAsync();

                // Accept: expected visit date required, not in the past; only the ticket's trade; never an administrator.
                var (r, page) = await StepAsync(env, env.Electrician1, id, accept, p => p.ExpectedVisitDate = null);
                Assert.Contains(MaintenanceRules.ExpectedDateRequiredMessage, Errors(page));
                (r, page) = await StepAsync(env, env.Electrician1, id, accept, p => p.ExpectedVisitDate = AppClock.Today.AddDays(-1));
                Assert.Contains(MaintenanceRules.ExpectedDatePastMessage, Errors(page));
                Assert.IsType<ForbidResult>((await StepAsync(env, env.Plumber, id, accept, p => p.ExpectedVisitDate = Tomorrow)).Result);
                Assert.IsType<ForbidResult>((await StepAsync(env, Admin, id, accept, p => p.ExpectedVisitDate = Tomorrow)).Result);
                (r, page) = await StepAsync(env, env.Electrician1, id, accept, p => { p.ExpectedVisitDate = Tomorrow; p.ExpectedVisitRemarks = "After lunch"; });
                Done(r, page);
                var t = (await env.Repo.GetByIdAsync(id))!;
                Assert.Equal((MaintenanceRules.Accepted, (int?)env.Electrician1, (DateTime?)Tomorrow, "After lunch"), (t.Status, t.AssignedWorkerId, t.ExpectedVisitDate, t.ExpectedVisitRemarks));

                // Arrive: only the assigned worker.
                Assert.IsType<ForbidResult>((await StepAsync(env, env.Electrician2, id, p => p.OnPostArriveAsync())).Result);
                Assert.IsType<ForbidResult>((await StepAsync(env, SowingSup, id, p => p.OnPostArriveAsync())).Result);
                (r, page) = await StepAsync(env, env.Electrician1, id, p => p.OnPostArriveAsync());
                Done(r, page);

                // Confirm arrival: a supervisor of the Area -- never the worker, never another Area.
                Assert.IsType<ForbidResult>((await StepAsync(env, env.Electrician1, id, p => p.OnPostConfirmArrivalAsync())).Result);
                Assert.IsType<ForbidResult>((await StepAsync(env, MpSup, id, p => p.OnPostConfirmArrivalAsync())).Result);
                (r, page) = await StepAsync(env, OfficeCoordinator, id, p => p.OnPostConfirmArrivalAsync());
                Done(r, page);

                // Start work: both texts required.
                (r, page) = await StepAsync(env, env.Electrician1, id, p => p.OnPostStartWorkAsync(), p => p.ProblemFound = "Burnt capacitor");
                Assert.Contains("What work is being performed is required.", Errors(page));
                (r, page) = await StepAsync(env, env.Electrician1, id, p => p.OnPostStartWorkAsync(), p => { p.ProblemFound = "Burnt capacitor"; p.WorkPerformed = "Replacing the capacitor"; });
                Done(r, page);

                // Resolve with a completion photo.
                (r, page) = await StepAsync(env, env.Electrician1, id, p => p.OnPostResolveAsync(), p =>
                {
                    p.WorkDone = "Capacitor replaced"; p.Resolution = "Fan running"; p.MaterialsUsed = "1 x 2.5 uF capacitor";
                    p.CompletionPhotos = new() { Upload(Png, "done.png") };
                });
                Done(r, page);
                Assert.Single(await env.Repo.GetPhotosAsync(id), ph => ph.Stage == "Completion");

                // Reject completion: reason required; back to In Progress, the work end cleared.
                Assert.IsType<ForbidResult>((await StepAsync(env, env.Electrician1, id, p => p.OnPostConfirmCompletionAsync())).Result);
                (r, page) = await StepAsync(env, SowingSup, id, p => p.OnPostRejectCompletionAsync(), p => p.Reason = " ");
                Assert.Contains(MaintenanceRules.ReasonRequiredMessage, Errors(page));
                (r, page) = await StepAsync(env, SowingSup, id, p => p.OnPostRejectCompletionAsync(), p => p.Reason = "Fan still noisy");
                Done(r, page);
                t = (await env.Repo.GetByIdAsync(id))!;
                Assert.Equal((MaintenanceRules.InProgress, (DateTime?)null), (t.Status, t.WorkEndedAt));
                // The worker's form starts from the previous resolution (nothing silently blanked).
                var reopened = env.Page<DetailsPage>(await env.LoginAsync(env.Electrician1), "GET");
                reopened.Id = id;
                Assert.IsType<PageResult>(await reopened.OnGetAsync());
                Assert.Equal(("Capacitor replaced", "Fan running", "1 x 2.5 uF capacitor"), (reopened.WorkDone, reopened.Resolution, reopened.MaterialsUsed));

                // Resolve again, then the supervisor closes.
                (r, page) = await StepAsync(env, env.Electrician1, id, p => p.OnPostResolveAsync(), p => { p.WorkDone = "Bearing greased"; p.Resolution = "Fan quiet"; });
                Done(r, page);
                Assert.IsType<ForbidResult>((await StepAsync(env, MpSup, id, p => p.OnPostConfirmCompletionAsync())).Result);
                (r, page) = await StepAsync(env, SowingSup, id, p => p.OnPostConfirmCompletionAsync());
                Assert.Equal("Completion confirmed. The ticket is closed.", Done(r, page));

                t = (await env.Repo.GetByIdAsync(id))!;
                Assert.Equal((MaintenanceRules.Closed, (int?)SowingSup, (int?)OfficeCoordinator, "Fan quiet"), (t.Status, t.CompletionConfirmedById, t.ArrivalConfirmedById, t.Resolution));
                // Server timestamps, in workflow order.
                var stamps = new[] { t.RequestedAt, t.AcceptedAt!.Value, t.ArrivedAt!.Value, t.ArrivalConfirmedAt!.Value, t.WorkStartedAt!.Value, t.WorkEndedAt!.Value, t.CompletionConfirmedAt!.Value, t.ClosedAt!.Value };
                Assert.Equal(stamps.OrderBy(x => x), stamps);
                Assert.True((DateTime.UtcNow - t.ClosedAt!.Value).Duration() < TimeSpan.FromMinutes(5));
                Assert.NotNull(t.ResponseTime);
                Assert.NotNull(t.WorkTime);
                Assert.NotNull(t.OnSiteTime);
                Assert.NotNull(t.ResolutionTime);

                // Every status change and workflow action is in the history.
                var events = await env.Repo.GetEventsAsync(id);
                Assert.Equal(new[] { "Created", "Accepted", "Arrived", "ArrivalConfirmed", "WorkStarted", "Resolved", "PhotoAdded", "CompletionRejected", "Resolved", "CompletionConfirmed" },
                    events.Select(e => e.EventType));
                Assert.Equal("Fan still noisy", events.Single(e => e.EventType == "CompletionRejected").Note);
                Assert.All(events.Where(e => e.EventType != "PhotoAdded" && e.EventType != "Created"), e => Assert.NotNull(e.FromStatus));
                Assert.Equal(new[] { env.Electrician1, OfficeCoordinator, SowingSup }, new[] { events[2].ActorId!.Value, events[3].ActorId!.Value, events[^1].ActorId!.Value });

                // Nothing more can happen to a closed ticket: the requester's cancel is refused with a message.
                AssertTooLate(await StepAsync(env, SowingSup, id, p => p.OnPostCancelAsync(), p => p.Reason = "x"));
                Assert.Equal(MaintenanceRules.Closed, (await env.Repo.GetByIdAsync(id))!.Status);
                var ex = await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync("UPDATE dbo.MaintenanceTicketEvents SET Note = 'x' WHERE TicketId = @I", ("@I", id)));
                Assert.Equal(51767, ex.Number);
            }
            finally { await env.CleanupAsync(); }
        }

        // ---- acceptance race, reassignment, cancellation ------------------------------------------------

        [SkippableFact]
        public async Task FirstWorkerToAcceptWins_Atomically()
        {
            using var env = await OpenAsync();
            try
            {
                var id = RaisedId((await RaiseAsync(env, SowingSup, MainOffice, null)).Result);
                var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(() =>
                    env.Repo.AcceptAsync(id, i % 2 == 0 ? env.Electrician1 : env.Electrician2, Tomorrow, null))).ToArray();
                var results = await Task.WhenAll(tasks);
                Assert.Single(results, x => x);
                var t = (await env.Repo.GetByIdAsync(id))!;
                Assert.Contains(t.AssignedWorkerId!.Value, new[] { env.Electrician1, env.Electrician2 });
                Assert.Single(await env.Repo.GetEventsAsync(id), e => e.EventType == "Accepted");

                // The loser, on the page: told the ticket changed, nothing happens.
                var loser = t.AssignedWorkerId == env.Electrician1 ? env.Electrician2 : env.Electrician1;
                var (r, page) = await StepAsync(env, loser, id, p => p.OnPostAcceptAsync(), p => p.ExpectedVisitDate = Tomorrow);
                Assert.IsType<ForbidResult>(r);   // not his ticket any more -- he cannot even see it now
                Assert.Equal(t.AssignedWorkerId, (await env.Repo.GetByIdAsync(id))!.AssignedWorkerId);

                // The database itself refuses a worker of another trade, whatever the caller.
                var plumbing = RaisedId((await RaiseAsync(env, SowingSup, MainOffice, null, category: env.Plumbing, title: "ZZTEST tap")).Result);
                Assert.False(await env.Repo.AcceptAsync(plumbing, env.Electrician1, Tomorrow, null));
                Assert.True(await env.Repo.AcceptAsync(plumbing, env.Plumber, Tomorrow, null));
            }
            finally { await env.CleanupAsync(); }
        }

        [SkippableFact]
        public async Task Administrator_AssignsAndReassigns_BeforeArrival_OnlyToTheTicketsTrade()
        {
            using var env = await OpenAsync();
            try
            {
                var id = RaisedId((await RaiseAsync(env, SowingSup, MainOffice, null)).Result);
                Func<DetailsPage, Task<IActionResult>> assign = p => p.OnPostAssignAsync();
                Assert.IsType<ForbidResult>((await StepAsync(env, SowingSup, id, assign, p => { p.WorkerId = env.Electrician1; p.ExpectedVisitDate = Tomorrow; })).Result);
                var (r, page) = await StepAsync(env, Admin, id, assign, p => { p.WorkerId = env.Plumber; p.ExpectedVisitDate = Tomorrow; });
                Assert.Contains("Choose an active Electrician.", Errors(page));
                (r, page) = await StepAsync(env, Admin, id, assign, p => { p.WorkerId = env.Electrician1; p.ExpectedVisitDate = Tomorrow; });
                Done(r, page);
                Assert.Equal((MaintenanceRules.Accepted, (int?)env.Electrician1), ((await env.Repo.GetByIdAsync(id))!.Status, (await env.Repo.GetByIdAsync(id))!.AssignedWorkerId));
                (r, page) = await StepAsync(env, Admin, id, assign, p => { p.WorkerId = env.Electrician2; p.ExpectedVisitDate = Tomorrow; });
                Assert.Equal("Worker changed.", Done(r, page));
                Assert.Equal(env.Electrician2, (await env.Repo.GetByIdAsync(id))!.AssignedWorkerId);
                var events = await env.Repo.GetEventsAsync(id);
                Assert.Equal(new[] { "Created", "Assigned", "Reassigned" }, events.Select(e => e.EventType));
                Assert.StartsWith("Worker changed from ZZTEST-MT-ELECTRICIAN-1 to ZZTEST-MT-ELECTRICIAN-2", events[^1].Note);
                // The old worker lost the ticket.
                Assert.IsType<ForbidResult>((await StepAsync(env, env.Electrician1, id, p => p.OnPostArriveAsync())).Result);

                // After arrival no reassignment.
                Assert.True(await env.Repo.ArriveAsync(id, env.Electrician2));
                AssertTooLate(await StepAsync(env, Admin, id, assign, p => { p.WorkerId = env.Electrician1; p.ExpectedVisitDate = Tomorrow; }));
                Assert.Equal(env.Electrician2, (await env.Repo.GetByIdAsync(id))!.AssignedWorkerId);
            }
            finally { await env.CleanupAsync(); }
        }

        [SkippableFact]
        public async Task Cancel_RequesterOrAdministrator_OnlyWhileOpenOrAccepted_ReasonRequired()
        {
            using var env = await OpenAsync();
            try
            {
                Func<DetailsPage, Task<IActionResult>> cancel = p => p.OnPostCancelAsync();
                var a = RaisedId((await RaiseAsync(env, SowingSup, MainOffice, null, title: "ZZTEST a")).Result);
                Assert.IsType<ForbidResult>((await StepAsync(env, OfficeCoordinator, a, cancel, p => p.Reason = "x")).Result);   // same Area, not the requester
                var (r, page) = await StepAsync(env, SowingSup, a, cancel, p => p.Reason = "");
                Assert.Contains(MaintenanceRules.ReasonRequiredMessage, Errors(page));
                (r, page) = await StepAsync(env, SowingSup, a, cancel, p => p.Reason = "Fixed it ourselves");
                Done(r, page);
                var t = (await env.Repo.GetByIdAsync(a))!;
                Assert.Equal((MaintenanceRules.Cancelled, (int?)SowingSup, "Fixed it ourselves"), (t.Status, t.CancelledById, t.CancelReason));

                var b = RaisedId((await RaiseAsync(env, SowingSup, MainOffice, null, title: "ZZTEST b")).Result);
                Assert.True(await env.Repo.AcceptAsync(b, env.Electrician1, Tomorrow, null));
                (r, page) = await StepAsync(env, Admin, b, cancel, p => p.Reason = "Duplicate");   // administrator, Accepted
                Done(r, page);

                var d = RaisedId((await RaiseAsync(env, SowingSup, MainOffice, null, title: "ZZTEST d")).Result);
                Assert.True(await env.Repo.AcceptAsync(d, env.Electrician1, Tomorrow, null));
                Assert.True(await env.Repo.ArriveAsync(d, env.Electrician1));
                AssertTooLate(await StepAsync(env, SowingSup, d, cancel, p => p.Reason = "too late"));
                Assert.Equal(MaintenanceRules.Arrived, (await env.Repo.GetByIdAsync(d))!.Status);
                Assert.False(await env.Repo.CancelAsync(d, SowingSup, false, "too late"));   // the repository guard too
            }
            finally { await env.CleanupAsync(); }
        }

        [SkippableFact]
        public async Task DuplicateSubmissions_AreStopped()
        {
            using var env = await OpenAsync();
            try
            {
                var token = SubmissionTokenGuard.NewToken();
                RaisedId((await RaiseAsync(env, SowingSup, MainOffice, null, token: token)).Result);
                var (r, page) = await RaiseAsync(env, SowingSup, MainOffice, null, token: token);
                Assert.Equal("/Maintenance/MyTickets", Assert.IsType<RedirectToPageResult>(r).PageName);
                Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.MaintenanceTickets WHERE Title LIKE 'ZZTEST%'"));

                var id = await env.ScalarAsync<int>("SELECT Id FROM dbo.MaintenanceTickets WHERE Title LIKE 'ZZTEST%'");
                var stepToken = SubmissionTokenGuard.NewToken();
                var (accepted, acceptPage) = await StepAsync(env, env.Electrician1, id, p => p.OnPostAcceptAsync(), p => p.ExpectedVisitDate = Tomorrow, stepToken);
                Done(accepted, acceptPage);
                var (again, againPage) = await StepAsync(env, env.Electrician1, id, p => p.OnPostArriveAsync(), token: stepToken);   // the same form token again
                Assert.IsType<RedirectToPageResult>(again);
                Assert.Equal(MaintenanceRules.AlreadySubmittedMessage, againPage.TempData["Error"]);
                Assert.Equal(MaintenanceRules.Accepted, (await env.Repo.GetByIdAsync(id))!.Status);
            }
            finally { await env.CleanupAsync(); }
        }

        // ---- visibility, overdue, reports ---------------------------------------------------------------

        [SkippableFact]
        public async Task Visibility_WorkersSeeOwnTrade_SupervisorsOwnAreas()
        {
            using var env = await OpenAsync();
            try
            {
                var electrical = RaisedId((await RaiseAsync(env, SowingSup, MainOffice, null)).Result);
                var plumbing = RaisedId((await RaiseAsync(env, SowingSup, MainOffice, null, category: env.Plumbing, title: "ZZTEST tap")).Result);
                var greenBless = RaisedId((await RaiseAsync(env, MpSup, GreenBless, GreenBlessP1, title: "ZZTEST GB light")).Result);

                async Task<AssignedPage> Assigned(int user) { var p = env.Page<AssignedPage>(await env.LoginAsync(user), "GET"); await p.OnGetAsync(); return p; }
                var e1 = await Assigned(env.Electrician1);
                Assert.Equal(new[] { "Electrician" }, e1.Trades);
                Assert.Contains(e1.Available, t => t.Id == electrical);
                Assert.Contains(e1.Available, t => t.Id == greenBless);       // workers serve every Area
                Assert.DoesNotContain(e1.Available, t => t.Id == plumbing);
                var plumber = await Assigned(env.Plumber);
                Assert.Contains(plumber.Available, t => t.Id == plumbing);
                Assert.DoesNotContain(plumber.Available, t => t.Id == electrical);
                Assert.False((await Assigned(Admin)).IsWorker);

                async Task<IActionResult> View(int user, int id) { var p = env.Page<DetailsPage>(await env.LoginAsync(user), "GET"); p.Id = id; return await p.OnGetAsync(); }
                Assert.IsType<ForbidResult>(await View(env.Plumber, electrical));
                Assert.IsType<ForbidResult>(await View(MpSup, electrical));
                Assert.IsType<ForbidResult>(await View(SowingSup, greenBless));
                Assert.IsType<PageResult>(await View(Admin, greenBless));
                Assert.IsType<NotFoundResult>(await View(SowingSup, 999999));

                async Task<MyTicketsPage> Mine(int user) { var p = env.Page<MyTicketsPage>(await env.LoginAsync(user), "GET"); await p.OnGetAsync(); return p; }
                var sowing = (await Mine(SowingSup)).Tickets.Where(t => t.Title.StartsWith("ZZTEST")).ToList();
                Assert.Equal(new[] { electrical, plumbing }.OrderBy(x => x), sowing.Select(t => t.Id).OrderBy(x => x));
                var mp = (await Mine(MpSup)).Tickets.Where(t => t.Title.StartsWith("ZZTEST")).ToList();
                Assert.Equal(greenBless, Assert.Single(mp).Id);
            }
            finally { await env.CleanupAsync(); }
        }

        [SkippableFact]
        public async Task Overdue_AndReports_CountsWorkHoursAndAreaScope()
        {
            using var env = await OpenAsync();
            try
            {
                // Critical, raised 5 hours ago and not accepted -> overdue.
                var late = RaisedId((await RaiseAsync(env, SowingSup, MainOffice, null, priority: "Critical", title: "ZZTEST late")).Result);
                await env.ExecAsync("UPDATE dbo.MaintenanceTickets SET RequestedAt = DATEADD(HOUR, -5, SYSUTCDATETIME()) WHERE Id = @I", ("@I", late));
                // Accepted with an expected visit date in the past -> overdue.
                var missed = RaisedId((await RaiseAsync(env, SowingSup, MainOffice, null, priority: "Normal", title: "ZZTEST missed")).Result);
                Assert.True(await env.Repo.AcceptAsync(missed, env.Electrician1, Tomorrow, null));
                await env.ExecAsync("UPDATE dbo.MaintenanceTickets SET ExpectedVisitDate = DATEADD(DAY, -2, ExpectedVisitDate) WHERE Id = @I", ("@I", missed));
                // A closed ticket with known work time (60 minutes) for the worker report.
                var done = RaisedId((await RaiseAsync(env, SowingSup, MainOffice, Facility5, priority: "Urgent", title: "ZZTEST done")).Result);
                Assert.True(await env.Repo.AcceptAsync(done, env.Electrician2, Tomorrow, null));
                Assert.True(await env.Repo.ArriveAsync(done, env.Electrician2));
                Assert.True(await env.Repo.ConfirmArrivalAsync(done, SowingSup));
                Assert.True(await env.Repo.StartWorkAsync(done, env.Electrician2, "p", "w"));
                Assert.True(await env.Repo.ResolveAsync(done, env.Electrician2, "d", "r", null, null, Array.Empty<MaintenancePhotoStore.SavedPhoto>()));
                Assert.True(await env.Repo.ConfirmCompletionAsync(done, SowingSup));
                await env.ExecAsync("UPDATE dbo.MaintenanceTickets SET WorkEndedAt = DATEADD(MINUTE, 60, WorkStartedAt), CompletionConfirmedAt = DATEADD(MINUTE, 61, WorkStartedAt), ClosedAt = DATEADD(MINUTE, 61, WorkStartedAt) WHERE Id = @I", ("@I", done));
                var gb = RaisedId((await RaiseAsync(env, MpSup, GreenBless, null, title: "ZZTEST GB")).Result);

                var lateTicket = (await env.Repo.GetByIdAsync(late))!;
                Assert.Equal("Not accepted within 4 h", lateTicket.OverdueReason(DateTime.UtcNow, AppClock.Today));
                Assert.Equal("Expected visit date passed", (await env.Repo.GetByIdAsync(missed))!.OverdueReason(DateTime.UtcNow, AppClock.Today));
                Assert.Null((await env.Repo.GetByIdAsync(done))!.OverdueReason(DateTime.UtcNow, AppClock.Today));

                async Task<ReportsPage> Report(int user, Action<ReportsPage>? set = null)
                {
                    var p = env.Page<ReportsPage>(await env.LoginAsync(user), "GET");
                    p.FromDate = AppClock.Today.AddDays(-1); p.ToDate = AppClock.Today;
                    set?.Invoke(p);
                    await p.OnGetAsync();
                    return p;
                }
                var admin = await Report(Admin);
                var mine = admin.Tickets.Where(t => t.Title.StartsWith("ZZTEST")).ToList();
                Assert.Equal(4, mine.Count);
                Assert.True(admin.Counts["Overdue"] >= 2);
                Assert.True(admin.Counts["Critical"] >= 1);
                Assert.True(admin.Counts["Closed"] >= 1);
                var worker2 = (await Report(Admin, p => p.WorkerId = env.Electrician2)).ByWorker.Single();
                Assert.Equal(("ZZTEST-MT-ELECTRICIAN-2", 1, 1, "1"), (worker2.Key, worker2.Tickets, worker2.Closed, MaintenanceRules.Hours(worker2.WorkTime)));
                Assert.Contains((await Report(Admin, p => p.CategoryId = env.Electrical)).ByCategory, r => r.Key == "Electrical");
                Assert.Contains((await Report(Admin, p => { p.AreaId = MainOffice; p.PolyhouseId = Facility5; })).ByPolyhouse, r => r.Key == "Main-Office-Areas / Facility-5");

                // A supervisor's report covers only his own Areas, even when asking for another.
                var mp = await Report(MpSup, p => p.AreaId = MainOffice);
                Assert.Null(mp.AreaId);
                Assert.Equal(gb, Assert.Single(mp.Tickets.Where(t => t.Title.StartsWith("ZZTEST"))).Id);
            }
            finally { await env.CleanupAsync(); }
        }
    }
}
