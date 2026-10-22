using Microsoft.AspNetCore.Mvc;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using ApprovalsIndex = PlantStockManager.Pages.Production.ReadyConfirmation.IndexModel;
using ConfirmPage = PlantStockManager.Pages.Production.ReadyConfirmation.ConfirmModel;
using TransplantPage = PlantStockManager.Pages.Production.BedSowing.TransplantModel;

namespace PlantStockManager.Tests
{
    // 2026-10-22: Variety / Sowing Date filters on Bed Transplanting (step 1, the beds waiting) and Sowing Approvals (Waiting for
    // approval). SCRATCH COPY only (same fixtures as the 1019 tests: REAL users, REAL page models and repositories). The filters
    // narrow the list inside the user's own scope; they never widen it, never change which bed is transplanted or which batch
    // is approved, and the transplant / approval rules are unchanged.
    public partial class WorkflowCorrections1019E2ETests
    {
        // ---- Bed Transplanting ----------------------------------------------------------------------------------------

        private static async Task<TransplantPage> BedListAsync(Env env, int user, int? variety, DateTime? sowingDate, int? bedId = null)
        {
            var page = env.Page<TransplantPage>(await env.LoginAsync(user));
            page.Variety = variety;
            page.SowingDate = sowingDate;
            page.BedId = bedId;
            await page.OnGetAsync();
            return page;
        }

        private static async Task<BedSowing> ExtraBedAsync(Env env, BedSetup s, int speciesId, int seedStockId, DateTime sowingDate, decimal qty)
        {
            var bed = new BedSowing { SpeciesId = speciesId, SeedStockId = seedStockId, PolyhouseId = s.PolyhouseId, Quantity = qty,
                SowingDate = sowingDate, Remarks = "list filters", SubmissionToken = Guid.NewGuid() };
            var (ok, msg, _) = await env.Get<BedSowingRepository>().CreateAsync(bed, new BedSowingRepository.Actor(s.Admin, "wf1022-filter", true), Any);
            Assert.True(ok, msg);
            return bed;
        }

        // An active user who is neither a System Administrator nor a Bed Sowing Transplanter of the area.
        private static async Task<int> NotTransplanterOfAsync(Env env, int area)
        {
            var repo = env.Get<BedSowingRepository>();
            foreach (var id in await env.IdsAsync("SELECT TOP 60 Id FROM dbo.IMSUsers WHERE IsActive = 1 ORDER BY Id"))
            {
                var principal = await env.LoginAsync(id);
                if (principal.IsFullAccess())
                    continue;
                if (!(await repo.TransplantAreaIdsAsync(id, false))!.Contains(area))
                    return id;
            }
            Skip.If(true, "No active user outside the bed's Transplanter Area in this copy.");
            return 0;
        }

        private sealed record BedFilterSetup(BedSetup S, BedSowing A1, BedSowing A2, BedSowing B1, int VarietyA, int VarietyB, DateTime D1, DateTime D2);

        private static async Task<BedFilterSetup> BedsForFiltersAsync(Env env)
        {
            var s = await BedForTransplantAsync(env, 1000);                      // variety A, sown today-12 (trays + cocopeat stocked)
            var d1 = AppClock.Today.AddDays(-12);
            var d2 = AppClock.Today.AddDays(-14);
            var a2 = await ExtraBedAsync(env, s, s.Bed.SpeciesId, s.Bed.SeedStockId, d2, 500);
            var vb = await NewVarietyAsync(env, s.AreaId);
            var b1 = await ExtraBedAsync(env, s, vb.SpeciesId, vb.SeedStockId, d1, 480);
            return new BedFilterSetup(s, s.Bed, a2, b1, s.Bed.SpeciesId, vb.SpeciesId, d1, d2);
        }

        private static HashSet<int> Ours(BedFilterSetup f, IEnumerable<BedSowing> beds)
            => beds.Select(b => b.Id).Where(id => id == f.A1.Id || id == f.A2.Id || id == f.B1.Id).ToHashSet();

        [SkippableFact]
        public async Task BedTransplantFilters_Variety_SowingDate_Both_Clear_AppliedInTheListQuery()
        {
            var env = await OpenAsync(BedSowingSchema.ProbeSql);
            var f = await BedsForFiltersAsync(env);
            var t = f.S.Transplanter;

            // unfiltered: all three beds, and both varieties offered
            var all = await BedListAsync(env, t, null, null);
            Assert.Equal(new[] { f.A1.Id, f.A2.Id, f.B1.Id }.ToHashSet(), Ours(f, all.Beds));
            Assert.Contains(all.VarietyOptions, o => o.Id == f.VarietyA);
            Assert.Contains(all.VarietyOptions, o => o.Id == f.VarietyB);
            Assert.False(all.Filter.IsActive);

            // 1: Variety only
            var byVariety = await BedListAsync(env, t, f.VarietyA, null);
            Assert.True(byVariety.Filter.IsActive);
            Assert.All(byVariety.Beds, b => Assert.Equal(f.VarietyA, b.SpeciesId));
            Assert.Equal(new[] { f.A1.Id, f.A2.Id }.ToHashSet(), Ours(f, byVariety.Beds));

            // 2: Sowing Date only (a time on the posted value is ignored -- the DATE column is the calendar day)
            foreach (var date in new[] { f.D1, f.D1.AddHours(18).AddMinutes(30) })
            {
                var byDate = await BedListAsync(env, t, null, date);
                Assert.Equal(f.D1, byDate.Filter.SowingDate);
                Assert.All(byDate.Beds, b => Assert.Equal(f.D1, b.SowingDate.Date));
                Assert.Equal(new[] { f.A1.Id, f.B1.Id }.ToHashSet(), Ours(f, byDate.Beds));
            }
            var otherDay = await BedListAsync(env, t, null, f.D2);
            Assert.Equal(new[] { f.A2.Id }.ToHashSet(), Ours(f, otherDay.Beds));

            // 3: both (AND)
            var both = await BedListAsync(env, t, f.VarietyA, f.D1);
            Assert.All(both.Beds, b => Assert.True(b.SpeciesId == f.VarietyA && b.SowingDate.Date == f.D1));
            Assert.Equal(new[] { f.A1.Id }.ToHashSet(), Ours(f, both.Beds));
            var none = await BedListAsync(env, t, f.VarietyB, f.D2);              // no bed matches both
            Assert.Empty(Ours(f, none.Beds));

            // 4: cleared -- the unfiltered list again
            var cleared = await BedListAsync(env, t, null, null);
            Assert.Equal(all.Beds.Select(b => b.Id), cleared.Beds.Select(b => b.Id));
        }

        [SkippableFact]
        public async Task BedTransplantFilters_NeverWidenTheAreaScope_ForgedVarietyIsDropped()
        {
            var env = await OpenAsync(BedSowingSchema.ProbeSql);
            var f = await BedsForFiltersAsync(env);
            var outsider = await NotTransplanterOfAsync(env, f.S.AreaId);

            // 6: the filters pointed straight at another Area's beds -- nothing of that Area is listed or offered
            var page = await BedListAsync(env, outsider, f.VarietyA, f.D1);
            Assert.Empty(Ours(f, page.Beds));
            Assert.DoesNotContain(page.VarietyOptions, o => o.Id == f.VarietyA || o.Id == f.VarietyB);
            Assert.Null(page.Variety);                                          // dropped, not applied
            Assert.Equal(SowingListFilter.VarietyClearedMessage, page.FilterNote);
            Assert.Empty(Ours(f, (await BedListAsync(env, outsider, null, f.D1)).Beds));
            // ... and choosing that bed by id still finds nothing (unchanged)
            Assert.Null((await BedListAsync(env, outsider, f.VarietyA, f.D1, f.A1.Id)).Selected);

            // a variety id that exists nowhere: dropped; the list is the user's normal list
            var t = f.S.Transplanter;
            var forged = await BedListAsync(env, t, int.MaxValue, null);
            Assert.Null(forged.Filter.SpeciesId);
            Assert.Equal(new[] { f.A1.Id, f.A2.Id, f.B1.Id }.ToHashSet(), Ours(f, forged.Beds));
        }

        [SkippableFact]
        public async Task BedTransplantFilters_KeptThroughTheSteps_ChosenBedNotChangedByThem_ValidationLockingAndConfirmUnchanged()
        {
            var env = await OpenAsync(BedSowingSchema.ProbeSql);
            var f = await BedsForFiltersAsync(env);
            var s = f.S;
            var t = s.Transplanter;

            // the chosen bed comes by its id, even when the filters would not list it
            var other = await BedListAsync(env, t, f.VarietyA, f.D1, f.B1.Id);
            Assert.Equal(f.B1.Id, other.Selected?.Id);
            Assert.Equal((f.VarietyA, f.D1), (other.Variety!.Value, other.SowingDate!.Value));

            async Task<TransplantPage> Step(BedTransplantRequest e, Func<TransplantPage, Task<IActionResult>> handler)
            {
                var page = env.Page<TransplantPage>(await env.LoginAsync(t));
                page.Entry = e;
                page.BedId = e.BedSowingId;
                page.Variety = f.VarietyA;
                page.SowingDate = f.D1;
                await handler(page);
                return page;
            }
            var material = env.Cocopeat;
            var token = Guid.NewGuid();

            // step 2 -> 3: validation unchanged (a refusal stays on the details), the filters kept
            var refused = await Step(Entry(s, 250, token, material: material), p => p.OnPostReviewAsync());
            Assert.False(refused.Reviewing);
            Assert.Contains("must be a multiple of 24", refused.Error);
            Assert.Equal((f.VarietyA, f.D1), (refused.Variety!.Value, refused.SowingDate!.Value));
            var review = await Step(Entry(s, 240, token, material: material), p => p.OnPostReviewAsync());
            Assert.True(review.Reviewing, review.Error);
            Assert.Equal(f.A1.Id, review.Selected?.Id);
            Assert.Equal(1000m, (await env.Get<BedSowingRepository>().GetAsync(f.A1.Id))!.RemainingQuantity);   // nothing written

            // Confirm: recorded once; back to the SAME filtered list
            var confirm = env.Page<TransplantPage>(await env.LoginAsync(t));
            confirm.Entry = Entry(s, 240, token, material: material);
            confirm.BedId = f.A1.Id;
            confirm.Variety = f.VarietyA;
            confirm.SowingDate = f.D1;
            var result = Assert.IsType<RedirectToPageResult>(await confirm.OnPostTransplantAsync());
            Assert.Null(confirm.Error);
            Assert.Equal(f.A1.Id, result.RouteValues!["bedId"]);
            Assert.Equal(f.VarietyA, result.RouteValues["variety"]);
            Assert.Equal(f.D1.ToString("yyyy-MM-dd"), result.RouteValues["sowingDate"]);
            var again = await Step(Entry(s, 240, token, material: material), p => p.OnPostTransplantAsync());
            Assert.Contains("already saved", again.TempData["Success"] as string);
            Assert.Equal(1, await BedOpsAsync(env, f.A1.Id, BedSowingOperation.TypeTransplant));
            var a1 = (await env.Get<BedSowingRepository>().GetAsync(f.A1.Id))!;
            Assert.Equal((240m, 760m, BedSowing.StatusCompleted), (a1.TransplantedQuantity, a1.LostQuantity, a1.Status));
            // the other beds are untouched, and the filtered list now holds only what is still open
            Assert.Equal(500m, (await env.Get<BedSowingRepository>().GetAsync(f.A2.Id))!.RemainingQuantity);
            Assert.Equal(480m, (await env.Get<BedSowingRepository>().GetAsync(f.B1.Id))!.RemainingQuantity);
            Assert.Equal(new[] { f.A2.Id }.ToHashSet(), Ours(f, (await BedListAsync(env, t, f.VarietyA, null)).Beds));
        }

        // ---- Sowing Approvals -----------------------------------------------------------------------------------------

        // A Main Office Direct Sowing still waiting for approval (assigned supervisor = SowingSupervisor). Ready Date in the past.
        private static async Task<int> PendingMainSowingAsync(Env env, Variety v, DateTime sowingDate, int plants = 48)
        {
            Assert.True((await env.Get<TrayStockRepository>().AddStockAsync(MainArea, MainPolyhouse, "24 Cavity", plants / 24 + 2, DateTime.UtcNow.AddDays(-60),
                Random.Shared.Next(1, int.MaxValue), SowingSupervisor, "wf1022-filter", "filter setup", Any)).Success);
            Assert.True((await env.Get<MaterialStockRepository>().AdjustAsync(MainArea, MainPolyhouse, env.Cocopeat, 1, 50m, "filter setup", Guid.NewGuid(), Recorder, Any,
                LedgerDates.Instant(AppClock.Today.AddDays(-60)).TransactionUtc)).Success);
            var sowing = new SeedSowing
            {
                SourceSeedStockId = v.SeedStockId, SeedQuantity = plants, CavityType = "24 Cavity", SowingDate = sowingDate,
                PolyhouseId = MainPolyhouse, SupervisorId = SowingSupervisor, CreatedBy = "wf1022-filter", CreatedById = Recorder, GrowingMaterialTypeId = env.Cocopeat
            };
            var (ok, msg, id) = await env.Get<SeedSowingRepository>().InsertAsync(sowing, Recorder, Any);
            Assert.True(ok, msg);
            return id;
        }

        private static async Task<ApprovalsIndex> ApprovalsAsync(Env env, int user, int? variety, DateTime? sowingDate, string? category = null)
        {
            var page = env.Page<ApprovalsIndex>(await env.LoginAsync(user));
            page.Variety = variety;
            page.SowingDate = sowingDate;
            page.Category = category;
            await page.OnGetAsync();
            return page;
        }

        private sealed record SowingFilterSetup(int A1, int A2, int B1, int VarietyA, int VarietyB, DateTime D1, DateTime D2);

        // A1: variety A on D1 at 23:59:59.9999999 (the last instant of the day); A2: variety A at D2 23:59:59 (one second before
        // D1); B1: variety B on D1 at 00:00. SowingDate is DATETIME2 -- the times are set on the scratch copy only.
        private static async Task<SowingFilterSetup> SowingsForFiltersAsync(Env env)
        {
            var d1 = AppClock.Today.AddDays(-35);
            var d2 = d1.AddDays(-1);
            var va = await NewVarietyAsync(env);
            var vb = await NewVarietyAsync(env);
            var a1 = await PendingMainSowingAsync(env, va, d1);
            var a2 = await PendingMainSowingAsync(env, va, d2);
            var b1 = await PendingMainSowingAsync(env, vb, d1);
            // as text cast to DATETIME2(7) (a DateTime parameter would be sent as DATETIME and round 23:59:59.9999999 up to the next day)
            const string SetTime = "UPDATE dbo.SeedSowings SET SowingDate = CAST(@T AS DATETIME2(7)) WHERE Id = @S; SELECT @@ROWCOUNT";
            string Iso(DateTime d) => d.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(1, await env.ScalarAsync<int>(SetTime, ("@T", Iso(d1.AddDays(1).AddTicks(-1))), ("@S", a1)));
            Assert.Equal(1, await env.ScalarAsync<int>(SetTime, ("@T", Iso(d1.AddSeconds(-1))), ("@S", a2)));
            Assert.Equal(d1.AddDays(1).AddTicks(-1), await env.ScalarAsync<DateTime>("SELECT SowingDate FROM dbo.SeedSowings WHERE Id = @S", ("@S", a1)));
            return new SowingFilterSetup(a1, a2, b1, va.SpeciesId, vb.SpeciesId, d1, d2);
        }

        private static HashSet<int> Ours(SowingFilterSetup f, IEnumerable<SowingApprovalQueueRepository.Item> items)
            => items.Select(i => i.Sowing.Id).Where(id => id == f.A1 || id == f.A2 || id == f.B1).ToHashSet();

        [SkippableFact]
        public async Task SowingApprovalFilters_Variety_SowingDateWholeDay_Both_Clear_CategoryKept()
        {
            var env = await OpenAsync();
            var f = await SowingsForFiltersAsync(env);
            var sup = SowingSupervisor;

            var all = await ApprovalsAsync(env, sup, null, null);
            Assert.Equal(new[] { f.A1, f.A2, f.B1 }.ToHashSet(), Ours(f, all.Items));
            Assert.Contains(all.VarietyOptions, o => o.Id == f.VarietyA);
            Assert.Contains(all.VarietyOptions, o => o.Id == f.VarietyB);

            // 1: Variety
            var byVariety = await ApprovalsAsync(env, sup, f.VarietyA, null);
            Assert.All(byVariety.Items, i => Assert.Equal(f.VarietyA, i.Sowing.SpeciesId));
            Assert.Equal(new[] { f.A1, f.A2 }.ToHashSet(), Ours(f, byVariety.Items));

            // 2 + 5: Sowing Date = the whole calendar day: 00:00 and 23:59:59.9999999 are in, one second before midnight of the
            // previous day is not (also when the posted value carries a time)
            foreach (var date in new[] { f.D1, f.D1.AddHours(9) })
            {
                var byDate = await ApprovalsAsync(env, sup, null, date);
                Assert.All(byDate.Items, i => Assert.Equal(f.D1, i.Sowing.SowingDate.Date));
                Assert.Equal(new[] { f.A1, f.B1 }.ToHashSet(), Ours(f, byDate.Items));
            }
            Assert.Equal(new[] { f.A2 }.ToHashSet(), Ours(f, (await ApprovalsAsync(env, sup, null, f.D2)).Items));

            // 3: both
            var both = await ApprovalsAsync(env, sup, f.VarietyA, f.D1);
            Assert.All(both.Items, i => Assert.True(i.Sowing.SpeciesId == f.VarietyA && i.Sowing.SowingDate.Date == f.D1));
            Assert.Equal(new[] { f.A1 }.ToHashSet(), Ours(f, both.Items));
            Assert.Empty(Ours(f, (await ApprovalsAsync(env, sup, f.VarietyB, f.D2)).Items));

            // the workflow filter still applies with them (AND)
            var sowingTab = await ApprovalsAsync(env, sup, f.VarietyA, null, SowingApprovalRules.CategorySowing);
            Assert.Equal(new[] { f.A1, f.A2 }.ToHashSet(), Ours(f, sowingTab.Items));
            Assert.All(sowingTab.Items, i => Assert.Equal(SowingApprovalRules.CategorySowing, i.Category));
            Assert.Empty(Ours(f, (await ApprovalsAsync(env, sup, f.VarietyA, null, SowingApprovalRules.CategoryBedSowing)).Items));

            // 4: cleared
            var cleared = await ApprovalsAsync(env, sup, null, null);
            Assert.Equal(all.Items.Select(i => i.Sowing.Id), cleared.Items.Select(i => i.Sowing.Id));
        }

        [SkippableFact]
        public async Task SowingApprovalFilters_NeverExposeAnotherAreasBatches_EvenWhenForged()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.TransplantAreaScopeProbeSql);
            var f = await SowingsForFiltersAsync(env);
            var p = await PeopleAsync(env);

            // 6: a Mother Plant Area supervisor asks for the Main Office batches by variety and date: nothing listed or offered
            foreach (var outsider in new[] { p.OtherAreaSup, p.Viewer })
            {
                var page = await ApprovalsAsync(env, outsider, f.VarietyA, f.D1);
                Assert.Empty(Ours(f, page.Items));
                Assert.DoesNotContain(page.VarietyOptions, o => o.Id == f.VarietyA || o.Id == f.VarietyB);
                Assert.Null(page.Variety);
                Assert.Equal(SowingListFilter.VarietyClearedMessage, page.FilterNote);
                Assert.Empty(Ours(f, (await ApprovalsAsync(env, outsider, null, f.D1)).Items));
            }
            // the repository itself, given the filter directly (bypassing the page's check): the scope still decides
            var queue = env.Get<SowingApprovalQueueRepository>();
            var noScope = new SowingApprovalQueueRepository.Viewer(p.OtherAreaSup, "wf1022", true, _ => false, _ => false);
            Assert.Empty(Ours(f, (await queue.GetPendingAsync(noScope, null, false, new SowingListFilter(f.VarietyA, f.D1))).Items));
            // the administrator (every pending batch) gets exactly the filtered rows
            Assert.Equal(new[] { f.A1 }.ToHashSet(), Ours(f, (await ApprovalsAsync(env, p.Admin, f.VarietyA, f.D1)).Items));
        }

        [SkippableFact]
        public async Task SowingApprovalFilters_ApprovingAFilteredRow_ApprovesThatBatchOnly()
        {
            var env = await OpenAsync();
            var f = await SowingsForFiltersAsync(env);
            var sup = SowingSupervisor;

            var filtered = await ApprovalsAsync(env, sup, f.VarietyA, f.D1);
            var row = Assert.Single(filtered.Items, i => Ours(f, new[] { i }).Count == 1);
            Assert.Equal(f.A1, row.Sowing.Id);
            Assert.True(row.Eligibility.CanApprove, row.Eligibility.Reason);

            // 7: the Approve action (Confirm page, by the batch id) -- unchanged; only that batch is approved
            var confirm = env.Page<ConfirmPage>(await env.LoginAsync(sup));
            confirm.SeedSowingId = row.Sowing.Id;
            confirm.ActualReadyTrays = row.Sowing.NumberOfTrays ?? 0;
            var result = await confirm.OnPostAsync();
            Assert.IsType<RedirectToPageResult>(result);
            Assert.Null(confirm.TempData["Error"]);
            Assert.Equal(1, await env.ApprovalsOfAsync(f.A1));
            Assert.Equal(0, await env.ApprovalsOfAsync(f.A2));
            Assert.Equal(0, await env.ApprovalsOfAsync(f.B1));
            Assert.NotEqual(0, await env.ReadyStockOfAsync(f.A1));

            // the approved batch leaves the queue; the others are still waiting (filtered and unfiltered)
            var after = await ApprovalsAsync(env, sup, null, null);
            Assert.Equal(new[] { f.A2, f.B1 }.ToHashSet(), Ours(f, after.Items));
            Assert.Empty(Ours(f, (await ApprovalsAsync(env, sup, f.VarietyA, f.D1)).Items));

            // a user who may not approve is still refused on a direct post, filters or not (authorization unchanged)
            var p = await PeopleAsync(env);
            var outsider = env.Page<ConfirmPage>(await env.LoginAsync(p.OtherAreaSup));
            outsider.SeedSowingId = f.A2;
            outsider.ActualReadyTrays = 2;
            Assert.IsType<RedirectToPageResult>(await outsider.OnPostAsync());
            Assert.NotNull(outsider.TempData["Error"]);
            Assert.Equal(0, await env.ApprovalsOfAsync(f.A2));
        }

        [SkippableFact]
        public async Task SowingApprovalFilters_ApproveAndHistory_ReturnToTheSameFilteredList_ApprovalUnchanged()
        {
            var env = await OpenAsync();
            var f = await SowingsForFiltersAsync(env);
            var sup = SowingSupervisor;
            var cat = SowingApprovalRules.CategorySowing;
            var date = f.D1.ToString("yyyy-MM-dd");

            // the list hands its state to the Approve / History links
            var list = await ApprovalsAsync(env, sup, f.VarietyA, f.D1, cat);
            Assert.Equal(new Dictionary<string, string> { ["category"] = cat, ["variety"] = f.VarietyA.ToString(), ["sowingDate"] = date },
                list.ListState.Route);

            // Confirm carries it (navigation only) -- the approval is the same one, of that batch only, then History keeps it
            var confirm = env.Page<ConfirmPage>(await env.LoginAsync(sup));
            confirm.SeedSowingId = f.A1;
            confirm.ActualReadyTrays = 2;
            confirm.ListCategory = cat;
            confirm.ListVariety = f.VarietyA;
            confirm.ListSowingDate = f.D1;
            var result = Assert.IsType<RedirectToPageResult>(await confirm.OnPostAsync());
            Assert.Equal("/Production/ReadyConfirmation/History", result.PageName);
            Assert.Equal((object)f.A1, result.RouteValues!["id"]);
            Assert.Equal((cat, f.VarietyA.ToString(), date),
                ((string)result.RouteValues["category"]!, (string)result.RouteValues["variety"]!, (string)result.RouteValues["sowingDate"]!));
            Assert.Equal(1, await env.ApprovalsOfAsync(f.A1));
            Assert.Equal(0, await env.ApprovalsOfAsync(f.A2) + await env.ApprovalsOfAsync(f.B1));

            var history = env.Page<PlantStockManager.Pages.Production.ReadyConfirmation.HistoryModel>(await env.LoginAsync(sup));
            history.ListCategory = cat;
            history.ListVariety = f.VarietyA;
            history.ListSowingDate = f.D1;
            Assert.IsType<Microsoft.AspNetCore.Mvc.RazorPages.PageResult>(await history.OnGetAsync(f.A1));
            Assert.Equal(list.ListState.Route, history.ListState.Route);

            // a refused approval (another Area's supervisor) goes back to the filtered list -- still refused, nothing written
            var p = await PeopleAsync(env);
            var outsider = env.Page<ConfirmPage>(await env.LoginAsync(p.OtherAreaSup));
            outsider.SeedSowingId = f.A2;
            outsider.ActualReadyTrays = 2;
            outsider.ListVariety = f.VarietyA;
            var refused = Assert.IsType<RedirectToPageResult>(await outsider.OnPostAsync());
            Assert.Equal("/Production/ReadyConfirmation/Index", refused.PageName);
            Assert.Equal(f.VarietyA.ToString(), refused.RouteValues!["variety"]);
            Assert.Equal(0, await env.ApprovalsOfAsync(f.A2));

            // opened from Mother Plant & Cutting: the list state is ignored (that page binds only its tab)
            var mp = env.Page<ConfirmPage>(await env.LoginAsync(sup));
            mp.From = ConfirmPage.FromMotherPlant;
            mp.ListVariety = f.VarietyA;
            Assert.Empty(mp.ListState.Route);
        }

        [Fact]
        public void ApprovalListState_KeepsOnlyWellFormedValues()
        {
            var ok = PlantStockManager.Pages.Production.ReadyConfirmation.ApprovalListState.From(SowingApprovalRules.CategoryBedSowing, 12, new DateTime(2026, 9, 3, 14, 0, 0));
            Assert.Equal(new Dictionary<string, string> { ["category"] = "BedSowing", ["variety"] = "12", ["sowingDate"] = "2026-09-03" }, ok.Route);
            var bad = PlantStockManager.Pages.Production.ReadyConfirmation.ApprovalListState.From("<script>", -4, new DateTime(1900, 1, 1));
            Assert.Empty(bad.Route);
            Assert.Equal(7, PlantStockManager.Pages.Production.ReadyConfirmation.ApprovalListState.None.RouteWith("id", 7)["id"]);
        }

        [Fact]
        public void ApprovalLinks_EachGetTheirOwnRoute_WithTheBatchId_SharedListStateNeverMutated()
        {
            var shared = new Dictionary<string, string> { ["variety"] = "5", ["sowingDate"] = "2026-09-17" };
            PlantStockManager.Pages.Production.ReadyConfirmation.ApprovalActionView View(int id, string? from) => new(
                new SowingApprovalQueueRepository.Item(new SeedSowing { Id = id }, SowingApprovalRules.CategorySowing, null,
                    new SowingApprovalRules.Eligibility(true, null, "x", null), null, null), from, shared);
            var a = View(11, null).LinkRoute(withFrom: true);
            var b = View(12, "mp").LinkRoute(withFrom: true);
            Assert.Equal(new Dictionary<string, string> { ["variety"] = "5", ["sowingDate"] = "2026-09-17", ["id"] = "11" }, a);
            Assert.Equal("12", b["id"]);
            Assert.Equal("mp", b["from"]);
            Assert.False(View(12, "mp").LinkRoute(withFrom: false).ContainsKey("from"));
            Assert.Equal(2, shared.Count);                                                       // never mutated
            Assert.Equal("9", PlantStockManager.Pages.Production.ReadyConfirmation.ApprovalListState.None.RouteForBatch(9)["id"]);
            // the tag helpers: asp-all-route-data REPLACES asp-route-* values, so no link may mix them (an Approve link
            // rendered with href="" in the browser before this was fixed)
            foreach (var view in new[] { "_ApprovalAction.cshtml", "History.cshtml", "Confirm.cshtml", "Index.cshtml" })
            {
                var src = File.ReadAllText(Path.Combine(RepoRootForTests(), "Pages", "Production", "ReadyConfirmation", view));
                Assert.DoesNotMatch(@"<a[^>]*asp-all-route-data[^>]*asp-route-|<a[^>]*asp-route-[^>]*asp-all-route-data", src);
            }
        }

        [Fact]
        public void BothPages_BindOnlyTheFilterValues_AndPassThemToTheQuery()
        {
            var root = RepoRootForTests();
            var approvals = File.ReadAllText(Path.Combine(root, "Pages", "Production", "ReadyConfirmation", "Index.cshtml.cs"));
            Assert.Contains("SowingListFilter.Normalize(Variety, SowingDate, VarietyOptions.Select(v => v.Id))", approvals);
            Assert.Contains("approvableOnly: SowingApprovalVisibility.PendingApprovableOnly(User), filter: Filter)", approvals);
            var bed = File.ReadAllText(Path.Combine(root, "Pages", "Production", "BedSowing", "Transplant.cshtml.cs"));
            Assert.Contains("_repo.ListAsync(areas, openOnly: true, filter: Filter)", bed);
            Assert.Contains("Selected = (await _repo.ListAsync(areas, openOnly: true)).FirstOrDefault(b => b.Id == BedId)", bed);
        }
    }
}
