using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Primitives;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using ApprovalsIndex = PlantStockManager.Pages.Production.ReadyConfirmation.IndexModel;
using ConfirmPage = PlantStockManager.Pages.Production.ReadyConfirmation.ConfirmModel;
using DirectSowingPage = PlantStockManager.Pages.Production.MotherPlant.DirectSowingModel;
using MpApprovals = PlantStockManager.Pages.Production.MotherPlant.SowingApprovalsModel;

namespace PlantStockManager.Tests
{
    // 2026-10-21 (owner requirement 2026-10-13): Mother Plant & Cutting > Direct Sowing, end to end on a SCRATCH COPY only
    // (PSM_SCRATCH_CONNECTION, PlantsIMS2_Scratch_*; SKIPPED without 2026-10-21_MotherPlantDirectSowing.sql). REAL users (their
    // principals from dbo.UserRoles via the real claims factory) drive the REAL page models, repositories and database triggers.
    public partial class WorkflowCorrections1019E2ETests
    {
        private const string MpdCavity = "24 Cavity";

        private static Task<Env> OpenMpdAsync() => OpenAsync(WorkflowCorrectionsSchema.MotherPlantDirectSowingProbeSql, WorkflowCorrectionsSchema.TransplantAreaScopeProbeSql);

        private static async Task AddAreaTraysAsync(Env env, int area, decimal trays, string cavity = MpdCavity)
            => Assert.True((await env.Get<TrayStockRepository>().AddStockAsync(area, null, cavity, trays, DateTime.UtcNow.AddDays(-1),
                Random.Shared.Next(1, int.MaxValue), SowingSupervisor, "mpd-test", "mpd trays", Any)).Success);

        private static Task<decimal> AreaTraysAsync(Env env, int area, string cavity = MpdCavity)
            => env.ScalarAsync<decimal>("SELECT ISNULL(SUM(PhysicalQuantity), 0) FROM dbo.TrayStock WHERE AreaId = @A AND PolyhouseId IS NULL AND TraySize = @C",
                ("@A", area), ("@C", cavity));

        private static Task<decimal> LotAvailableAsync(Env env, int lot)
            => env.ScalarAsync<decimal>("SELECT PhysicalQuantity - InTransitQuantity FROM dbo.SeedStock WHERE Id = @L", ("@L", lot));

        // The real page: real principal, posted form (with any extra -- forged -- fields), the page's own OnPostAsync.
        private static async Task<(IActionResult Result, DirectSowingPage Page)> SowViaPageAsync(Env env, int user, int area, Variety v, decimal seeds,
            Guid? token = null, IDictionary<string, string>? extraForm = null, DateTime? date = null)
        {
            var page = env.Page<DirectSowingPage>(await env.LoginAsync(user));
            var form = new Dictionary<string, StringValues> { ["AreaId"] = area.ToString() };
            foreach (var (k, val) in extraForm ?? new Dictionary<string, string>())
                form[k] = val;
            page.HttpContext.Request.ContentType = "application/x-www-form-urlencoded";
            page.HttpContext.Request.Form = new FormCollection(form);
            page.AreaId = area;
            page.SpeciesId = v.SpeciesId;
            page.SourceSeedStockId = v.SeedStockId;
            page.SeedQuantity = seeds;
            page.CavityType = MpdCavity;
            page.SowingDate = date ?? AppClock.Today.AddDays(-2);
            page.SubmissionToken = token ?? Guid.NewGuid();
            return (await page.OnPostAsync(), page);
        }

        private static string Errors(DirectSowingPage page) => string.Join(" | ", page.ModelState.Values.SelectMany(x => x.Errors).Select(e => e.ErrorMessage));

        private static async Task<int> SowingOfTokenAsync(Env env, Guid token)
            => await env.ScalarAsync<int>("SELECT ISNULL(MAX(Id), 0) FROM dbo.SeedSowings WHERE SubmissionToken = @T", ("@T", token));

        // A pending Mother Plant Area Direct Sowing recorded by `user` through the page -> its sowing id.
        private static async Task<int> MpdBatchAsync(Env env, int user, int area, Variety v, decimal seeds = 240)
        {
            await AddAreaTraysAsync(env, area, Math.Ceiling(seeds / 24) + 2);
            var token = Guid.NewGuid();
            var (result, page) = await SowViaPageAsync(env, user, area, v, seeds, token);
            Assert.True(result is RedirectToPageResult, Errors(page));
            var id = await SowingOfTokenAsync(env, token);
            Assert.True(id > 0);
            return id;
        }

        // =====================================================================================================
        // 2, 3, 7, 8: a valid sowing -> pending, in the right Area Supervisors' queue only; trays from THAT Area; nothing posted
        [SkippableFact]
        public async Task MpDirectSowing_Valid_IsPendingInItsAreaSupervisorsQueue_TraysFromItsArea_NothingPostedBeforeApproval()
        {
            var env = await OpenMpdAsync();
            var p = await PeopleAsync(env);
            var v = await NewVarietyAsync(env);
            var otherArea = await MotherPlantAreaOfAsync(env, p.OtherAreaSup);
            await AddAreaTraysAsync(env, p.Area, 20);
            await AddAreaTraysAsync(env, otherArea, 5);
            var areaBefore = await AreaTraysAsync(env, p.Area);
            var otherBefore = await AreaTraysAsync(env, otherArea);
            var mainBefore = await env.ScalarAsync<decimal>("SELECT ISNULL(SUM(PhysicalQuantity), 0) FROM dbo.TrayStock WHERE AreaId = @A", ("@A", MainArea));
            var lotBefore = await LotAvailableAsync(env, v.SeedStockId);

            // the form offers only this supervisor's active Mother Plant Areas
            var get = env.Page<DirectSowingPage>(await env.LoginAsync(p.Sup1));
            await get.OnGetAsync();
            Assert.True(get.SchemaReady);
            Assert.Contains(get.Areas, a => a.Id == p.Area);
            Assert.DoesNotContain(get.Areas, a => a.Id == otherArea || a.Id == MainArea);
            Assert.All(get.Areas, a => Assert.Equal(MotherPlantAreaSchema.MotherPlantAreaType, a.AreaType));

            // 250 seeds at 24 -> 10 complete trays, 240 sown, 10 wasted, 11 physical trays
            var token = Guid.NewGuid();
            var (result, page) = await SowViaPageAsync(env, p.Sup1, p.Area, v, 250, token);
            Assert.True(result is RedirectToPageResult, Errors(page));
            var id = await SowingOfTokenAsync(env, token);
            var s = (await env.Get<SeedSowingRepository>().GetByIdAsync(id))!;
            Assert.True(s.IsMotherPlantDirectSowing);
            Assert.Equal(SeedSowing.SourceSeed, s.SourceType);
            Assert.Equal(p.Area, s.AreaId);
            Assert.Null(s.PolyhouseId);
            Assert.Null(s.SupervisorId);
            Assert.Null(s.GrowingMaterialTypeId);
            Assert.Equal("Sown", s.Status);
            Assert.Equal(p.Sup1, s.CreatedById);
            Assert.Equal(10, s.NumberOfTrays);
            Assert.Equal(240m, s.QuantitySown);
            Assert.Equal(MpdCavity, s.CavityType);
            Assert.Equal(AppClock.Today.AddDays(-2), s.SowingDate.Date);
            Assert.Equal(AppClock.Today.AddDays(-2 + ReadyDays), s.ExpectedReadyDate);   // Direct Sowing rule: date + growing days
            Assert.Equal(SowingApprovalRules.CategoryMotherPlantDirectSowing, SowingApprovalRules.CategoryOfBatch(s));

            // stock: the Main Office lot (-240 sown, -10 waste), 11 trays from THIS Area's pool only, no material, no Ready Stock
            Assert.Equal(lotBefore - 250, await LotAvailableAsync(env, v.SeedStockId));
            Assert.Equal(areaBefore - 11, await AreaTraysAsync(env, p.Area));
            Assert.Equal(otherBefore, await AreaTraysAsync(env, otherArea));
            Assert.Equal(mainBefore, await env.ScalarAsync<decimal>("SELECT ISNULL(SUM(PhysicalQuantity), 0) FROM dbo.TrayStock WHERE AreaId = @A", ("@A", MainArea)));
            Assert.Equal(1, await env.ScalarAsync<int>(@"SELECT COUNT(*) FROM dbo.TrayStockTransactions t JOIN dbo.TrayStock ts ON ts.Id = t.TrayStockId
WHERE t.ReferenceType = N'SeedSowing' AND t.ReferenceId = @S AND ts.AreaId = @A AND ts.PolyhouseId IS NULL AND t.Quantity = -11", ("@S", id), ("@A", p.Area)));
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.MaterialTransactions WHERE ReferenceType = N'SeedSowing' AND ReferenceId = @S", ("@S", id)));
            Assert.Equal(0, await env.ReadyStockOfAsync(id));
            Assert.Equal(0, await env.ApprovalsOfAsync(id));

            // queue: both Area Supervisors of the Area may approve it; nobody else sees it; the admin sees it, with who approves
            foreach (var sup in new[] { p.Sup1, p.Sup2 })
            {
                var item = Assert.Single((await MpApprovalsPageAsync(env, sup, MpApprovals.TabPending)).Items, i => i.Sowing.Id == id);
                Assert.True(item.Eligibility.CanApprove, item.Eligibility.Reason);
                Assert.Equal(SowingApprovalRules.CategoryMotherPlantDirectSowing, item.Category);
            }
            foreach (var user in new[] { p.OtherAreaSup, p.MainSup, p.Viewer })
            {
                Assert.DoesNotContain((await MpApprovalsPageAsync(env, user, MpApprovals.TabPending)).Items, i => i.Sowing.Id == id);
                Assert.DoesNotContain((await SeedPageAsync(env, user)).Items, i => i.Sowing.Id == id);
            }
            var adminItem = Assert.Single((await MpApprovalsPageAsync(env, p.Admin, MpApprovals.TabPending)).Items, i => i.Sowing.Id == id);
            Assert.Equal($"Area Supervisor of {s.AreaName}", adminItem.Eligibility.Approver);
            Assert.Single((await SeedPageAsync(env, p.Admin, category: SowingApprovalRules.CategoryMotherPlantDirectSowing)).Items, i => i.Sowing.Id == id);
            // the page's own "waiting" list
            var after = env.Page<DirectSowingPage>(await env.LoginAsync(p.Sup2));
            await after.OnGetAsync();
            Assert.Contains(after.Pending, x => x.Id == id);
        }

        // =====================================================================================================
        // 4, 9: the Area Supervisor (here: the recorder) approves -- exactly once, even with concurrent clicks
        [SkippableFact]
        public async Task MpDirectSowing_ApprovedByItsAreaSupervisor_PostedExactlyOnce_EvenConcurrently_HistoryByApprover()
        {
            var env = await OpenMpdAsync();
            var p = await PeopleAsync(env);
            var v = await NewVarietyAsync(env);
            var id = await MpdBatchAsync(env, p.Sup1, p.Area, v, 240);   // 10 trays

            var approvals = env.Get<ReadyConfirmationRepository>();
            var clicks = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => approvals.ConfirmAsync(id, 9, "Germination failure", null, null, "mpd-test", p.Sup1, Any)));
            Assert.Single(clicks, c => c.Success);
            Assert.Equal(1, await env.ApprovalsOfAsync(id));
            var rs = await env.ReadyStockOfAsync(id);
            Assert.True(rs > 0);
            Assert.Equal(216m, await env.ScalarAsync<decimal>("SELECT Quantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs)));   // 9 x 24
            Assert.Equal(p.Area, await env.ScalarAsync<int>("SELECT AreaId FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs)));
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStock WHERE Id = @R AND PolyhouseId IS NOT NULL", ("@R", rs)));
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStockTransactions WHERE ReferenceType = N'ReadyConfirmation' AND ReferenceId = (SELECT Id FROM dbo.ReadyConfirmations WHERE SeedSowingId = @S)", ("@S", id)));
            var s = (await env.Get<SeedSowingRepository>().GetByIdAsync(id))!;
            Assert.Equal("Completed", s.Status);
            Assert.Equal(216m, s.ConfirmedReadyQuantity);
            Assert.Equal(24m, s.WastageQuantity);
            Assert.Equal(p.Sup1, await env.ScalarAsync<int>("SELECT ApprovedById FROM dbo.ReadyConfirmations WHERE SeedSowingId = @S", ("@S", id)));

            // a later approval of the closed batch is refused; nothing more is posted
            Assert.False((await approvals.ConfirmAsync(id, 1, null, null, null, "mpd-test", p.Sup2, Any)).Success);
            Assert.Equal(1, await env.ApprovalsOfAsync(id));

            // history: the approver's own list has it; the other supervisor's does not; the admin's has it with the approver
            Assert.Contains((await MpApprovalsPageAsync(env, p.Sup1)).History, h => h.SeedSowingId == id && h.Category == SowingApprovalRules.CategoryMotherPlantDirectSowing);
            Assert.DoesNotContain((await MpApprovalsPageAsync(env, p.Sup2)).History, h => h.SeedSowingId == id);
            Assert.Equal(p.Sup1, Assert.Single((await MpApprovalsPageAsync(env, p.Admin)).History, h => h.SeedSowingId == id).ApproverId);

            // downstream: the Area's Ready Stock page lists it as an approved sowing batch (the existing Direct Sowing Ready Stock)
            var readyPage = env.Page<PlantStockManager.Pages.Production.MotherPlant.AreaReadyStockModel>(await env.LoginAsync(p.Sup1));
            await readyPage.OnGetAsync();
            Assert.True(Assert.Single(readyPage.Trays, b => b.Id == rs).IsSowingBatch);

            // reversing the approval: only the user who made it (no assigned supervisor)
            var confirmation = await env.ScalarAsync<int>("SELECT Id FROM dbo.ReadyConfirmations WHERE SeedSowingId = @S", ("@S", id));
            Assert.False((await approvals.CancelAsync(confirmation, "mpd-test", p.Sup2)).Success);
            Assert.True((await approvals.CancelAsync(confirmation, "mpd-test", p.Sup1)).Success);
        }

        // =====================================================================================================
        // 5: another Area's supervisor and a Main Office supervisor cannot approve (page, repository, database); the admin can
        [SkippableFact]
        public async Task MpDirectSowing_OtherAreaOrMainSupervisor_CannotApprove_PageRepositoryAndDatabase_AdminCan()
        {
            var env = await OpenMpdAsync();
            var p = await PeopleAsync(env);
            var v = await NewVarietyAsync(env);
            var id = await MpdBatchAsync(env, p.Sup1, p.Area, v, 48);   // 2 trays
            var approvals = env.Get<ReadyConfirmationRepository>();

            foreach (var user in new[] { p.OtherAreaSup, p.MainSup })
            {
                var (ok, msg, _) = await approvals.ConfirmAsync(id, 2, null, null, null, "mpd-test", user, Any);
                Assert.False(ok);
                Assert.Contains("Area Supervisor", msg);
                // the Confirm page refuses it on GET (it is not even in their queue)
                var page = env.Page<ConfirmPage>(await env.LoginAsync(user));
                Assert.IsType<RedirectToPageResult>(await page.OnGetAsync(id));
            }
            Assert.Equal(0, await env.ApprovalsOfAsync(id));

            // the database refuses it too, whatever the application does (TR_ReadyConfirmations_AssignedSupervisor)
            var ex = await Assert.ThrowsAsync<SqlException>(() => env.ScalarAsync<int>(@"
INSERT INTO dbo.ReadyConfirmations (ConfirmationCode, SeedSowingId, ReadyStockId, ActualTrayQuantity, ConfirmedQuantity, WastageQuantity, ApprovedById,
 ConfirmationDate, Status, CreatedDate, CreatedBy)
SELECT N'ZZMPD-' + LEFT(CONVERT(NVARCHAR(36), NEWID()), 8), @S, (SELECT TOP 1 Id FROM dbo.ReadyStock), 2, 48, 0, @U, SYSUTCDATETIME(), N'Confirmed', SYSUTCDATETIME(), N'mpd-test';   -- 2 trays x 24 = the whole batch
SELECT 1", ("@S", id), ("@U", p.OtherAreaSup)));
            Assert.Contains("Mother Plant Area", ex.Message);
            // ... and the workflow flag cannot be switched off to slip it into the Seed Production rules
            var flip = await Assert.ThrowsAsync<SqlException>(() => env.ScalarAsync<int>("UPDATE dbo.SeedSowings SET IsMotherPlantDirectSowing = 0 WHERE Id = @S; SELECT 1", ("@S", id)));
            Assert.Contains("cannot be changed", flip.Message);

            // the System Administrator may approve it (the established Mother Plant Area approval policy)
            Assert.True((await approvals.ConfirmAsync(id, 2, null, null, null, "mpd-test", p.Admin, Any)).Success);
            Assert.Equal(1, await env.ApprovalsOfAsync(id));
        }

        // =====================================================================================================
        // 6: unauthorized Areas, forged Area / Polyhouse / supervisor / user values are refused -- nothing recorded or deducted
        [SkippableFact]
        public async Task MpDirectSowing_ForgedOrUnauthorizedValues_AreRefused_NothingRecordedOrDeducted()
        {
            var env = await OpenMpdAsync();
            var p = await PeopleAsync(env);
            var v = await NewVarietyAsync(env);
            var otherArea = await MotherPlantAreaOfAsync(env, p.OtherAreaSup);
            await AddAreaTraysAsync(env, p.Area, 10);
            await AddAreaTraysAsync(env, otherArea, 10);
            var lotBefore = await LotAvailableAsync(env, v.SeedStockId);
            var traysBefore = (await AreaTraysAsync(env, p.Area), await AreaTraysAsync(env, otherArea));

            // page: another supervisor's Area, a Main Office Area, forged hidden fields, a future date
            var cases = new (int Area, Dictionary<string, string>? Extra, DateTime? Date, string Expect)[]
            {
                (otherArea, null, null, DirectSowingRules.MotherPlantDirectNotAuthorizedMessage),
                (MainArea, null, null, DirectSowingRules.MotherPlantDirectNotAuthorizedMessage),
                (p.Area, new() { ["PolyhouseId"] = MainPolyhouse.ToString() }, null, DirectSowingPage.ForgedFieldMessage),
                (p.Area, new() { ["SeedSowing.SupervisorId"] = p.Sup2.ToString() }, null, DirectSowingPage.ForgedFieldMessage),
                (p.Area, new() { ["CreatedById"] = p.Sup2.ToString() }, null, DirectSowingPage.ForgedFieldMessage),
                (p.Area, null, AppClock.Today.AddDays(1), "cannot be in the future"),
            };
            foreach (var (area, extra, date, expect) in cases)
            {
                var token = Guid.NewGuid();
                var (result, page) = await SowViaPageAsync(env, p.Sup1, area, v, 48, token, extra, date);
                Assert.IsType<Microsoft.AspNetCore.Mvc.RazorPages.PageResult>(result);
                Assert.Contains(expect, Errors(page));
                Assert.Equal(0, await SowingOfTokenAsync(env, token));
            }

            // repository (a caller that skipped the page's checks): every forged value is refused under the transaction
            var repo = env.Get<SeedSowingRepository>();
            SeedSowing Entry(int area) => new()
            {
                AreaId = area, SpeciesId = v.SpeciesId, SourceSeedStockId = v.SeedStockId, SeedQuantity = 48, CavityType = MpdCavity,
                SowingDate = AppClock.Today, CreatedBy = "mpd-test", CreatedById = p.Sup1, SubmissionToken = Guid.NewGuid()
            };
            var withPolyhouse = Entry(p.Area); withPolyhouse.PolyhouseId = MainPolyhouse;
            var withSupervisor = Entry(p.Area); withSupervisor.SupervisorId = p.Sup2;
            var withMaterial = Entry(p.Area); withMaterial.GrowingMaterialTypeId = env.Cocopeat;
            foreach (var (entry, canAccess, expect) in new (SeedSowing, Func<int, bool>, string)[]
            {
                (withPolyhouse, Any, DirectSowingRules.MotherPlantDirectNoPolyhouseMessage),
                (withSupervisor, Any, DirectSowingRules.MotherPlantDirectNoSupervisorMessage),
                (withMaterial, Any, DirectSowingRules.MotherPlantDirectNoMaterialMessage),
                (Entry(MainArea), Any, DirectSowingRules.MotherPlantDirectNotMotherPlantAreaMessage),          // a Main Office Area
                (Entry(otherArea), a => a == p.Area, DirectSowingRules.MotherPlantDirectNotAuthorizedMessage), // not this user's Area
                (Entry(int.MaxValue), Any, DirectSowingRules.MotherPlantDirectAreaNotFoundMessage),
            })
            {
                var (ok, msg, _) = await repo.InsertMotherPlantDirectAsync(entry, p.Sup1, canAccess);
                Assert.False(ok);
                Assert.Equal(expect, msg);
            }

            Assert.Equal(lotBefore, await LotAvailableAsync(env, v.SeedStockId));
            Assert.Equal(traysBefore, (await AreaTraysAsync(env, p.Area), await AreaTraysAsync(env, otherArea)));
        }

        // =====================================================================================================
        // 9 + stock: a repeated / concurrent submit of the same form creates ONE batch and ONE deduction; short trays refused
        [SkippableFact]
        public async Task MpDirectSowing_DuplicateAndConcurrentSubmits_OneBatchOneDeduction_ShortTraysRefused()
        {
            var env = await OpenMpdAsync();
            var p = await PeopleAsync(env);
            var v = await NewVarietyAsync(env);
            await AddAreaTraysAsync(env, p.Area, 10);
            var lotBefore = await LotAvailableAsync(env, v.SeedStockId);
            var traysBefore = await AreaTraysAsync(env, p.Area);

            var repo = env.Get<SeedSowingRepository>();
            var token = Guid.NewGuid();
            SeedSowing Same() => new()
            {
                AreaId = p.Area, SpeciesId = v.SpeciesId, SourceSeedStockId = v.SeedStockId, SeedQuantity = 72, CavityType = MpdCavity,
                SowingDate = AppClock.Today, CreatedBy = "mpd-test", CreatedById = p.Sup1, SubmissionToken = token
            };
            var entries = Enumerable.Range(0, 3).Select(_ => Same()).ToList();
            var results = await Task.WhenAll(entries.Select(e => repo.InsertMotherPlantDirectAsync(e, p.Sup1, Any)));
            Assert.All(results, r => Assert.True(r.Success, r.Message));
            Assert.Single(entries, e => !e.IsDuplicateSubmission);
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings WHERE SubmissionToken = @T", ("@T", token)));
            Assert.Equal(lotBefore - 72, await LotAvailableAsync(env, v.SeedStockId));
            Assert.Equal(traysBefore - 3, await AreaTraysAsync(env, p.Area));

            // the page's re-post of the same form: "already saved", nothing twice
            var (result, page) = await SowViaPageAsync(env, p.Sup1, p.Area, v, 72, token);
            Assert.True(result is RedirectToPageResult, Errors(page));
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings WHERE SubmissionToken = @T", ("@T", token)));
            Assert.Equal(traysBefore - 3, await AreaTraysAsync(env, p.Area));

            // more trays than the Area holds -> refused, nothing deducted
            var lotMid = await LotAvailableAsync(env, v.SeedStockId);
            var big = Same(); big.SubmissionToken = Guid.NewGuid(); big.SeedQuantity = 24 * (traysBefore - 3 + 1);
            var (bigOk, _, _) = await repo.InsertMotherPlantDirectAsync(big, p.Sup1, Any);
            Assert.False(bigOk);
            Assert.Equal(lotMid, await LotAvailableAsync(env, v.SeedStockId));
            Assert.Equal(traysBefore - 3, await AreaTraysAsync(env, p.Area));
        }

        // =====================================================================================================
        // 11: Seed Production Direct Sowing is unchanged -- Main Area only, assigned supervisor approves, never a Mother Plant user
        [SkippableFact]
        public async Task SeedProductionDirectSowing_Unchanged_NotFlagged_AssignedSupervisorOnly()
        {
            var env = await OpenMpdAsync();
            var p = await PeopleAsync(env);
            var v = await NewVarietyAsync(env);
            Assert.True((await env.Get<TrayStockRepository>().AddStockAsync(MainArea, MainPolyhouse, MpdCavity, 10, DateTime.UtcNow.AddDays(-5),
                Random.Shared.Next(1, int.MaxValue), SowingSupervisor, "mpd-test", "mpd", Any)).Success);
            Assert.True((await env.Get<MaterialStockRepository>().AdjustAsync(MainArea, MainPolyhouse, env.Cocopeat, 1, 50m, "mpd setup", Guid.NewGuid(), Recorder, Any,
                LedgerDates.Instant(AppClock.Today.AddDays(-5)).TransactionUtc)).Success);
            var sowing = new SeedSowing
            {
                SourceSeedStockId = v.SeedStockId, SeedQuantity = 48, CavityType = MpdCavity, SowingDate = AppClock.Today,
                PolyhouseId = MainPolyhouse, SupervisorId = SowingSupervisor, CreatedBy = "mpd-test", CreatedById = Recorder, GrowingMaterialTypeId = env.Cocopeat
            };
            var (ok, msg, id) = await env.Get<SeedSowingRepository>().InsertAsync(sowing, Recorder, Any);
            Assert.True(ok, msg);
            var s = (await env.Get<SeedSowingRepository>().GetByIdAsync(id))!;
            Assert.False(s.IsMotherPlantDirectSowing);
            Assert.Equal(SowingSupervisor, s.SupervisorId);
            Assert.Equal(SowingApprovalRules.CategorySowing, SowingApprovalRules.CategoryOfBatch(s));
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.MaterialTransactions WHERE ReferenceType = N'SeedSowing' AND ReferenceId = @S", ("@S", id)));

            // a Mother Plant Area Supervisor (or another approver) cannot approve it; its assigned supervisor can
            var approvals = env.Get<ReadyConfirmationRepository>();
            Assert.False((await approvals.ConfirmAsync(id, 2, null, null, null, "mpd-test", p.Sup1, Any)).Success);
            Assert.True((await approvals.ConfirmAsync(id, 2, null, null, null, "mpd-test", SowingSupervisor, Any)).Success);

            // a Mother Plant Area cannot be used through Seed Production Direct Sowing either (Polyhouse of a Main Area only)
            Assert.Equal(DirectSowingRules.DirectSowingNotMainAreaMessage,
                DirectSowingRules.ResolveDirectSowingArea(null, 1, true, p.Area, MotherPlantAreaSchema.MotherPlantAreaType, true).Error);
        }
    }
}
