using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Phase 4 (Cutting Delivery to Main Office): the tray/cavity math is
    // NOT reimplemented for cuttings -- InternalTransferRepository calls
    // the exact same DirectSowingRules.CalculateTrays/ComputeTrayApproval
    // functions already covered by TrayCalculationTests/TrayApprovalTests
    // for Direct Sowing. These tests exercise that same reuse from a
    // Cutting Delivery angle (raw cutting quantity + cavity in, complete
    // trays / cuttings sent / actual seedlings / wastage out) and cover
    // the one genuinely new piece: InternalTransfer.RemainingCuttings.
    public class CuttingDeliveryTrayCavityTests
    {
        // ---- Send side (GiveToMainOffice): raw cutting quantity -> trays ----

        [Fact]
        public void SendingCuttings_OnlyCompleteTrays_AreActuallySent()
        {
            // 300 cuttings at 102/tray -> 2 complete trays = 204 sent; 96 stay behind.
            var (ok, trays, cuttingsUsed, remaining, error) = DirectSowingRules.CalculateTrays(300, "102 Cavity");
            Assert.True(ok, error);
            Assert.Equal(2, trays);
            Assert.Equal(204m, cuttingsUsed);
            Assert.Equal(96m, remaining);
        }

        [Fact]
        public void SendingCuttings_ExactMultipleOfCavity_LeavesNothingBehind()
        {
            var (ok, trays, cuttingsUsed, remaining, _) = DirectSowingRules.CalculateTrays(204, "102 Cavity");
            Assert.True(ok);
            Assert.Equal(2, trays);
            Assert.Equal(204m, cuttingsUsed);
            Assert.Equal(0m, remaining);
        }

        [Fact]
        public void SendingCuttings_FewerThanOneTray_IsRefused()
        {
            var (ok, _, _, _, error) = DirectSowingRules.CalculateTrays(50, "102 Cavity");
            Assert.False(ok);
            Assert.Contains("less than one complete", error);
        }

        [Fact]
        public void SendingCuttings_InvalidCavity_IsRefused()
        {
            var (ok, _, _, _, error) = DirectSowingRules.CalculateTrays(300, "10 Cavity");
            Assert.False(ok);
            Assert.Contains("Tray size must be one of", error);
        }

        [Fact]
        public void SendingCuttings_FractionalQuantity_IsRejectedBeforeCalculation()
        {
            // Cuttings are whole countable units -- InternalTransferRepository
            // checks this itself (DirectSowingRules.IsWholeNumber) before ever
            // calling CalculateTrays, mirroring PlanSowing's own check for seeds.
            Assert.False(DirectSowingRules.IsWholeNumber(300.5m));
            Assert.True(DirectSowingRules.IsWholeNumber(300m));
        }

        // ---- Confirm side (ConfirmReceipt): actual ready trays -> seedlings/wastage ----

        [Fact]
        public void ConfirmingAllTraysSent_HasZeroWastage()
        {
            // 2 trays sent (204 cuttings), all 2 confirmed ready.
            var (ok, actualTrays, seedlings, wastage, wastagePct, error) = DirectSowingRules.ComputeTrayApproval(
                seedsUsed: 204, sowingTrays: 2, cavityType: "102 Cavity", alreadyReady: 0, alreadyWasted: 0, actualTrays: 2, wastageReason: null);
            Assert.True(ok, error);
            Assert.Equal(2, actualTrays);
            Assert.Equal(204m, seedlings);
            Assert.Equal(0m, wastage);
            Assert.Equal(0m, wastagePct);
        }

        [Fact]
        public void ConfirmingFewerTrays_ComputesWastage_RequiresAReason()
        {
            // 2 trays sent, only 1 confirmed ready -> 102 cuttings wastage.
            var (ok, _, _, wastage, _, error) = DirectSowingRules.ComputeTrayApproval(
                204, 2, "102 Cavity", 0, 0, 1, wastageReason: null);
            Assert.False(ok);
            Assert.Contains("Wastage Reason is required", error);

            var (ok2, _, seedlings, wastage2, wastagePct, error2) = DirectSowingRules.ComputeTrayApproval(
                204, 2, "102 Cavity", 0, 0, 1, "Disease");
            Assert.True(ok2, error2);
            Assert.Equal(102m, seedlings);
            Assert.Equal(102m, wastage2);
            Assert.Equal(50m, wastagePct);
        }

        [Fact]
        public void ConfirmingMoreTraysThanSent_IsRefused()
        {
            var (ok, _, _, _, _, error) = DirectSowingRules.ComputeTrayApproval(204, 2, "102 Cavity", 0, 0, 3, "Disease");
            Assert.False(ok);
            Assert.Contains("cannot exceed the", error);
        }

        [Fact]
        public void ConfirmingFractionalTrays_IsRefused()
        {
            var (ok, _, _, _, _, error) = DirectSowingRules.ComputeTrayApproval(204, 2, "102 Cavity", 0, 0, 1.5m, "Disease");
            Assert.False(ok);
            Assert.Contains("whole number", error);
        }

        [Fact]
        public void ConfirmingZeroTrays_IsRefused()
        {
            var (ok, _, _, _, _, error) = DirectSowingRules.ComputeTrayApproval(204, 2, "102 Cavity", 0, 0, 0, "Disease");
            Assert.False(ok);
            Assert.Contains("at least 1 complete tray", error);
        }

        // ---- InternalTransfer.RemainingCuttings (display-only; not persisted
        //      as a separate "remaining seedling quantity" concept) ----

        [Fact]
        public void RemainingCuttings_IsEnteredMinusSent()
        {
            var transfer = new InternalTransfer { CuttingQuantityEntered = 300, Quantity = 204 };
            Assert.Equal(96m, transfer.RemainingCuttings);
        }

        [Fact]
        public void RemainingCuttings_IsZero_WhenNothingWasEntered()
        {
            var transfer = new InternalTransfer { Quantity = 0 };
            Assert.Equal(0m, transfer.RemainingCuttings);
        }
    }
}
