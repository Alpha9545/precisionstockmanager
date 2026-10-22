using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // 2026-10-17: Bed Sowing tray batches are approved on ANY date (2026-10-17_BedSowingApprovalAnyDate.sql); every other
    // batch type keeps its Ready Date rule; who approves is unchanged.
    public class AreaReadyStock1017RulesTests
    {
        private static readonly DateTime Ready = new(2026, 11, 1);

        [Theory]
        [InlineData(-20)]   // long before the calculated Ready Date
        [InlineData(-1)]
        [InlineData(0)]     // on it
        [InlineData(15)]    // after it
        public void BedSowingBatch_AnyDate_WhenTheRuleIsOn(int daysFromReady)
            => Assert.Equal((true, (string?)null), BedSowingRules.ValidateApprovalReadyDate(SeedSowing.SourceBedSowing, Ready, Ready.AddDays(daysFromReady), bedAnyDate: true));

        [Fact]
        public void BedSowingBatch_WithoutAReadyDate_AlsoAllowedWhenTheRuleIsOn()
            => Assert.True(BedSowingRules.ValidateApprovalReadyDate(SeedSowing.SourceBedSowing, null, Ready, bedAnyDate: true).Ok);

        [Fact]
        public void RuleOff_TheEarlierRuleStays()
        {
            Assert.False(BedSowingRules.ValidateApprovalReadyDate(SeedSowing.SourceBedSowing, Ready, Ready.AddDays(-1)).Ok);
            Assert.True(BedSowingRules.ValidateApprovalReadyDate(SeedSowing.SourceBedSowing, Ready, Ready).Ok);
        }

        [Fact]
        public void OtherSources_Unchanged_ByTheBedRule()
        {
            // a tray batch transplanted from Ready Stock: still never before its Ready Date
            var early = BedSowingRules.ValidateApprovalReadyDate(SeedSowing.SourceTransplant, Ready, Ready.AddDays(-1), bedAnyDate: true);
            Assert.False(early.Ok);
            Assert.Equal(string.Format(BedSowingRules.TransplantNotReadyYetMessageFormat, Ready), early.Error);
            Assert.False(BedSowingRules.ValidateApprovalReadyDate(SeedSowing.SourceTransplant, null, Ready, bedAnyDate: true).Ok);
            Assert.True(BedSowingRules.ValidateApprovalReadyDate(SeedSowing.SourceTransplant, Ready, Ready, bedAnyDate: true).Ok);
            // Direct Seed and Cutting: never gated by a Ready Date (as before)
            Assert.True(BedSowingRules.ValidateApprovalReadyDate(SeedSowing.SourceSeed, Ready, Ready.AddDays(-30), bedAnyDate: true).Ok);
            Assert.True(BedSowingRules.ValidateApprovalReadyDate(SeedSowing.SourceCutting, Ready, Ready.AddDays(-30), bedAnyDate: true).Ok);
        }

        [Fact]
        public void WhoApproves_IsNotChangedByTheDateRule()
        {
            var maya = new DirectSowingRules.BedApprover(true, 9, "Maya Shinde");
            Assert.True(DirectSowingRules.CanApprove(null, 9, "Maya", 9, "Maya", SeedSowing.SourceBedSowing, maya).Ok);
            Assert.False(DirectSowingRules.CanApprove(null, 9, "Maya", 13, "Somnath", SeedSowing.SourceBedSowing, maya).Ok);
            Assert.False(DirectSowingRules.CanApprove(null, 9, "Maya", 21, "Admin", SeedSowing.SourceBedSowing, maya).Ok);
        }
    }
}
