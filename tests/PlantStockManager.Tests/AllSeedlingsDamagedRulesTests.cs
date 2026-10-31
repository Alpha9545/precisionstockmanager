using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // 2026-10-31: Services/AllSeedlingsDamagedRules.cs -- pure rules for "All Seedlings Damaged -- Approve with 0 Ready
    // Seedlings" (no database). The database-backed cases are in AllSeedlingsDamagedE2ETests / AllSeedlingsDamagedCuttingE2ETests.
    public class AllSeedlingsDamagedRulesTests
    {
        [Fact]
        public void Compute_AllStillExpectedIsDamagedWastage_ReasonKeptInRemarks()
        {
            var (ok, wastage, pct, remarks, error) = AllSeedlingsDamagedRules.Compute(480, 0, 0, "  Hail storm  ", null);
            Assert.True(ok, error);
            Assert.Equal(480m, wastage);
            Assert.Equal(100m, pct);
            Assert.Equal("All seedlings damaged: Hail storm", remarks);
        }

        [Fact]
        public void Compute_OptionalRemarksAreAppended_NeverReplaceTheReason()
            => Assert.Equal("All seedlings damaged: Frost | moved to compost",
                AllSeedlingsDamagedRules.Compute(300, 0, 0, "Frost", " moved to compost ").Remarks);

        [Fact]
        public void Compute_PartialTrayCuttingSowing_WastageIsItsOwnCuttings()
        {
            var (ok, wastage, _, _, _) = AllSeedlingsDamagedRules.Compute(62, 0, 0, "Rot", null);
            Assert.True(ok);
            Assert.Equal(62m, wastage);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Compute_ReasonIsRequired(string? reason)
        {
            var (ok, _, _, _, error) = AllSeedlingsDamagedRules.Compute(480, 0, 0, reason, "remarks alone are not a reason");
            Assert.False(ok);
            Assert.Equal(AllSeedlingsDamagedRules.ReasonRequiredMessage, error);
        }

        [Fact]
        public void Compute_ReasonTooLong_IsRefused()
            => Assert.False(AllSeedlingsDamagedRules.Compute(480, 0, 0, new string('x', AllSeedlingsDamagedRules.MaxReasonLength + 1), null).Ok);

        [Fact]
        public void Compute_ReasonAndRemarksMustFitTheRemarksColumn()
        {
            var reason = new string('r', AllSeedlingsDamagedRules.MaxReasonLength);
            Assert.True(AllSeedlingsDamagedRules.Compute(480, 0, 0, reason, new string('m', 50)).Ok);
            var (ok, _, _, _, error) = AllSeedlingsDamagedRules.Compute(480, 0, 0, reason, new string('m', 400));
            Assert.False(ok);
            Assert.Contains("at most", error);
        }

        [Theory]
        [InlineData(480, 480, 0)]   // already approved
        [InlineData(480, 0, 480)]   // already all wasted
        [InlineData(480, 500, 0)]   // over-approved
        public void Compute_NothingLeftToApprove_IsRefused(int sown, int ready, int wasted)
        {
            var (ok, _, _, _, error) = AllSeedlingsDamagedRules.Compute(sown, ready, wasted, "Frost", null);
            Assert.False(ok);
            Assert.Equal("This sowing has already been fully approved.", error);
        }

        // The stored reason is the EXISTING closed reason that represents damage -- it stays distinct from the others, and the
        // database CHECK (CK_ReadyConfirmations_WastageReason) accepts it.
        [Fact]
        public void WastageReason_IsTheExistingDamageReason()
        {
            Assert.Equal("Damaged plants", AllSeedlingsDamagedRules.WastageReason);
            Assert.True(DirectSowingRules.IsValidWastageReason(AllSeedlingsDamagedRules.WastageReason));
        }

        [Theory]
        [InlineData(0, true)]
        [InlineData(1, false)]
        [InlineData(null, false)]
        public void IsAllDamagedApproval_OnlyZeroTrays(int? trays, bool expected)
            => Assert.Equal(expected, AllSeedlingsDamagedRules.IsAllDamagedApproval(trays));

        // The ordinary approval rule is NOT weakened: 0 trays is still refused there.
        [Fact]
        public void OrdinaryTrayApproval_StillRefusesZeroTrays()
        {
            var (ok, _, _, _, _, error) = DirectSowingRules.ComputeTrayApproval(480, 20, "24 Cavity", 0, 0, 0, "Damaged plants", "Seed");
            Assert.False(ok);
            Assert.Equal("Actual Ready Trays must be at least 1 complete tray.", error);
            var (pOk, _, _, _, _, _) = DirectSowingRules.ComputeTrayApproval(62, 1, "150 Cavity", 0, 0, 0, "Damaged plants", "Cutting");
            Assert.False(pOk);   // a partial-tray cutting sowing still needs exactly 1
        }

        // Ordinary approval arithmetic unchanged (15 of 20 trays of 24).
        [Fact]
        public void OrdinaryTrayApproval_Unchanged()
        {
            var (ok, trays, seedlings, wastage, _, _) = DirectSowingRules.ComputeTrayApproval(480, 20, "24 Cavity", 0, 0, 15, "Poor growth", "Seed");
            Assert.True(ok);
            Assert.Equal((15, 360m, 120m), (trays, seedlings, wastage));
        }

        // The migration and its rollback exist as a pair, are guarded, and the code probes for exactly what they create.
        [Fact]
        public void Migration_IsPreparedWithRollback_AndMatchesTheProbe()
        {
            var dir = Path.Combine(FindRepoRoot(), "Database", "Migrations");
            var forward = File.ReadAllText(Path.Combine(dir, "2026-10-31_AllSeedlingsDamagedApproval.sql"));
            var rollback = File.ReadAllText(Path.Combine(dir, "2026-10-31_AllSeedlingsDamagedApproval_Rollback.sql"));
            foreach (var script in new[] { forward, rollback })
            {
                Assert.Contains("$(TARGET_DB_CREATED)", script);
                Assert.Contains("N'DRYRUN', N'COMMIT'", script);
            }
            Assert.Contains("AllSeedlingsDamaged", forward);
            Assert.Contains("[WastageReason] IS NOT NULL AND [WastageReason]=N'Damaged plants'", forward);
            Assert.Contains("WHERE ActualTrayQuantity = 0", rollback);
            Assert.Contains("CK_ReadyConfirmations_ActualTrays", PlantStockManager.Data.WorkflowCorrectionsSchema.AllSeedlingsDamagedProbeSql);
        }

        private static string FindRepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PlantStockManager.csproj")))
                d = d.Parent;
            return d?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }
    }
}
