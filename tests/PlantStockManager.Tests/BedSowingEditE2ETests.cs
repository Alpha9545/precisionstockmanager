using Microsoft.Data.SqlClient;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using EditValues = PlantStockManager.Services.BedSowingRules.BedEditValues;

namespace PlantStockManager.Tests
{
    // 2026-10-22: Bed Sowing Edit, end to end on a SCRATCH COPY only (same fixtures and safety rules as BedSowingE2ETests;
    // SKIPPED without 2026-10-22_BedSowingEdit.sql). Every test sows its own variety and lot, and checks the seed ledger
    // rows of its own bed: the original deduction reversed at its own instant, the revised one recorded once, and the
    // database trigger refusing anything that is not reconciled.
    public partial class BedSowingE2ETests
    {
        private static async Task<Env> OpenEditAsync()
        {
            var env = await OpenAsync();
            Skip.If(await env.ScalarAsync<int>(WorkflowCorrectionsSchema.BedSowingEditProbeSql) != 1,
                "2026-10-22_BedSowingEdit.sql is not applied to this scratch database.");
            return env;
        }

        private static BedSowingRepository.Actor Editor(Env env) => new(env.Supervisor, "bed-edit-test", false);

        private static EditValues Values(BedSowing b) => new(b.SowingDate, b.SpeciesId, b.SeedStockId, b.Quantity, b.Remarks);

        private static Task<(bool Success, string? Message, bool Changed)> EditAsync(Env env, BedSowing current, EditValues revised,
            string? reason = "bed edit test", Func<int, bool>? access = null)
            => env.Beds.EditAsync(new BedSowingRepository.BedEditRequest(current.Id, Values(current), revised, reason), Editor(env), access ?? Any);

        private sealed record LedgerRow(int Lot, string Type, decimal Quantity, DateTime AtUtc);

        private static async Task<List<LedgerRow>> BedLedgerAsync(Env env, int bedId)
        {
            var rows = new List<LedgerRow>();
            using var conn = new SqlConnection(env.ConnectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"SELECT SeedStockId, TransactionType, Quantity, TransactionDate FROM dbo.SeedStockTransactions
WHERE ReferenceType = N'BedSowing' AND ReferenceId = @B ORDER BY TransactionDate, Id", conn);
            cmd.Parameters.AddWithValue("@B", bedId);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) rows.Add(new LedgerRow(r.GetInt32(0), r.GetString(1), r.GetDecimal(2), r.GetDateTime(3)));
            return rows;
        }

        // ---- the edit itself --------------------------------------------------------------------------------------

        [SkippableFact]
        public async Task Edit_QuantityAndDate_SameBedSameCode_OriginalReversedAtItsInstant_RevisedDeductedOnce_Audited()
        {
            var env = await OpenEditAsync();
            var v = await NewVarietyAsync(env, seeds: 5000);
            var bed = await SowAsync(env, v, qty: 1000, daysAgo: 12);
            var before = await BedAsync(env, bed.Id);
            var original = Assert.Single(await BedLedgerAsync(env, bed.Id));

            var revised = Values(before) with { Quantity = 900, SowingDate = AppClock.Today.AddDays(-10), Remarks = "corrected count" };
            var (ok, msg, changed) = await EditAsync(env, before, revised, "counted again");
            Assert.True(ok, msg);
            Assert.True(changed);

            var after = await BedAsync(env, bed.Id);
            Assert.Equal((before.Id, before.BedSowingCode, before.CreatedById, before.CreatedDate, before.PolyhouseId),
                (after.Id, after.BedSowingCode, after.CreatedById, after.CreatedDate, after.PolyhouseId));   // same record
            Assert.Equal((900m, AppClock.Today.AddDays(-10), "corrected count", BedSowing.StatusPending),
                (after.Quantity, after.SowingDate, after.Remarks, after.Status));
            Assert.Equal(5000m - 900m, await env.LotAsync(v.SeedStockId));

            var ledger = await BedLedgerAsync(env, bed.Id);
            Assert.Equal(3, ledger.Count);                                                         // original, reversal, revised
            var reversal = Assert.Single(ledger, x => x.Type == "ReversalReturn");
            Assert.Equal((original.Lot, 1000m, original.AtUtc.AddMilliseconds(-10)), (reversal.Lot, reversal.Quantity, reversal.AtUtc));   // at the ORIGINAL instant (10 ms before it)
            var revisedRow = Assert.Single(ledger, x => x.Type == "Sown" && x.Quantity == -900m);
            Assert.Equal(AppClock.Today.AddDays(-10), AppClock.ToIndiaTime(revisedRow.AtUtc).Date);  // at the new sowing date
            Assert.Equal(-900m, ledger.Sum(x => x.Quantity));
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.BedSowingAuditLog WHERE BedSowingId = @B AND Action = N'Edit' AND Reason = N'counted again'", ("@B", bed.Id)));
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.BedSowings WHERE SubmissionToken = @T AND Id <> @B", ("@T", bed.SubmissionToken), ("@B", bed.Id)));
        }

        [SkippableFact]
        public async Task Edit_VarietyAndLot_MovesTheDeductionToTheNewLot_OldLotRestored_SameDateKeepsTheOriginalInstant()
        {
            var env = await OpenEditAsync();
            var v1 = await NewVarietyAsync(env, seeds: 3000);
            var v2 = await NewVarietyAsync(env, seeds: 4000);
            var bed = await SowAsync(env, v1, qty: 1000, daysAgo: 8);
            var before = await BedAsync(env, bed.Id);
            var originalAt = Assert.Single(await BedLedgerAsync(env, bed.Id)).AtUtc;

            var (ok, msg, _) = await EditAsync(env, before, Values(before) with { SpeciesId = v2.SpeciesId, SeedStockId = v2.SeedStockId, Quantity = 1200 });
            Assert.True(ok, msg);

            var after = await BedAsync(env, bed.Id);
            Assert.Equal((v2.SpeciesId, v2.SeedStockId, 1200m), (after.SpeciesId, after.SeedStockId, after.Quantity));
            Assert.Equal(await env.ScalarAsync<string>("SELECT BatchNo FROM dbo.SeedStock WHERE Id = @L", ("@L", v2.SeedStockId)), after.SeedLotNo);
            Assert.Equal(3000m, await env.LotAsync(v1.SeedStockId));                 // fully restored
            Assert.Equal(4000m - 1200m, await env.LotAsync(v2.SeedStockId));
            var ledger = await BedLedgerAsync(env, bed.Id);
            Assert.Equal(0m, ledger.Where(x => x.Lot == v1.SeedStockId).Sum(x => x.Quantity));
            Assert.Equal(-1200m, ledger.Where(x => x.Lot == v2.SeedStockId).Sum(x => x.Quantity));
            Assert.All(ledger, x => Assert.Equal(originalAt, x.Type == "ReversalReturn" ? x.AtUtc.AddMilliseconds(10) : x.AtUtc));   // date unchanged -> the original instant throughout
        }

        [SkippableFact]
        public async Task Edit_RemarksOnly_NoLedgerRows_NoChanges_WritesNothing()
        {
            var env = await OpenEditAsync();
            var v = await NewVarietyAsync(env, seeds: 2000);
            var bed = await BedAsync(env, (await SowAsync(env, v, qty: 500)).Id);

            var (ok, msg, changed) = await EditAsync(env, bed, Values(bed) with { Remarks = "only the note" }, reason: null);   // no reason needed
            Assert.True(ok, msg);
            Assert.True(changed);
            Assert.Single(await BedLedgerAsync(env, bed.Id));
            Assert.Equal("only the note", (await BedAsync(env, bed.Id)).Remarks);

            var now = await BedAsync(env, bed.Id);
            var (ok2, msg2, changed2) = await EditAsync(env, now, Values(now));
            Assert.True(ok2);
            Assert.False(changed2);
            Assert.Equal(BedSowingRules.EditNoChangesMessage, msg2);
            Assert.Single(await BedLedgerAsync(env, bed.Id));
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.BedSowingAuditLog WHERE BedSowingId = @B AND Action = N'Edit'", ("@B", bed.Id)));
        }

        [SkippableFact]
        public async Task Edit_Twice_DateMovedEarlierThenQuantity_SecondEditReversesTheFirstEditsDeduction()
        {
            var env = await OpenEditAsync();
            var v = await NewVarietyAsync(env, seeds: 6000);
            var bed = await BedAsync(env, (await SowAsync(env, v, qty: 1000, daysAgo: 3)).Id);
            var (ok1, msg1, _) = await EditAsync(env, bed, Values(bed) with { SowingDate = AppClock.Today.AddDays(-9) });   // earlier
            Assert.True(ok1, msg1);
            var first = await BedAsync(env, bed.Id);
            var firstSown = (await BedLedgerAsync(env, bed.Id)).Where(x => x.Type == "Sown").OrderBy(x => x.AtUtc).First();   // the revised, earlier row
            Assert.Equal(AppClock.Today.AddDays(-9), AppClock.ToIndiaTime(firstSown.AtUtc).Date);

            var (ok2, msg2, _) = await EditAsync(env, first, Values(first) with { Quantity = 1500 });
            Assert.True(ok2, msg2);
            var ledger = await BedLedgerAsync(env, bed.Id);
            Assert.Equal(5, ledger.Count);                                           // sown, reversal, sown, reversal, sown
            Assert.Equal(-1500m, ledger.Sum(x => x.Quantity));
            // the second reversal targets the FIRST EDIT's deduction (10 ms before it), not the original one
            Assert.Contains(ledger, x => x.Type == "ReversalReturn" && x.Quantity == 1000m && x.AtUtc == firstSown.AtUtc.AddMilliseconds(-10));
            Assert.Equal(6000m - 1500m, await env.LotAsync(v.SeedStockId));
            // no instant in the lot's history goes negative
            Assert.Equal(0, await env.ScalarAsync<int>(@"SELECT COUNT(*) FROM (SELECT SUM(Quantity) OVER (ORDER BY TransactionDate, Id ROWS UNBOUNDED PRECEDING) r
FROM dbo.SeedStockTransactions WHERE SeedStockId = @L) x
WHERE x.r + (SELECT PhysicalQuantity FROM dbo.SeedStock WHERE Id = @L) - (SELECT SUM(Quantity) FROM dbo.SeedStockTransactions WHERE SeedStockId = @L) < 0",
                ("@L", v.SeedStockId)));   // opening balance = physical - every ledger row; no running balance below 0
        }

        // ---- never twice, never stale ------------------------------------------------------------------------------

        [SkippableFact]
        public async Task Edit_RepeatedOrConcurrentSubmits_AppliedExactlyOnce()
        {
            var env = await OpenEditAsync();
            var v = await NewVarietyAsync(env, seeds: 9000);
            var bed = await BedAsync(env, (await SowAsync(env, v, qty: 1000)).Id);
            var revised = Values(bed) with { Quantity = 1500 };

            var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => EditAsync(env, bed, revised)));
            Assert.Single(results, r => r.Success && r.Changed);
            Assert.All(results.Where(r => !r.Success), r => Assert.Equal(BedSowingRules.EditStaleMessage, r.Message));
            Assert.Equal(-1500m, (await BedLedgerAsync(env, bed.Id)).Sum(x => x.Quantity));
            Assert.Equal(3, (await BedLedgerAsync(env, bed.Id)).Count);
            Assert.Equal(9000m - 1500m, await env.LotAsync(v.SeedStockId));

            // the same (now stale) form submitted again later: refused, nothing more written
            var (again, againMsg, _) = await EditAsync(env, bed, revised);
            Assert.False(again);
            Assert.Equal(BedSowingRules.EditStaleMessage, againMsg);
            Assert.Equal(3, (await BedLedgerAsync(env, bed.Id)).Count);
        }

        // ---- validation: stock history, lot, eligibility, Area -----------------------------------------------------

        [SkippableFact]
        public async Task Edit_RefusedWithoutWriting_WhenStockHistoryLotEligibilityOrAreaDoNotAllowIt()
        {
            var env = await OpenEditAsync();
            var other = await NewVarietyAsync(env, seeds: 5000);
            // a lot whose seed ARRIVED 5 days ago (dated receipt), so the history matters
            var species = (await NewVarietyAsync(env, seeds: 1)).SpeciesId;
            var (rOk, rMsg, lotId) = await env.Get<SeedStockRepository>().ReceiveAsync(species, env.AreaId, "EDIT-" + Guid.NewGuid().ToString("N")[..6], null, "Seeds",
                2000, DateTime.Now.AddDays(-5), "bed edit test lot", env.Supervisor, "bed-edit-test");
            Assert.True(rOk, rMsg);
            var v = new Variety(species, lotId);
            var bed = await BedAsync(env, (await SowAsync(env, v, qty: 1000, daysAgo: 3)).Id);
            async Task Unchanged()
            {
                var now = await BedAsync(env, bed.Id);
                Assert.Equal((1000m, AppClock.Today.AddDays(-3), v.SeedStockId), (now.Quantity, now.SowingDate, now.SeedStockId));
                Assert.Single(await BedLedgerAsync(env, bed.Id));
                Assert.Equal(1000m, await env.LotAsync(lotId));
            }

            Assert.Contains("seeds were in this lot", (await EditAsync(env, bed, Values(bed) with { SowingDate = AppClock.Today.AddDays(-7) })).Message);   // before the seed arrived
            await Unchanged();
            Assert.False((await EditAsync(env, bed, Values(bed) with { Quantity = 2500 })).Success);                    // more than the lot ever held
            await Unchanged();
            Assert.Equal("The selected seed lot does not belong to the selected Variety.",
                (await EditAsync(env, bed, Values(bed) with { SeedStockId = other.SeedStockId })).Message);            // lot of another variety
            await Unchanged();
            Assert.Contains("cannot be in the future", (await EditAsync(env, bed, Values(bed) with { SowingDate = AppClock.Today.AddDays(1) })).Message);
            Assert.Contains("reason is required", (await EditAsync(env, bed, Values(bed) with { Quantity = 900 }, reason: " ")).Message);
            Assert.Equal("You are not authorized to work in this bed's Area.",
                (await EditAsync(env, bed, Values(bed) with { Quantity = 900 }, access: _ => false)).Message);
            await Unchanged();
            // within what the lot held from the new date onward: allowed
            var (wOk, wMsg, _) = await EditAsync(env, bed, Values(bed) with { Quantity = 2000, SowingDate = AppClock.Today.AddDays(-4) });
            Assert.True(wOk, wMsg);
            Assert.Equal(0m, await env.LotAsync(lotId));
        }

        [SkippableFact]
        public async Task Edit_OnlyUntouchedBeds_TransplantedReversedOrCancelledAreRefused_AndTheDatabaseRefusesUnreconciledChanges()
        {
            var env = await OpenEditAsync();
            await StockTraysAsync(env, 40);
            var v = await NewVarietyAsync(env, seeds: 10000);
            var touched = await BedAsync(env, (await SowAsync(env, v, qty: 1000)).Id);
            var (tOk, tMsg, opId, _, _) = await env.Beds.TransplantAsync(Move(env, touched, 240), env.Maya);
            Assert.True(tOk, tMsg);
            touched = await BedAsync(env, touched.Id);
            Assert.Equal(BedSowingRules.EditNotUntouchedMessage, (await EditAsync(env, touched, Values(touched) with { Quantity = 1100 })).Message);
            Assert.True((await env.Beds.ReverseOperationAsync(opId, "bed edit test", env.Maya)).Success);
            touched = await BedAsync(env, touched.Id);
            Assert.Equal(0m, touched.TransplantedQuantity);
            Assert.Equal(BedSowingRules.EditNotUntouchedMessage, (await EditAsync(env, touched, Values(touched) with { Quantity = 1100 })).Message);   // an operation was recorded

            var cancelled = await BedAsync(env, (await SowAsync(env, v, qty: 300)).Id);
            Assert.True((await env.Beds.CancelAsync(cancelled.Id, "bed edit test", Editor(env), Any)).Success);
            cancelled = await BedAsync(env, cancelled.Id);
            Assert.Equal(BedSowingRules.EditCancelledMessage, (await EditAsync(env, cancelled, Values(cancelled) with { Remarks = "x" })).Message);

            // the database itself: no change to what was sown without its ledger reconciled; never on a touched bed;
            // the code stays fixed
            var plain = await BedAsync(env, (await SowAsync(env, v, qty: 400)).Id);
            var e1 = await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync("UPDATE dbo.BedSowings SET Quantity = Quantity - 1 WHERE Id = @B", ("@B", plain.Id)));
            Assert.Equal(52803, e1.Number);
            var e2 = await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync("UPDATE dbo.BedSowings SET SowingDate = DATEADD(DAY, -1, SowingDate) WHERE Id = @B", ("@B", touched.Id)));
            Assert.Equal(52801, e2.Number);
            var e3 = await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync("UPDATE dbo.BedSowings SET BedSowingCode = BedSowingCode + N'X' WHERE Id = @B", ("@B", plain.Id)));
            Assert.Equal(52171, e3.Number);
            await env.ExecAsync("UPDATE dbo.BedSowings SET Remarks = N'remarks stay editable' WHERE Id = @B", ("@B", plain.Id));
        }
    }
}
