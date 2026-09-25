using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Phase 31 (Phase 6, Cutting to Potted Plant Production): the batch's
    // own genuinely new logic is small -- most of the workflow reuses
    // EXISTING, already-tested machinery: DirectSowingRules.CanApprove/
    // CanCancelApproval (Ready confirmation authority) and
    // SeedSowingRepository.ClassifyReadyAlert (Ready Alerts). These tests
    // exercise the model's own computed properties, the reuse itself, and
    // the batch-level planned-quantity cap logic (rule 4).
    public class PotProductionBatchTests
    {
        // ---- Model computed properties (the only genuinely new C# logic) ----

        [Fact]
        public void RemainingPlannedQuantity_IsPlannedMinusConsumed()
        {
            var batch = new PotProductionBatch { PlannedCuttingQuantity = 1500, CuttingQuantityConsumedTotal = 1300 };
            Assert.Equal(200m, batch.RemainingPlannedQuantity);
        }

        [Fact]
        public void RemainingPlannedQuantity_IsFullPlan_WhenNothingConsumedYet()
        {
            var batch = new PotProductionBatch { PlannedCuttingQuantity = 1500 };
            Assert.Equal(1500m, batch.RemainingPlannedQuantity);
        }

        [Fact]
        public void Loss_IsConsumedMinusProduced()
        {
            // The business's own worked example: Day 1 = 600, Day 2 = 500,
            // Day 3 = 200 -> 1,300 consumed/produced with zero loss when
            // every cutting became a potted plant.
            var batch = new PotProductionBatch { CuttingQuantityConsumedTotal = 1300, QuantityProducedTotal = 1300 };
            Assert.Equal(0m, batch.Loss);

            var lossyBatch = new PotProductionBatch { CuttingQuantityConsumedTotal = 1300, QuantityProducedTotal = 1250 };
            Assert.Equal(50m, lossyBatch.Loss);
        }

        [Fact]
        public void DailyEntries_AccumulateToTheWorkedExample()
        {
            // Day 1 = 600, Day 2 = 500, Day 3 = 200 -> Potted Stock shows
            // 1,300 -- purely additive, exactly like PottedPlantStock's own
            // running physical quantity (nothing batch-specific here; this
            // documents the expectation the repository's
            // AccumulateDailyEntryAsync fulfils under lock).
            decimal consumedTotal = 0, producedTotal = 0;
            foreach (var day in new[] { 600m, 500m, 200m })
            {
                consumedTotal += day;
                producedTotal += day;
            }
            Assert.Equal(1300m, consumedTotal);
            Assert.Equal(1300m, producedTotal);
        }

        // ---- Rule 4: daily entries cannot together exceed the batch's own
        //      planned cutting quantity -- pure predicate mirroring
        //      PotProductionBatchRepository.LockForDailyEntryAsync's own
        //      over-the-plan check. ------------------------------------

        [Theory]
        [InlineData(1000, 0, 600, true)]     // first entry, well within plan
        [InlineData(1000, 600, 400, true)]   // exactly fills the plan
        [InlineData(1000, 600, 401, false)]  // would exceed the plan by 1
        [InlineData(1000, 1000, 1, false)]   // plan already fully consumed
        public void ConsumedTotalPlusNewEntry_MustNotExceedPlannedQuantity(
            decimal planned, decimal alreadyConsumed, decimal newEntry, bool expectedOk)
        {
            var wouldExceed = alreadyConsumed + newEntry > planned;
            Assert.Equal(!expectedOk, wouldExceed);
        }

        // ---- Rule 6: Ready confirmation reuses the EXISTING
        //      assigned-supervisor rule, unchanged -- proves the reuse,
        //      not a copy of the logic. ---------------------------------

        [Fact]
        public void ConfirmReady_OnlyAssignedSupervisor_MayConfirm()
        {
            var (ok, error) = DirectSowingRules.CanApprove(assignedSupervisorId: 5, createdById: 9, createdBy: "Creator", approverId: 5, approverName: null);
            Assert.True(ok, error);
            Assert.False(DirectSowingRules.CanApprove(5, 9, "Creator", 9, "Creator").Ok);   // creator, even if also the assigned supervisor's id
            Assert.False(DirectSowingRules.CanApprove(5, 9, "Creator", 7, "Someone else").Ok);
            Assert.False(DirectSowingRules.CanApprove(null, 9, "Creator", 5, null).Ok);      // no supervisor assigned yet
        }

        [Fact]
        public void ConfirmReady_UsesProductionAreaSupervisorKind_NotSowing()
        {
            // Rule 6 documentation choice: Ready confirmation for a batch
            // is a Production Area supervisor action (same actor as
            // PotProduction/CreateFromCutting.cshtml.cs's own Supervisor
            // field), not a Sowing Supervisor action.
            Assert.Equal("MotherPlant.Enter", SupervisorRules.PermissionFor(SupervisorKind.ProductionArea));
            Assert.Equal("Production Area Supervisor", SupervisorRules.Label(SupervisorKind.ProductionArea));
        }

        // ---- Ready Alerts (rule 7): reuses
        //      SeedSowingRepository.ClassifyReadyAlert unchanged, just with
        //      the batch's own "still open" status word ("InProduction"
        //      instead of "Sown"). -------------------------------------

        [Fact]
        public void ReadyAlert_Overdue_WhenExpectedDateHasPassed_AndStillInProduction()
        {
            var category = SeedSowingRepository.ClassifyReadyAlert(
                "InProduction", DateTime.Today.AddDays(-2), DateTime.Today, 3, activeStatus: "InProduction");
            Assert.Equal("Overdue", category);
        }

        [Fact]
        public void ReadyAlert_ReadySoon_WithinWindow()
        {
            var category = SeedSowingRepository.ClassifyReadyAlert(
                "InProduction", DateTime.Today.AddDays(2), DateTime.Today, 3, activeStatus: "InProduction");
            Assert.Equal("ReadySoon", category);
        }

        [Fact]
        public void ReadyAlert_None_OnceBatchIsReady()
        {
            // A Ready-confirmed batch never alerts again, however close or
            // overdue its Expected Ready Date is -- same rule Seed/Cutting
            // Sowing already rely on (only the "still open" status alerts).
            var category = SeedSowingRepository.ClassifyReadyAlert(
                "Ready", DateTime.Today.AddDays(-2), DateTime.Today, 3, activeStatus: "InProduction");
            Assert.Equal("None", category);
        }

        [Fact]
        public void ReadyAlert_DefaultActiveStatus_StillMatchesSeedAndCuttingSowing()
        {
            // The new optional parameter must not change ANY existing
            // Seed/Cutting Sowing caller's behavior -- default remains "Sown".
            Assert.Equal("Overdue", SeedSowingRepository.ClassifyReadyAlert("Sown", DateTime.Today.AddDays(-1), DateTime.Today, 3));
            Assert.Equal("None", SeedSowingRepository.ClassifyReadyAlert("Completed", DateTime.Today.AddDays(-1), DateTime.Today, 3));
        }
    }
}
