using PlantStockManager.Data;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // 2026-10-31: "All Seedlings Damaged" for a CUTTING tray sowing (Mother Plant Area, 150 Cavity), on a scratch copy with
    // 2026-10-31_CuttingPartialTraySowing.sql AND 2026-10-31_AllSeedlingsDamagedApproval.sql (SKIPPED otherwise). The sowing's
    // own cuttings and trays were consumed when it was recorded; the zero approval takes no extra cuttings / trays and returns
    // none, adds nothing to Ready Stock, records every cutting still expected as damaged wastage and closes the sowing.
    public partial class PartialTrayCuttingSowingE2ETests
    {
        private static async Task<Env> OpenDamagedAsync()
        {
            var env = await OpenAsync();
            Skip.If(await env.ScalarAsync<int>(WorkflowCorrectionsSchema.AllSeedlingsDamagedProbeSql) != 1,
                "2026-10-31_AllSeedlingsDamagedApproval.sql is not applied to this scratch copy.");
            return env;
        }

        private static Task<(bool Success, string? Message, int Id)> ApproveDamagedAsync(Env env, int sowing, int? userId = null, string? reason = "Frost")
            => env.Get<ReadyConfirmationRepository>().ConfirmAsync(sowing, 0, null, null, "e2e", "e2e-approver", userId ?? env.Supervisor, _ => true,
                allDamaged: true, damageReason: reason);

        [SkippableTheory]
        [InlineData(300, 2)]   // two complete trays
        [InlineData(62, 1)]    // one partial tray
        public async Task AllDamaged_CuttingSowing_ZeroReady_AllWasted_NoExtraOrReturn_Completed(int cuttings, int trays)
        {
            var env = await OpenDamagedAsync();
            var pool = await env.ResetPoolAsync(500);
            var traysBefore = await env.TraysAsync(C150);
            var (ok, message, sowing) = await SowAsync(env, Entry(env, pool, cuttings));
            Assert.True(ok, message);
            Assert.Equal(500m - cuttings, await env.PoolAsync(pool));
            Assert.Equal(traysBefore - trays, await env.TraysAsync(C150));
            var ledgerRows = await env.CuttingLedgerRowsAsync(pool);

            var (aOk, aMessage, approval) = await ApproveDamagedAsync(env, sowing);
            Assert.True(aOk, aMessage);

            var row = (await env.Get<ReadyConfirmationRepository>().GetBySeedSowingIdAsync(sowing)).Single();
            Assert.Equal((0, 0m, (decimal)cuttings, "Damaged plants", "All seedlings damaged: Frost | e2e"),
                (row.ActualTrayQuantity!.Value, row.ConfirmedQuantity, row.WastageQuantity, row.WastageReason, row.Remarks));
            Assert.Equal(0m, await env.ReadyStockAsync(sowing));
            Assert.Equal(0, await env.ScalarAsync<int>(@"SELECT COUNT(*) FROM dbo.ReadyStockTransactions t
                JOIN dbo.ReadyStock rs ON rs.Id = t.ReadyStockId WHERE rs.SeedSowingId = @S", ("@S", sowing)));
            Assert.Equal("Completed", await env.ScalarAsync<string>("SELECT Status FROM dbo.SeedSowings WHERE Id = @S", ("@S", sowing)));
            Assert.Equal((decimal)cuttings, await env.ScalarAsync<decimal>("SELECT WastageQuantity FROM dbo.SeedSowings WHERE Id = @S", ("@S", sowing)));

            // Cutting Stock and trays exactly as after the sowing: no extra taken, nothing returned, no new ledger rows
            Assert.Equal(500m - cuttings, await env.PoolAsync(pool));
            Assert.Equal(ledgerRows, await env.CuttingLedgerRowsAsync(pool));
            Assert.Equal(0m, await env.CuttingLedgerAsync(pool, "Sown", "ReadyConfirmation", approval));
            Assert.Equal(traysBefore - trays, await env.TraysAsync(C150));
            Assert.Equal(0m, await env.TrayLedgerAsync("ReadyConfirmation", approval));
        }

        [SkippableFact]
        public async Task AllDamaged_CuttingSowing_Unauthorized_OrNoReason_Refused_NothingChanges()
        {
            var env = await OpenDamagedAsync();
            var pool = await env.ResetPoolAsync(500);
            var (ok, message, sowing) = await SowAsync(env, Entry(env, pool, 300));
            Assert.True(ok, message);
            Assert.False((await ApproveDamagedAsync(env, sowing, userId: env.OtherUser)).Success);
            var (nOk, nMessage, _) = await ApproveDamagedAsync(env, sowing, reason: " ");
            Assert.False(nOk);
            Assert.Equal(AllSeedlingsDamagedRules.ReasonRequiredMessage, nMessage);
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyConfirmations WHERE SeedSowingId = @S", ("@S", sowing)));
            Assert.Equal("Sown", await env.ScalarAsync<string>("SELECT Status FROM dbo.SeedSowings WHERE Id = @S", ("@S", sowing)));
            Assert.Equal(200m, await env.PoolAsync(pool));
        }

        [SkippableFact]
        public async Task AllDamaged_CuttingSowing_CancelApprovalThenSowing_ReturnsTheSowingsOwnCuttingsAndTrays()
        {
            var env = await OpenDamagedAsync();
            var pool = await env.ResetPoolAsync(500);
            var traysBefore = await env.TraysAsync(C150);
            var (ok, message, sowing) = await SowAsync(env, Entry(env, pool, 300));
            Assert.True(ok, message);
            var (aOk, aMessage, approval) = await ApproveDamagedAsync(env, sowing);
            Assert.True(aOk, aMessage);
            var (cOk, cMessage) = await env.Get<ReadyConfirmationRepository>().CancelAsync(approval, "e2e", env.Supervisor);
            Assert.True(cOk, cMessage);
            Assert.Equal(200m, await env.PoolAsync(pool));   // cancelling the approval returns nothing (it took nothing)
            var (sOk, sMessage) = await env.Get<SeedSowingRepository>().CancelAsync(sowing, "e2e", env.Supervisor);
            Assert.True(sOk, sMessage);
            Assert.Equal(500m, await env.PoolAsync(pool));
            Assert.Equal(traysBefore, await env.TraysAsync(C150));
        }
    }
}
