using Microsoft.AspNetCore.Mvc;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using TransplantPage = PlantStockManager.Pages.Production.BedSowing.TransplantModel;

namespace PlantStockManager.Tests
{
    // 2026-10-19: Bed Transplanting in three steps on one page -- 1) choose a bed, 2) transplant details of THAT bed, 3) Review
    // (server-side plan, nothing written), then Confirm (the unchanged save, BedSowingRepository.TransplantAsync). SCRATCH COPY
    // only; the Bed Sowing Transplanter of the bed's Area is a REAL user (principal from dbo.UserRoles via the claims factory).
    public partial class WorkflowCorrections1019E2ETests
    {
        private const string BedCavity = "24 Cavity";

        private sealed record BedSetup(int AreaId, int PolyhouseId, int Transplanter, int Admin, BedSowing Bed, int ForeignPolyhouse);

        private static async Task<BedSetup> BedForTransplantAsync(Env env, decimal seedlings, decimal trays = 40)
        {
            var polyhouse = await env.ScalarAsync<int>("SELECT TOP 1 PolyhouseId FROM dbo.BedSowingLocations WHERE IsActive = 1 ORDER BY PolyhouseId");
            Skip.If(polyhouse == 0, "No Bed Sowing location in this copy.");
            var area = await env.ScalarAsync<int>("SELECT AreaId FROM dbo.BedSowingLocations WHERE PolyhouseId = @P", ("@P", polyhouse));
            var transplanter = await env.ScalarAsync<int>(@"SELECT TOP 1 ur.UserId FROM dbo.UserRoles ur JOIN dbo.Roles r ON r.Id = ur.RoleId
JOIN dbo.IMSUsers u ON u.Id = ur.UserId AND u.IsActive = 1
WHERE COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = N'Bed Sowing Transplanter' AND ur.AreaId = @A ORDER BY ur.UserId", ("@A", area));
            var admin = await env.ScalarAsync<int>(@"SELECT TOP 1 ur.UserId FROM dbo.UserRoles ur JOIN dbo.Roles r ON r.Id = ur.RoleId JOIN dbo.IMSUsers u ON u.Id = ur.UserId AND u.IsActive = 1
WHERE r.Name = N'System Administrator' ORDER BY ur.UserId");
            // a Polyhouse that is NOT a transplant target of this bed (another Area's, or a non-bed Polyhouse)
            var foreign = await env.ScalarAsync<int>(@"SELECT TOP 1 p.Id FROM dbo.Polyhouses p
WHERE p.Id NOT IN (SELECT PolyhouseId FROM dbo.BedSowingLocations WHERE AreaId = @A AND IsActive = 1) ORDER BY p.Id", ("@A", area));
            Skip.If(transplanter == 0 || admin == 0 || foreign == 0, "The copy lacks a Bed Sowing Transplanter, an administrator or a second Polyhouse.");

            Assert.True((await env.Get<TrayStockRepository>().AddStockAsync(area, polyhouse, BedCavity, trays, DateTime.UtcNow.AddDays(-20),
                Random.Shared.Next(1, int.MaxValue), SowingSupervisor, "wf1019-bed", "bed steps trays", Any)).Success);
            Assert.True((await env.Get<MaterialStockRepository>().AdjustAsync(area, polyhouse, env.Cocopeat, 1, trays * 2m, "bed steps cocopeat", Guid.NewGuid(),
                Recorder, Any, LedgerDates.Instant(AppClock.Today.AddDays(-20)).TransactionUtc)).Success);
            var v = await NewVarietyAsync(env, area);
            var bed = new BedSowing { SpeciesId = v.SpeciesId, SeedStockId = v.SeedStockId, PolyhouseId = polyhouse, Quantity = seedlings,
                SowingDate = AppClock.Today.AddDays(-12), Remarks = "bed steps", SubmissionToken = Guid.NewGuid() };
            var (ok, msg, _) = await env.Get<BedSowingRepository>().CreateAsync(bed, new BedSowingRepository.Actor(admin, "wf1019-bed", true), Any);
            Assert.True(ok, msg);
            return new BedSetup(area, polyhouse, transplanter, admin, bed, foreign);
        }

        private static BedTransplantRequest Entry(BedSetup s, decimal qty, Guid token, int? polyhouse = null, string cavity = BedCavity, int? material = -1)
            => new()
            {
                BedSowingId = s.Bed.Id, TargetPolyhouseId = polyhouse ?? s.PolyhouseId, CavityType = cavity, Quantity = qty,
                TransplantDate = AppClock.Today.AddDays(-2), Remarks = "bed steps", SubmissionToken = token,
                GrowingMaterialTypeId = material == -1 ? null : material
            };

        private static async Task<TransplantPage> StepAsync(Env env, int user, BedTransplantRequest entry, Func<TransplantPage, Task<IActionResult>> handler)
        {
            var page = env.Page<TransplantPage>(await env.LoginAsync(user));
            page.Entry = entry;
            page.BedId = entry.BedSowingId;
            await handler(page);
            return page;
        }

        private static Task<int> BedOpsAsync(Env env, int bed, string type)
            => env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.BedSowingOperations WHERE BedSowingId = @B AND OperationType = @T AND Status = N'Active'", ("@B", bed), ("@T", type));

        private static Task<decimal> BedTraysAsync(Env env, BedSetup s)
            => env.ScalarAsync<decimal>("SELECT ISNULL(SUM(PhysicalQuantity), 0) FROM dbo.TrayStock WHERE AreaId = @A AND PolyhouseId = @P AND TraySize = @C AND IsActive = 1",
                ("@A", s.AreaId), ("@P", s.PolyhouseId), ("@C", BedCavity));

        [SkippableFact]
        public async Task BedTransplantSteps_DetailsShowTheBed_ReviewPlansOnTheServerAndWritesNothing_ConfirmRecordsOnce()
        {
            var env = await OpenAsync(BedSowingSchema.ProbeSql);
            var s = await BedForTransplantAsync(env, 1000);
            var t = s.Transplanter;
            var withMaterial = env.Cocopeat;

            // step 1: the list, no bed chosen
            var list = env.Page<TransplantPage>(await env.LoginAsync(t));
            await list.OnGetAsync();
            Assert.Null(list.Selected);
            Assert.Contains(list.Beds, b => b.Id == s.Bed.Id);

            // step 2: the chosen bed, its targets, the whole remainder as the default quantity, a fresh token
            var details = env.Page<TransplantPage>(await env.LoginAsync(t));
            details.BedId = s.Bed.Id;
            await details.OnGetAsync();
            Assert.Equal(s.Bed.Id, details.Selected?.Id);
            Assert.True(details.CanOperate);
            Assert.False(details.Reviewing);
            Assert.Contains(details.Targets, x => x.PolyhouseId == s.PolyhouseId);
            Assert.Equal((1000m, s.Bed.Id), (details.Entry.Quantity, details.Entry.BedSowingId));
            Assert.NotEqual(Guid.Empty, details.Entry.SubmissionToken!.Value);

            // step 3 refusals -- each shown on the details step, nothing written
            async Task Refused(BedTransplantRequest e, string expect)
            {
                var page = await StepAsync(env, t, e, p => p.OnPostReviewAsync());
                Assert.False(page.Reviewing);
                Assert.Equal(s.Bed.Id, page.Selected?.Id);
                Assert.Contains(expect, page.Error);
            }
            var token = Guid.NewGuid();
            await Refused(Entry(s, 240, token, polyhouse: s.ForeignPolyhouse, material: withMaterial), "Transplant into the Bed Sowing Polyhouse of this bed's Area.");
            await Refused(Entry(s, 240, token, cavity: "13 Cavity", material: withMaterial), "Cavity must be one of");
            await Refused(Entry(s, 250, token, material: withMaterial), "must be a multiple of 24");
            await Refused(Entry(s, 1200, token, material: withMaterial), "seedlings remain in this bed");
            await Refused(Entry(s, 240, token), MaterialStockRules.MaterialRequiredMessage);
            var stocked = (int)await BedTraysAsync(env, s);
            if ((stocked + 1) * 24 <= 1000)                                                        // one tray more than the pool holds
                await Refused(Entry(s, 24 * (stocked + 1), token, material: withMaterial), "empty 24 Cavity trays");
            Assert.Equal(0, await BedOpsAsync(env, s.Bed.Id, BedSowingOperation.TypeTransplant) + await BedOpsAsync(env, s.Bed.Id, BedSowingOperation.TypeLoss));

            // step 3: the server's plan -- 240 into 10 trays, 760 waste, Ready Date = transplant date + Ready Days; nothing saved
            var trays0 = await BedTraysAsync(env, s);
            var review = await StepAsync(env, t, Entry(s, 240, token, material: withMaterial), p => p.OnPostReviewAsync());
            Assert.True(review.Reviewing, review.Error);
            Assert.Null(review.Error);
            Assert.Equal((10, 240m, 760m), (review.ReviewPlan!.Trays, review.ReviewPlan.Transplanted, review.ReviewPlan.Waste));
            Assert.Equal(s.PolyhouseId, review.ReviewTarget!.PolyhouseId);
            Assert.Equal(AppClock.Today.AddDays(-2 + ReadyDays), review.ReviewReadyDate);
            Assert.Equal(withMaterial, review.ReviewMaterial!.MaterialTypeId);
            Assert.Equal(trays0, review.ReviewTraysAvailable);
            Assert.Equal(token, review.Entry.SubmissionToken);
            Assert.Equal(1000m, (await env.Get<BedSowingRepository>().GetAsync(s.Bed.Id))!.RemainingQuantity);
            Assert.Equal(trays0, await BedTraysAsync(env, s));

            // Back: the details again with the entered values and the SAME token
            var back = await StepAsync(env, t, Entry(s, 240, token, material: withMaterial), p => p.OnPostEditAsync());
            Assert.False(back.Reviewing);
            Assert.Equal((240m, token, s.Bed.Id), (back.Entry.Quantity, back.Entry.SubmissionToken!.Value, back.Selected?.Id));

            // Confirm: recorded once with the existing rules; the same confirmation again records nothing more
            var confirm = await StepAsync(env, t, Entry(s, 240, token, material: withMaterial), p => p.OnPostTransplantAsync());
            Assert.Null(confirm.Error);
            Assert.Contains("240 seedlings were transplanted into 10 24 Cavity trays, and the remaining 760 seedlings were recorded as waste.", confirm.TempData["Success"] as string);
            var again = await StepAsync(env, t, Entry(s, 240, token, material: withMaterial), p => p.OnPostTransplantAsync());
            Assert.Contains("already saved", again.TempData["Success"] as string);
            var bed = (await env.Get<BedSowingRepository>().GetAsync(s.Bed.Id))!;
            Assert.Equal((240m, 760m, 0m, BedSowing.StatusCompleted), (bed.TransplantedQuantity, bed.LostQuantity, bed.RemainingQuantity, bed.Status));
            Assert.Equal((1, 1), (await BedOpsAsync(env, s.Bed.Id, BedSowingOperation.TypeTransplant), await BedOpsAsync(env, s.Bed.Id, BedSowingOperation.TypeLoss)));
            Assert.Equal(trays0 - 10, await BedTraysAsync(env, s));
            // the tray batch waits for the (unchanged) approval
            Assert.Equal("Sown", await env.ScalarAsync<string>("SELECT TOP 1 Status FROM dbo.SeedSowings WHERE BedSowingId = @B", ("@B", s.Bed.Id)));

            // the closed bed is no longer offered: a review of it goes back to the list
            var closed = await StepAsync(env, t, Entry(s, 240, Guid.NewGuid(), material: withMaterial), p => p.OnPostReviewAsync());
            Assert.False(closed.Reviewing);
            Assert.Null(closed.Selected);
            Assert.Equal("That bed is not waiting for transplanting in your Areas.", closed.Error);
        }

        [SkippableFact]
        public async Task BedTransplantSteps_StaleReviewIsRevalidatedOnConfirm_WasteOnlyReview_OnlyTheTransplanterOperates()
        {
            var env = await OpenAsync(BedSowingSchema.ProbeSql);
            var s = await BedForTransplantAsync(env, 480);
            var t = s.Transplanter;

            // waste-only review: no tray batch, all 480 as waste, no target needed; a quantity with it is refused
            var waste = Entry(s, 0, Guid.NewGuid(), polyhouse: 0, cavity: "");
            (waste.CloseAsWaste, waste.WasteReason) = (true, "wf1019 seedlings dead");
            var wasteReview = await StepAsync(env, t, waste, p => p.OnPostReviewAsync());
            Assert.True(wasteReview.Reviewing, wasteReview.Error);
            Assert.Equal((0, 480m), (wasteReview.ReviewPlan!.Trays, wasteReview.ReviewPlan.Waste));
            Assert.Null(wasteReview.ReviewTarget);
            waste.Quantity = 24;
            Assert.Contains("leave the Quantity to Transplant at 0", (await StepAsync(env, t, waste, p => p.OnPostReviewAsync())).Error);

            // an administrator sees the bed but never operates: no review, and a forged confirm records nothing
            var admin = await StepAsync(env, s.Admin, Entry(s, 240, Guid.NewGuid(), material: env.Cocopeat), p => p.OnPostReviewAsync());
            Assert.False(admin.CanOperate);
            Assert.False(admin.Reviewing);
            var adminConfirm = await StepAsync(env, s.Admin, Entry(s, 240, Guid.NewGuid(), material: env.Cocopeat), p => p.OnPostTransplantAsync());
            Assert.NotNull(adminConfirm.Error);
            // a user without BedSowing.Transplant cannot open the page at all
            var viewer = (await PeopleAsync(env)).Viewer;
            Assert.False(await env.Get<FeatureAccessService>().CanAccessPageAsync(await env.LoginAsync(viewer), "/Production/BedSowing/Transplant"));

            // two reviews of the whole bed; the first is confirmed, the second (stale) confirm is re-checked and refused
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            Assert.True((await StepAsync(env, t, Entry(s, 480, first, material: env.Cocopeat), p => p.OnPostReviewAsync())).Reviewing);
            Assert.True((await StepAsync(env, t, Entry(s, 240, second, material: env.Cocopeat), p => p.OnPostReviewAsync())).Reviewing);
            var confirms = await Task.WhenAll(
                StepAsync(env, t, Entry(s, 480, first, material: env.Cocopeat), p => p.OnPostTransplantAsync()),
                StepAsync(env, t, Entry(s, 240, second, material: env.Cocopeat), p => p.OnPostTransplantAsync()));
            Assert.Equal(1, confirms.Count(c => c.Error == null));
            var refused = Assert.Single(confirms, c => c.Error != null);
            Assert.Contains("nothing is left to transplant", refused.Error);
            var bed = (await env.Get<BedSowingRepository>().GetAsync(s.Bed.Id))!;
            Assert.Equal(480m, bed.TransplantedQuantity + bed.LostQuantity);
            Assert.Equal(0m, bed.RemainingQuantity);
            Assert.Equal(1, await BedOpsAsync(env, s.Bed.Id, BedSowingOperation.TypeTransplant));
            Assert.True(BedSowingRules.IsConserved(bed.Quantity, bed.TransplantedQuantity, bed.LostQuantity, bed.RemainingQuantity));
        }
    }
}
