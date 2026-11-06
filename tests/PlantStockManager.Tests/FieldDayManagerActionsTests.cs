using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Services;
using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.Tests
{
    // Field Work → Day Details → Manager action (2026-11-06 fix). The buttons' formaction replaced the form's action and
    // carried no day id, so every Approve / Flag / Reopen POST arrived with id = 0 and answered 404. These tests go through
    // the RENDERED page over real HTTP: they read each button's formaction and post to it with the page's anti-forgery
    // token, as a browser does, then check the row and the audit trail. Scratch copy only (PSM_SCRATCH_CONNECTION,
    // PlantsIMS2_Scratch_*); skipped otherwise or without 2026-11-03_FieldWorkDays.sql.
    [Collection("ScratchDb")]
    public class FieldDayManagerActionsTests
    {
        private sealed class ScratchFactory : WebApplicationFactory<Program>
        {
            private readonly string _cs;
            public ScratchFactory(string cs) => _cs = cs;
            protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
            {
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = _cs,
                    ["DailyReportWhatsApp:Enabled"] = "false",
                    ["FieldWork:AutoCloseEnabled"] = "false",
                    ["FieldWork:PhotoRoot"] = Path.Combine(Path.GetTempPath(), "psm-fw-review-http"),
                }));
                builder.ConfigureTestServices(services =>
                {
                    services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, FieldCustomerAuthorizationTests.UserHeaderAuthHandler>(
                        FieldCustomerAuthorizationTests.UserHeaderAuthHandler.SchemeName, _ => { });
                    services.PostConfigure<AuthenticationOptions>(o => o.DefaultAuthenticateScheme = FieldCustomerAuthorizationTests.UserHeaderAuthHandler.SchemeName);
                });
            }
        }

        private sealed record Env(string Cs, FieldDayService Service, FieldDayRepository Repo, int Employee, int Other, int Manager);

        private static async Task<Env> OpenAsync()
        {
            var cs = Environment.GetEnvironmentVariable("PSM_SCRATCH_CONNECTION");
            Skip.If(string.IsNullOrWhiteSpace(cs), "Set PSM_SCRATCH_CONNECTION to a PlantsIMS2_Scratch_* copy.");
            var users = new List<int>();
            using (var conn = new SqlConnection(cs))
            {
                await conn.OpenAsync();
                using (var cmd = new SqlCommand("SELECT DB_NAME()", conn))
                {
                    var name = (string)(await cmd.ExecuteScalarAsync())!;
                    if (!name.StartsWith("PlantsIMS2_Scratch_", StringComparison.Ordinal))
                        throw new InvalidOperationException($"Refusing to run these tests against '{name}'.");
                }
                using (var probe = new SqlCommand(FieldWorkSchema.ProbeSql, conn))
                    Skip.If((int)(await probe.ExecuteScalarAsync())! != 1, "2026-11-03_FieldWorkDays.sql is not applied to this scratch copy.");
                using (var cmd = new SqlCommand("SELECT TOP 3 Id FROM dbo.IMSUsers WHERE IsActive = 1 ORDER BY Id", conn))
                using (var r = await cmd.ExecuteReaderAsync())
                    while (await r.ReadAsync())
                        users.Add(r.GetInt32(0));
                Skip.If(users.Count < 3, "The scratch copy needs 3 active users.");
                using var clean = new SqlCommand(FieldCustomerE2ETests.Phase2CleanupSql + @"
DELETE e FROM dbo.FieldDayEvents e JOIN dbo.FieldDays d ON d.Id = e.FieldDayId WHERE d.UserId IN (@A, @B, @C);
DELETE FROM dbo.FieldDays WHERE UserId IN (@A, @B, @C);
DELETE FROM dbo.FieldPhotos WHERE OwnerUserId IN (@A, @B, @C);", conn);
                clean.Parameters.AddWithValue("@A", users[0]);
                clean.Parameters.AddWithValue("@B", users[1]);
                clean.Parameters.AddWithValue("@C", users[2]);
                await clean.ExecuteNonQueryAsync();
            }
            var db = new DatabaseHelper(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs }).Build());
            var options = new FieldWorkOptions { PhotoRoot = Path.Combine(Path.GetTempPath(), "psm-fw-review-http"), DistanceMismatchSlackKm = 2m };
            var repo = new FieldDayRepository(db);
            return new Env(cs!, new FieldDayService(repo, new FieldPhotoStore(options), options), repo, users[0], users[1], users[2]);
        }

        private static byte[] Jpeg()
        {
            var b = new byte[1024];
            Random.Shared.NextBytes(b);
            b[0] = 0xFF; b[1] = 0xD8; b[2] = 0xFF; b[3] = 0xE0; b[^2] = 0xFF; b[^1] = 0xD9;
            return b;
        }

        private static FieldDayService.ReadingRequest Reading(decimal km)
            => new(km, 12.9716m, 77.5946m, 10m, null, Jpeg(), Guid.NewGuid().ToString());

        // today's day of the employee, ended (Closed, review Pending)
        private static async Task<int> ClosedDayAsync(Env env)
        {
            await env.Service.RecordConsentAsync(env.Employee);
            var started = await env.Service.StartDayAsync(env.Employee, "OwnBike", Reading(100m));
            Assert.True(started.Ok, string.Join(" ", started.Errors));
            var ended = await env.Service.EndDayAsync(env.Employee, Reading(110m));
            Assert.True(ended.Ok, string.Join(" ", ended.Errors));
            return started.DayId!.Value;
        }

        private static ClaimsPrincipal Principal(int id, params string[] permissions)
        {
            var claims = new List<Claim> { new("UserId", id.ToString()) };
            claims.AddRange(permissions.Select(p => p == "FULL"
                ? new Claim(ClaimsPrincipalSecurityExtensions.FullAccessClaimType, "true")
                : new Claim(MinimumAuthorizationLevelHandler.PermissionClaimType, p)));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        }

        private static async Task<int> EventCountAsync(Env env, int dayId, string type)
        {
            using var conn = new SqlConnection(env.Cs);
            await conn.OpenAsync();
            using var cmd = new SqlCommand("SELECT COUNT(*) FROM dbo.FieldDayEvents WHERE FieldDayId = @Id AND EventType = @T", conn);
            cmd.Parameters.AddWithValue("@Id", dayId);
            cmd.Parameters.AddWithValue("@T", type);
            return (int)(await cmd.ExecuteScalarAsync())!;
        }

        private static async Task<int?> LastEventByAsync(Env env, int dayId)
        {
            using var conn = new SqlConnection(env.Cs);
            await conn.OpenAsync();
            using var cmd = new SqlCommand("SELECT TOP 1 ByUserId FROM dbo.FieldDayEvents WHERE FieldDayId = @Id ORDER BY Id DESC", conn);
            cmd.Parameters.AddWithValue("@Id", dayId);
            var v = await cmd.ExecuteScalarAsync();
            return v is int i ? i : null;
        }

        private static HttpClient Client(ScratchFactory f) => f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, string? user,
            IEnumerable<KeyValuePair<string, string>>? form = null)
        {
            var request = new HttpRequestMessage(method, url);
            if (method == HttpMethod.Post)
                request.Content = new FormUrlEncodedContent(form ?? Array.Empty<KeyValuePair<string, string>>());
            if (user != null)
                request.Headers.Add(FieldCustomerAuthorizationTests.UserHeaderAuthHandler.Header, user);
            return await client.SendAsync(request);
        }

        // the rendered page: anti-forgery token + the formaction of each manager button that is shown
        private sealed record Rendered(string Html, string? Token, Dictionary<string, string> Actions);

        private static async Task<Rendered> RenderAsync(HttpClient client, int dayId, string user)
        {
            var r = await SendAsync(client, HttpMethod.Get, $"/FieldWork/DayDetails?id={dayId}", user);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var html = await r.Content.ReadAsStringAsync();
            var token = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");
            var actions = new Dictionary<string, string>();
            foreach (Match m in Regex.Matches(html, "formaction=\"([^\"]+)\""))
            {
                var url = WebUtility.HtmlDecode(m.Groups[1].Value);
                var handler = Regex.Match(url, "handler=(\\w+)");
                if (handler.Success)
                    actions[handler.Groups[1].Value] = url;
            }
            return new Rendered(html, token.Success ? token.Groups[1].Value : null, actions);
        }

        private static KeyValuePair<string, string>[] Form(string token, string? comment = null)
            => comment == null
                ? new[] { new KeyValuePair<string, string>("__RequestVerificationToken", token) }
                : new[] { new("__RequestVerificationToken", token), new KeyValuePair<string, string>("Comment", comment) };

        private static void AssertBackOnTheDay(HttpResponseMessage r, int dayId)
        {
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Contains($"/FieldWork/DayDetails?id={dayId}", r.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [SkippableFact]
        public async Task EachRenderedButton_PostsToItsOwnDay_AndApproveFlagReopenWork()
        {
            var env = await OpenAsync();
            var id = await ClosedDayAsync(env);
            using var factory = new ScratchFactory(env.Cs);
            var client = Client(factory);
            var mgr = $"{env.Manager}|FieldWork.Manage";

            var page = await RenderAsync(client, id, mgr);
            Assert.NotNull(page.Token);
            Assert.Equal(new[] { "Approve", "Flag", "Reopen" }, page.Actions.Keys.OrderBy(k => k == "Approve" ? 0 : k == "Flag" ? 1 : 2));
            foreach (var (handler, url) in page.Actions)
                Assert.True(Regex.IsMatch(url, $"[?&]id={id}(&|$)"), $"{handler} button posts to '{url}' without the day id");

            // Flag (needs a reason)
            var noReason = await SendAsync(client, HttpMethod.Post, page.Actions["Flag"], mgr, Form(page.Token!, "  "));
            Assert.Equal(HttpStatusCode.OK, noReason.StatusCode);
            Assert.Contains("Write why the day is flagged.", await noReason.Content.ReadAsStringAsync());
            Assert.Equal(0, await EventCountAsync(env, id, "Flagged"));

            var before = DateTime.UtcNow.AddMinutes(-1);
            AssertBackOnTheDay(await SendAsync(client, HttpMethod.Post, page.Actions["Flag"], mgr, Form(page.Token!, "Meter blurred")), id);
            var flagged = (await env.Repo.GetDayAsync(id))!;
            Assert.Equal(FieldDayRules.ReviewFlagged, flagged.ReviewStatus);
            Assert.Equal(env.Manager, flagged.ReviewedByUserId);
            Assert.Equal("Meter blurred", flagged.ReviewComment);
            Assert.True(flagged.ReviewedAtUtc >= before);
            var after = await RenderAsync(client, id, mgr);
            Assert.Contains("Day flagged.", after.Html);
            Assert.Contains("Flagged by", after.Html);

            // the same flag submitted again (double click / resubmit): refused, no second event
            var again = await SendAsync(client, HttpMethod.Post, page.Actions["Flag"], mgr, Form(page.Token!, "Meter blurred"));
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.Contains("already flagged with this reason", await again.Content.ReadAsStringAsync());
            Assert.Equal(1, await EventCountAsync(env, id, "Flagged"));

            // Approve
            AssertBackOnTheDay(await SendAsync(client, HttpMethod.Post, after.Actions["Approve"], mgr, Form(after.Token!, "OK after call")), id);
            var approved = (await env.Repo.GetDayAsync(id))!;
            Assert.Equal(FieldDayRules.ReviewApproved, approved.ReviewStatus);
            Assert.Equal(env.Manager, approved.ReviewedByUserId);
            var shown = await RenderAsync(client, id, mgr);
            Assert.Contains("Day approved.", shown.Html);
            Assert.False(shown.Actions.ContainsKey("Approve"));           // no Approve button on an approved day
            var dup = await SendAsync(client, HttpMethod.Post, after.Actions["Approve"], mgr, Form(after.Token!));
            Assert.Contains("already approved", await dup.Content.ReadAsStringAsync());
            Assert.Equal(1, await EventCountAsync(env, id, "Approved"));

            // Reopen (needs a reason; keeps the old end reading in the history)
            AssertBackOnTheDay(await SendAsync(client, HttpMethod.Post, shown.Actions["Reopen"], mgr, Form(shown.Token!, "Ended by mistake")), id);
            var reopened = (await env.Repo.GetDayAsync(id))!;
            Assert.Equal(FieldDayRules.StatusOpen, reopened.Status);
            Assert.Null(reopened.EndKm);
            var events = await env.Service.GetEventsAsync(id);
            var last = events.Last();
            Assert.Equal("Reopened", last.EventType);
            Assert.Equal(env.Manager, await LastEventByAsync(env, id));
            Assert.Contains("end 110.0 km", last.Details);
            Assert.True(last.AtUtc >= before);
            Assert.Equal(new[] { "Started", "Ended", "Flagged", "Approved", "Reopened" }, events.Select(e => e.EventType));
            var open = await RenderAsync(client, id, mgr);
            Assert.Contains("Day reopened.", open.Html);
            Assert.False(open.Actions.ContainsKey("Reopen"));
            Assert.False(open.Actions.ContainsKey("Approve"));
            var reopenAgain = await SendAsync(client, HttpMethod.Post, shown.Actions["Reopen"], mgr, Form(shown.Token!, "Ended by mistake"));
            Assert.Contains("already open", await reopenAgain.Content.ReadAsStringAsync());
            Assert.Equal(1, await EventCountAsync(env, id, "Reopened"));

            // the reopened day is available for End Day again
            Assert.True((await env.Service.EndDayAsync(env.Employee, Reading(125m))).Ok);
        }

        [SkippableFact]
        public async Task Unauthorized_MissingOrForeignIds_AndForgedPosts_ChangeNothing()
        {
            var env = await OpenAsync();
            var id = await ClosedDayAsync(env);
            using var factory = new ScratchFactory(env.Cs);
            var client = Client(factory);
            var mgr = $"{env.Manager}|FieldWork.Manage";
            var page = await RenderAsync(client, id, mgr);

            // an employee (FieldWork.Day, own day or not) never runs a manager action, even with a valid token
            foreach (var who in new[] { $"{env.Employee}|FieldWork.Day", $"{env.Other}|FieldWork.Day" })
            {
                var c = Client(factory);
                var r = await SendAsync(c, HttpMethod.Get, $"/FieldWork/DayDetails?id={id}", who);
                var token = Regex.Match(await r.Content.ReadAsStringAsync(), "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");
                foreach (var handler in new[] { "Approve", "Flag", "Reopen" })
                {
                    var post = await SendAsync(c, HttpMethod.Post, $"/FieldWork/DayDetails?id={id}&handler={handler}", who,
                        token.Success ? Form(token.Groups[1].Value, "x") : null);
                    Assert.True(post.StatusCode == HttpStatusCode.BadRequest
                                || (post.StatusCode == HttpStatusCode.Redirect && post.Headers.Location!.ToString().Contains("/Account/AccessDenied")),
                        $"{who} {handler}: {(int)post.StatusCode} {post.Headers.Location}");
                }
            }
            // a role without Field Work and an anonymous request
            var denied = await SendAsync(Client(factory), HttpMethod.Post, $"/FieldWork/DayDetails?id={id}&handler=Approve", $"{env.Other}|Dashboard.View");
            Assert.True(denied.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Redirect);
            var anon = await SendAsync(Client(factory), HttpMethod.Post, $"/FieldWork/DayDetails?id={id}&handler=Approve", null);
            Assert.True(anon.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Redirect);
            // a manager's POST without the anti-forgery token
            Assert.Equal(HttpStatusCode.BadRequest,
                (await SendAsync(client, HttpMethod.Post, $"/FieldWork/DayDetails?id={id}&handler=Approve", mgr)).StatusCode);
            // a manager's POST without an id / with an unknown id: 404, nothing written
            Assert.Equal(HttpStatusCode.NotFound,
                (await SendAsync(client, HttpMethod.Post, "/FieldWork/DayDetails?handler=Approve", mgr, Form(page.Token!))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,
                (await SendAsync(client, HttpMethod.Post, "/FieldWork/DayDetails?id=2147483000&handler=Approve", mgr, Form(page.Token!))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,
                (await SendAsync(client, HttpMethod.Post, "/FieldWork/DayDetails?id=abc&handler=Approve", mgr, Form(page.Token!))).StatusCode);
            // a manager who is the day's own employee may not review it
            var selfMgr = $"{env.Employee}|FieldWork.Manage";
            var selfPage = await RenderAsync(Client(factory), id, selfMgr);
            Assert.Empty(selfPage.Actions);
            Assert.Contains("You cannot review your own day.", selfPage.Html);

            var day = (await env.Repo.GetDayAsync(id))!;
            Assert.Equal(FieldDayRules.StatusClosed, day.Status);
            Assert.Equal(FieldDayRules.ReviewPending, day.ReviewStatus);
            Assert.Null(day.ReviewedByUserId);
            Assert.Equal(new[] { "Started", "Ended" }, (await env.Service.GetEventsAsync(id)).Select(e => e.EventType));
        }

        [SkippableFact]
        public async Task ConcurrentApprovals_WriteOneUpdateAndOneEvent()
        {
            var env = await OpenAsync();
            var id = await ClosedDayAsync(env);
            var manager = Principal(env.Manager, FieldDayRules.ManagePermission);
            var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
                Task.Run(() => env.Service.ReviewAsync(manager, id, FieldDayRules.ReviewAction.Approve, null))));
            Assert.Equal(1, results.Count(r => r.Ok));
            Assert.Equal(1, await EventCountAsync(env, id, "Approved"));

            var flags = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
                Task.Run(() => env.Service.ReviewAsync(manager, id, FieldDayRules.ReviewAction.Flag, "Odometer looks edited"))));
            Assert.Equal(1, flags.Count(r => r.Ok));
            Assert.Equal(1, await EventCountAsync(env, id, "Flagged"));

            var reopens = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
                Task.Run(() => env.Service.ReviewAsync(manager, id, FieldDayRules.ReviewAction.Reopen, "Ended too early"))));
            Assert.Equal(1, reopens.Count(r => r.Ok));
            Assert.Equal(1, await EventCountAsync(env, id, "Reopened"));
        }

        [SkippableFact]
        public async Task AFailureInsideTheTransaction_LeavesTheDayUnchanged()
        {
            var env = await OpenAsync();
            var id = await ClosedDayAsync(env);
            var before = (await env.Repo.GetDayAsync(id))!;
            // Reopen's UPDATE succeeds, then the audit row fails on FK_FieldDayEvents_User: everything is rolled back
            await Assert.ThrowsAnyAsync<Exception>(() => env.Repo.ReviewAsync(id, int.MaxValue, FieldDayRules.ReviewAction.Reopen,
                "x", AppClock.Today, _ => null));
            // Approve's UPDATE fails on FK_FieldDays_ReviewedBy
            await Assert.ThrowsAnyAsync<Exception>(() => env.Repo.ReviewAsync(id, int.MaxValue, FieldDayRules.ReviewAction.Approve,
                null, AppClock.Today, _ => null));
            var after = (await env.Repo.GetDayAsync(id))!;
            Assert.Equal(before.Status, after.Status);
            Assert.Equal(before.EndKm, after.EndKm);
            Assert.Equal(before.EndPhotoId, after.EndPhotoId);
            Assert.Equal(before.ReviewStatus, after.ReviewStatus);
            Assert.Null(after.ReviewedByUserId);
            Assert.Equal(new[] { "Started", "Ended" }, (await env.Service.GetEventsAsync(id)).Select(e => e.EventType));
        }
    }
}