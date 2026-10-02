using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Cutting -> Tray/Cavity sowing: the same complete-tray rule as seeds.
    //   Complete Trays  = FLOOR(Cutting Quantity / Cavity)
    //   Used Cutting    = Complete Trays x Cavity
    //   Remaining       = Cutting Quantity - Used
    //
    // PlanSowing itself is pure arithmetic -- it computes Trays/Used/Remaining
    // and previews the stock balance right after the 'Sown' deduction only
    // (AvailableAfter below), exactly as it already does for Direct Seed
    // Sowing (see SeedDeductionTests.cs's own identical "remainder is still
    // in the lot [at THIS point]" assertions). It does not decide what
    // happens to the remainder next -- that is the REPOSITORY's job.
    // Business rule (this change): SeedSowingRepository.InsertFromCuttingAsync
    // now wastes that remainder automatically, in the same database
    // transaction as the sowing, mirroring InsertAsync's already-approved
    // Direct Seed Sowing rule exactly -- it is NEVER left in or returned to
    // Cutting Stock. See CuttingSowingOverageE2ETests.cs for the end-to-end
    // (real database) proof of that behavior; this class only covers the
    // pure calculation.
    public class CuttingTraySowingTests
    {
        private const string Label = DirectSowingRules.CuttingQuantityLabel;

        [Theory]
        [InlineData(1000, "9 Cavity", 111, 999, 1)]
        [InlineData(1000, "24 Cavity", 41, 984, 16)]
        [InlineData(1000, "42 Cavity", 23, 966, 34)]
        [InlineData(1000, "102 Cavity", 9, 918, 82)]
        [InlineData(1000, "150 Cavity", 6, 900, 100)]
        [InlineData(14492, "102 Cavity", 142, 14484, 8)]
        public void OnlyCompleteTrays_PlanSowingComputesTraysUsedAndRemainder(decimal qty, string cavity, int trays, decimal used, decimal remaining)
        {
            var r = DirectSowingRules.PlanSowing(qty, cavity, physical: 20000, inTransit: 0, Label, "Cutting Stock");
            Assert.True(r.Ok, r.Error);
            Assert.Equal(trays, r.Trays);
            Assert.Equal(used, r.SeedsUsed);
            Assert.Equal(remaining, r.RemainingSeeds);
            // AvailableAfter previews the balance right after the 'Sown' entry
            // ONLY -- the repository's separate 'Wastage' entry for the
            // remainder (see class comment above) is not part of this preview,
            // exactly like Direct Seed Sowing's own PlanSowing preview.
            Assert.Equal(20000 - used, r.AvailableAfter);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-10)]
        [InlineData(500.5)]
        public void ZeroNegativeFractional_AreRefused(decimal qty)
        {
            var r = DirectSowingRules.PlanSowing(qty, "24 Cavity", 20000, 0, Label, "Cutting Stock");
            Assert.False(r.Ok);
            Assert.StartsWith("Cutting Quantity", r.Error);
        }

        [Fact]
        public void LessThanOneTray_IsRefused()
        {
            var r = DirectSowingRules.PlanSowing(23, "24 Cavity", 20000, 0, Label, "Cutting Stock");
            Assert.False(r.Ok);
            Assert.Contains("less than one complete 24-cavity tray", r.Error);
        }

        [Theory]
        [InlineData("10 Cavity")]
        [InlineData("24")]
        [InlineData(null)]
        public void InvalidCavity_IsRefused(string? cavity)
            => Assert.False(DirectSowingRules.PlanSowing(1000, cavity, 20000, 0, Label, "Cutting Stock").Ok);

        [Fact]
        public void MoreThanAvailableCuttingStock_IsRefused()
        {
            // 300 physical, 100 already on the way to Main Office -> 200 available.
            var r = DirectSowingRules.PlanSowing(240, "24 Cavity", physical: 300, inTransit: 100, Label, "Cutting Stock");
            Assert.False(r.Ok);
            Assert.Contains("Insufficient Cutting Stock", r.Error);
        }

        [Fact]
        public void Approval_UsesTheSameTrayRule_ForCuttingSowings()
        {
            // 1000 cuttings, 24 Cavity -> 41 trays / 984 used; 35 ready trays.
            var r = DirectSowingRules.ComputeTrayApproval(984, 41, "24 Cavity", 0, 0, 35, "Poor growth");
            Assert.True(r.Ok, r.Error);
            Assert.Equal(840, r.ActualSeedlings);   // 35 x 24
            Assert.Equal(144, r.Wastage);           // 984 used - 840 ready. (The 16 sub-tray leftover cuttings from the
                                                     // ORIGINAL 1000-cutting sowing are a SEPARATE, already-recorded
                                                     // 'Wastage' ledger entry made automatically at sowing time -- not
                                                     // part of this approval-time wastage figure, and not double-counted.)
        }
    }
}
