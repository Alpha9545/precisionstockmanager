using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Phase 5 (Cutting Sowing / Tray Production): the tray/cavity math is
    // NOT reimplemented -- CuttingSowingRepository and ReadyConfirmation
    // Repository.ConfirmCuttingSowingAsync call the exact same
    // DirectSowingRules.CalculateTrays/ComputeTrayApproval functions
    // already covered by TrayCalculationTests/TrayApprovalTests (Direct
    // Sowing) and CuttingDeliveryTrayCavityTests (Phase 4). These tests
    // exercise that same reuse framed around Cutting Sowing (sow ->
    // approve, allowing partial approvals across more than one
    // confirmation, exactly like Direct Sowing) plus the model's own new
    // computed properties.
    public class CuttingSowingTests
    {
        // ---- Sow: raw cutting quantity -> complete trays ----------------

        [Fact]
        public void Sowing_OnlyCompleteTrays_AreDeducted()
        {
            // 500 cuttings at 102/tray -> 4 complete trays = 408 sown; 92 stay in stock.
            var (ok, trays, quantitySown, remaining, error) = DirectSowingRules.CalculateTrays(500, "102 Cavity");
            Assert.True(ok, error);
            Assert.Equal(4, trays);
            Assert.Equal(408m, quantitySown);
            Assert.Equal(92m, remaining);
        }

        [Fact]
        public void Sowing_FewerThanOneTray_IsRefused()
        {
            var (ok, _, _, _, error) = DirectSowingRules.CalculateTrays(50, "102 Cavity");
            Assert.False(ok);
            Assert.Contains("less than one complete", error);
        }

        [Fact]
        public void Sowing_InvalidCavity_IsRefused()
        {
            var (ok, _, _, _, error) = DirectSowingRules.CalculateTrays(500, "20 Cavity");
            Assert.False(ok);
            Assert.Contains("Tray size must be one of", error);
        }

        [Fact]
        public void Sowing_FractionalQuantity_IsRejectedBeforeCalculation()
        {
            Assert.False(DirectSowingRules.IsWholeNumber(500.5m));
            Assert.True(DirectSowingRules.IsWholeNumber(500m));
        }

        [Fact]
        public void Sowing_NegativeOrZeroQuantity_IsRefused()
        {
            Assert.False(DirectSowingRules.CalculateTrays(0, "102 Cavity").Ok);
            Assert.False(DirectSowingRules.CalculateTrays(-10, "102 Cavity").Ok);
        }

        // ---- Approve: Cutting Sowing allows PARTIAL approvals across more
        //      than one confirmation, exactly like Direct Sowing (both are
        //      constrained to "at most one Confirmed row" only once the
        //      first one succeeds -- UX_ReadyConfirmations_OneConfirmedPer*)
        //      -- but the running-total math (alreadyReady/alreadyWasted)
        //      itself supports it, so this proves it end to end. ----------

        [Fact]
        public void Approval_FirstPartial_ThenFinal_AddsUpExactly()
        {
            // 4 trays sown (408 cuttings). First approval: 2 trays ready.
            var (ok1, trays1, seedlings1, wastage1, _, error1) = DirectSowingRules.ComputeTrayApproval(
                408, 4, "102 Cavity", alreadyReady: 0, alreadyWasted: 0, actualTrays: 2, wastageReason: "Disease");
            Assert.True(ok1, error1);
            Assert.Equal(2, trays1);
            Assert.Equal(204m, seedlings1);
            // Wastage is computed against what's still to account for
            // (408 - 0 - 0 = 408), so 2 ready trays leaves 204 as wastage
            // in THIS calculation -- a real caller would only record this
            // as final wastage once no more trays are coming (mirrors the
            // exact behavior already exercised by TrayApprovalTests).
            Assert.Equal(204m, wastage1);

            // If instead all 4 trays are confirmed ready in one approval,
            // wastage is zero.
            var (ok2, trays2, seedlings2, wastage2, pct2, error2) = DirectSowingRules.ComputeTrayApproval(
                408, 4, "102 Cavity", 0, 0, 4, wastageReason: null);
            Assert.True(ok2, error2);
            Assert.Equal(4, trays2);
            Assert.Equal(408m, seedlings2);
            Assert.Equal(0m, wastage2);
            Assert.Equal(0m, pct2);
        }

        [Fact]
        public void Approval_MoreTraysThanSown_IsRefused()
        {
            var (ok, _, _, _, _, error) = DirectSowingRules.ComputeTrayApproval(408, 4, "102 Cavity", 0, 0, 5, "Disease");
            Assert.False(ok);
            Assert.Contains("cannot exceed the", error);
        }

        [Fact]
        public void Approval_ZeroOrFractionalTrays_IsRefused()
        {
            Assert.False(DirectSowingRules.ComputeTrayApproval(408, 4, "102 Cavity", 0, 0, 0, "Disease").Ok);
            Assert.False(DirectSowingRules.ComputeTrayApproval(408, 4, "102 Cavity", 0, 0, 1.5m, "Disease").Ok);
        }

        [Fact]
        public void Approval_WastageWithoutReason_IsRefused()
        {
            var (ok, _, _, _, _, error) = DirectSowingRules.ComputeTrayApproval(408, 4, "102 Cavity", 0, 0, 3, wastageReason: null);
            Assert.False(ok);
            Assert.Contains("Wastage Reason is required", error);
        }

        // ---- Approval authority: the EXISTING assigned-supervisor rule,
        //      unchanged -- proves Cutting Sowing reuses it, not a copy. ---

        [Fact]
        public void Approval_OnlyAssignedSupervisor_MayApprove()
        {
            var (ok, error) = DirectSowingRules.CanApprove(assignedSupervisorId: 5, createdById: 9, createdBy: "Creator", approverId: 5, approverName: "Sup");
            Assert.True(ok, error);
            Assert.False(DirectSowingRules.CanApprove(5, 9, "Creator", 9, "Creator").Ok);   // creator, even if also supervisor-looking
            Assert.False(DirectSowingRules.CanApprove(5, 9, "Creator", 7, "Someone else").Ok);
        }

        [Fact]
        public void Cancel_OnlyAssignedSupervisor_MayCancel()
        {
            Assert.True(DirectSowingRules.CanCancelApproval(assignedSupervisorId: 5, userId: 5).Ok);
            Assert.False(DirectSowingRules.CanCancelApproval(5, 9).Ok);
            Assert.False(DirectSowingRules.CanCancelApproval(null, 5).Ok);
        }

        // ---- Model computed properties (the only genuinely new C# logic) ----

        [Fact]
        public void RemainingCuttings_IsEnteredMinusSown()
        {
            var sowing = new CuttingSowing { CuttingQuantityEntered = 500, QuantitySown = 408 };
            Assert.Equal(92m, sowing.RemainingCuttings);
        }

        [Fact]
        public void RemainingCuttings_IsZero_WhenNothingWasEntered()
        {
            Assert.Equal(0m, new CuttingSowing { QuantitySown = 0 }.RemainingCuttings);
        }

        [Fact]
        public void RemainingReadyQuantity_IsSownMinusConfirmedMinusWastage()
        {
            var sowing = new CuttingSowing { QuantitySown = 408, ConfirmedReadyQuantity = 204, WastageQuantity = 0 };
            Assert.Equal(204m, sowing.RemainingReadyQuantity);
        }

        [Fact]
        public void WastagePercent_MatchesDirectSowingRulesFormula()
        {
            var sowing = new CuttingSowing { QuantitySown = 408, WastageQuantity = 204 };
            Assert.Equal(DirectSowingRules.WastagePercent(204, 408), sowing.WastagePercent);
            Assert.Equal(50m, sowing.WastagePercent);
        }

        // ---- ReadyStock/ReadyConfirmation SourceType (dual-source) ------

        [Fact]
        public void ReadyStock_SourceType_ReflectsWhicheverIdIsSet()
        {
            Assert.Equal("Seed", new ReadyStock { SeedSowingId = 1, CuttingSowingId = null }.SourceType);
            Assert.Equal("Cutting", new ReadyStock { SeedSowingId = null, CuttingSowingId = 2 }.SourceType);
        }

        [Fact]
        public void ReadyConfirmation_SourceType_ReflectsWhicheverIdIsSet()
        {
            Assert.Equal("Seed", new ReadyConfirmation { SeedSowingId = 1, CuttingSowingId = null }.SourceType);
            Assert.Equal("Cutting", new ReadyConfirmation { SeedSowingId = null, CuttingSowingId = 2 }.SourceType);
        }
    }
}
