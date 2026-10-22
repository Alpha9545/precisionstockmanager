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
    // 2026-10-20 (owner requirement): Mother Plant & Cutting > Sowing Approvals > Waiting for approval lists ONLY the batches the
    // signed-in user is authorised to approve (filtered on the server: SowingApprovalQueueRepository.GetPendingAsync
    // approvableOnly). Nothing is shown as "Not your approval". Approved by me, the main Sowing Approvals page, the approval
    // handler and the database rules are unchanged. SCRATCH COPY only (same fixtures as the 1019 tests).
    public partial class WorkflowCorrections1019E2ETests
    {
        private static async Task<List<SowingApprovalQueueRepository.Item>> WaitingAsync(Env env, int user)
            => (await MpApprovalsPageAsync(env, user, MpApprovals.TabPending)).Items;

        [SkippableFact]
        public async Task Waiting_ListsOnlyBatchesTheUserMayApprove_AreaSupervisorsAndAdministrator_NeverOtherAreasOrMainOffice()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.TransplantAreaScopeProbeSql);
            var p = await PeopleAsync(env);
            var v = await NewVarietyAsync(env);
            var batch = await TransplantedTrayBatchAsync(env, v, p.Area, 48, ReadyDays);

            // 1 + 6: both Area Supervisors of the Area and a System Administrator see it, with Approve
            foreach (var who in new[] { p.Sup1, p.Sup2, p.Admin })
            {
                var item = Assert.Single(await WaitingAsync(env, who), x => x.Sowing.Id == batch);
                Assert.True(item.Eligibility.CanApprove, item.Eligibility.Reason);
            }
            // 2: another Mother Plant Area's supervisor and a Main Office supervisor: not listed at all
            foreach (var outsider in new[] { p.OtherAreaSup, p.MainSup })
                Assert.DoesNotContain(await WaitingAsync(env, outsider), x => x.Sowing.Id == batch);

            // 3: whoever looks, the Waiting list never holds a batch the user may not approve (so "Not your approval" / "View
            // only" -- rendered only for !Authorized -- can never appear in it)
            foreach (var who in new[] { p.Sup1, p.Sup2, p.OtherAreaSup, p.MainSup, p.Admin, p.Viewer })
                Assert.All(await WaitingAsync(env, who), x => Assert.True(x.Eligibility.Authorized, x.Eligibility.Reason));
        }

        [SkippableFact]
        public async Task Waiting_FilterIsServerSide_ABatchVisibleButNotApprovable_IsLeftOutRatherThanShownAsNotYours()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.TransplantAreaScopeProbeSql);
            var p = await PeopleAsync(env);
            var v = await NewVarietyAsync(env);
            var batch = await TransplantedTrayBatchAsync(env, v, p.Area, 48, ReadyDays);
            var queue = env.Get<SowingApprovalQueueRepository>();
            // a viewer who IS in scope of the batch but may not approve it: another Area's supervisor whose scope is widened for
            // the test, and an in-scope user without the approval permission
            SowingApprovalQueueRepository.Viewer InScope(int id, bool canConfirm) => new(id, "wf1020", canConfirm, _ => true, _ => true);
            foreach (var viewer in new[] { InScope(p.OtherAreaSup, true), InScope(p.Viewer, false) })
            {
                var all = await queue.GetPendingAsync(viewer, SowingApprovalRules.CategoryMotherPlantTransplant);
                var shown = Assert.Single(all.Items, x => x.Sowing.Id == batch);                   // unfiltered: listed, but not theirs
                Assert.False(shown.Eligibility.Authorized);
                var mine = await queue.GetPendingAsync(viewer, SowingApprovalRules.CategoryMotherPlantTransplant, approvableOnly: true);
                Assert.DoesNotContain(mine.Items, x => x.Sowing.Id == batch);                      // filtered: left out
            }
            Assert.Contains((await queue.GetPendingAsync(InScope(p.Sup1, true), SowingApprovalRules.CategoryMotherPlantTransplant, approvableOnly: true)).Items,
                x => x.Sowing.Id == batch);
            // the page asks for the filtered queue (source check: the filter is not only in the Razor view)
            var src = File.ReadAllText(Path.Combine(RepoRootForTests(), "Pages", "Production", "MotherPlant", "SowingApprovals.cshtml.cs"));
            Assert.Contains("approvableOnly: SowingApprovalVisibility.PendingApprovableOnly(User)", src);
        }

        [SkippableFact]
        public async Task Waiting_AnyDate_TheAreaSupervisorSeesAndMayApproveBeforeOnAndAfterTheReadyDate()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.TransplantAreaScopeProbeSql, WorkflowCorrectionsSchema.TransplantAnyDateProbeSql);
            var p = await PeopleAsync(env);
            var v = await NewVarietyAsync(env);
            var batches = new[] { 5, ReadyDays, ReadyDays + 10 };                                    // before / on / after
            var ids = new List<int>();
            foreach (var daysAgo in batches)
                ids.Add(await TransplantedTrayBatchAsync(env, v, p.Area, 24, daysAgo));
            var waiting = await WaitingAsync(env, p.Sup1);
            foreach (var id in ids)
                Assert.True(Assert.Single(waiting, x => x.Sowing.Id == id).Eligibility.CanApprove);
            // and approving the "before" one still works and posts once
            Assert.True((await env.Get<ReadyConfirmationRepository>().ConfirmAsync(ids[0], 1, null, null, null, "wf1020", p.Sup1, Any)).Success);
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStock WHERE SeedSowingId = @S", ("@S", ids[0])));
            Assert.DoesNotContain(await WaitingAsync(env, p.Sup2), x => x.Sowing.Id == ids[0]);
        }

        [SkippableFact]
        public async Task Queue_BedSowing_OnlyMayaHasItAmongHerApprovableBatches_OtherSupervisorsDoNot()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.BedApproverProbeSql);
            var settings = await env.Get<ReadyConfirmationRepository>().GetApprovalSettingsAsync();
            Skip.If(settings.Bed.UserId is null, "No active Bed Sowing approver in this copy.");
            var maya = settings.Bed.UserId!.Value;
            var bedBatch = await env.ScalarAsync<int>(@"SELECT TOP 1 Id FROM dbo.SeedSowings WHERE SourceType = N'BedSowing' AND Status = N'Sown'
AND ConfirmedReadyQuantity + WastageQuantity < QuantitySown ORDER BY Id");
            Skip.If(bedBatch == 0, "No pending Bed Sowing batch in this copy.");
            var other = (await env.IdsAsync(ConfirmersOfArea + " AND ur.UserId <> @Maya ORDER BY ur.UserId", ("@A", MainArea), ("@Maya", maya))).First();
            var queue = env.Get<SowingApprovalQueueRepository>();
            async Task<List<SowingApprovalQueueRepository.Item>> ApprovableAsync(int user)
            {
                var principal = await env.LoginAsync(user);
                var canConfirm = await env.Get<FeatureAccessService>().CanAccessPageAsync(principal, "/Production/ReadyConfirmation/Confirm");
                return (await queue.GetPendingAsync(ApprovalsIndex.Viewer(principal, canConfirm, env.Get<SeedlingAreaScope>(), env.Get<AreaAccessService>()),
                    SowingApprovalRules.CategoryBedSowing, approvableOnly: true)).Items;
            }
            Assert.Contains(await ApprovableAsync(maya), x => x.Sowing.Id == bedBatch);
            Assert.DoesNotContain(await ApprovableAsync(other), x => x.Sowing.Id == bedBatch);
            Assert.DoesNotContain(await ApprovableAsync((await PeopleAsync(env)).Admin), x => x.Sowing.Id == bedBatch);   // not even an administrator
            // the Mother Plant Waiting list never carries Bed Sowing batches (separate workflow)
            Assert.All(await WaitingAsync(env, maya), x => Assert.True(SowingApprovalRules.IsMotherPlantCategory(x.Category), x.Category));
        }

        [SkippableFact]
        public async Task Waiting_ForgedIdsGrantNothing_HiddenBatchStillRefusedOnDirectPost_HistoryAndMainPageUnchanged()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.TransplantAreaScopeProbeSql);
            var p = await PeopleAsync(env);
            var v = await NewVarietyAsync(env);
            var batch = await TransplantedTrayBatchAsync(env, v, p.Area, 48, ReadyDays);

            // 8: another Area's supervisor forges the Area Supervisor's id everywhere -- the Waiting list stays without the batch
            var page = env.Page<MpApprovals>(await env.LoginAsync(p.OtherAreaSup));
            var http = (DefaultHttpContext)page.HttpContext;
            http.Request.QueryString = new QueryString($"?tab=pending&userId={p.Sup1}&approverId={p.Sup1}&UserId={p.Sup1}&areaId={p.Area}");
            http.Request.ContentType = "application/x-www-form-urlencoded";
            http.Request.Form = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
                { ["UserId"] = p.Sup1.ToString(), ["AreaId"] = p.Area.ToString() });
            page.Tab = MpApprovals.TabPending;
            await page.OnGetAsync();
            Assert.DoesNotContain(page.Items, x => x.Sowing.Id == batch);

            // 9: the hidden batch posted straight to the approval handler: refused, nothing posted
            var (result, confirm) = await ApproveViaPageAsync(env, p.OtherAreaSup, batch, 2);
            Assert.IsType<RedirectToPageResult>(result);
            Assert.NotEqual("", Errors(confirm));
            var (ok, msg, _) = await env.Get<ReadyConfirmationRepository>().ConfirmAsync(batch, 2, null, null, null, "wf1020", p.OtherAreaSup, Any);
            Assert.False(ok, msg);
            Assert.Equal((0, 0), (await env.ApprovalsOfAsync(batch), await env.ReadyStockOfAsync(batch)));

            // 7: Approved by me unchanged -- after Sup1 approves, it is Sup1's history only
            Assert.True((await env.Get<ReadyConfirmationRepository>().ConfirmAsync(batch, 2, null, null, null, "wf1020", p.Sup1, Any)).Success);
            Assert.Contains((await MpApprovalsPageAsync(env, p.Sup1)).History, x => x.SeedSowingId == batch);
            Assert.DoesNotContain((await MpApprovalsPageAsync(env, p.Sup2)).History, x => x.SeedSowingId == batch);
            Assert.DoesNotContain(await WaitingAsync(env, p.Sup1), x => x.Sowing.Id == batch);       // approved: no longer waiting

            // 10: 2026-10-20 visibility policy on the main Sowing Approvals page too -- a view-only user is shown nothing to approve
            // (no "View only" rows), the administrator every pending Main Office batch; the Seed-sowing rule is the same
            Assert.Empty(await MainPageAsync(env, p.Viewer));
            Assert.Equal(await env.ScalarAsync<int>(@"SELECT COUNT(*) FROM dbo.SeedSowings sw JOIN dbo.Area a ON a.Id = sw.AreaId
WHERE sw.Status = N'Sown' AND sw.ConfirmedReadyQuantity + sw.WastageQuantity < sw.QuantitySown AND sw.AreaId = @A", ("@A", MainArea)),
                (await MainPageAsync(env, p.Admin)).Count(x => x.Sowing.AreaId == MainArea));
            var seed = new SeedSowing { SourceType = SeedSowing.SourceSeed, SupervisorId = p.MainSup, CreatedById = Recorder, CreatedBy = "rec", AreaName = "Main" };
            var settings = new SowingApprovalRules.Settings(DirectSowingRules.BedApprover.NotConfigured, false, true, true);
            Assert.True(SowingApprovalRules.Evaluate(seed, p.MainSup, "m", true, false, settings, AppClock.Today).CanApprove);
            Assert.Equal(DirectSowingRules.NotAssignedMessage, SowingApprovalRules.Evaluate(seed, p.Sup1, "a", true, true, settings, AppClock.Today).Reason);
        }

        private static string RepoRootForTests()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.csproj")))
                dir = dir.Parent;
            return dir!.FullName;
        }
    }
}
