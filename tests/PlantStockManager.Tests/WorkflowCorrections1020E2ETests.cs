using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using ConfirmPage = PlantStockManager.Pages.Production.ReadyConfirmation.ConfirmModel;
using MpApprovals = PlantStockManager.Pages.Production.MotherPlant.SowingApprovalsModel;

namespace PlantStockManager.Tests
{
    // 2026-10-20 (owner requirements), end-to-end on a SCRATCH COPY only (same fixtures and safety rules as the 1019 tests):
    //   1. An Area Supervisor of a Mother Plant Area approves its transplant batch on ANY date (before / on / after its Ready
    //      Date) once 2026-10-20_TransplantApprovalAnyDate.sql is applied -- exactly once into Ready Stock, still only in its
    //      own Area, Ready Date unchanged, the approver and the real approval time recorded.
    //   2. Mother Plant & Cutting > Sowing Approvals > "Approved by me" lists only the batches THIS supervisor approved
    //      (ReadyConfirmations.ApprovedById from the signed-in principal); the pending queue stays shared.
    public partial class WorkflowCorrections1019E2ETests
    {
        private static async Task<MpApprovals> MpApprovalsPageAsync(Env env, int user, string? tab = null)
        {
            var page = env.Page<MpApprovals>(await env.LoginAsync(user));
            page.Tab = tab;
            await page.OnGetAsync();
            return page;
        }

        private static async Task<(IActionResult Result, ConfirmPage Page)> ApproveViaPageAsync(Env env, int user, int sowing, decimal trays)
        {
            var page = env.Page<ConfirmPage>(await env.LoginAsync(user));
            page.SeedSowingId = sowing;
            page.ActualReadyTrays = trays;
            page.From = ConfirmPage.FromMotherPlant;
            return (await page.OnPostAsync(), page);
        }

        private static string Errors(PageModel page) => string.Join(" | ", page.ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage))
            + (page.TempData["Error"] as string ?? "");

        // =====================================================================================================
        // 1. Any-date approval by the Area Supervisor (needs 2026-10-20_TransplantApprovalAnyDate.sql)
        // =====================================================================================================

        [SkippableFact]
        public async Task AnyDate_AreaSupervisorApprovesBeforeOnAndAfterTheReadyDate_EachPostedOnce_ReadyDateUnchanged_ApproverAndTimeRecorded()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.TransplantAreaScopeProbeSql, WorkflowCorrectionsSchema.TransplantAnyDateProbeSql);
            var p = await PeopleAsync(env);
            var v = await NewVarietyAsync(env);
            var batches = new (string When, int Sowing, DateTime Ready)[3];
            var i = 0;
            foreach (var (when, daysAgo) in new[] { ("before", 5), ("on", ReadyDays), ("after", ReadyDays + 10) })
            {
                var sowing = await TransplantedTrayBatchAsync(env, v, p.Area, 48, daysAgo);
                batches[i++] = (when, sowing, AppClock.Today.AddDays(ReadyDays - daysAgo));
            }
            Assert.True(batches[0].Ready > AppClock.Today && batches[1].Ready == AppClock.Today && batches[2].Ready < AppClock.Today);

            // the list offers Approve on all three (no "Approve from" wait), for both Area Supervisors
            foreach (var sup in new[] { p.Sup1, p.Sup2 })
            {
                var items = (await MpApprovalsPageAsync(env, sup, MpApprovals.TabPending)).Items;
                Assert.All(batches, b => Assert.True(Assert.Single(items, x => x.Sowing.Id == b.Sowing).Eligibility.CanApprove));
            }

            foreach (var b in batches)
            {
                var before = DateTime.UtcNow.AddSeconds(-5);
                // three simultaneous submits of the Approve form (double click): one approval, one Ready Stock posting
                var clicks = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => ApproveViaPageAsync(env, p.Sup1, b.Sowing, 2)));
                Assert.Equal(1, clicks.Count(c => c.Page.TempData["Success"] != null));
                Assert.Equal(1, await env.ApprovalsOfAsync(b.Sowing));
                Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStock WHERE SeedSowingId = @S", ("@S", b.Sowing)));
                var rs = await env.ReadyStockOfAsync(b.Sowing);
                Assert.Equal((p.Area, 48m), (await env.ScalarAsync<int>("SELECT AreaId FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs)),
                    await env.ScalarAsync<decimal>("SELECT SUM(Quantity) FROM dbo.ReadyStockTransactions WHERE ReadyStockId = @R AND TransactionType = N'Confirmed'", ("@R", rs))));
                // the approver is the signed-in supervisor; the approval time is the real moment; the Ready Date is unchanged
                Assert.Equal(p.Sup1, await env.ScalarAsync<int>("SELECT ApprovedById FROM dbo.ReadyConfirmations WHERE SeedSowingId = @S", ("@S", b.Sowing)));
                var at = await env.ScalarAsync<DateTime>("SELECT ConfirmationDate FROM dbo.ReadyConfirmations WHERE SeedSowingId = @S", ("@S", b.Sowing));
                Assert.InRange(at, before, DateTime.UtcNow.AddSeconds(5));
                Assert.Equal(b.Ready, await env.ScalarAsync<DateTime>("SELECT ExpectedReadyDate FROM dbo.SeedSowings WHERE Id = @S", ("@S", b.Sowing)));
                Assert.Equal("Completed", await env.ScalarAsync<string>("SELECT Status FROM dbo.SeedSowings WHERE Id = @S", ("@S", b.Sowing)));
                // a later approval of the same batch (the other supervisor, a stale page): refused, still one
                var (_, late) = await ApproveViaPageAsync(env, p.Sup2, b.Sowing, 2);
                Assert.NotEqual("", Errors(late));
                Assert.Equal(1, await env.ApprovalsOfAsync(b.Sowing));
            }
        }

        [SkippableFact]
        public async Task AnyDate_StillOnlyThisAreasSupervisor_OtherAreaAndMainOffice_RefusedByPageRepositoryAndTrigger()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.TransplantAreaScopeProbeSql, WorkflowCorrectionsSchema.TransplantAnyDateProbeSql);
            var p = await PeopleAsync(env);
            var v = await NewVarietyAsync(env);
            var early = await TransplantedTrayBatchAsync(env, v, p.Area, 48, 5);                       // before its Ready Date
            foreach (var outsider in new[] { p.OtherAreaSup, p.MainSup })
            {
                var (_, page) = await ApproveViaPageAsync(env, outsider, early, 2);
                Assert.NotEqual("", Errors(page));
                var (ok, msg, _) = await env.Get<ReadyConfirmationRepository>().ConfirmAsync(early, 2, null, null, null, "wf1020", outsider, Any);
                Assert.False(ok);
                Assert.Contains("Area Supervisor of", msg);
                Assert.DoesNotContain(await MpPageAsync(env, outsider), x => x.Sowing.Id == early);
            }
            // the database refuses a forged direct insert by another Area's supervisor, whatever the date
            var anyRs = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.ReadyStock ORDER BY Id");
            var ex = await Assert.ThrowsAsync<SqlException>(() => env.ScalarAsync<int>(@"
INSERT INTO dbo.ReadyConfirmations (ConfirmationCode, SeedSowingId, ReadyStockId, ActualTrayQuantity, ConfirmedQuantity, WastageQuantity, ApprovedById,
    ConfirmationDate, Status, SupervisorId, CreatedDate, CreatedBy)
VALUES (@C, @S, @R, 2, 48, 0, @U, SYSUTCDATETIME(), 'Confirmed', @U, SYSUTCDATETIME(), N'wf1020'); SELECT 1",
                ("@C", "ZZT-" + Guid.NewGuid().ToString("N")[..10]), ("@S", early), ("@R", anyRs), ("@U", p.OtherAreaSup)));
            Assert.Contains("Only the assigned supervisor", ex.Message);
            Assert.Equal((0, 0), (await env.ApprovalsOfAsync(early), await env.ReadyStockOfAsync(early)));
            // the Area's own supervisor may -- before the Ready Date
            Assert.True((await env.Get<ReadyConfirmationRepository>().ConfirmAsync(early, 2, null, null, null, "wf1020", p.Sup2, Any)).Success);
        }

        [SkippableFact]
        public async Task WithoutTheAnyDateMigration_TransplantBatch_StillWaitsForItsReadyDate()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.TransplantAreaScopeProbeSql);
            Skip.If(await env.ScalarAsync<int>(WorkflowCorrectionsSchema.TransplantAnyDateProbeSql) == 1, "2026-10-20_TransplantApprovalAnyDate.sql is applied to this copy.");
            var p = await PeopleAsync(env);
            var v = await NewVarietyAsync(env);
            var early = await TransplantedTrayBatchAsync(env, v, p.Area, 48, 5);
            Assert.True(Assert.Single((await MpApprovalsPageAsync(env, p.Sup1, MpApprovals.TabPending)).Items, x => x.Sowing.Id == early).Eligibility.WaitingForReadyDate);
            var (ok, msg, _) = await env.Get<ReadyConfirmationRepository>().ConfirmAsync(early, 2, null, null, null, "wf1020", p.Sup1, Any);
            Assert.False(ok);
            Assert.Equal(string.Format(BedSowingRules.TransplantNotReadyYetMessageFormat, AppClock.Today.AddDays(ReadyDays - 5)), msg);
            Assert.Equal((0, 0), (await env.ApprovalsOfAsync(early), await env.ReadyStockOfAsync(early)));
        }

        // Other workflows keep their date rules (with or without the migration): Bed Sowing -- designated approver only
        // (any date only with 2026-10-17 BedAnyDate); Seed / Cutting -- no date rule, assigned supervisor only.
        [SkippableFact]
        public async Task OtherWorkflows_KeepTheirDateRules_AndBedSowingStaysMayaOnly()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.BedApproverProbeSql);
            var settings = await env.Get<ReadyConfirmationRepository>().GetApprovalSettingsAsync();
            // the any-date flag is never applied to Bed Sowing / Seed / Cutting
            var future = AppClock.Today.AddDays(10);
            Assert.False(BedSowingRules.ValidateApprovalReadyDate(SeedSowing.SourceBedSowing, future, AppClock.Today, bedAnyDate: false, transplantAnyDate: true).Ok);
            Assert.True(BedSowingRules.ValidateApprovalReadyDate(SeedSowing.SourceSeed, future, AppClock.Today, false, true).Ok);
            Assert.True(BedSowingRules.ValidateApprovalReadyDate(SeedSowing.SourceCutting, future, AppClock.Today, false, true).Ok);
            Assert.Equal(settings.BedAnyDate, BedSowingRules.ValidateApprovalReadyDate(SeedSowing.SourceBedSowing, future, AppClock.Today, settings.BedAnyDate, settings.TransplantAnyDate).Ok);
            // the Bed Sowing trigger rule in the database is unchanged by 2026-10-20 (Maya only), whatever the Transplant rule
            var def = await env.ScalarAsync<string>("SELECT OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_AssignedSupervisor'))");
            Assert.Contains("BedSowingApprover", def);
            Assert.Contains("Only the designated Bed Sowing approver", def);
            var bedTrigger = await env.ScalarAsync<string>("SELECT OBJECT_DEFINITION(OBJECT_ID('dbo.TR_ReadyConfirmations_BedReadyDate'))");
            Assert.DoesNotContain("N'BedSowing'", bedTrigger);                                   // no Bed Sowing date check was added back
            Skip.If(settings.Bed.UserId is null, "No active Bed Sowing approver in this copy.");
            var maya = settings.Bed.UserId!.Value;
            var bedBatch = await env.ScalarAsync<int>(@"SELECT TOP 1 Id FROM dbo.SeedSowings WHERE SourceType = N'BedSowing' AND Status = N'Sown'
AND ConfirmedReadyQuantity + WastageQuantity < QuantitySown ORDER BY Id");
            Skip.If(bedBatch == 0, "No pending Bed Sowing batch in this copy.");
            var other = (await env.IdsAsync(ConfirmersOfArea + " AND ur.UserId <> @Maya ORDER BY ur.UserId", ("@A", MainArea), ("@Maya", maya))).First();
            // 2026-10-20 visibility policy: hidden from a supervisor who may not approve it; the server still refuses them
            Assert.DoesNotContain(await MainPageAsync(env, other, SowingApprovalRules.CategoryBedSowing), x => x.Sowing.Id == bedBatch);
            Assert.Equal(DirectSowingRules.NotBedApproverMessage(settings.Bed.Name),
                (await env.Get<ReadyConfirmationRepository>().ConfirmAsync(bedBatch, 1, null, null, null, "wf1020", other, Any)).Message);
            // Maya keeps her authority (any refusal for her can only be about the date when BedAnyDate is not applied)
            var forMaya = Assert.Single(await MainPageAsync(env, maya, SowingApprovalRules.CategoryBedSowing), x => x.Sowing.Id == bedBatch).Eligibility;
            Assert.True(forMaya.CanApprove || forMaya.WaitingForReadyDate, forMaya.Reason);
            if (settings.BedAnyDate)
                Assert.True(forMaya.CanApprove, forMaya.Reason);
        }

        // =====================================================================================================
        // 2. "Approved by me" -- the signed-in supervisor's own approval history
        // =====================================================================================================

        [SkippableFact]
        public async Task ApprovedByMe_EachSupervisorSeesOnlyTheirOwnApprovals_PendingQueueStaysShared_KeptAfterTheStockMoves()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.TransplantAreaScopeProbeSql);
            var p = await PeopleAsync(env);
            var v = await NewVarietyAsync(env);
            var byA = await TransplantedTrayBatchAsync(env, v, p.Area, 48, ReadyDays);
            var byB = await TransplantedTrayBatchAsync(env, v, p.Area, 72, ReadyDays);
            var pending = await TransplantedTrayBatchAsync(env, v, p.Area, 24, ReadyDays);
            Assert.Equal("/Production/MotherPlant/SowingApprovals", Assert.IsType<RedirectToPageResult>((await ApproveViaPageAsync(env, p.Sup1, byA, 2)).Result).PageName);
            Assert.IsType<RedirectToPageResult>((await ApproveViaPageAsync(env, p.Sup2, byB, 3)).Result);
            // 2026-10-21: "Approved by me" also lists Mother Plant Area Direct Sowings (with 2026-10-21_MotherPlantDirectSowing.sql).
            // Both are RECORDED by Sup1; one is approved by Sup1 and one by Sup2 -- the approver, never the recorder, decides
            // whose history a batch is in.
            var mpDirect = await env.ScalarAsync<int>(WorkflowCorrectionsSchema.MotherPlantDirectSowingProbeSql) == 1;
            int? directA = null, directB = null;
            if (mpDirect)
            {
                directA = await MpdBatchAsync(env, p.Sup1, p.Area, v, 48);
                directB = await MpdBatchAsync(env, p.Sup1, p.Area, v, 72);
                var (rA, pA) = await ApproveViaPageAsync(env, p.Sup1, directA.Value, 2);
                Assert.True(rA is RedirectToPageResult && await env.ApprovalsOfAsync(directA.Value) == 1, Errors(pA));
                var (rB, pB) = await ApproveViaPageAsync(env, p.Sup2, directB.Value, 3);
                Assert.True(rB is RedirectToPageResult && await env.ApprovalsOfAsync(directB.Value) == 1, Errors(pB));
            }

            var a = await MpApprovalsPageAsync(env, p.Sup1);                                            // default tab: Approved by me
            var b = await MpApprovalsPageAsync(env, p.Sup2);
            Assert.False(a.ShowPending);
            Assert.Contains(a.History, x => x.SeedSowingId == byA);
            Assert.DoesNotContain(a.History, x => x.SeedSowingId == byB);
            Assert.Contains(b.History, x => x.SeedSowingId == byB);
            Assert.DoesNotContain(b.History, x => x.SeedSowingId == byA);
            // every row of each list really is that supervisor's approval (straight from the table, independent of the code)
            foreach (var (page, me) in new[] { (a, p.Sup1), (b, p.Sup2) })
                foreach (var row in page.History)
                    Assert.Equal(me, await env.ScalarAsync<int>("SELECT ApprovedById FROM dbo.ReadyConfirmations WHERE Id = @C", ("@C", row.ConfirmationId)));
            // ...and each list holds ALL of that supervisor's Mother Plant approvals -- Transplant batches and (2026-10-21) Mother
            // Plant Area Direct Sowings -- and nothing else. The workflow OR is parenthesised so it can never bypass the
            // ApprovedById filter.
            var workflows = mpDirect ? "(sw.SourceType = N'Transplant' OR sw.IsMotherPlantDirectSowing = 1)" : "(sw.SourceType = N'Transplant')";
            foreach (var (page, me) in new[] { (a, p.Sup1), (b, p.Sup2) })
                Assert.Equal(await env.ScalarAsync<int>(@"SELECT COUNT(*) FROM dbo.ReadyConfirmations rc JOIN dbo.SeedSowings sw ON sw.Id = rc.SeedSowingId
JOIN dbo.Area ar ON ar.Id = sw.AreaId WHERE rc.ApprovedById = @U AND ar.AreaType = N'MotherPlant' AND " + workflows, ("@U", me)), page.History.Count);
            if (mpDirect)
            {
                // the Direct Sowing each supervisor approved is in THEIR list (as a Direct Sowing), never in the other's
                Assert.Equal(SowingApprovalRules.CategoryMotherPlantDirectSowing, Assert.Single(a.History, x => x.SeedSowingId == directA).Category);
                Assert.Equal(SowingApprovalRules.CategoryMotherPlantDirectSowing, Assert.Single(b.History, x => x.SeedSowingId == directB).Category);
                Assert.DoesNotContain(a.History, x => x.SeedSowingId == directB);   // recorded by Sup1, approved by Sup2
                Assert.DoesNotContain(b.History, x => x.SeedSowingId == directA);
                Assert.Contains(a.History, x => x.Category == SowingApprovalRules.CategoryMotherPlantTransplant);   // both workflows in one list
            }
            var mine = Assert.Single(a.History, x => x.SeedSowingId == byA);
            Assert.Equal(("Confirmed", 2, 48m, p.Area), (mine.Status, mine.ActualTrays ?? 0, mine.ApprovedQuantity,
                await env.ScalarAsync<int>("SELECT AreaId FROM dbo.SeedSowings WHERE Id = @S", ("@S", byA))));
            Assert.NotNull(mine.SourceBatchNo);

            // the pending queue is NOT filtered by user: both supervisors see the same pending batch
            Assert.Contains((await MpApprovalsPageAsync(env, p.Sup1, MpApprovals.TabPending)).Items, x => x.Sowing.Id == pending);
            Assert.Contains((await MpApprovalsPageAsync(env, p.Sup2, MpApprovals.TabPending)).Items, x => x.Sowing.Id == pending);
            Assert.DoesNotContain(a.History, x => x.SeedSowingId == pending);

            // after A's batch moves to an Outlet it is still in A's history (the approval row is the filter, not the stock's Area)
            var rs = await env.ReadyStockOfAsync(byA);
            var (sent, why, _) = await env.Get<InternalTransferRepository>().InsertAsync(new InternalTransfer
            {
                StockType = "ReadyStock", SourceReadyStockId = rs, DestinationAreaId = OutletArea, Quantity = 48, CreatedBy = "wf1020", SubmissionToken = Guid.NewGuid()
            }, p.Sup1);
            Assert.True(sent, why);
            var moved = Assert.Single((await MpApprovalsPageAsync(env, p.Sup1)).History, x => x.SeedSowingId == byA);
            Assert.Equal(await env.ScalarAsync<string>("SELECT Name FROM dbo.Area WHERE Id = @A", ("@A", OutletArea)), moved.ReadyStockAreaName);
            Assert.DoesNotContain((await MpReadyStockTraysAsync(env, p.Sup1)), id => id == rs);

            // other users: another Area's supervisor sees neither A's nor B's approvals; 2026-10-20 (visibility policy) the
            // administrator sees both -- each with its recorded approver
            Assert.DoesNotContain((await MpApprovalsPageAsync(env, p.OtherAreaSup)).History,
                x => x.SeedSowingId == byA || x.SeedSowingId == byB || x.SeedSowingId == directA || x.SeedSowingId == directB);
            var adminHistory = (await MpApprovalsPageAsync(env, p.Admin)).History;
            Assert.Equal(p.Sup1, Assert.Single(adminHistory, x => x.SeedSowingId == byA).ApproverId);
            Assert.Equal(p.Sup2, Assert.Single(adminHistory, x => x.SeedSowingId == byB).ApproverId);
            if (mpDirect)
            {
                Assert.Equal(p.Sup1, Assert.Single(adminHistory, x => x.SeedSowingId == directA).ApproverId);
                Assert.Equal(p.Sup2, Assert.Single(adminHistory, x => x.SeedSowingId == directB).ApproverId);
            }
        }

        private static async Task<List<int>> MpReadyStockTraysAsync(Env env, int user)
        {
            var page = env.Page<PlantStockManager.Pages.Production.MotherPlant.AreaReadyStockModel>(await env.LoginAsync(user));
            await page.OnGetAsync();
            return page.Trays.Select(t => t.Id).ToList();
        }

        [SkippableFact]
        public async Task ApprovedByMe_UsesTheSignedInIdentity_AForgedRequestCannotShowAnotherSupervisorsApprovals()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.TransplantAreaScopeProbeSql);
            var p = await PeopleAsync(env);
            var v = await NewVarietyAsync(env);
            var byB = await TransplantedTrayBatchAsync(env, v, p.Area, 48, ReadyDays);
            Assert.True((await env.Get<ReadyConfirmationRepository>().ConfirmAsync(byB, 2, null, null, null, "wf1020", p.Sup2, Any)).Success);

            // A's request forged to name B in every way a browser could: query string, form fields, route values
            var page = env.Page<MpApprovals>(await env.LoginAsync(p.Sup1));
            var http = (DefaultHttpContext)page.HttpContext;
            var forged = $"?approverId={p.Sup2}&ApprovedById={p.Sup2}&userId={p.Sup2}&UserId={p.Sup2}&supervisorId={p.Sup2}&tab=approved";
            http.Request.QueryString = new QueryString(forged);
            http.Request.ContentType = "application/x-www-form-urlencoded";
            http.Request.Form = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
                { ["ApprovedById"] = p.Sup2.ToString(), ["UserId"] = p.Sup2.ToString() });
            page.RouteData.Values["approverId"] = p.Sup2;
            await page.OnGetAsync();
            Assert.DoesNotContain(page.History, x => x.SeedSowingId == byB);
            foreach (var row in page.History)
                Assert.Equal(p.Sup1, await env.ScalarAsync<int>("SELECT ApprovedById FROM dbo.ReadyConfirmations WHERE Id = @C", ("@C", row.ConfirmationId)));
            // and B sees it
            Assert.Contains((await MpApprovalsPageAsync(env, p.Sup2)).History, x => x.SeedSowingId == byB);
        }
    }
}
