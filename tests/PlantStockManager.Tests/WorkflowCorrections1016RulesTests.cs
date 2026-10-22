using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // 2026-10-16: the pure rules behind the workflow corrections (no database).
    public class WorkflowCorrections1016RulesTests
    {
        // ===== Mother Plant transplanting: processed -> complete trays + waste =====

        [Theory]
        [InlineData(170, 170, "150 Cavity", 1, 150, 20, 0)]     // the owner's example
        [InlineData(240, 240, "24 Cavity", 10, 240, 0, 0)]      // exact multiple
        [InlineData(240, 240, "42 Cavity", 5, 210, 30, 0)]      // several trays + remainder
        [InlineData(240, 100, "42 Cavity", 2, 84, 16, 140)]     // part of the batch: 140 stay, not waste
        [InlineData(300, 300, "102 Cavity", 2, 204, 96, 0)]
        [InlineData(9, 9, "9 Cavity", 1, 9, 0, 0)]
        public void PlanProcessing_CompleteTraysTransplanted_RemainderWasted_UnprocessedLeft(
            int available, int processed, string cavity, int trays, int transplanted, int waste, int left)
        {
            var (ok, plan, error) = TransplantRules.PlanProcessing(available, processed, cavity, false, waste > 0 ? "remainder" : null);
            Assert.True(ok, error);
            Assert.Equal((trays, (decimal)transplanted, (decimal)waste, (decimal)processed, (decimal)left), (plan!.Trays, plan.Transplanted, plan.Waste, plan.Processed, plan.LeftInBatch));
            Assert.True(plan.MakesTrayBatch);
            Assert.Equal((decimal)available, plan.Transplanted + plan.Waste + plan.LeftInBatch);       // conservation
            Assert.Equal(plan.Trays * DirectSowingRules.CavityCount(cavity)!.Value, plan.Transplanted);  // whole trays only
        }

        [Fact]
        public void PlanProcessing_TheExample_SummaryReadsAsTheOwnerWroteIt()
        {
            var (_, plan, _) = TransplantRules.PlanProcessing(170, 170, "150 Cavity", false, "weak");
            Assert.Equal("150 seedlings will be transplanted into 1 150 Cavity trays; the remaining 20 will be recorded as waste.", plan!.Summary("150 Cavity"));
        }

        [Fact]
        public void PlanProcessing_Refusals()
        {
            Assert.Contains("reason", TransplantRules.PlanProcessing(170, 170, "150 Cavity", false, null).Error);           // waste needs a reason
            Assert.Contains("reason", TransplantRules.PlanProcessing(170, 170, "150 Cavity", false, "   ").Error);
            Assert.Contains("do not fill one", TransplantRules.PlanProcessing(100, 100, "150 Cavity", false, "x").Error);   // < 1 tray, not confirmed
            Assert.Contains("Only 100", TransplantRules.PlanProcessing(100, 101, "24 Cavity", false, "x").Error);
            Assert.False(TransplantRules.PlanProcessing(100, 0, "24 Cavity", false, null).Ok);
            Assert.False(TransplantRules.PlanProcessing(100, -24, "24 Cavity", false, null).Ok);
            Assert.False(TransplantRules.PlanProcessing(100, 24.5m, "24 Cavity", false, null).Ok);
            Assert.False(TransplantRules.PlanProcessing(100, 48, "25 Cavity", false, null).Ok);                            // not a listed cavity
            Assert.False(TransplantRules.PlanProcessing(100, 48, "24 Cavity", false, new string('x', 501)).Ok);
            Assert.Contains("whole 24-cavity trays", TransplantRules.PlanProcessing(240, 100, "9 Cavity", false, "x", sourceWholeTrayCavity: 24).Error);
        }

        [Fact]
        public void PlanProcessing_WasteOnly_NeedsAReason_MakesNoTrayBatch()
        {
            Assert.Contains("reason", TransplantRules.PlanProcessing(20, 20, null, true, null).Error);
            var (ok, plan, error) = TransplantRules.PlanProcessing(20, 20, null, true, "dead on arrival");
            Assert.True(ok, error);
            Assert.Equal((0, 0m, 20m, 0m), (plan!.Trays, plan.Transplanted, plan.Waste, plan.LeftInBatch));
            Assert.False(plan.MakesTrayBatch);
            Assert.Equal("No tray batch: all 20 seedlings will be recorded as waste.", plan.Summary(null));
        }

        // ===== Bed Sowing tray batches: the designated approver only =====

        private const int Maya = 9, Somnath = 13, Admin = 21, Vasant = 110;
        private static readonly DirectSowingRules.BedApprover MayaApproves = new(true, Maya, "Maya Shinde");

        [Fact]
        public void BedBatch_OnlyTheDesignatedApprover_IncludingHerOwnTransplant()
        {
            Assert.True(DirectSowingRules.CanApprove(null, Maya, "Maya", Maya, "Maya", SeedSowing.SourceBedSowing, MayaApproves).Ok);       // maker = checker: Bed Sowing exception
            Assert.True(DirectSowingRules.CanApprove(null, Somnath, "Somnath", Maya, "Maya", SeedSowing.SourceBedSowing, MayaApproves).Ok);
            var (ok, error) = DirectSowingRules.CanApprove(null, Maya, "Maya", Somnath, "Somnath", SeedSowing.SourceBedSowing, MayaApproves);
            Assert.False(ok);
            Assert.Equal("Only Maya Shinde (the designated Bed Sowing approver) can approve a tray batch from a Bed Sowing.", error);
            Assert.False(DirectSowingRules.CanApprove(null, Maya, "Maya", Admin, "Prajwal", SeedSowing.SourceBedSowing, MayaApproves).Ok);   // not an administrator
            Assert.False(DirectSowingRules.CanApprove(null, Maya, "Maya", null, null, SeedSowing.SourceBedSowing, MayaApproves).Ok);
            // the rule is on but nobody (active) is designated: nobody approves
            Assert.Equal((false, DirectSowingRules.NoBedApproverMessage),
                DirectSowingRules.CanApprove(null, Somnath, "Somnath", Maya, "Maya", SeedSowing.SourceBedSowing, new DirectSowingRules.BedApprover(true, null, null)));
        }

        [Fact]
        public void BedApprover_ChangesNoOtherBatchType_AndWithoutTheMigrationTheEarlierRuleStays()
        {
            // transplanted from Ready Stock: any approver (Maya need not be involved)
            Assert.True(DirectSowingRules.CanApprove(null, Vasant, "Vasant", Vasant, "Vasant", SeedSowing.SourceTransplant, MayaApproves).Ok);
            Assert.True(DirectSowingRules.CanApprove(null, Vasant, "Vasant", Somnath, "Somnath", SeedSowing.SourceTransplant, MayaApproves).Ok);
            // Direct Seed Sowing: only its assigned supervisor, never its recorder -- Maya gets nothing extra
            Assert.True(DirectSowingRules.CanApprove(Somnath, Admin, "rec", Somnath, "Somnath", SeedSowing.SourceSeed, MayaApproves).Ok);
            Assert.False(DirectSowingRules.CanApprove(Somnath, Admin, "rec", Maya, "Maya", SeedSowing.SourceSeed, MayaApproves).Ok);
            Assert.False(DirectSowingRules.CanApprove(Maya, Maya, "Maya", Maya, "Maya", SeedSowing.SourceSeed, MayaApproves).Ok);
            // Cutting: unchanged
            Assert.True(DirectSowingRules.CanApprove(Somnath, Somnath, "Somnath", Somnath, "Somnath", SeedSowing.SourceCutting, MayaApproves).Ok);
            Assert.False(DirectSowingRules.CanApprove(Somnath, Admin, "rec", Maya, "Maya", SeedSowing.SourceCutting, MayaApproves).Ok);
            // migration not applied: the 2026-10-13 rule (any approver, never the recorder)
            Assert.True(DirectSowingRules.CanApprove(null, Maya, "Maya", Somnath, "Somnath", SeedSowing.SourceBedSowing, DirectSowingRules.BedApprover.NotConfigured).Ok);
            Assert.False(DirectSowingRules.CanApprove(null, Maya, "Maya", Maya, "Maya", SeedSowing.SourceBedSowing, DirectSowingRules.BedApprover.NotConfigured).Ok);
        }
    }
}
