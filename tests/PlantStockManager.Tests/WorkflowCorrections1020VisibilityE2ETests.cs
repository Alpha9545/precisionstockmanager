using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using ApprovalsIndex = PlantStockManager.Pages.Production.ReadyConfirmation.IndexModel;
using MpApprovals = PlantStockManager.Pages.Production.MotherPlant.SowingApprovalsModel;

namespace PlantStockManager.Tests
{
    // 2026-10-20 (owner requirement): the SAME visibility policy (SowingApprovalVisibility) on both Sowing Approvals pages --
    // the System Administrator sees every pending batch and every user's approvals (with the recorded approver); everyone else
    // only the batches they may approve and the approvals they made. SCRATCH COPY only (same fixtures as the 1019 tests).
    public partial class WorkflowCorrections1019E2ETests
    {
        private static async Task<ApprovalsIndex> SeedPageAsync(Env env, int user, string? tab = null, string? category = null)
        {
            var page = env.Page<ApprovalsIndex>(await env.LoginAsync(user));
            page.Tab = tab;
            page.Category = category;
            await page.OnGetAsync();
            return page;
        }

        private static Task<int> MotherPlantAreaOfAsync(Env env, int user)
            => env.ScalarAsync<int>(@"SELECT TOP 1 ur.AreaId FROM dbo.UserRoles ur JOIN dbo.Area a ON a.Id = ur.AreaId AND a.AreaType = N'MotherPlant'
WHERE ur.UserId = @U ORDER BY ur.AreaId", ("@U", user));

        [SkippableFact]
        public async Task Visibility_AdminSeesEveryPendingBatchOnBothPages_NonAdminOnlyWhatTheyMayApprove_SamePolicyOnBothPages()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.TransplantAreaScopeProbeSql);
            var p = await PeopleAsync(env);
            var v = await NewVarietyAsync(env);
            var otherArea = await MotherPlantAreaOfAsync(env, p.OtherAreaSup);
            var mine = await TransplantedTrayBatchAsync(env, v, p.Area, 24, ReadyDays);              // Sup1 / Sup2's Area
            var theirs = await TransplantedTrayBatchAsync(env, v, otherArea, 24, ReadyDays);          // another supervisor's Area

            // 1: the administrator sees both, on both pages, with who approves each
            var adminMp = (await MpApprovalsPageAsync(env, p.Admin, MpApprovals.TabPending)).Items;
            var adminSeed = (await SeedPageAsync(env, p.Admin)).Items;
            foreach (var list in new[] { adminMp, adminSeed })
            {
                Assert.Contains(list, x => x.Sowing.Id == mine);
                Assert.Contains(list, x => x.Sowing.Id == theirs);
            }
            Assert.True((await SeedPageAsync(env, p.Admin)).IsAdministrator);

            // 2 + 3: non-admins -- their own Area's batch only; another supervisor's batch is absent from BOTH pages
            foreach (var (user, sees, notSees) in new[] { (p.Sup1, mine, theirs), (p.OtherAreaSup, theirs, mine) })
            {
                var mp = (await MpApprovalsPageAsync(env, user, MpApprovals.TabPending)).Items;
                var seed = (await SeedPageAsync(env, user)).Items;
                foreach (var list in new[] { mp, seed })
                {
                    Assert.True(Assert.Single(list, x => x.Sowing.Id == sees).Eligibility.CanApprove);
                    Assert.DoesNotContain(list, x => x.Sowing.Id == notSees);
                    Assert.All(list, x => Assert.True(x.Eligibility.Authorized, x.Eligibility.Reason));   // nothing "Not your approval"
                }
            }
            // a Main Office supervisor and a view-only user: neither batch, on either page
            foreach (var user in new[] { p.MainSup, p.Viewer })
            {
                Assert.DoesNotContain((await MpApprovalsPageAsync(env, user, MpApprovals.TabPending)).Items, x => x.Sowing.Id == mine || x.Sowing.Id == theirs);
                var seed = (await SeedPageAsync(env, user)).Items;
                Assert.DoesNotContain(seed, x => x.Sowing.Id == mine || x.Sowing.Id == theirs);
                Assert.All(seed, x => Assert.True(x.Eligibility.Authorized, x.Eligibility.Reason));
            }

            // 9: the two pages apply the same policy -- the Mother Plant batches on the Seed Production page (transplant batches and,
            // since 2026-10-21, Mother Plant Area Direct Sowings) equal the Mother Plant page's Waiting list, for every kind of user
            foreach (var user in new[] { p.Admin, p.Sup1, p.Sup2, p.OtherAreaSup, p.MainSup, p.Viewer })
            {
                var mp = (await MpApprovalsPageAsync(env, user, MpApprovals.TabPending)).Items.Select(x => x.Sowing.Id).OrderBy(x => x).ToList();
                var seed = (await SeedPageAsync(env, user)).Items.Where(x => SowingApprovalRules.IsMotherPlantCategory(x.Category))
                    .Select(x => x.Sowing.Id).OrderBy(x => x).ToList();
                Assert.Equal(mp, seed);
            }
        }

        [SkippableFact]
        public async Task History_NonAdminOnlyTheirOwn_AdminEveryUsersWithTheRecordedApprover_OnBothPages()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.TransplantAreaScopeProbeSql);
            var p = await PeopleAsync(env);
            var v = await NewVarietyAsync(env);
            var otherArea = await MotherPlantAreaOfAsync(env, p.OtherAreaSup);
            var byA = await TransplantedTrayBatchAsync(env, v, p.Area, 24, ReadyDays);
            var byOther = await TransplantedTrayBatchAsync(env, v, otherArea, 24, ReadyDays);
            var approvals = env.Get<ReadyConfirmationRepository>();
            Assert.True((await approvals.ConfirmAsync(byA, 1, null, null, null, "wf1020", p.Sup1, Any)).Success);
            Assert.True((await approvals.ConfirmAsync(byOther, 1, null, null, null, "wf1020", p.OtherAreaSup, Any)).Success);
            var names = new Dictionary<int, string>();
            foreach (var id in new[] { p.Sup1, p.OtherAreaSup })
                names[id] = await env.ScalarAsync<string>("SELECT Name FROM dbo.IMSUsers WHERE Id = @U", ("@U", id));

            // 4: each non-admin -- only their own, on both pages (and every row's recorded approver is them)
            foreach (var (user, own, other) in new[] { (p.Sup1, byA, byOther), (p.OtherAreaSup, byOther, byA) })
            {
                var mp = (await MpApprovalsPageAsync(env, user)).History;
                var seed = (await SeedPageAsync(env, user, ApprovalsIndex.TabHistory)).History;
                foreach (var list in new[] { mp, seed })
                {
                    Assert.Contains(list, x => x.SeedSowingId == own);
                    Assert.DoesNotContain(list, x => x.SeedSowingId == other);
                    Assert.All(list, x => Assert.Equal(user, x.ApproverId));
                }
                Assert.False((await SeedPageAsync(env, user, ApprovalsIndex.TabHistory)).IsAdministrator);
            }
            // the Seed Production history of a non-admin equals exactly their approval rows (all workflows)
            Assert.Equal(await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyConfirmations WHERE ApprovedById = @U", ("@U", p.Sup1)),
                (await SeedPageAsync(env, p.Sup1, ApprovalsIndex.TabHistory)).History.Count);

            // 1 + 5: the administrator -- every user's approvals, each with the RECORDED approver (id and name), on both pages
            foreach (var list in new[] { (await MpApprovalsPageAsync(env, p.Admin)).History, (await SeedPageAsync(env, p.Admin, ApprovalsIndex.TabHistory)).History })
            {
                var a = Assert.Single(list, x => x.SeedSowingId == byA);
                var o = Assert.Single(list, x => x.SeedSowingId == byOther);
                Assert.Equal((p.Sup1, names[p.Sup1]), (a.ApproverId!.Value, a.ApproverName!));
                Assert.Equal((p.OtherAreaSup, names[p.OtherAreaSup]), (o.ApproverId!.Value, o.ApproverName!));
            }
            Assert.Equal(await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyConfirmations"),
                (await SeedPageAsync(env, p.Admin, ApprovalsIndex.TabHistory)).History.Count);
            foreach (var row in (await SeedPageAsync(env, p.Admin, ApprovalsIndex.TabHistory)).History.Take(50))
                Assert.Equal(await env.ScalarAsync<int?>("SELECT ApprovedById FROM dbo.ReadyConfirmations WHERE Id = @C", ("@C", row.ConfirmationId)), row.ApproverId);
        }

        [SkippableFact]
        public async Task Visibility_ForgedParametersAndIdsGrantNothing_HiddenBatchesStillRefusedOnDirectApproval()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.TransplantAreaScopeProbeSql);
            var p = await PeopleAsync(env);
            var v = await NewVarietyAsync(env);
            var otherArea = await MotherPlantAreaOfAsync(env, p.OtherAreaSup);
            var hidden = await TransplantedTrayBatchAsync(env, v, otherArea, 24, ReadyDays);
            var theirsApproved = await TransplantedTrayBatchAsync(env, v, otherArea, 24, ReadyDays);
            Assert.True((await env.Get<ReadyConfirmationRepository>().ConfirmAsync(theirsApproved, 1, null, null, null, "wf1020", p.OtherAreaSup, Any)).Success);

            // 6: Sup1 forges an administrator / another approver in every request channel -- on both pages, nothing more is shown
            var forgedQuery = $"?tab=history&approverId={p.OtherAreaSup}&userId={p.Admin}&UserId={p.Admin}&role=System%20Administrator&isAdministrator=true&FullAccess=true";
            var forgedForm = new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
                { ["UserId"] = p.Admin.ToString(), ["ApprovedById"] = p.OtherAreaSup.ToString(), ["IsAdministrator"] = "true", ["RoleName"] = "System Administrator" };
            void Forge(Microsoft.AspNetCore.Mvc.RazorPages.PageModel page)
            {
                var http = (DefaultHttpContext)page.HttpContext;
                http.Request.QueryString = new QueryString(forgedQuery);
                http.Request.ContentType = "application/x-www-form-urlencoded";
                http.Request.Form = new FormCollection(forgedForm);
                page.RouteData.Values["approverId"] = p.OtherAreaSup;
            }
            var seedPage = env.Page<ApprovalsIndex>(await env.LoginAsync(p.Sup1));
            Forge(seedPage);
            seedPage.Tab = ApprovalsIndex.TabHistory;
            await seedPage.OnGetAsync();
            Assert.False(seedPage.IsAdministrator);
            Assert.DoesNotContain(seedPage.Items, x => x.Sowing.Id == hidden);
            Assert.DoesNotContain(seedPage.History, x => x.SeedSowingId == theirsApproved);
            Assert.All(seedPage.History, x => Assert.Equal(p.Sup1, x.ApproverId));
            var mpPage = env.Page<MpApprovals>(await env.LoginAsync(p.Sup1));
            Forge(mpPage);
            await mpPage.OnGetAsync();
            Assert.False(mpPage.IsAdministrator);
            Assert.DoesNotContain(mpPage.Items, x => x.Sowing.Id == hidden);
            Assert.DoesNotContain(mpPage.History, x => x.SeedSowingId == theirsApproved);

            // 7: the hidden batch submitted straight to the approval handler / repository: still refused, nothing posted
            var (result, confirm) = await ApproveViaPageAsync(env, p.Sup1, hidden, 1);
            Assert.IsType<RedirectToPageResult>(result);
            Assert.NotEqual("", Errors(confirm));
            Assert.False((await env.Get<ReadyConfirmationRepository>().ConfirmAsync(hidden, 1, null, null, null, "wf1020", p.Sup1, Any)).Success);
            Assert.Equal((0, 0), (await env.ApprovalsOfAsync(hidden), await env.ReadyStockOfAsync(hidden)));
            // the administrator sees it -- and the Area's own supervisor may still approve it (rules unchanged)
            Assert.Contains((await SeedPageAsync(env, p.Admin)).Items, x => x.Sowing.Id == hidden);
            Assert.True((await env.Get<ReadyConfirmationRepository>().ConfirmAsync(hidden, 1, null, null, null, "wf1020", p.OtherAreaSup, Any)).Success);
        }

        [SkippableFact]
        public async Task Visibility_BedSowing_MayaSeesAndMayApprove_OtherSupervisorsDoNotSeeIt_AdminSeesButMayNotApprove()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.BedApproverProbeSql);
            var settings = await env.Get<ReadyConfirmationRepository>().GetApprovalSettingsAsync();
            Skip.If(settings.Bed.UserId is null, "No active Bed Sowing approver in this copy.");
            var maya = settings.Bed.UserId!.Value;
            var bedBatch = await env.ScalarAsync<int>(@"SELECT TOP 1 Id FROM dbo.SeedSowings WHERE SourceType = N'BedSowing' AND Status = N'Sown'
AND ConfirmedReadyQuantity + WastageQuantity < QuantitySown ORDER BY Id");
            Skip.If(bedBatch == 0, "No pending Bed Sowing batch in this copy.");
            var p = await PeopleAsync(env);
            var other = (await env.IdsAsync(ConfirmersOfArea + " AND ur.UserId <> @Maya ORDER BY ur.UserId", ("@A", MainArea), ("@Maya", maya))).First();

            // 8: Maya-only, unchanged: she sees it with Approve; another Sowing Supervisor does not see it at all
            var forMaya = Assert.Single((await SeedPageAsync(env, maya, category: SowingApprovalRules.CategoryBedSowing)).Items, x => x.Sowing.Id == bedBatch);
            Assert.True(forMaya.Eligibility.CanApprove || forMaya.Eligibility.WaitingForReadyDate, forMaya.Eligibility.Reason);
            Assert.DoesNotContain((await SeedPageAsync(env, other, category: SowingApprovalRules.CategoryBedSowing)).Items, x => x.Sowing.Id == bedBatch);
            // the administrator SEES it (sees everything) but may still not approve it -- the rule says so, and the server refuses
            var forAdmin = Assert.Single((await SeedPageAsync(env, p.Admin, category: SowingApprovalRules.CategoryBedSowing)).Items, x => x.Sowing.Id == bedBatch);
            Assert.False(forAdmin.Eligibility.CanApprove);
            Assert.Equal(DirectSowingRules.NotBedApproverMessage(settings.Bed.Name), forAdmin.Eligibility.Reason);
            Assert.Equal(DirectSowingRules.NotBedApproverMessage(settings.Bed.Name),
                (await env.Get<ReadyConfirmationRepository>().ConfirmAsync(bedBatch, 1, null, null, null, "wf1020", p.Admin, Any)).Message);
        }
    }

    // The policy itself (no database).
    public class SowingApprovalVisibilityTests
    {
        private static ClaimsPrincipal Principal(bool authenticated, int? userId, params Claim[] claims)
        {
            var all = new List<Claim>(claims);
            if (userId.HasValue) all.Add(new Claim("UserId", userId.Value.ToString()));
            return new ClaimsPrincipal(new ClaimsIdentity(all, authenticated ? "Test" : null));
        }

        private static Claim FullAccess() => new(ClaimsPrincipalSecurityExtensions.FullAccessClaimType, "true");

        [Fact]
        public void Administrator_IsTheAuthoritativeFullAccessClaimOnly()
        {
            var admin = Principal(true, 21, FullAccess());
            Assert.True(SowingApprovalVisibility.IsAdministrator(admin));
            Assert.False(SowingApprovalVisibility.PendingApprovableOnly(admin));
            Assert.Same(HistoryScope.All, SowingApprovalVisibility.History(admin));
            // a role NAME claim alone, or the wide-Area roles, are not the administrator; neither is an unauthenticated principal
            foreach (var notAdmin in new[]
                     {
                         Principal(true, 113, new Claim(AreaAccessService.RoleNameClaimType, "System Administrator")),
                         Principal(true, 113, new Claim(AreaAccessService.RoleNameClaimType, "Management")),
                         Principal(true, 113, new Claim(AreaAccessService.RoleNameClaimType, "MainOfficeOfficer")),
                         Principal(false, 113, FullAccess()),
                     })
            {
                Assert.False(SowingApprovalVisibility.IsAdministrator(notAdmin));
                Assert.True(SowingApprovalVisibility.PendingApprovableOnly(notAdmin));
            }
        }

        [Fact]
        public void History_NonAdminIsTheirOwnId_UnidentifiedIsNothing()
        {
            var scope = SowingApprovalVisibility.History(Principal(true, 113));
            Assert.Equal((false, (int?)113), (scope.IncludesAllApprovers, scope.ApproverId));
            Assert.Same(HistoryScope.Nobody, SowingApprovalVisibility.History(Principal(true, null)));
            Assert.Same(HistoryScope.Nobody, SowingApprovalVisibility.History(Principal(false, 113)));
            Assert.Equal((false, (int?)null), (HistoryScope.Nobody.IncludesAllApprovers, HistoryScope.Nobody.ApproverId));
        }

        [Fact]
        public void BothPages_BindNoIdentityOrRole()
        {
            // 2026-10-22: + Seed Production's Variety / Sowing Date list filters (SowingListFilter.Normalize) -- display filters,
            // never an identity or role; Mother Plant & Cutting > Sowing Approvals still binds only its tab.
            var allowed = new Dictionary<Type, Dictionary<string, Type>>
            {
                [typeof(ApprovalsIndex)] = new() { ["Tab"] = typeof(string), ["Category"] = typeof(string), ["Variety"] = typeof(int?), ["SowingDate"] = typeof(DateTime?) },
                [typeof(MpApprovals)] = new() { ["Tab"] = typeof(string), ["Category"] = typeof(string) },
            };
            foreach (var (t, ok) in allowed)
            {
                var bound = t.GetProperties().Where(p => p.GetCustomAttributes(typeof(BindPropertyAttribute), true).Any()).Select(p => p.Name).ToList();
                Assert.All(bound, n => Assert.Contains(n, ok.Keys));
                Assert.All(bound, n => Assert.Equal(ok[n], t.GetProperty(n)!.PropertyType));
            }
        }
    }
}
