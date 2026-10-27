using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using OvergrowthPage = PlantStockManager.Pages.Production.ReadyStock.OvergrowthModel;
using ReadyStockIndex = PlantStockManager.Pages.Production.ReadyStock.IndexModel;

namespace PlantStockManager.Tests
{
    // 2026-10-22: "⚠ OG -- Overgrowth" on Ready Stock batches (2026-10-24_ReadyStockOvergrowth.sql). SCRATCH COPY only, with that
    // migration applied (skipped otherwise). REAL users -- principals from dbo.UserRoles via the claims factory -- drive the real
    // page models and repositories. The marker belongs to the dbo.ReadyStock batch row: it survives partial fulfilment and
    // dispatch, moves with Send to Outlet, is copied onto a Mother Plant receipt, and dispatch lines keep a snapshot of it.
    public partial class WorkflowCorrections1019E2ETests
    {
        private static Task<Env> OpenOvergrowthAsync(params string[] probes)
            => OpenAsync(new[] { ReadyStockOvergrowthSchema.ProbeSql }.Concat(probes).ToArray());

        private static ReadyStockRepository.OvergrowthActor OgActor(int user, bool full = false) => new(user, "og-test-" + user, full);

        private static Task<int> OgLogCountAsync(Env env, int batch, string action)
            => env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStockOvergrowthLog WHERE ReadyStockId = @B AND Action = @A", ("@B", batch), ("@A", action));

        private sealed record Balances(decimal Quantity, decimal Reserved, decimal Dispatched, decimal InTransit, int AreaId, bool Og);

        private static async Task<Balances> BalancesAsync(Env env, int batch)
        {
            using var conn = new SqlConnection(env.Cs);
            await conn.OpenAsync();
            using var cmd = new SqlCommand("SELECT Quantity, ReservedQuantity, DispatchedQuantity, InTransitQuantity, AreaId, IsOvergrowth FROM dbo.ReadyStock WHERE Id = @B", conn);
            cmd.Parameters.AddWithValue("@B", batch);
            using var r = await cmd.ExecuteReaderAsync();
            Assert.True(await r.ReadAsync());
            return new Balances(r.GetDecimal(0), r.GetDecimal(1), r.GetDecimal(2), r.GetDecimal(3), r.GetInt32(4), r.GetBoolean(5));
        }

        private static async Task<int> SeedlingBookingAsync(Env env, Variety v, int quantity)
        {
            var plantType = await env.ScalarAsync<int>("SELECT PlantTypeId FROM dbo.PlantSpecies WHERE Id = @S", ("@S", v.SpeciesId));
            var district = await env.ScalarAsync<int>("SELECT TOP 1 DistrictId FROM dbo.Districts ORDER BY DistrictId");
            var state = await env.ScalarAsync<int>("SELECT StateId FROM dbo.Districts WHERE DistrictId = @D", ("@D", district));
            var id = await env.Get<BookingRepository>().InsertBookingAsync(new Booking
            {
                PlantId = plantType, SpeciesId = v.SpeciesId, CustomerName = "OG-TEST " + Guid.NewGuid().ToString("N")[..6], DeliveryDate = AppClock.Today.AddDays(3),
                Quantity = quantity, AddedBy = "og-test", Address = "og-test", Contact = "9999999999", AdvanceTaken = false, StateId = state, DistrictId = district
            });
            Assert.True(id > 0, "booking not created");
            return id;
        }

        // ---- 1. marking / unmarking ----------------------------------------------------------------------------------------

        [SkippableFact]
        public async Task Overgrowth_SupervisorOfTheArea_MarksAndUnmarks_Audited_QuantitiesUntouched_RepeatIsANoOp()
        {
            var env = await OpenOvergrowthAsync();
            var batch = await ApprovedMainBatchAsync(env, await NewVarietyAsync(env));        // a normal Main Office batch
            var before = await BalancesAsync(env, batch);
            Assert.False(before.Og);                                                            // new stock is never marked automatically
            var repo = env.Get<ReadyStockRepository>();

            // a reason is required; an over-long one is refused; nothing changes
            Assert.Equal((false, OvergrowthRules.ReasonRequiredMessage), Two(await repo.SetOvergrowthAsync(batch, true, "  ", OgActor(SowingSupervisor))));
            Assert.False((await repo.SetOvergrowthAsync(batch, true, new string('x', 201), OgActor(SowingSupervisor))).Success);
            Assert.False((await BalancesAsync(env, batch)).Og);

            // the Main Office Sowing Supervisor marks it -- through the page, as in the browser
            var page = env.Page<OvergrowthPage>(await env.LoginAsync(SowingSupervisor));
            page.Reason = "  leggy, past transplant size  ";
            Assert.IsType<RedirectToPageResult>(await page.OnPostMarkAsync(batch));
            Assert.Equal(OvergrowthRules.MarkedMessage, page.TempData["Success"]);
            var marked = (await repo.GetByIdAsync(batch))!;
            Assert.True(marked.IsOvergrowth);
            Assert.Equal("leggy, past transplant size", marked.OvergrowthReason);
            Assert.NotNull(marked.OvergrowthMarkedAt);
            Assert.False(string.IsNullOrEmpty(marked.OvergrowthMarkedByName));
            Assert.Equal(before with { Og = true }, await BalancesAsync(env, batch));          // quantities untouched
            Assert.Equal(1, await OgLogCountAsync(env, batch, OvergrowthRules.ActionMarked));

            // marking again (a repeated submit): success, nothing changed, no second log row
            var again = await repo.SetOvergrowthAsync(batch, true, "again", OgActor(SowingSupervisor));
            Assert.Equal((true, false), (again.Success, again.Changed));
            Assert.Equal(1, await OgLogCountAsync(env, batch, OvergrowthRules.ActionMarked));

            // unmark (note optional) -> normal stock again, audited
            var view = env.Page<OvergrowthPage>(await env.LoginAsync(SowingSupervisor));
            Assert.IsType<Microsoft.AspNetCore.Mvc.RazorPages.PageResult>(await view.OnGetAsync(batch));
            Assert.True(view.CanChange);
            Assert.Single(view.History);
            Assert.IsType<RedirectToPageResult>(await view.OnPostUnmarkAsync(batch));
            var cleared = (await repo.GetByIdAsync(batch))!;
            Assert.Equal((false, (string?)null, (DateTime?)null), (cleared.IsOvergrowth, cleared.OvergrowthReason, cleared.OvergrowthMarkedAt));
            Assert.Equal(1, await OgLogCountAsync(env, batch, OvergrowthRules.ActionUnmarked));
            Assert.Equal((true, false), ((await repo.SetOvergrowthAsync(batch, false, null, OgActor(SowingSupervisor))) is var u ? (u.Success, u.Changed) : default));
            Assert.Equal(before, await BalancesAsync(env, batch));

            // the history is append-only in the database
            await Assert.ThrowsAsync<SqlException>(() => env.ScalarAsync<int>("UPDATE dbo.ReadyStockOvergrowthLog SET Reason = N'x' WHERE ReadyStockId = @B; SELECT 1", ("@B", batch)));
            await Assert.ThrowsAsync<SqlException>(() => env.ScalarAsync<int>("DELETE FROM dbo.ReadyStockOvergrowthLog WHERE ReadyStockId = @B; SELECT 1", ("@B", batch)));
            // and the row itself cannot be marked without a reason / time (CK_ReadyStock_Overgrowth)
            await Assert.ThrowsAsync<SqlException>(() => env.ScalarAsync<int>("UPDATE dbo.ReadyStock SET IsOvergrowth = 1 WHERE Id = @B; SELECT 1", ("@B", batch)));
        }

        private static (bool, string) Two((bool Success, string Message, bool Changed) r) => (r.Success, r.Message);

        [SkippableFact]
        public async Task Overgrowth_OnlyThePermissionInTheBatchsOwnArea_OrAnAdministrator_MayChangeIt()
        {
            var env = await OpenOvergrowthAsync(WorkflowCorrectionsSchema.TransplantAreaScopeProbeSql);
            var p = await PeopleAsync(env);
            var batch = await ApprovedMainBatchAsync(env, await NewVarietyAsync(env));        // Main Office (Area 2)
            var repo = env.Get<ReadyStockRepository>();

            // a Mother Plant Area supervisor HOLDS ReadyStock.Overgrowth -- but in another Area: refused (repository and page)
            Assert.Equal((false, OvergrowthRules.NotAuthorisedMessage), Two(await repo.SetOvergrowthAsync(batch, true, "forged", OgActor(p.OtherAreaSup))));
            Assert.Equal((false, OvergrowthRules.NotAuthorisedMessage), Two(await repo.SetOvergrowthAsync(batch, true, "forged", OgActor(p.Sup1))));
            var outsiderPage = env.Page<OvergrowthPage>(await env.LoginAsync(p.OtherAreaSup));
            outsiderPage.Reason = "forged";
            Assert.IsType<RedirectToPageResult>(await outsiderPage.OnPostMarkAsync(batch));   // the batch's Area is not theirs: not even shown
            Assert.Contains("cannot access", outsiderPage.TempData["Error"] as string);
            // a Main Office user WITHOUT the permission (may view Ready Stock): sees the batch, cannot change it
            var viewerPage = env.Page<OvergrowthPage>(await env.LoginAsync(p.Viewer));
            viewerPage.Reason = "forged";
            Assert.IsType<Microsoft.AspNetCore.Mvc.RazorPages.PageResult>(await viewerPage.OnPostMarkAsync(batch));
            Assert.False(viewerPage.CanChange);
            Assert.Equal(OvergrowthRules.NotAuthorisedMessage, viewerPage.Error);
            // an unidentified caller
            Assert.False((await repo.SetOvergrowthAsync(batch, true, "x", new ReadyStockRepository.OvergrowthActor(null, null, false))).Success);
            Assert.False((await BalancesAsync(env, batch)).Og);
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStockOvergrowthLog WHERE ReadyStockId = @B", ("@B", batch)));

            // a System Administrator (full access) may; and the Area's own supervisor may unmark what the admin marked
            var admin = env.Page<OvergrowthPage>(await env.LoginAsync(p.Admin));
            admin.Reason = "admin check";
            Assert.IsType<RedirectToPageResult>(await admin.OnPostMarkAsync(batch));
            Assert.True((await BalancesAsync(env, batch)).Og);
            Assert.True((await repo.SetOvergrowthAsync(batch, false, null, OgActor(SowingSupervisor))).Changed);

            // a batch in a MOTHER PLANT Area: the Main Office Sowing Supervisor (permission, other Area) and another Mother Plant
            // Area's supervisor are refused; that Area's own supervisors (both) and the administrator may
            var mpBatch = await ReceivedAsync(env, await NewVarietyAsync(env), p.Area, MotherPlantTransferRules.PurposeTrayTransplant, 48, 2);
            foreach (var refused in new[] { SowingSupervisor, p.OtherAreaSup, p.MainSup })
                Assert.Equal((false, OvergrowthRules.NotAuthorisedMessage), Two(await repo.SetOvergrowthAsync(mpBatch, true, "forged", OgActor(refused))));
            Assert.False((await BalancesAsync(env, mpBatch)).Og);
            Assert.True((await repo.SetOvergrowthAsync(mpBatch, true, "own Area", OgActor(p.Sup1))).Changed);
            Assert.True((await repo.SetOvergrowthAsync(mpBatch, false, null, OgActor(p.Sup2))).Changed);
            Assert.True((await repo.SetOvergrowthAsync(mpBatch, true, "admin", OgActor(p.Admin, full: true))).Changed);
            Assert.Equal((false, OvergrowthRules.NotAuthorisedMessage), Two(await repo.SetOvergrowthAsync(mpBatch, false, null, OgActor(SowingSupervisor))));
            Assert.True((await BalancesAsync(env, mpBatch)).Og);

            // the permission is granted only to the two Ready Stock approver roles
            var roles = await env.IdsAsync(@"SELECT rp.RoleId FROM dbo.RolePermissions rp JOIN dbo.Permissions p ON p.Id = rp.PermissionId WHERE p.Code = N'ReadyStock.Overgrowth'");
            var names = new List<string>();
            foreach (var r in roles)
                names.Add(await env.ScalarAsync<string>("SELECT COALESCE(NULLIF(LTRIM(RTRIM(Name)), N''), RoleName) FROM dbo.Roles WHERE Id = @R", ("@R", r)));
            Assert.Equal(new[] { "Mother Plant Supervisor", "Sowing Supervisor" }, names.OrderBy(n => n));
        }

        [SkippableFact]
        public async Task Overgrowth_ABatchWithNoPlantsLeft_CannotBeMarked()
        {
            var env = await OpenOvergrowthAsync();
            var v = await NewVarietyAsync(env);
            var batch = await ApprovedMainBatchAsync(env, v, plants: 48);
            var booking = await SeedlingBookingAsync(env, v, 48);
            var fulfil = env.Get<SeedlingFulfilmentRepository>();
            var actor = new SeedlingFulfilmentRepository.Actor("og-test", SowingSupervisor);
            Assert.True((await fulfil.AllocateBatchAsync(booking, batch, 48, null, actor, Any)).Success);
            var alloc = Assert.Single(await fulfil.GetAllocationsAsync(booking));
            Assert.True((await fulfil.DispatchAsync(booking, new[] { (alloc.Id, 48m) }, AppClock.Today, null, "og", actor, Any)).Success);
            Assert.Equal((false, OvergrowthRules.NothingLeftMessage),
                Two(await env.Get<ReadyStockRepository>().SetOvergrowthAsync(batch, true, "too late", OgActor(SowingSupervisor))));
        }

        // ---- 2. fulfilment and dispatch ---------------------------------------------------------------------------------

        [SkippableFact]
        public async Task Overgrowth_IsAllocated_PartlyDispatched_RemainderStaysOvergrowth_DispatchLineKeepsTheSnapshot()
        {
            var env = await OpenOvergrowthAsync();
            var v = await NewVarietyAsync(env);
            var og = await ApprovedMainBatchAsync(env, v, plants: 480);                         // 20 x 24
            var normal = await ApprovedMainBatchAsync(env, v, plants: 240);
            var repo = env.Get<ReadyStockRepository>();
            Assert.True((await repo.SetOvergrowthAsync(og, true, "overgrown in tray", OgActor(SowingSupervisor))).Changed);

            // the Dispatch page's batch choices: the Overgrowth batch is offered (still selectable), flagged; the normal one not flagged
            var fulfil = env.Get<SeedlingFulfilmentRepository>();
            var plantType = await env.ScalarAsync<int>("SELECT PlantTypeId FROM dbo.PlantSpecies WHERE Id = @S", ("@S", v.SpeciesId));
            var options = await fulfil.GetBatchOptionsAsync(plantType);
            Assert.True(Assert.Single(options, o => o.ReadyStockId == og).IsOvergrowth);
            Assert.False(Assert.Single(options, o => o.ReadyStockId == normal).IsOvergrowth);
            // the Allocate Batch search, narrowed to this test's own (unique) variety -- paging must not depend on how many batches exist
            var varietyName = await env.ScalarAsync<string>("SELECT Name FROM dbo.PlantSpecies WHERE Id = @S", ("@S", v.SpeciesId));
            var search = await fulfil.SearchBatchOptionsAsync(plantType, v.SpeciesId, new BatchSearch { Query = varietyName, PageSize = 100 }, null);
            Assert.Equal(2, search.Total);
            Assert.True(Assert.Single(search.Rows, o => o.ReadyStockId == og).IsOvergrowth);

            // allocate 96 to a booking and dispatch 48 of them: the existing rules, unchanged
            var booking = await SeedlingBookingAsync(env, v, 120);
            var actor = new SeedlingFulfilmentRepository.Actor("og-test", SowingSupervisor);
            Assert.True((await fulfil.AllocateBatchAsync(booking, og, 96, null, actor, Any)).Success);
            Assert.True((await fulfil.AllocateBatchAsync(booking, normal, 24, null, actor, Any)).Success);
            var allocations = await fulfil.GetAllocationsAsync(booking);
            Assert.True(Assert.Single(allocations, a => a.ReadyStockId == og).IsOvergrowth);
            Assert.False(Assert.Single(allocations, a => a.ReadyStockId == normal).IsOvergrowth);
            var ogAlloc = allocations.Single(a => a.ReadyStockId == og);
            var normalAlloc = allocations.Single(a => a.ReadyStockId == normal);
            var (ok, msg) = await fulfil.DispatchAsync(booking, new[] { (ogAlloc.Id, 48m), (normalAlloc.Id, 24m) }, AppClock.Today, null, "og partial", actor, Any);
            Assert.True(ok, msg);

            // balances: exactly the existing arithmetic; the remainder of the batch is still Overgrowth
            Assert.Equal(new Balances(480, 48, 48, 0, MainArea, true), await BalancesAsync(env, og));
            Assert.Equal(new Balances(240, 0, 24, 0, MainArea, false), await BalancesAsync(env, normal));
            var b = (await repo.GetByIdAsync(og))!;
            Assert.Equal((432m, 384m), (b.PhysicalQuantity, b.AvailableQuantity));

            // the dispatch lines: the Overgrowth one recorded as Overgrowth, the normal one not
            var dispatch = Assert.Single(await fulfil.GetDispatchesAsync(booking));
            Assert.True(Assert.Single(dispatch.Lines, l => l.ReadyStockId == og).IsOvergrowth);
            Assert.False(Assert.Single(dispatch.Lines, l => l.ReadyStockId == normal).IsOvergrowth);

            // unmarking later: the open allocation follows the batch NOW, the dispatch keeps what was dispatched
            Assert.True((await repo.SetOvergrowthAsync(og, false, "recovered", OgActor(SowingSupervisor))).Changed);
            Assert.False((await fulfil.GetAllocationsAsync(booking)).Single(a => a.ReadyStockId == og).IsOvergrowth);
            Assert.True((await fulfil.GetDispatchesAsync(booking)).Single().Lines.Single(l => l.ReadyStockId == og).IsOvergrowth);
            Assert.Equal(new Balances(480, 48, 48, 0, MainArea, false), await BalancesAsync(env, og));
        }

        [SkippableFact]
        public async Task Overgrowth_CustomerDispatchOfWholeTrays_KeepsTheMarker_OnTheLineAndTheRest()
        {
            var env = await OpenOvergrowthAsync(WorkflowCorrectionsSchema.SeedlingSaleTokenProbeSql);
            var v = await NewVarietyAsync(env);
            var og = await ApprovedMainBatchAsync(env, v, plants: 240);                         // 10 x 24
            Assert.True((await env.Get<ReadyStockRepository>().SetOvergrowthAsync(og, true, "sell soon", OgActor(SowingSupervisor))).Changed);
            var (ok, msg, code) = await env.Get<SeedlingFulfilmentRepository>().DirectTraySaleAsync(
                new SeedlingFulfilmentRepository.DirectTraySaleRequest(og, 3, "OG customer", "9999999999", null, "og", Guid.NewGuid(), FromReadySeedlingStock: true),
                new SeedlingFulfilmentRepository.Actor("og-test", SowingSupervisor), Any);
            Assert.True(ok, msg);
            Assert.Equal(new Balances(240, 0, 72, 0, MainArea, true), await BalancesAsync(env, og));
            Assert.Equal(1, await env.ScalarAsync<int>(@"SELECT COUNT(*) FROM dbo.SeedlingDispatchLines l JOIN dbo.SeedlingDispatches d ON d.Id = l.SeedlingDispatchId
WHERE l.ReadyStockId = @B AND d.DispatchCode = @C AND l.IsOvergrowth = 1", ("@B", og), ("@C", code!)));
        }

        // ---- 3. movements -------------------------------------------------------------------------------------------------

        [SkippableFact]
        public async Task Overgrowth_TravelsWithSendToOutlet_AndIsCopiedOntoAMotherPlantReceipt_NotOntoATransplantedOutput()
        {
            var env = await OpenOvergrowthAsync(WorkflowCorrectionsSchema.TransplantAreaScopeProbeSql);
            var p = await PeopleAsync(env);
            var repo = env.Get<ReadyStockRepository>();

            // Send to Outlet: the same row moves -- still Overgrowth, same reason, nothing else changed
            var toOutlet = await ApprovedMainBatchAsync(env, await NewVarietyAsync(env), plants: 240);
            Assert.True((await repo.SetOvergrowthAsync(toOutlet, true, "outlet sale", OgActor(SowingSupervisor))).Changed);
            var (sent, sentMsg, _) = await env.Get<InternalTransferRepository>().InsertAsync(new InternalTransfer
            {
                StockType = "ReadyStock", SourceReadyStockId = toOutlet, DestinationAreaId = OutletArea, Quantity = 240, CreatedBy = "og-test", SubmissionToken = Guid.NewGuid()
            }, Recorder);
            Assert.True(sent, sentMsg);
            var moved = (await repo.GetByIdAsync(toOutlet))!;
            Assert.Equal((OutletArea, true, "outlet sale"), (moved.AreaId, moved.IsOvergrowth, moved.OvergrowthReason));

            // Transfer to a Mother Plant Area: the RECEIVED batch carries the marker (logged 'Inherited'); the source keeps it
            var main = await ApprovedMainBatchAsync(env, await NewVarietyAsync(env), plants: 480);
            Assert.True((await repo.SetOvergrowthAsync(main, true, "send to MP", OgActor(SowingSupervisor))).Changed);
            var transfers = env.Get<MotherPlantTransferRepository>();
            var (tOk, tMsg, transferId, _) = await transfers.CreateAsync(new MotherPlantTransferRepository.CreateRequest(main, 240, p.Area, null,
                AppClock.Today, "og", Guid.NewGuid(), MotherPlantTransferRules.PurposeTrayTransplant), Actor, Any);
            Assert.True(tOk, tMsg);
            Assert.True((await BalancesAsync(env, main)).Og);
            var (rOk, rMsg, received) = await transfers.ReceiveAsync(transferId, 240, null, Actor, Any, null, null);
            Assert.True(rOk, rMsg);
            var child = (await repo.GetByIdAsync(received!.Value))!;
            Assert.Equal((true, "send to MP", ReadyStock.OriginTransfer), (child.IsOvergrowth, child.OvergrowthReason, child.OriginType));
            var inherited = Assert.Single(await repo.GetOvergrowthLogAsync(child.Id));
            Assert.Equal((OvergrowthRules.ActionInherited, (int?)main), (inherited.Action, inherited.SourceReadyStockId));
            // plants conserved: 240 stay in the (still Overgrowth) source, 240 are in the received batch, nothing in transit
            var mainAfter = await BalancesAsync(env, main);
            Assert.Equal((240m, 0m, true), (mainAfter.Quantity - mainAfter.Dispatched - mainAfter.InTransit, mainAfter.InTransit, mainAfter.Og));
            Assert.Equal(240m, (await BalancesAsync(env, child.Id)).Quantity);
            // the Area's own supervisor can unmark the received batch; the source keeps its own marker
            Assert.True((await repo.SetOvergrowthAsync(child.Id, false, null, OgActor(p.Sup1))).Changed);
            Assert.True((await BalancesAsync(env, main)).Og);

            // a received batch of a NORMAL source starts unmarked (no Inherited row)
            var plainMain = await ApprovedMainBatchAsync(env, await NewVarietyAsync(env), plants: 240);
            var (t2Ok, t2Msg, t2, _) = await transfers.CreateAsync(new MotherPlantTransferRepository.CreateRequest(plainMain, 240, p.Area, null,
                AppClock.Today, "og", Guid.NewGuid(), MotherPlantTransferRules.PurposeTrayTransplant), Actor, Any);
            Assert.True(t2Ok, t2Msg);
            var (r2Ok, r2Msg, plainChild) = await transfers.ReceiveAsync(t2, 240, null, Actor, Any, null, null);
            Assert.True(r2Ok, r2Msg);
            Assert.False((await BalancesAsync(env, plainChild!.Value)).Og);
            Assert.Empty(await repo.GetOvergrowthLogAsync(plainChild.Value));

            // a transplanted output (OriginTransplant) is new trays: it does NOT inherit (checked inside a rolled-back transaction)
            using var conn = new SqlConnection(env.Cs);
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            var output = await repo.CreateDerivedBatchAsync(conn, tx, main, ReadyStock.OriginTransplant, int.MaxValue - Random.Shared.Next(1, 100000),
                p.Area, null, "24 Cavity", "og-test");
            using (var check = new SqlCommand("SELECT IsOvergrowth FROM dbo.ReadyStock WHERE Id = @I", conn, tx))
            {
                check.Parameters.AddWithValue("@I", output);
                Assert.False((bool)(await check.ExecuteScalarAsync())!);
            }
            tx.Rollback();
        }

        // Owner decision 2026-10-22: a transplanted batch starts UNMARKED, and the transplant's existing trace keeps whether the
        // source was Overgrowth at that moment (source TransplantOut ledger row + the new tray batch's remarks) -- even if the
        // source is unmarked later.
        [SkippableFact]
        public async Task Overgrowth_TransplantedBatch_StartsUnmarked_TraceKeepsTheSourcesOvergrowthAtTransplant()
        {
            var env = await OpenOvergrowthAsync(WorkflowCorrectionsSchema.TransplantAreaScopeProbeSql);
            var p = await PeopleAsync(env);
            var repo = env.Get<ReadyStockRepository>();

            async Task<(int Source, int Transplant, int OutputSowing)> TransplantAsync(bool markSource)
            {
                var source = await ReceivedAsync(env, await NewVarietyAsync(env), p.Area, MotherPlantTransferRules.PurposeTrayTransplant, 48, ReadyDays);
                if (markSource)
                    Assert.True((await repo.SetOvergrowthAsync(source, true, "roots circling", OgActor(p.Sup1))).Changed);   // the Area's own supervisor
                Assert.True((await env.Get<TrayStockRepository>().AddStockAsync(p.Area, null, "24 Cavity", 3, DateTime.UtcNow.AddDays(-ReadyDays - 1),
                    Random.Shared.Next(1, int.MaxValue), SowingSupervisor, "og-test", "og trays", Any)).Success);
                var (ok, msg, id, _) = await env.Get<TransplantRepository>().CreateAsync(new TransplantRepository.CreateRequest(source, p.Area, null, "24 Cavity",
                    48, 48, 0, AppClock.Today.AddDays(-ReadyDays), "og notes", Guid.NewGuid(), null), Actor, Any);
                Assert.True(ok, msg);
                var output = await env.ScalarAsync<int>("SELECT OutputSeedSowingId FROM dbo.ReadyStockTransplants WHERE Id = @T", ("@T", id));
                Assert.True(output > 0);
                return (source, id, output);
            }
            Task<string> SourceLedgerRemarkAsync(int source, int transplant) => env.ScalarAsync<string>(
                "SELECT Remarks FROM dbo.ReadyStockTransactions WHERE ReadyStockId = @S AND TransactionType = N'TransplantOut' AND ReferenceId = @T", ("@S", source), ("@T", transplant));
            Task<string> OutputRemarkAsync(int sowing) => env.ScalarAsync<string>("SELECT Remarks FROM dbo.SeedSowings WHERE Id = @S", ("@S", sowing));

            var og = await TransplantAsync(markSource: true);
            Assert.Contains(OvergrowthRules.TransplantSourceLedgerNote("roots circling"), await SourceLedgerRemarkAsync(og.Source, og.Transplant));
            var outputRemark = await OutputRemarkAsync(og.OutputSowing);
            Assert.Contains(OvergrowthRules.TransplantOutputNote, outputRemark);
            Assert.True(outputRemark.IndexOf(OvergrowthRules.TransplantOutputNote, StringComparison.Ordinal) < outputRemark.IndexOf("og notes", StringComparison.Ordinal));
            Assert.True((await BalancesAsync(env, og.Source)).Og);                                // the source keeps its own marker

            // the tray batch, once approved, is a NEW Ready Stock batch -- unmarked, no history of its own
            Assert.True((await env.Get<ReadyConfirmationRepository>().ConfirmAsync(og.OutputSowing, 2, null, null, null, "og-test", p.Sup1, Any)).Success);
            var approved = await env.ScalarAsync<int>("SELECT Id FROM dbo.ReadyStock WHERE SeedSowingId = @S AND OriginType = N'Sowing'", ("@S", og.OutputSowing));
            Assert.False((await BalancesAsync(env, approved)).Og);
            Assert.Empty(await repo.GetOvergrowthLogAsync(approved));
            // its lineage still leads to the source batch (the existing trace)
            Assert.Contains(await repo.GetLineageAsync(approved), b => b.Id == og.Source);

            // unmarking the source later does not rewrite the transplant's trace
            Assert.True((await repo.SetOvergrowthAsync(og.Source, false, "recovered", OgActor(p.Sup1))).Changed);
            Assert.Contains(OvergrowthRules.TransplantSourceLedgerNote("roots circling"), await SourceLedgerRemarkAsync(og.Source, og.Transplant));
            Assert.Contains(OvergrowthRules.TransplantOutputNote, await OutputRemarkAsync(og.OutputSowing));

            // a normal source: no Overgrowth note anywhere
            var plain = await TransplantAsync(markSource: false);
            Assert.DoesNotContain("Overgrowth", await SourceLedgerRemarkAsync(plain.Source, plain.Transplant));
            Assert.DoesNotContain("Overgrowth", await OutputRemarkAsync(plain.OutputSowing));
        }

        // ---- 4. monitoring ----------------------------------------------------------------------------------------------

        [SkippableFact]
        public async Task Overgrowth_ReadyStockList_FiltersAndTotalsOvergrowthSeparately_InTheUsersAreas()
        {
            var env = await OpenOvergrowthAsync();
            var v = await NewVarietyAsync(env);
            var og = await ApprovedMainBatchAsync(env, v, plants: 240);
            var normal = await ApprovedMainBatchAsync(env, v, plants: 480);
            Assert.True((await env.Get<ReadyStockRepository>().SetOvergrowthAsync(og, true, "monitor", OgActor(SowingSupervisor))).Changed);

            async Task<ReadyStockIndex> ListAsync(string? stock)
            {
                var page = env.Page<ReadyStockIndex>(await env.LoginAsync(SowingSupervisor));
                page.Search = v.SpeciesId > 0 ? (await env.ScalarAsync<string>("SELECT Name FROM dbo.PlantSpecies WHERE Id = @S", ("@S", v.SpeciesId))) : null;
                page.Stock = stock;
                await page.OnGetAsync();
                return page;
            }
            var all = await ListAsync(null);
            Assert.Equal(new[] { normal, og }.OrderBy(x => x), all.Items.Select(i => i.Id).OrderBy(x => x));
            Assert.Equal((1, 240m, 240m), (all.OvergrowthTotals.Batches, all.OvergrowthTotals.Physical, all.OvergrowthTotals.Available));
            Assert.Equal((1, 480m), (all.NormalTotals.Batches, all.NormalTotals.Physical));
            Assert.Equal(new[] { og }, (await ListAsync(ReadyStockIndex.StockOvergrowth)).Items.Select(i => i.Id));
            Assert.Equal(new[] { normal }, (await ListAsync(ReadyStockIndex.StockNormal)).Items.Select(i => i.Id));
            // the totals ignore the Stock filter (both kinds always visible side by side)
            Assert.Equal(all.OvergrowthTotals, (await ListAsync(ReadyStockIndex.StockNormal)).OvergrowthTotals);
        }

        // ---- 5. without the migration ---------------------------------------------------------------------------------------

        [SkippableFact]
        public async Task Overgrowth_WithoutTheMigration_ReadyStockWorksAsBefore_NothingCanBeMarked()
        {
            var env = await OpenAsync();
            Skip.If(await env.ScalarAsync<int>(ReadyStockOvergrowthSchema.ProbeSql) == 1, "2026-10-24_ReadyStockOvergrowth.sql is applied to this copy.");
            var batch = await ApprovedMainBatchAsync(env, await NewVarietyAsync(env));
            var repo = env.Get<ReadyStockRepository>();
            Assert.False((await repo.GetByIdAsync(batch))!.IsOvergrowth);
            Assert.Equal((false, ReadyStockOvergrowthSchema.MigrationMessage), Two(await repo.SetOvergrowthAsync(batch, true, "x", OgActor(SowingSupervisor, full: true))));
            Assert.Empty(await repo.GetOvergrowthLogAsync(batch));
            var page = env.Page<OvergrowthPage>(await env.LoginAsync(SowingSupervisor));
            Assert.IsType<Microsoft.AspNetCore.Mvc.RazorPages.PageResult>(await page.OnGetAsync(batch));
            Assert.False(page.SchemaReady || page.CanChange);
        }
    }
}
