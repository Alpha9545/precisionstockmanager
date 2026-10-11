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
using PlantStockManager.Services;
using CreatePage = PlantStockManager.Pages.Suggestions.CreateModel;
using DetailsPage = PlantStockManager.Pages.Suggestions.DetailsModel;
using IndexPage = PlantStockManager.Pages.Suggestions.IndexModel;

namespace PlantStockManager.Tests
{
    // Suggestions & Problems -- end-to-end: the REAL pages, SuggestionRepository,
    // SuggestionAuthorization, SuggestionPhotoStore and SubmissionTokenGuard with REAL login claims,
    // against a SCRATCH COPY that has Database/Migrations/2026-10-07_SuggestionsProblems.sql applied
    // (PSM_SCRATCH_CONNECTION; the name must start with PlantsIMS2_Scratch_). Test posts are titled
    // "ZZSP ..." and removed again (a post row delete takes its comments / photos / history with it;
    // the application itself never deletes a post). Photos go to a temporary folder.
    //
    //   6   Akshay   Sowing Supervisor        Area 2 (MainOffice: Polyhouse 2 Facility-5, 3 Facility-3)
    //   4   Kanchan  Office Coordinator       Area 2
    //   110 Vasant   Mother Plant Supervisor  Area 127 (Green Bless; Polyhouse 14 P-1)
    //   21  Prajwal  System Administrator     (full access = Suggestion.Manage)
    //   Anonymous (-1)                        a principal that is NOT signed in
    //   zzsp.* users                          one test user per role, created and removed by the
    //                                         every-role test (Electrician / Welder / Plumber with no Area)
    //
    // The module is GLOBAL: every signed-in user, any role, any Area assignment (or none), may view,
    // post, comment and reply for every Area; only Suggestion.Manage (administrator) is restricted.
    [Collection("ScratchDb")]
    public class SuggestionE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";
        private const int SowingSup = 6, OfficeCoordinator = 4, MpSup = 110, Admin = 21, Anonymous = -1;
        private const int MainOffice = 2, GreenBless = 127, Facility5 = 2, GreenBlessP1 = 14;

        private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1, 0, 1, 0, 0 };

        private sealed class NullTempData : ITempDataProvider
        {
            public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
            public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
        }

        private sealed class Env
        {
            public string ConnectionString { get; init; } = "";
            public string PhotoRoot { get; init; } = "";
            public ServiceProvider Provider { get; init; } = null!;
            public T Get<T>() where T : notnull => Provider.GetRequiredService<T>();
            public SuggestionRepository Repo => Get<SuggestionRepository>();

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

            public async Task CleanupAsync()
            {
                await ExecAsync("DELETE FROM dbo.SuggestionPosts WHERE Title LIKE N'ZZSP%'");
                await ExecAsync(@"
DELETE ur FROM dbo.UserRoles ur JOIN dbo.IMSUsers u ON u.Id = ur.UserId WHERE u.Username LIKE N'zzsp.%';
DELETE FROM dbo.IMSUsers WHERE Username LIKE N'zzsp.%'");
                if (Directory.Exists(PhotoRoot)) Directory.Delete(PhotoRoot, true);
            }

            // A post written straight to the database with a chosen age (the application always
            // uses the database clock; only a test can create an "old" post).
            public Task<int> InsertPostAsync(string title, int area, int? polyhouse, int postedBy, double hoursAgo, string type = "Problem")
                => ScalarAsync<int>(@"
INSERT INTO dbo.SuggestionPosts (TypeCode, Title, Description, AreaId, PolyhouseId, PostedById, CreatedAt)
VALUES (@T, @Title, N'Test details', @A, @P, @U, DATEADD(MINUTE, -@M, SYSUTCDATETIME()));
SELECT CAST(SCOPE_IDENTITY() AS INT)", ("@T", type), ("@Title", title), ("@A", area), ("@P", polyhouse), ("@U", postedBy), ("@M", (int)(hoursAgo * 60)));

            public async Task<ClaimsPrincipal> LoginAsync(int userId)
            {
                if (userId == Anonymous)
                    return new ClaimsPrincipal(new ClaimsIdentity());   // not signed in
                var factory = Get<UserClaimsFactory>();
                var user = await factory.GetActiveUserAsync(userId);
                Assert.NotNull(user);
                return await factory.CreatePrincipalAsync(user!);
            }

            // A test user holding exactly one role (and that role's usual Area, or none).
            public Task<int> CreateUserAsync(string role, int? area)
                => ScalarAsync<int>(@"
INSERT INTO dbo.IMSUsers (Username, Password, Name, IsActive) VALUES (@U, N'not-a-login', @N, 1);
DECLARE @Id INT = CAST(SCOPE_IDENTITY() AS INT);
INSERT INTO dbo.UserRoles (UserId, RoleId, AreaId)
SELECT @Id, r.Id, @A FROM dbo.Roles r WHERE COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = @R;
IF @@ROWCOUNT <> 1 THROW 50000, 'Role not found', 1;
SELECT @Id", ("@U", "zzsp." + Guid.NewGuid().ToString("N")[..12]), ("@N", "ZZSP " + role), ("@A", area), ("@R", role));

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

            var photoRoot = Path.Combine(Path.GetTempPath(), "psm-sp-e2e-" + Guid.NewGuid().ToString("N"));
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs }).Build());
            services.AddSingleton<DatabaseHelper>();
            services.AddSingleton<AreaAccessService>();
            services.AddSingleton<SubmissionTokenGuard>();
            services.AddScoped<SuggestionAuthorization>();
            services.AddSingleton(new SuggestionStorageOptions { PhotoRoot = photoRoot });
            services.AddSingleton<SuggestionPhotoStore>();
            services.AddSingleton<IOptions<SecurityOptions>>(Options.Create(new SecurityOptions { FullAccessRoleNames = new[] { "System Administrator" } }));
            services.AddScoped<UserClaimsFactory>();
            foreach (var t in typeof(DatabaseHelper).Assembly.GetTypes().Where(t => t.Namespace == "PlantStockManager.Data" && t.IsClass && t.Name.EndsWith("Repository")))
                services.AddScoped(t);
            var env = new Env { ConnectionString = cs!, PhotoRoot = photoRoot, Provider = services.BuildServiceProvider() };
            Skip.If(await env.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID('dbo.SuggestionPosts') IS NULL THEN 0 ELSE 1 END") == 0,
                "The scratch copy does not have 2026-10-07_SuggestionsProblems.sql applied.");
            await env.CleanupAsync();
            return env;
        }

        private static string[] Errors(PageModel page) => page.ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).ToArray();

        private static IFormFile Upload(byte[] content, string name)
            => new FormFile(new MemoryStream(content), 0, content.Length, "Photos", name) { Headers = new HeaderDictionary(), ContentType = "image/jpeg" };

        private static async Task<(IActionResult Result, CreatePage Page)> CreateAsync(Env env, int user, string type, int area, int? polyhouse, string title,
            string? description = "Water is leaking near the entrance. Please check and repair it.", IFormFile[]? photos = null, string? token = null)
        {
            var page = env.Page<CreatePage>(await env.LoginAsync(user));
            page.TypeCode = type; page.AreaId = area; page.PolyhouseId = polyhouse; page.Title = title; page.Description = description;
            page.Photos = (photos ?? Array.Empty<IFormFile>()).ToList();
            page.SubmitToken = token ?? SubmissionTokenGuard.NewToken();
            return (await page.OnPostAsync(), page);
        }

        private static int PostId(IActionResult r)
        {
            var redirect = Assert.IsType<RedirectToPageResult>(r);
            Assert.Equal("/Suggestions/Details", redirect.PageName);
            return (int)redirect.RouteValues!["id"]!;
        }

        private static async Task<(IActionResult Result, DetailsPage Page)> DetailsAsync(Env env, int user, int id, Func<DetailsPage, Task<IActionResult>> handler,
            Action<DetailsPage>? set = null, string? token = null, string method = "POST")
        {
            var page = env.Page<DetailsPage>(await env.LoginAsync(user), method);
            page.Id = id;
            page.SubmitToken = token ?? SubmissionTokenGuard.NewToken();
            set?.Invoke(page);
            return (await handler(page), page);
        }

        private static async Task<IndexPage> ListAsync(Env env, int user, Action<IndexPage>? set = null)
        {
            var page = env.Page<IndexPage>(await env.LoginAsync(user), "GET");
            set?.Invoke(page);
            Assert.IsType<PageResult>(await page.OnGetAsync());
            return page;
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

        private static Task<int> CountAsync(Env env, string table, string where, params (string, object?)[] args)
            => env.ScalarAsync<int>($"SELECT COUNT(*) FROM dbo.{table} WHERE {where}", args);

        // ---- post creation -------------------------------------------------------------------------

        [SkippableFact]
        public async Task CreateProblemWithPhoto_AndSuggestion_ServerSetsLifetime()
        {
            var env = await OpenAsync();
            try
            {
                var (r, _) = await CreateAsync(env, SowingSup, "Problem", MainOffice, Facility5, "ZZSP Irrigation pipe leakage", photos: new[] { Upload(Jpeg, "IMG_2041.jpg") });
                var id = PostId(r);
                var post = (await env.Repo.GetPostAsync(id))!;
                Assert.Matches("^SP-\\d{6}$", post.PostNo);
                Assert.Equal(("Problem", MainOffice, (int?)Facility5, SowingSup, "Open", false, false, false),
                    (post.TypeCode, post.AreaId, post.PolyhouseId, post.PostedById, post.Status, post.IsPermanent, post.IsDeleted, post.IsExpired));
                Assert.Equal(TimeSpan.FromHours(48), post.ExpiresAt - post.CreatedAt);                 // set by the database
                Assert.InRange((DateTime.UtcNow - post.CreatedAt).TotalMinutes, -1, 1);
                var photo = (await env.Repo.GetPhotoAsync(id))!;
                Assert.Equal(("image/jpeg", "IMG_2041.jpg"), (photo.ContentType, photo.OriginalFileName));
                Assert.Matches("^[0-9a-f]{32}\\.jpg$", photo.StoredFileName);
                Assert.Equal(Jpeg, await File.ReadAllBytesAsync(Path.Combine(env.PhotoRoot, photo.StoredFileName)));
                Assert.Equal(1, await CountAsync(env, "SuggestionPostEvents", "PostId = @P AND EventType = 'Created' AND ActorId = @U", ("@P", id), ("@U", SowingSup)));

                var (r2, _) = await CreateAsync(env, MpSup, "Suggestion", GreenBless, null, "ZZSP Shade net for P-1", "Add a shade net on the west side.");
                var suggestion = (await env.Repo.GetPostAsync(PostId(r2)))!;
                Assert.Equal(("Suggestion", GreenBless, (int?)null, (int?)null), (suggestion.TypeCode, suggestion.AreaId, suggestion.PolyhouseId, suggestion.PhotoId));
                // The page has no way to receive a lifetime, a permanent flag or a status from the browser.
                foreach (var name in new[] { "ExpiresAt", "CreatedAt", "IsPermanent", "Status", "PostedById" })
                    Assert.Null(typeof(CreatePage).GetProperty(name));
            }
            finally { await env.CleanupAsync(); }
        }

        [SkippableFact]
        public async Task Create_Validation_AreaAndPolyhouseTampering_AndUnauthorizedUser()
        {
            var env = await OpenAsync();
            try
            {
                var (_, p) = await CreateAsync(env, SowingSup, "Like", MainOffice, Facility5, "  ", description: " ");
                Assert.Contains("Choose a type.", Errors(p));
                Assert.Contains("Title is required.", Errors(p));
                Assert.Contains("Details is required.", Errors(p));
                (_, p) = await CreateAsync(env, SowingSup, "Problem", MainOffice, Facility5, new string('x', 151));
                Assert.Contains("Title cannot be longer than 150 characters.", Errors(p));
                (_, p) = await CreateAsync(env, SowingSup, "Problem", MainOffice, null, "ZZSP no polyhouse");
                Assert.Contains(SuggestionRules.PolyhouseRequiredMessage, Errors(p));
                (_, p) = await CreateAsync(env, SowingSup, "Problem", MainOffice, GreenBlessP1, "ZZSP wrong polyhouse");          // PolyhouseId tampering
                Assert.Contains(SuggestionRules.PolyhouseNotInAreaMessage, Errors(p));
                (_, p) = await CreateAsync(env, SowingSup, "Problem", 99999999, null, "ZZSP unknown area");                       // AreaId tampering
                Assert.Contains(SuggestionRules.ChooseAreaMessage, Errors(p));
                (_, p) = await CreateAsync(env, SowingSup, "Problem", GreenBless, Facility5, "ZZSP other area wrong polyhouse");    // Area 127 + a Polyhouse of Area 2
                Assert.Contains(SuggestionRules.PolyhouseNotInAreaMessage, Errors(p));
                (_, p) = await CreateAsync(env, SowingSup, "Problem", MainOffice, Facility5, "ZZSP fake photo", photos: new[] { Upload(System.Text.Encoding.ASCII.GetBytes("<html><script>x</script>"), "leak.jpg") });
                Assert.Contains("leak.jpg: only JPEG, PNG or WebP photos are allowed.", Errors(p));
                (_, p) = await CreateAsync(env, SowingSup, "Problem", MainOffice, Facility5, "ZZSP two photos", photos: new[] { Upload(Jpeg, "a.jpg"), Upload(Jpeg, "b.jpg") });
                Assert.Contains("Attach one photo -- either from the camera or from the gallery.", Errors(p));

                // A request that is not signed in is refused outright (the pages also need sign-in).
                var anonymous = await env.LoginAsync(Anonymous);
                Assert.IsType<ForbidResult>(await env.Page<CreatePage>(anonymous, "GET").OnGetAsync());
                var (forbidden, _) = await CreateAsync(env, Anonymous, "Problem", MainOffice, Facility5, "ZZSP anonymous");
                Assert.IsType<ForbidResult>(forbidden);
                Assert.IsType<ForbidResult>(await env.Page<IndexPage>(anonymous, "GET").OnGetAsync());

                Assert.Equal(0, await CountAsync(env, "SuggestionPosts", "Title LIKE N'ZZSP%' OR Title LIKE N'xxxx%'"));
                Assert.False(Directory.Exists(env.PhotoRoot) && Directory.EnumerateFiles(env.PhotoRoot).Any());   // nothing written for refused posts
            }
            finally { await env.CleanupAsync(); }
        }

        [SkippableFact]
        public async Task DuplicateSubmission_CreatesOnePost_AndOneComment()
        {
            var env = await OpenAsync();
            try
            {
                var token = SubmissionTokenGuard.NewToken();
                await CreateAsync(env, SowingSup, "Problem", MainOffice, Facility5, "ZZSP double click", token: token);
                var (second, _) = await CreateAsync(env, SowingSup, "Problem", MainOffice, Facility5, "ZZSP double click", token: token);
                Assert.Equal("/Suggestions/Index", Assert.IsType<RedirectToPageResult>(second).PageName);
                Assert.Equal(1, await CountAsync(env, "SuggestionPosts", "Title = N'ZZSP double click'"));

                var id = await env.ScalarAsync<int>("SELECT Id FROM dbo.SuggestionPosts WHERE Title = N'ZZSP double click'");
                var commentToken = SubmissionTokenGuard.NewToken();
                Done(await DetailsAsync(env, OfficeCoordinator, id, p => p.OnPostCommentAsync(), p => p.CommentText = "Seen it.", commentToken));
                Assert.Equal(SuggestionRules.AlreadySubmittedMessage, Refused(await DetailsAsync(env, OfficeCoordinator, id, p => p.OnPostCommentAsync(), p => p.CommentText = "Seen it.", commentToken)));
                Assert.Equal(1, await CountAsync(env, "SuggestionComments", "PostId = @P", ("@P", id)));
            }
            finally { await env.CleanupAsync(); }
        }

        // ---- lifetime, visibility, administrator actions ------------------------------------------

        [SkippableFact]
        public async Task Expiry_IsAutomatic_AdminSeesExpired_PermanentRevivesIt()
        {
            var env = await OpenAsync();
            try
            {
                var fresh = await env.InsertPostAsync("ZZSP 47 hours old", MainOffice, Facility5, SowingSup, 47);
                var old = await env.InsertPostAsync("ZZSP 49 hours old", MainOffice, Facility5, SowingSup, 49);
                Assert.False((await env.Repo.GetPostAsync(fresh))!.IsExpired);
                Assert.True((await env.Repo.GetPostAsync(old))!.IsExpired);

                var feed = await ListAsync(env, OfficeCoordinator);
                Assert.Contains(feed.Posts, p => p.Id == fresh);
                Assert.DoesNotContain(feed.Posts, p => p.Id == old);
                var peek = await ListAsync(env, OfficeCoordinator, p => p.Lifetime = "Expired");     // employees cannot ask for expired posts
                Assert.Equal("Active", peek.Lifetime);
                Assert.DoesNotContain(peek.Posts, p => p.Id == old);
                Assert.IsType<ForbidResult>((await DetailsAsync(env, OfficeCoordinator, old, p => p.OnGetAsync(), method: "GET")).Result);
                Assert.IsType<ForbidResult>((await DetailsAsync(env, OfficeCoordinator, old, p => p.OnPostCommentAsync(), p => p.CommentText = "late")).Result);

                var admin = await ListAsync(env, Admin, p => p.Lifetime = "Expired");
                Assert.Contains(admin.Posts, p => p.Id == old);
                Assert.DoesNotContain(admin.Posts, p => p.Id == fresh);
                Assert.IsType<PageResult>((await DetailsAsync(env, Admin, old, p => p.OnGetAsync(), method: "GET")).Result);

                // Only the administrator makes a post permanent -- once.
                Assert.IsType<ForbidResult>((await DetailsAsync(env, SowingSup, fresh, p => p.OnPostMakePermanentAsync())).Result);   // not even the author
                Assert.Equal("Post made permanent -- it no longer expires.", Done(await DetailsAsync(env, Admin, old, p => p.OnPostMakePermanentAsync())));
                Assert.Equal("This post is already permanent.", Refused(await DetailsAsync(env, Admin, old, p => p.OnPostMakePermanentAsync())));
                Assert.False(await env.Repo.MakePermanentAsync(old, Admin));                                                            // even bypassing the page
                Assert.Equal(1, await CountAsync(env, "SuggestionPostEvents", "PostId = @P AND EventType = 'MadePermanent'", ("@P", old)));
                var permanent = (await env.Repo.GetPostAsync(old))!;
                Assert.Equal((true, false), (permanent.IsPermanent, permanent.IsExpired));
                Assert.Equal(TimeSpan.FromHours(49), TimeSpan.FromMinutes(Math.Round((DateTime.UtcNow - permanent.CreatedAt).TotalMinutes)));   // CreatedAt untouched
                Assert.Contains((await ListAsync(env, OfficeCoordinator)).Posts, p => p.Id == old);                                        // visible again
                Assert.Contains((await ListAsync(env, MpSup)).Posts, p => p.Id == old);                                                  // ... and in every other Area's view
                Assert.Contains((await ListAsync(env, Admin, p => p.Lifetime = "Permanent")).Posts, p => p.Id == old);
            }
            finally { await env.CleanupAsync(); }
        }

        [SkippableFact]
        public async Task AdminDelete_SoftDeletes_WithReason_HiddenFromUsers_KeptForAudit()
        {
            var env = await OpenAsync();
            try
            {
                var id = PostId((await CreateAsync(env, SowingSup, "Problem", MainOffice, Facility5, "ZZSP duplicate report")).Result);
                Assert.IsType<ForbidResult>((await DetailsAsync(env, SowingSup, id, p => p.OnPostDeleteAsync(), p => p.DeleteReason = "mine")).Result);   // the author cannot
                Assert.IsType<ForbidResult>((await DetailsAsync(env, OfficeCoordinator, id, p => p.OnPostStatusAsync(), p => p.NewStatus = "Closed")).Result);
                var missing = await DetailsAsync(env, Admin, id, p => p.OnPostDeleteAsync(), p => p.DeleteReason = "  ");
                Assert.Contains("Reason is required.", Errors(missing.Page));

                Assert.Equal("Status changed to In Progress.", Done(await DetailsAsync(env, Admin, id, p => p.OnPostStatusAsync(), p => { p.NewStatus = "InProgress"; p.StatusNote = "Plumber informed"; })));
                Assert.Contains("The post is already In Progress.", Errors((await DetailsAsync(env, Admin, id, p => p.OnPostStatusAsync(), p => p.NewStatus = "InProgress")).Page));

                Done(await DetailsAsync(env, Admin, id, p => p.OnPostDeleteAsync(), p => p.DeleteReason = "Duplicate of an earlier report"));
                var deleted = (await env.Repo.GetPostAsync(id))!;
                Assert.Equal((true, "Duplicate of an earlier report", "InProgress"), (deleted.IsDeleted, deleted.DeleteReason, deleted.Status));
                Assert.NotNull(deleted.DeletedAt);
                Assert.Equal("This post was already deleted.", Refused(await DetailsAsync(env, Admin, id, p => p.OnPostDeleteAsync(), p => p.DeleteReason = "again")));
                Assert.DoesNotContain((await ListAsync(env, SowingSup)).Posts, p => p.Id == id);
                Assert.IsType<ForbidResult>((await DetailsAsync(env, SowingSup, id, p => p.OnGetAsync(), method: "GET")).Result);
                Assert.DoesNotContain((await ListAsync(env, Admin)).Posts, p => p.Id == id);                                    // not in the normal feed
                Assert.Contains((await ListAsync(env, Admin, p => p.Lifetime = "Deleted")).Posts, p => p.Id == id);             // but kept for audit
                Assert.Equal("This post was deleted.", Refused(await DetailsAsync(env, Admin, id, p => p.OnPostMakePermanentAsync())));
                var history = await env.Repo.GetEventsAsync(id);
                Assert.Equal(new[] { "Created", "StatusChanged", "Deleted" }, history.Select(e => e.EventType));
                Assert.Equal(("Open", "InProgress", "Plumber informed"), (history[1].FromStatus, history[1].ToStatus, history[1].Note));
            }
            finally { await env.CleanupAsync(); }
        }

        // ---- comments and replies ---------------------------------------------------------------------

        [SkippableFact]
        public async Task Comments_Replies_AndTamperedParents()
        {
            var env = await OpenAsync();
            try
            {
                var id = PostId((await CreateAsync(env, SowingSup, "Problem", MainOffice, Facility5, "ZZSP Irrigation pipe leakage")).Result);
                var other = PostId((await CreateAsync(env, SowingSup, "Suggestion", MainOffice, Facility5, "ZZSP other post")).Result);

                Assert.Contains("Comment is required.", Errors((await DetailsAsync(env, OfficeCoordinator, id, p => p.OnPostCommentAsync(), p => p.CommentText = "   ")).Page));
                Assert.Contains("Comment cannot be longer than 1000 characters.", Errors((await DetailsAsync(env, OfficeCoordinator, id, p => p.OnPostCommentAsync(), p => p.CommentText = new string('x', 1001))).Page));
                Assert.IsType<ForbidResult>((await DetailsAsync(env, Anonymous, id, p => p.OnPostCommentAsync(), p => p.CommentText = "not signed in")).Result);
                Assert.IsType<NotFoundResult>((await DetailsAsync(env, OfficeCoordinator, 99999999, p => p.OnPostCommentAsync(), p => p.CommentText = "no such post")).Result);

                const string xss = "  Leakage is increasing. <script>alert('x')</script>  ";
                Assert.Equal("Comment added.", Done(await DetailsAsync(env, OfficeCoordinator, id, p => p.OnPostCommentAsync(), p => p.CommentText = xss)));
                var top = (await env.Repo.GetCommentsAsync(id)).Single();
                Assert.Equal(xss.Trim(), top.CommentText);   // stored as typed (trimmed); Razor encodes it when shown
                var post = (await env.Repo.GetPostAsync(id))!;
                Assert.Equal(("InDiscussion", 1), (post.Status, post.CommentCount));   // first comment by someone else

                Assert.Equal("Reply added.", Done(await DetailsAsync(env, SowingSup, id, p => p.OnPostCommentAsync(), p => { p.CommentText = "I have informed the plumber."; p.ParentCommentId = top.Id; })));
                var reply = (await env.Repo.GetCommentsAsync(id)).Single().Replies.Single();
                // A reply to a reply attaches to the same top-level comment (one level).
                Done(await DetailsAsync(env, OfficeCoordinator, id, p => p.OnPostCommentAsync(), p => { p.CommentText = "Okay."; p.ParentCommentId = reply.Id; }));
                var thread = (await env.Repo.GetCommentsAsync(id)).Single();
                Assert.Equal(new[] { "I have informed the plumber.", "Okay." }, thread.Replies.Select(r => r.CommentText));
                Assert.All(thread.Replies, r => Assert.Equal(top.Id, r.ParentCommentId));
                // Global: a supervisor of ANOTHER Area (127) joins the Area 2 discussion.
                Assert.Equal("Reply added.", Done(await DetailsAsync(env, MpSup, id, p => p.OnPostCommentAsync(), p => { p.CommentText = "Green Bless had the same leak."; p.ParentCommentId = top.Id; })));

                // Tampered ParentCommentId: a comment of ANOTHER post, a missing one.
                Done(await DetailsAsync(env, OfficeCoordinator, other, p => p.OnPostCommentAsync(), p => p.CommentText = "on the other post"));
                var foreign = (await env.Repo.GetCommentsAsync(other)).Single().Id;
                foreach (var parent in new[] { foreign, 99999999 })
                {
                    var step = await DetailsAsync(env, OfficeCoordinator, id, p => p.OnPostCommentAsync(), p => { p.CommentText = "sneaky"; p.ParentCommentId = parent; });
                    Assert.Contains("The comment you are replying to was not found on this post.", Errors(step.Page));
                }
                Assert.Equal(4, await CountAsync(env, "SuggestionComments", "PostId = @P", ("@P", id)));
                Assert.Equal(1, await CountAsync(env, "SuggestionComments", "PostId = @P", ("@P", other)));
                // Even past the page, the database refuses a reply whose parent is on another post.
                var ex = await Assert.ThrowsAsync<SqlException>(() => env.Repo.AddCommentAsync(id, foreign, "direct", OfficeCoordinator));
                Assert.Equal(547, ex.Number);
                Assert.Equal(1, await CountAsync(env, "SuggestionPostEvents", "PostId = @P AND EventType = 'StatusChanged'", ("@P", id)));
            }
            finally { await env.CleanupAsync(); }
        }

        [SkippableFact]
        public async Task CommentEditAndDelete_OwnOnly_Window_AdministratorModerates()
        {
            var env = await OpenAsync();
            try
            {
                var id = PostId((await CreateAsync(env, SowingSup, "Problem", MainOffice, Facility5, "ZZSP comments")).Result);
                var other = PostId((await CreateAsync(env, SowingSup, "Problem", MainOffice, Facility5, "ZZSP elsewhere")).Result);
                Done(await DetailsAsync(env, SowingSup, id, p => p.OnPostCommentAsync(), p => p.CommentText = "First text"));
                Done(await DetailsAsync(env, OfficeCoordinator, id, p => p.OnPostCommentAsync(), p => p.CommentText = "Kanchan's note"));
                var comments = await env.Repo.GetCommentsAsync(id);
                var mine = comments.Single(c => c.AuthorId == SowingSup).Id;
                var hers = comments.Single(c => c.AuthorId == OfficeCoordinator).Id;

                Assert.Equal("Comment updated.", Done(await DetailsAsync(env, SowingSup, id, p => p.OnPostEditCommentAsync(), p => { p.CommentId = mine; p.CommentText = "Corrected text"; })));
                Assert.Equal("Corrected text", (await env.Repo.GetCommentAsync(mine))!.CommentText);
                Assert.NotNull((await env.Repo.GetCommentAsync(mine))!.UpdatedAt);
                Assert.Equal(1, await CountAsync(env, "SuggestionPostEvents", "PostId = @P AND EventType = 'CommentEdited' AND Note = N'Previous text: First text'", ("@P", id)));
                Assert.IsType<ForbidResult>((await DetailsAsync(env, SowingSup, id, p => p.OnPostEditCommentAsync(), p => { p.CommentId = hers; p.CommentText = "rewritten"; })).Result);
                Assert.IsType<ForbidResult>((await DetailsAsync(env, SowingSup, id, p => p.OnPostDeleteCommentAsync(), p => p.CommentId = hers)).Result);
                Assert.IsType<NotFoundResult>((await DetailsAsync(env, SowingSup, other, p => p.OnPostDeleteCommentAsync(), p => p.CommentId = mine)).Result);   // comment of another post

                // Outside the 30-minute window: no edit.
                var old = await env.ScalarAsync<int>(@"
INSERT INTO dbo.SuggestionComments (PostId, AuthorId, CommentText, CreatedAt) VALUES (@P, @U, N'old text', DATEADD(MINUTE, -40, SYSUTCDATETIME()));
SELECT CAST(SCOPE_IDENTITY() AS INT)", ("@P", id), ("@U", SowingSup));
                Assert.Equal("A comment can only be edited within 30 minutes of posting.",
                    Refused(await DetailsAsync(env, SowingSup, id, p => p.OnPostEditCommentAsync(), p => { p.CommentId = old; p.CommentText = "late edit"; })));
                Assert.False(await env.Repo.EditCommentAsync(old, SowingSup, "late edit"));
                Assert.Equal("old text", (await env.Repo.GetCommentAsync(old))!.CommentText);

                Assert.Equal("Comment deleted.", Done(await DetailsAsync(env, SowingSup, id, p => p.OnPostDeleteCommentAsync(), p => p.CommentId = mine)));
                Assert.Equal("This comment was already deleted.", Refused(await DetailsAsync(env, SowingSup, id, p => p.OnPostDeleteCommentAsync(), p => p.CommentId = mine)));
                Done(await DetailsAsync(env, Admin, id, p => p.OnPostDeleteCommentAsync(), p => p.CommentId = hers));
                var deleted = await env.Repo.GetCommentAsync(hers);
                Assert.Equal((true, "Kanchan's note"), (deleted!.IsDeleted, deleted.CommentText));   // soft delete keeps the text for audit
                var notes = (await env.Repo.GetEventsAsync(id)).Where(e => e.EventType == "CommentDeleted").Select(e => e.Note).ToList();
                Assert.Equal(new[] { "Deleted by its author", "Removed by an administrator" }, notes);
                Assert.Equal("The comment you are replying to was deleted.",
                    Errors((await DetailsAsync(env, OfficeCoordinator, id, p => p.OnPostCommentAsync(), p => { p.CommentText = "reply"; p.ParentCommentId = hers; })).Page).Single());
                Assert.Equal(1, (await env.Repo.GetPostAsync(id))!.CommentCount);   // only the old one is still visible
            }
            finally { await env.CleanupAsync(); }
        }

        // ---- photo access ---------------------------------------------------------------------------

        [SkippableFact]
        public async Task Photo_OnlyForUsersWhoMaySeeThePost_WithSafeHeaders()
        {
            var env = await OpenAsync();
            try
            {
                var id = PostId((await CreateAsync(env, SowingSup, "Problem", MainOffice, Facility5, "ZZSP photo", photos: new[] { Upload(Jpeg, "leak.jpg") })).Result);
                var (r, page) = await DetailsAsync(env, OfficeCoordinator, id, p => p.OnGetPhotoAsync(), method: "GET");
                var file = Assert.IsType<PhysicalFileResult>(r);
                Assert.Equal("image/jpeg", file.ContentType);
                Assert.StartsWith(Path.GetFullPath(env.PhotoRoot), file.FileName);
                Assert.Equal("nosniff", page.Response.Headers["X-Content-Type-Options"].ToString());
                Assert.Equal("no-store", page.Response.Headers["Cache-Control"].ToString());   // never served from a shared browser's cache
                Assert.IsType<PhysicalFileResult>((await DetailsAsync(env, Admin, id, p => p.OnGetPhotoAsync(), method: "GET")).Result);
                Assert.IsType<PhysicalFileResult>((await DetailsAsync(env, MpSup, id, p => p.OnGetPhotoAsync(), method: "GET")).Result);  // other Area: global module
                Assert.IsType<ForbidResult>((await DetailsAsync(env, Anonymous, id, p => p.OnGetPhotoAsync(), method: "GET")).Result);   // not signed in
                Assert.IsType<NotFoundResult>((await DetailsAsync(env, Admin, 99999999, p => p.OnGetPhotoAsync(), method: "GET")).Result);

                // A stored name that is not one this store created (e.g. tampered in the database)
                // never reaches a file outside the photo folder.
                await env.ExecAsync("UPDATE dbo.SuggestionPostPhotos SET StoredFileName = N'appsettings.json' WHERE PostId = @P", ("@P", id));
                Assert.IsType<NotFoundResult>((await DetailsAsync(env, Admin, id, p => p.OnGetPhotoAsync(), method: "GET")).Result);
                var noPhoto = PostId((await CreateAsync(env, SowingSup, "Problem", MainOffice, Facility5, "ZZSP no photo")).Result);
                Assert.IsType<NotFoundResult>((await DetailsAsync(env, Admin, noPhoto, p => p.OnGetPhotoAsync(), method: "GET")).Result);
            }
            finally { await env.CleanupAsync(); }
        }

        // ---- list scope, counts, permissions -----------------------------------------------------------

        [SkippableFact]
        public async Task List_ShowsEveryArea_ToEveryone_Filters_AndPaging()
        {
            var env = await OpenAsync();
            try
            {
                var main = PostId((await CreateAsync(env, SowingSup, "Problem", MainOffice, Facility5, "ZZSP main office")).Result);
                var green = PostId((await CreateAsync(env, MpSup, "Suggestion", GreenBless, GreenBlessP1, "ZZSP green bless")).Result);

                // Every user sees every Area -- a Green Bless supervisor sees the Main Office post and vice versa.
                var mp = await ListAsync(env, MpSup);
                Assert.Contains(mp.Posts, p => p.Id == green);
                Assert.Contains(mp.Posts, p => p.Id == main);
                Assert.Equal(mp.Posts.Count(p => p.IsActive), mp.Counts.Active);
                Assert.False(mp.IsManager);
                Assert.Contains(mp.Areas, a => a.Id == MainOffice);                       // the Area filter offers all Areas
                var mpMain = await ListAsync(env, MpSup, p => { p.AreaId = MainOffice; p.PolyhouseId = Facility5; });
                Assert.Equal((MainOffice, (int?)Facility5), (mpMain.AreaId!.Value, mpMain.PolyhouseId));
                Assert.Contains(mpMain.Posts, p => p.Id == main);
                Assert.All(mpMain.Posts, p => Assert.Equal((MainOffice, (int?)Facility5), (p.AreaId, p.PolyhouseId)));
                var sowingGreen = await ListAsync(env, SowingSup, p => { p.AreaId = GreenBless; p.Lifetime = "Permanent"; });
                Assert.Equal(GreenBless, sowingGreen.AreaId);
                Assert.Equal("Permanent", sowingGreen.Lifetime);                           // Active / Permanent for everyone
                Assert.Equal("Active", (await ListAsync(env, SowingSup, p => p.Lifetime = "Expired")).Lifetime);   // Expired stays an admin view
                Assert.Equal("Active", (await ListAsync(env, SowingSup, p => p.Lifetime = "Deleted")).Lifetime);

                var sowing = await ListAsync(env, SowingSup, p => { p.Type = "Problem"; p.PostedById = MpSup; p.PolyhouseId = GreenBlessP1; });   // admin-only / mismatched filters ignored
                Assert.Equal((null, null), (sowing.PostedById, sowing.PolyhouseId));
                Assert.Contains(sowing.Posts, p => p.Id == main);
                Assert.All(sowing.Posts, p => Assert.Equal("Problem", p.TypeCode));

                var admin = await ListAsync(env, Admin, p => { p.AreaId = GreenBless; p.PolyhouseId = GreenBlessP1; p.PostedById = MpSup; p.FromDate = AppClock.Today; p.ToDate = AppClock.Today; });
                Assert.Equal(new[] { green }, admin.Posts.Where(p => p.Title.StartsWith("ZZSP")).Select(p => p.Id));
                Assert.True(admin.IsManager);

                // Paging: 25 posts -> 20 + 5.
                for (var i = 0; i < 23; i++)
                    await env.InsertPostAsync($"ZZSP bulk {i:00}", MainOffice, Facility5, SowingSup, 1);
                var first = await ListAsync(env, Admin, p => p.Type = "Problem");
                var second = await ListAsync(env, Admin, p => { p.Type = "Problem"; p.PageNo = 2; });
                Assert.Equal(SuggestionRules.PageSize, first.Posts.Count);
                Assert.Empty(first.Posts.Select(p => p.Id).Intersect(second.Posts.Select(p => p.Id)));
                Assert.True(first.Total >= 24);
            }
            finally { await env.CleanupAsync(); }
        }

        [SkippableFact]
        public async Task Permissions_GlobalGrantsInCode_NoPerRoleRows_ManageAdministratorOnly()
        {
            var env = await OpenAsync();
            Skip.If(await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Permissions WHERE Code = N'Suggestion.View'") > 0,
                "The scratch copy does not have 2026-10-07_SuggestionsGlobalAccess.sql applied.");
            Assert.Equal("Suggestion.Manage", await env.ScalarAsync<string>("SELECT STRING_AGG(Code, ',') FROM dbo.Permissions WHERE Code LIKE N'Suggestion.%'"));
            Assert.Equal(0, await env.ScalarAsync<int>(@"
SELECT COUNT(*) FROM dbo.RolePermissions rp JOIN dbo.Permissions p ON p.Id = rp.PermissionId WHERE p.Code LIKE N'Suggestion.%'"));   // Manage: no role; full access has it
            var auth = env.Get<SuggestionAuthorization>();
            foreach (var user in new[] { SowingSup, MpSup, OfficeCoordinator })
            {
                var principal = await env.LoginAsync(user);
                Assert.True(SuggestionAuthorization.GlobalPermissions.All(code => auth.Has(principal, code)));
                Assert.False(auth.IsManager(principal));
            }
            Assert.True(auth.IsManager(await env.LoginAsync(Admin)));                              // full access
            Assert.False(auth.CanUseModule(await env.LoginAsync(Anonymous)));
        }

        // Every role of the application, as a REAL user built from the database (UserClaimsFactory),
        // works with the REAL pages: sees posts of other Areas (permanent ones included), posts for an
        // Area it is not assigned to, comments and replies -- and only the administrator manages.
        [SkippableFact]
        public async Task EveryRole_ViewsCreatesCommentsAndReplies_AcrossAreas_AdminOnlyStaysAdminOnly()
        {
            var env = await OpenAsync();
            var roles = new (string Role, int? Area)[]
            {
                ("Sowing Supervisor", MainOffice), ("Mother Plant Supervisor", GreenBless), ("Fertilizer Supervisor", MainOffice),
                ("Office Coordinator", MainOffice), ("Booking Executive", MainOffice), ("Dispatch Executive", MainOffice),
                ("Electrician", null), ("Welder", null), ("Plumber", null),
                ("Sowing Operator", MainOffice), ("Pot Production Operator", MainOffice), ("Main Office Store Keeper", MainOffice),
                ("Outlet Sales", 124), ("Purchase Officer", MainOffice), ("System Administrator", null)
            };
            try
            {
                // Someone else's post in each of two Areas -- the Green Bless one permanent -- with a comment.
                var inMain = PostId((await CreateAsync(env, SowingSup, "Problem", MainOffice, Facility5, "ZZSP role check: Main Office")).Result);
                var inGreen = PostId((await CreateAsync(env, MpSup, "Suggestion", GreenBless, GreenBlessP1, "ZZSP role check: Green Bless safety notice")).Result);
                Done(await DetailsAsync(env, Admin, inGreen, p => p.OnPostMakePermanentAsync()));
                Done(await DetailsAsync(env, OfficeCoordinator, inMain, p => p.OnPostCommentAsync(), p => p.CommentText = "Seen."));
                var top = (await env.Repo.GetCommentsAsync(inMain)).Single().Id;
                var access = new FeatureAccessService(null!);   // sign-in-only pages never consult a policy

                foreach (var (role, area) in roles)
                {
                    var userId = await env.CreateUserAsync(role, area);
                    var user = await env.LoginAsync(userId);
                    Assert.Contains(user.Claims, c => c.Type == AreaAccessService.RoleNameClaimType && c.Value == role);
                    var isAdmin = role == "System Administrator";

                    // Menu entries and pages
                    foreach (var page in new[] { "/Suggestions/Index", "/Suggestions/Create", "/Suggestions/Details" })
                        Assert.True(await access.CanAccessPageAsync(user, page), $"{role}: {page}");

                    // Sees both Areas' posts, the permanent one included
                    var list = env.Page<IndexPage>(user, "GET");
                    Assert.IsType<PageResult>(await list.OnGetAsync());
                    Assert.Contains(list.Posts, p => p.Id == inMain);
                    Assert.Contains(list.Posts, p => p.Id == inGreen);
                    foreach (var id in new[] { inMain, inGreen })
                        Assert.IsType<PageResult>((await DetailsAsync(env, userId, id, p => p.OnGetAsync(), method: "GET")).Result);

                    // Posts for an Area it is NOT assigned to (with a valid Polyhouse of that Area) ...
                    var (target, polyhouse) = area == GreenBless ? (MainOffice, (int?)Facility5) : (GreenBless, (int?)GreenBlessP1);
                    var (created, page2) = await CreateAsync(env, userId, "Problem", target, polyhouse, $"ZZSP {role}: lighting issue");
                    Assert.True(created is RedirectToPageResult, $"{role}: {string.Join("; ", Errors(page2))}");
                    var mine = (await env.Repo.GetPostAsync(PostId(created)))!;
                    Assert.Equal((target, polyhouse, userId), (mine.AreaId, mine.PolyhouseId, mine.PostedById));
                    // ... but never an invalid Area + Polyhouse combination
                    var (_, bad) = await CreateAsync(env, userId, "Problem", target, target == MainOffice ? GreenBlessP1 : Facility5, $"ZZSP {role}: bad combination");
                    Assert.Contains(SuggestionRules.PolyhouseNotInAreaMessage, Errors(bad));

                    // Comments on the other Area's post and replies in the discussion
                    Assert.Equal("Comment added.", Done(await DetailsAsync(env, userId, inGreen, p => p.OnPostCommentAsync(), p => p.CommentText = $"{role} agrees.")));
                    Assert.Equal("Reply added.", Done(await DetailsAsync(env, userId, inMain, p => p.OnPostCommentAsync(), p => { p.CommentText = $"{role} replies."; p.ParentCommentId = top; })));

                    // Administrator actions stay administrator-only
                    if (isAdmin)
                        Assert.Equal("Status changed to In Progress.", Done(await DetailsAsync(env, userId, mine.Id, p => p.OnPostStatusAsync(), p => p.NewStatus = "InProgress")));
                    else
                    {
                        Assert.IsType<ForbidResult>((await DetailsAsync(env, userId, mine.Id, p => p.OnPostMakePermanentAsync())).Result);
                        Assert.IsType<ForbidResult>((await DetailsAsync(env, userId, mine.Id, p => p.OnPostStatusAsync(), p => p.NewStatus = "Closed")).Result);
                        Assert.IsType<ForbidResult>((await DetailsAsync(env, userId, inMain, p => p.OnPostDeleteAsync(), p => p.DeleteReason = "x")).Result);
                        Assert.IsType<ForbidResult>((await DetailsAsync(env, userId, inMain, p => p.OnPostDeleteCommentAsync(), p => p.CommentId = top)).Result);
                        var expired = env.Page<IndexPage>(user, "GET");
                        expired.Lifetime = "Deleted";
                        await expired.OnGetAsync();
                        Assert.Equal("Active", expired.Lifetime);
                    }
                }

                Assert.Equal(roles.Length, await CountAsync(env, "SuggestionComments", "PostId = @P", ("@P", inGreen)));
                Assert.Equal(roles.Length, (await env.Repo.GetCommentsAsync(inMain)).Single().Replies.Count);
                Assert.Equal(roles.Length, await CountAsync(env, "SuggestionPosts", "Title LIKE N'ZZSP %: lighting issue'"));
            }
            finally { await env.CleanupAsync(); }
        }
    }
}
