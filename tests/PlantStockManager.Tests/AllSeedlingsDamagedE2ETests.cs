using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using ConfirmPage = PlantStockManager.Pages.Production.ReadyConfirmation.ConfirmModel;

namespace PlantStockManager.Tests
{
    // 2026-10-31: Supervisor Approval "All Seedlings Damaged -- Approve with 0 Ready Seedlings", end to end on a SCRATCH COPY
    // (PSM_SCRATCH_CONNECTION, PlantsIMS2_Scratch_*). A Main Office DIRECT SEED sowing (24 Cavity, 480 seeds = 20 trays) is
    // approved through the REAL Confirm page model by its REAL assigned supervisor (user 9) and the REAL repository / database
    // triggers. SKIPPED without 2026-10-31_AllSeedlingsDamagedApproval.sql, except the "without the migration" test, which
    // needs a copy WITHOUT it. The cutting-source cases are in PartialTrayCuttingSowingE2ETests (AllSeedlingsDamagedCutting).
    public partial class WorkflowCorrections1019E2ETests
    {
        private const string DamageReason = "Hail storm broke every seedling";

        private sealed record DamageCase(int Sowing, int SeedLot, decimal SeedLot0, decimal Trays0, decimal Cocopeat0);

        private sealed record DamageBalances(decimal SeedLot, decimal Trays, decimal Cocopeat);

        // A fresh variety + lot, 24-cavity trays and Cocopeat in Main Office, then a 480-seed sowing (20 trays) by user 21 for
        // supervisor 9. The *0 balances are taken AFTER the sowing: an approval must not move any of them.
        private static async Task<DamageCase> DamageSowingAsync(Env env)
        {
            var v = await NewVarietyAsync(env);
            Assert.True((await env.Get<TrayStockRepository>().AddStockAsync(MainArea, MainPolyhouse, "24 Cavity", 40, DateTime.UtcNow.AddDays(-45),
                Random.Shared.Next(1, int.MaxValue), SowingSupervisor, "dmg-test", "all-damaged setup", Any)).Success);
            Assert.True((await env.Get<MaterialStockRepository>().AdjustAsync(MainArea, MainPolyhouse, env.Cocopeat, 1, 500m, "all-damaged setup", Guid.NewGuid(), Recorder, Any,
                LedgerDates.Instant(AppClock.Today.AddDays(-45)).TransactionUtc)).Success);
            var (ok, msg, sowing) = await env.Get<SeedSowingRepository>().InsertAsync(new SeedSowing
            {
                SourceSeedStockId = v.SeedStockId, SeedQuantity = 480, CavityType = "24 Cavity", SowingDate = AppClock.Today.AddDays(-40),
                PolyhouseId = MainPolyhouse, SupervisorId = SowingSupervisor, CreatedBy = "dmg-test", CreatedById = Recorder, GrowingMaterialTypeId = env.Cocopeat
            }, Recorder, Any);
            Assert.True(ok, msg);
            var b = await DamageBalancesAsync(env, v.SeedStockId);
            Assert.Equal(100000m - 480m, b.SeedLot);
            return new DamageCase(sowing, v.SeedStockId, b.SeedLot, b.Trays, b.Cocopeat);
        }

        private static async Task<DamageBalances> DamageBalancesAsync(Env env, int seedLot) => new(
            await env.ScalarAsync<decimal>("SELECT PhysicalQuantity FROM dbo.SeedStock WHERE Id = @L", ("@L", seedLot)),
            await env.ScalarAsync<decimal>("SELECT ISNULL(SUM(PhysicalQuantity), 0) FROM dbo.TrayStock WHERE AreaId = @A AND PolyhouseId = @P AND TraySize = N'24 Cavity'",
                ("@A", MainArea), ("@P", MainPolyhouse)),
            await env.ScalarAsync<decimal>("SELECT ISNULL(SUM(CurrentQuantity), 0) FROM dbo.MaterialStock WHERE AreaId = @A AND PolyhouseId = @P AND MaterialTypeId = @M",
                ("@A", MainArea), ("@P", MainPolyhouse), ("@M", env.Cocopeat)));

        private static async Task AssertInventoryUnchangedAsync(Env env, DamageCase c)
            => Assert.Equal(new DamageBalances(c.SeedLot0, c.Trays0, c.Cocopeat0), await DamageBalancesAsync(env, c.SeedLot));

        private static Task<string> SowingStatusAsync(Env env, int sowing)
            => env.ScalarAsync<string>("SELECT Status FROM dbo.SeedSowings WHERE Id = @S", ("@S", sowing));

        private static Task<decimal> BatchQuantityAsync(Env env, int sowing)
            => env.ScalarAsync<decimal>("SELECT ISNULL(SUM(Quantity), 0) FROM dbo.ReadyStock WHERE SeedSowingId = @S", ("@S", sowing));

        private static Task<int> BatchLedgerRowsAsync(Env env, int sowing)
            => env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStockTransactions t JOIN dbo.ReadyStock rs ON rs.Id = t.ReadyStockId WHERE rs.SeedSowingId = @S", ("@S", sowing));

        private static async Task AssertNothingApprovedAsync(Env env, DamageCase c)
        {
            Assert.Equal(0, await env.ApprovalsOfAsync(c.Sowing));
            Assert.Equal(0m, await BatchQuantityAsync(env, c.Sowing));
            Assert.Equal(0, await BatchLedgerRowsAsync(env, c.Sowing));
            Assert.Equal("Sown", await SowingStatusAsync(env, c.Sowing));
            Assert.Equal(0m, await env.ScalarAsync<decimal>("SELECT ConfirmedReadyQuantity + WastageQuantity FROM dbo.SeedSowings WHERE Id = @S", ("@S", c.Sowing)));
            await AssertInventoryUnchangedAsync(env, c);
        }

        private static async Task<ConfirmPage> DamagePageAsync(Env env, int user, int sowing, bool damaged, string? reason, decimal trays = 0)
        {
            var page = env.Page<ConfirmPage>(await env.LoginAsync(user));
            page.SeedSowingId = sowing;
            page.AllSeedlingsDamaged = damaged;
            page.DamageReason = reason;
            page.ActualReadyTrays = trays;
            page.Remarks = "e2e remarks";
            return page;
        }

        private static Task<(bool Success, string? Message, int Id)> ApproveDamagedAsync(Env env, int sowing, int? user = SowingSupervisor, string? reason = DamageReason)
            => env.Get<ReadyConfirmationRepository>().ConfirmAsync(sowing, 0, null, null, null, "dmg-test", user, Any, allDamaged: true, damageReason: reason);

        // ---- 2 / 3 / 4 / 5 / 11: the option, end to end through the page -------------------------------------------------

        [SkippableFact]
        public async Task AllDamaged_SeedSowing_ApprovedWithZero_DamageRecorded_NoReadyStock_InventoryUnchanged_Completed()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.AllSeedlingsDamagedProbeSql);
            var c = await DamageSowingAsync(env);

            // GET offers the option
            var get = env.Page<ConfirmPage>(await env.LoginAsync(SowingSupervisor));
            Assert.IsType<PageResult>(await get.OnGetAsync(c.Sowing));
            Assert.True(get.AllSeedlingsDamagedAvailable);

            var page = await DamagePageAsync(env, SowingSupervisor, c.Sowing, damaged: true, reason: "  " + DamageReason + "  ");
            var result = await page.OnPostAsync();
            Assert.IsType<RedirectToPageResult>(result);
            Assert.Contains("approved with 0 ready seedlings", (string)page.TempData["Success"]!);

            // the approval: 0 trays, 0 seedlings, everything still expected = damaged wastage, the reason kept
            Assert.Equal(1, await env.ApprovalsOfAsync(c.Sowing));
            var approval = (await env.Get<ReadyConfirmationRepository>().GetBySeedSowingIdAsync(c.Sowing)).Single();
            Assert.Equal("Confirmed", approval.Status);
            Assert.Equal(0, approval.ActualTrayQuantity);
            Assert.True(AllSeedlingsDamagedRules.IsAllDamagedApproval(approval.ActualTrayQuantity));
            Assert.Equal(0m, approval.ConfirmedQuantity);
            Assert.Equal(480m, approval.WastageQuantity);
            Assert.Equal("Damaged plants", approval.WastageReason);
            Assert.Equal("All seedlings damaged: " + DamageReason + " | e2e remarks", approval.Remarks);
            Assert.Equal(SowingSupervisor, approval.ApprovedById);

            // no positive Ready Stock: the batch row the approval references holds 0 and has NO ledger row
            Assert.Equal(0m, await BatchQuantityAsync(env, c.Sowing));
            Assert.Equal(0, await BatchLedgerRowsAsync(env, c.Sowing));

            // the sowing is closed, not pending: Completed, Ready 0 + Wastage 480 = Sown
            Assert.Equal("Completed", await SowingStatusAsync(env, c.Sowing));
            Assert.Equal(0m, await env.ScalarAsync<decimal>("SELECT ConfirmedReadyQuantity FROM dbo.SeedSowings WHERE Id = @S", ("@S", c.Sowing)));
            Assert.Equal(480m, await env.ScalarAsync<decimal>("SELECT WastageQuantity FROM dbo.SeedSowings WHERE Id = @S", ("@S", c.Sowing)));
            // ...and no longer approvable (not stuck in the approval queue): the Confirm page refuses to open it
            var reopen = env.Page<ConfirmPage>(await env.LoginAsync(SowingSupervisor));
            Assert.IsType<RedirectToPageResult>(await reopen.OnGetAsync(c.Sowing));

            // seed lot, trays and growing material: exactly as after the sowing (nothing taken, nothing returned)
            await AssertInventoryUnchangedAsync(env, c);
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.TrayStockTransactions WHERE ReferenceType = N'ReadyConfirmation' AND ReferenceId = @A", ("@A", approval.Id)));
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.MaterialTransactions WHERE ReferenceType = N'ReadyConfirmation' AND ReferenceId = @A", ("@A", approval.Id)));

            // reporting: Wasted Stock lists it as damage (reason + the free-text reason in Remarks)
            var rows = await env.Get<WastageRepository>().GetAsync(AppClock.Today.AddDays(-1), AppClock.Today.AddDays(1), MainArea);
            var code = await env.ScalarAsync<string>("SELECT SowingCode FROM dbo.SeedSowings WHERE Id = @S", ("@S", c.Sowing));
            var row = Assert.Single(rows, r => r.Reference == code);
            Assert.Equal(480m, row.Quantity);
            Assert.Equal("Damaged plants", row.Reason);
            Assert.StartsWith("All seedlings damaged: ", row.Remarks);

            // and a second submit (duplicate) is refused -- the sowing is Completed -- nothing doubles
            var again = await DamagePageAsync(env, SowingSupervisor, c.Sowing, damaged: true, reason: DamageReason);
            Assert.IsType<PageResult>(await again.OnPostAsync());
            Assert.Contains(again.ModelState.Values.SelectMany(v => v.Errors), e => e.ErrorMessage == "This sowing has already been fully approved.");
            Assert.Equal(1, await env.ApprovalsOfAsync(c.Sowing));
            Assert.Equal(480m, await env.ScalarAsync<decimal>("SELECT WastageQuantity FROM dbo.SeedSowings WHERE Id = @S", ("@S", c.Sowing)));
        }

        // ---- 1: the ordinary approval is unchanged; 0 trays only through the option ----------------------------------------

        [SkippableFact]
        public async Task OrdinaryApproval_Unchanged_ZeroTraysOnlyThroughTheOption()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.AllSeedlingsDamagedProbeSql);
            var c = await DamageSowingAsync(env);
            var repo = env.Get<ReadyConfirmationRepository>();

            // 0 trays on the ordinary form / repository: refused as before
            var page = await DamagePageAsync(env, SowingSupervisor, c.Sowing, damaged: false, reason: null, trays: 0);
            Assert.IsType<PageResult>(await page.OnPostAsync());
            Assert.Contains(page.ModelState.Values.SelectMany(v => v.Errors), e => e.ErrorMessage == "Actual Ready Trays must be at least 1 complete tray.");
            var (zOk, zMsg, _) = await repo.ConfirmAsync(c.Sowing, 0, "Damaged plants", null, null, "dmg-test", SowingSupervisor, Any);
            Assert.False(zOk);
            Assert.Equal("Actual Ready Trays must be a whole number of at least 1.", zMsg);
            // the option with a tray count: refused
            var (tOk, tMsg, _) = await repo.ConfirmAsync(c.Sowing, 5, null, null, null, "dmg-test", SowingSupervisor, Any, allDamaged: true, damageReason: DamageReason);
            Assert.False(tOk);
            Assert.Equal("With All Seedlings Damaged the Actual Ready Trays must be 0.", tMsg);
            await AssertNothingApprovedAsync(env, c);

            // the ordinary approval: 15 of 20 trays = 360 ready, 120 wastage with its chosen reason -- exactly as before
            var ok = await DamagePageAsync(env, SowingSupervisor, c.Sowing, damaged: false, reason: null, trays: 15);
            ok.WastageReason = "Poor growth";
            Assert.IsType<RedirectToPageResult>(await ok.OnPostAsync());
            var approval = (await repo.GetBySeedSowingIdAsync(c.Sowing)).Single();
            Assert.Equal((15, 360m, 120m, "Poor growth", "e2e remarks"),
                (approval.ActualTrayQuantity!.Value, approval.ConfirmedQuantity, approval.WastageQuantity, approval.WastageReason, approval.Remarks));
            Assert.Equal(360m, await BatchQuantityAsync(env, c.Sowing));
            Assert.Equal(1, await BatchLedgerRowsAsync(env, c.Sowing));
            Assert.Equal("Completed", await SowingStatusAsync(env, c.Sowing));
            await AssertInventoryUnchangedAsync(env, c);
        }

        // ---- 7: the reason is required -----------------------------------------------------------------------------------

        [SkippableFact]
        public async Task MissingDamageReason_IsRefused_NothingChanges()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.AllSeedlingsDamagedProbeSql);
            var c = await DamageSowingAsync(env);
            foreach (var blank in new[] { null, "", "   " })
            {
                var page = await DamagePageAsync(env, SowingSupervisor, c.Sowing, damaged: true, reason: blank);
                Assert.IsType<PageResult>(await page.OnPostAsync());
                Assert.Contains(page.ModelState.Values.SelectMany(v => v.Errors), e => e.ErrorMessage == AllSeedlingsDamagedRules.ReasonRequiredMessage);
                var (ok, msg, _) = await ApproveDamagedAsync(env, c.Sowing, reason: blank);
                Assert.False(ok);
                Assert.Equal(AllSeedlingsDamagedRules.ReasonRequiredMessage, msg);
            }
            await AssertNothingApprovedAsync(env, c);
        }

        // ---- 8: only the user who may approve the sowing ----------------------------------------------------------------

        [SkippableFact]
        public async Task Unauthorized_CannotUseTheOption_NothingChanges()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.AllSeedlingsDamagedProbeSql);
            var c = await DamageSowingAsync(env);
            // the sowing's recorder (user 21) is never its approver: the page refuses, the repository refuses under lock
            var page = await DamagePageAsync(env, Recorder, c.Sowing, damaged: true, reason: DamageReason);
            Assert.IsType<RedirectToPageResult>(await page.OnPostAsync());
            Assert.False(string.IsNullOrWhiteSpace(page.TempData["Error"] as string));
            var (ok, msg, _) = await ApproveDamagedAsync(env, c.Sowing, user: Recorder);
            Assert.False(ok);
            Assert.False(string.IsNullOrWhiteSpace(msg));
            var (anonOk, _, _) = await ApproveDamagedAsync(env, c.Sowing, user: null);
            Assert.False(anonOk);
            await AssertNothingApprovedAsync(env, c);
        }

        // ---- 9: duplicate and concurrent submissions --------------------------------------------------------------------

        [SkippableFact]
        public async Task ConcurrentAndRepeated_AllDamagedApprovals_RecordOnce()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.AllSeedlingsDamagedProbeSql);
            var c = await DamageSowingAsync(env);
            var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ApproveDamagedAsync(env, c.Sowing)));
            Assert.Equal(1, results.Count(r => r.Success));
            var (againOk, againMsg, _) = await ApproveDamagedAsync(env, c.Sowing);
            Assert.False(againOk);
            Assert.Contains("Completed", againMsg);
            Assert.Equal(1, await env.ApprovalsOfAsync(c.Sowing));
            Assert.Equal(480m, await env.ScalarAsync<decimal>("SELECT WastageQuantity FROM dbo.SeedSowings WHERE Id = @S", ("@S", c.Sowing)));
            Assert.Equal(0m, await BatchQuantityAsync(env, c.Sowing));
            Assert.Equal(0, await BatchLedgerRowsAsync(env, c.Sowing));
            await AssertInventoryUnchangedAsync(env, c);
        }

        // ---- 10: a database failure rolls the whole approval back --------------------------------------------------------

        [SkippableFact]
        public async Task DatabaseFailure_DuringAllDamagedApproval_RollsEverythingBack()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.AllSeedlingsDamagedProbeSql);
            var c = await DamageSowingAsync(env);
            // scratch copy only: fail the LAST write (closing this sowing) -- after the Ready Stock row and the approval row
            await env.ScalarAsync<int>($@"EXEC (N'CREATE TRIGGER dbo.TR_ZZTEST_AllDamagedFailure ON dbo.SeedSowings AFTER UPDATE AS
BEGIN
    IF EXISTS (SELECT 1 FROM inserted WHERE Id = {c.Sowing} AND Status = N''Completed'')
        THROW 59032, ''e2e: simulated failure'', 1;
END'); SELECT 1");
            try
            {
                var (ok, _, _) = await ApproveDamagedAsync(env, c.Sowing);
                Assert.False(ok);
            }
            finally
            {
                await env.ScalarAsync<int>("DROP TRIGGER IF EXISTS dbo.TR_ZZTEST_AllDamagedFailure; SELECT 1");
            }
            await AssertNothingApprovedAsync(env, c);
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStock WHERE SeedSowingId = @S", ("@S", c.Sowing)));

            var (retryOk, retryMsg, _) = await ApproveDamagedAsync(env, c.Sowing);
            Assert.True(retryOk, retryMsg);
            Assert.Equal("Completed", await SowingStatusAsync(env, c.Sowing));
        }

        // ---- 12: cancelling the zero approval, then the sowing ----------------------------------------------------------

        [SkippableFact]
        public async Task CancelZeroApproval_ReopensTheSowing_ThenSowingCancelReturnsSeedsAndTrays()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.AllSeedlingsDamagedProbeSql);
            var c = await DamageSowingAsync(env);
            var repo = env.Get<ReadyConfirmationRepository>();
            var (ok, msg, approval) = await ApproveDamagedAsync(env, c.Sowing);
            Assert.True(ok, msg);

            // the assigned supervisor's cancel: nothing to remove from Ready Stock; the sowing re-opens with 0 / 0
            var (cOk, cMsg) = await repo.CancelAsync(approval, "dmg-test", SowingSupervisor);
            Assert.True(cOk, cMsg);
            Assert.Equal("Cancelled", await env.ScalarAsync<string>("SELECT Status FROM dbo.ReadyConfirmations WHERE Id = @A", ("@A", approval)));
            Assert.Equal("Sown", await SowingStatusAsync(env, c.Sowing));
            Assert.Equal(0m, await env.ScalarAsync<decimal>("SELECT ConfirmedReadyQuantity + WastageQuantity FROM dbo.SeedSowings WHERE Id = @S", ("@S", c.Sowing)));
            Assert.Equal(0, await BatchLedgerRowsAsync(env, c.Sowing));
            await AssertInventoryUnchangedAsync(env, c);
            // a cancelled damaged approval is no longer in Wasted Stock
            var code = await env.ScalarAsync<string>("SELECT SowingCode FROM dbo.SeedSowings WHERE Id = @S", ("@S", c.Sowing));
            Assert.DoesNotContain(await env.Get<WastageRepository>().GetAsync(AppClock.Today.AddDays(-1), AppClock.Today.AddDays(1), MainArea), r => r.Reference == code);

            // the sowing can now be cancelled (no active approval): every seed returns to the lot, the trays to the pool
            var (sOk, sMsg) = await env.Get<SeedSowingRepository>().CancelAsync(c.Sowing, "dmg-test", Recorder);
            Assert.True(sOk, sMsg);
            Assert.Equal(100000m, await env.ScalarAsync<decimal>("SELECT PhysicalQuantity FROM dbo.SeedStock WHERE Id = @L", ("@L", c.SeedLot)));
            Assert.Equal(c.Trays0 + 20m, (await DamageBalancesAsync(env, c.SeedLot)).Trays);
        }

        // ---- the database itself only accepts the one shape ------------------------------------------------------------

        [SkippableFact]
        public async Task Database_RefusesZeroTraysWithReadySeedlingsOrAnotherReason()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.AllSeedlingsDamagedProbeSql);
            var c = await DamageSowingAsync(env);
            var (ok, msg, approval) = await ApproveDamagedAsync(env, c.Sowing);
            Assert.True(ok, msg);
            var (cOk, cMsg) = await env.Get<ReadyConfirmationRepository>().CancelAsync(approval, "dmg-test", SowingSupervisor);
            Assert.True(cOk, cMsg);
            var batch = await env.ReadyStockOfAsync(c.Sowing);
            foreach (var (confirmed, wastage, reason) in new (int, int, string?)[] { (24, 456, "Damaged plants"), (0, 480, "Disease"), (0, 480, null) })
            {
                await Assert.ThrowsAsync<SqlException>(() => env.ScalarAsync<int>(@"
INSERT INTO dbo.ReadyConfirmations (ConfirmationCode, SeedSowingId, ReadyStockId, ActualTrayQuantity, ConfirmedQuantity, WastageQuantity, WastageReason,
 ApprovedById, ConfirmationDate, Status, CreatedDate, CreatedBy)
VALUES (N'ZZDMG-' + LEFT(CONVERT(NVARCHAR(36), NEWID()), 8), @S, @B, 0, @C, @W, @R, @U, SYSUTCDATETIME(), N'Confirmed', SYSUTCDATETIME(), N'dmg-test'); SELECT 1",
                    ("@S", c.Sowing), ("@B", batch), ("@C", confirmed), ("@W", wastage), ("@R", reason), ("@U", SowingSupervisor)));
            }
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyConfirmations WHERE SeedSowingId = @S AND Status = N'Confirmed'", ("@S", c.Sowing)));
        }

        // ---- without the migration: not offered, refused, nothing changes ------------------------------------------------

        [SkippableFact]
        public async Task WithoutTheMigration_AllDamagedIsNotOfferedAndRefused_NothingChanges()
        {
            var env = await OpenAsync();
            Skip.If(await env.ScalarAsync<int>(WorkflowCorrectionsSchema.AllSeedlingsDamagedProbeSql) == 1,
                "This test needs a scratch copy WITHOUT 2026-10-31_AllSeedlingsDamagedApproval.sql.");
            var c = await DamageSowingAsync(env);
            var get = env.Page<ConfirmPage>(await env.LoginAsync(SowingSupervisor));
            Assert.IsType<PageResult>(await get.OnGetAsync(c.Sowing));
            Assert.False(get.AllSeedlingsDamagedAvailable);
            var (ok, msg, _) = await ApproveDamagedAsync(env, c.Sowing);
            Assert.False(ok);
            Assert.Equal(AllSeedlingsDamagedRules.NotAvailableMessage, msg);
            var page = await DamagePageAsync(env, SowingSupervisor, c.Sowing, damaged: true, reason: DamageReason);
            Assert.IsType<PageResult>(await page.OnPostAsync());
            Assert.Contains(page.ModelState.Values.SelectMany(v => v.Errors), e => e.ErrorMessage == AllSeedlingsDamagedRules.NotAvailableMessage);
            await AssertNothingApprovedAsync(env, c);
        }
    }
}
