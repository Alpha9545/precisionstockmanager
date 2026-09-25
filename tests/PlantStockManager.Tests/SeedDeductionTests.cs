using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Direct Sowing seed consumption (DirectSowingRules.PlanSowing):
    // only the seeds that fill COMPLETE trays are sown and deducted; the
    // remaining seeds stay in the Main Office lot.
    //   SeedSowings.QuantitySown  = SeedsUsed      = Trays x tray size
    //   'Sown' ledger entry       = -SeedsUsed
    //   lot available afterwards  = available - SeedsUsed
    public class SeedDeductionTests
    {
        [Fact]
        public void OnlySeedsUsed_AreDeducted_RemainderStaysInStock()
        {
            // 20,000 in the lot; operator enters 14,492 seeds, 102-cavity trays.
            var r = DirectSowingRules.PlanSowing(seedQuantity: 14492, cavityType: "102 Cavity", physical: 20000, inTransit: 0);
            Assert.True(r.Ok, r.Error);
            Assert.Equal(142, r.Trays);
            Assert.Equal(14484, r.SeedsUsed);        // deducted, and stored as QuantitySown
            Assert.Equal(8, r.RemainingSeeds);       // NOT deducted
            Assert.Equal(5516, r.AvailableAfter);    // 20,000 - 14,484 (not 20,000 - 14,492 = 5,508)
        }

        [Theory]
        [InlineData(14492, "102 Cavity", 20000, 0)]
        [InlineData(100, "9 Cavity", 100, 0)]
        [InlineData(299, "150 Cavity", 1000, 200)]
        [InlineData(4000, "24 Cavity", 5000, 0)]
        [InlineData(126, "42 Cavity", 126, 0)]
        public void LedgerStockAndSowing_AreMathematicallyConsistent(decimal seedQuantity, string cavityType, decimal physical, decimal inTransit)
        {
            var r = DirectSowingRules.PlanSowing(seedQuantity, cavityType, physical, inTransit);
            Assert.True(r.Ok, r.Error);
            var cavity = DirectSowingRules.CavityCount(cavityType)!.Value;

            var sownLedgerEntry = -r.SeedsUsed;                        // 'Sown' SeedStockTransactions.Quantity
            var quantitySown = r.SeedsUsed;                            // SeedSowings.QuantitySown
            Assert.Equal(r.Trays * cavity, quantitySown);              // sowing = complete trays only
            Assert.Equal(seedQuantity, r.SeedsUsed + r.RemainingSeeds);// nothing lost
            Assert.Equal(physical - inTransit + sownLedgerEntry, r.AvailableAfter);   // stock balance = before + ledger
            Assert.True(r.RemainingSeeds >= 0 && r.RemainingSeeds < cavity);
            Assert.True(r.AvailableAfter >= r.RemainingSeeds);        // the remainder is still in the lot
        }

        [Fact]
        public void EnteredQuantity_MustBeAvailable_EvenThoughOnlySeedsUsedAreDeducted()
        {
            // 14,490 available: the operator cannot claim 14,492 seeds from this lot.
            var r = DirectSowingRules.PlanSowing(14492, "102 Cavity", 14490, 0);
            Assert.False(r.Ok);
            Assert.Contains("Insufficient", r.Error);
        }

        [Fact]
        public void SeedsInTransit_AreNotAvailable()
            => Assert.False(DirectSowingRules.PlanSowing(1000, "9 Cavity", 1000, 1).Ok);

        [Theory]
        [InlineData(1449.2)]
        [InlineData(14492.5)]
        [InlineData(0.5)]
        public void FractionalSeedQuantity_IsRejected(decimal seedQuantity)
        {
            var r = DirectSowingRules.PlanSowing(seedQuantity, "9 Cavity", 100000, 0);
            Assert.False(r.Ok);
            Assert.Equal("Seed Quantity must be a whole number.", r.Error);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-9)]
        public void ZeroOrNegativeSeedQuantity_IsRejected(decimal seedQuantity)
            => Assert.False(DirectSowingRules.PlanSowing(seedQuantity, "9 Cavity", 100000, 0).Ok);

        [Fact]
        public void LessThanOneTray_IsRejected_AndNothingIsDeducted()
        {
            var r = DirectSowingRules.PlanSowing(101, "102 Cavity", 1000, 0);
            Assert.False(r.Ok);
            Assert.Equal(0, r.SeedsUsed);
            Assert.Equal(1000, r.AvailableAfter);
        }

        [Fact]
        public void InvalidTraySize_IsRejected()
            => Assert.False(DirectSowingRules.PlanSowing(1000, "10 Cavity", 5000, 0).Ok);

        [Fact]
        public void ExactDivision_DeductsTheFullQuantity()
        {
            var r = DirectSowingRules.PlanSowing(14484, "102 Cavity", 20000, 0);
            Assert.True(r.Ok);
            Assert.Equal(14484, r.SeedsUsed);
            Assert.Equal(0, r.RemainingSeeds);
        }
    }
}
