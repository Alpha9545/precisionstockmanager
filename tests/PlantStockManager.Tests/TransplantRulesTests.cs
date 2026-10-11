using PlantStockManager.Services;
using static PlantStockManager.Services.MotherPlantTransferRules;

namespace PlantStockManager.Tests
{
    // Pure rules of Main Area -> Mother Plant transfer and transplanting (no database).
    public class TransplantRulesTests
    {
        private static readonly DateTime Today = new(2026, 10, 9);
        private static readonly DateTime Sown = new(2026, 9, 1);
        private static SourceBatch MainBatch(decimal available = 480) => new("Sowing", "MainOffice", true, 2, "24 Cavity", available);
        private static Destination Mp(int area = 127, int polyhouseArea = 127) => new(area, "MotherPlant", true, 14, polyhouseArea);

        [Fact]
        public void Transfer_WholeTraysOfAMainAreaSowingBatch_ToAnActiveMotherPlantPolyhouse()
        {
            var (ok, trays, error) = ValidateCreate(MainBatch(), Mp(), 240, Today, Today, Sown);
            Assert.True(ok, error);
            Assert.Equal(10m, trays);
        }

        [Theory]
        [InlineData(100, "multiple of 24")]
        [InlineData(0, "greater than zero")]
        [InlineData(504, "available")]
        public void Transfer_Quantity_MustBeWholeTraysWithinAvailable(decimal qty, string expect)
            => Assert.Contains(expect, ValidateCreate(MainBatch(), Mp(), qty, Today, Today, Sown).Error);

        [Fact]
        public void Transfer_Source_MustBeAMainAreaSowingBatch()
        {
            Assert.Contains("Main Area", ValidateCreate(MainBatch() with { OriginType = "Transfer" }, Mp(), 24, Today, Today, Sown).Error);
            Assert.Contains("Main Area", ValidateCreate(MainBatch() with { AreaType = "MotherPlant" }, Mp(), 24, Today, Today, Sown).Error);
            Assert.Contains("Main Area", ValidateCreate(MainBatch() with { AreaActive = false }, Mp(), 24, Today, Today, Sown).Error);
        }

        [Fact]
        public void Transfer_Destination_MustBeAnActiveMotherPlantAreaAndItsOwnPolyhouse()
        {
            Assert.Contains("active Mother Plant Area", ValidateCreate(MainBatch(), Mp() with { AreaType = "Outlet" }, 24, Today, Today, Sown).Error);
            Assert.Contains("active Mother Plant Area", ValidateCreate(MainBatch(), Mp() with { AreaActive = false }, 24, Today, Today, Sown).Error);
            Assert.Contains("does not belong", ValidateCreate(MainBatch(), Mp(polyhouseArea: 1), 24, Today, Today, Sown).Error);
            Assert.Contains("Polyhouse", ValidateCreate(MainBatch(), Mp() with { PolyhouseId = null }, 24, Today, Today, Sown).Error);
            Assert.Contains("future", ValidateCreate(MainBatch(), Mp(), 24, Today.AddDays(1), Today, Sown).Error);
            Assert.Contains("before the batch was sown", ValidateCreate(MainBatch(), Mp(), 24, Sown.AddDays(-1), Today, Sown).Error);
        }

        [Fact]
        public void Receive_ShortfallNeedsAReason_NeverMoreThanSent_OnlyWhilePending()
        {
            Assert.Equal((true, 0m, (string?)null), ValidateReceive(StatusPending, 240, 240, null));
            Assert.Equal(10m, ValidateReceive(StatusPending, 240, 230, "broken").Loss);
            Assert.Contains("reason", ValidateReceive(StatusPending, 240, 230, " ").Error);
            Assert.Contains("more than", ValidateReceive(StatusPending, 240, 241, null).Error);
            Assert.Contains("Reject", ValidateReceive(StatusPending, 240, 0, null).Error);
            Assert.Contains("already", ValidateReceive(StatusCompleted, 240, 240, null).Error);
            Assert.Contains("already", ValidateClose(StatusCancelled, "x", "cancelled").Error);
            Assert.Contains("reason", ValidateClose(StatusPending, "", "rejected").Error);
        }

        // ---- transplanting -------------------------------------------------------------------------------

        [Fact]
        public void Plan_150PlantsInto24Cavity_Is7Trays_6Full_LastWith6()   // the specification's example
        {
            var (ok, plan, error) = TransplantRules.PlanTransplant(150, 150, 0, 1000, "24 Cavity");
            Assert.True(ok, error);
            Assert.Equal(7, plan!.OutputTrays);
            Assert.Equal(6, plan.FullTrays);
            Assert.Equal(6m, plan.PlantsInLastTray);
        }

        [Theory]
        [InlineData("9 Cavity", 480, 54)]
        [InlineData("24 Cavity", 480, 20)]
        [InlineData("42 Cavity", 480, 12)]
        [InlineData("42 Cavity", 1, 1)]
        public void Plan_OutputTrays_AreCeilingOfPlantsOverCavity(string cavity, decimal plants, int trays)
            => Assert.Equal(trays, TransplantRules.PlanTransplant(plants, plants, 0, 10000, cavity).Plan!.OutputTrays);

        [Fact]
        public void Plan_SelectedIsTransplantedPlusLost_WithinAvailable_WholePlants()
        {
            Assert.True(TransplantRules.PlanTransplant(485, 480, 5, 1000, "24 Cavity").Ok);
            Assert.Contains("must equal", TransplantRules.PlanTransplant(485, 480, 0, 1000, "24 Cavity").Error);
            Assert.Contains("available", TransplantRules.PlanTransplant(485, 480, 5, 484, "24 Cavity").Error);
            Assert.Contains("greater than zero", TransplantRules.PlanTransplant(5, 0, 5, 1000, "24 Cavity").Error);
            Assert.Contains("whole numbers", TransplantRules.PlanTransplant(10.5m, 10.5m, 0, 1000, "24 Cavity").Error);
            Assert.Contains("Target tray", TransplantRules.PlanTransplant(10, 10, 0, 1000, "10 Cavity").Error);
        }

        [Fact]
        public void Plan_FromASowingBatch_TakesWholeSourceTrays()
        {
            Assert.True(TransplantRules.PlanTransplant(150, 145, 5, 1000, "24 Cavity", sourceWholeTrayCavity: 150).Ok);
            Assert.Contains("multiple of 150", TransplantRules.PlanTransplant(149, 145, 4, 1000, "24 Cavity", sourceWholeTrayCavity: 150).Error);
        }

        [Fact]
        public void Locations_BothMustBeActiveMotherPlantAreas_PolyhouseOfTheTarget()
        {
            var mp = new TransplantRules.Location("MotherPlant", true, 127);
            Assert.True(TransplantRules.ValidateLocations(mp, mp, 127).Ok);
            Assert.False(TransplantRules.ValidateLocations(mp with { AreaType = "MainOffice" }, mp, 127).Ok);
            Assert.False(TransplantRules.ValidateLocations(mp, mp with { AreaActive = false }, 127).Ok);
            Assert.False(TransplantRules.ValidateLocations(mp, mp, 1).Ok);
        }

        [Fact]
        public void Trays_MustBeInTheTargetPolyhouse()
        {
            Assert.True(TransplantRules.ValidateTrays(7, 7, "24 Cavity").Ok);
            Assert.Contains("7 needed", TransplantRules.ValidateTrays(7, 6, "24 Cavity").Error);
        }

        [Fact]
        public void Reverse_OnlyWhileTheOutputIsUntouched_WithAReason()
        {
            var untouched = new TransplantRules.Output(145, 0, 0, 0, 0);
            Assert.True(TransplantRules.ValidateReverse(TransplantRules.StatusCompleted, 145, untouched, "wrong batch").Ok);
            Assert.False(TransplantRules.ValidateReverse(TransplantRules.StatusReversed, 145, untouched, "x").Ok);
            Assert.False(TransplantRules.ValidateReverse(TransplantRules.StatusCompleted, 145, untouched, " ").Ok);
            Assert.False(TransplantRules.ValidateReverse(TransplantRules.StatusCompleted, 145, untouched with { Reserved = 9 }, "x").Ok);
            Assert.False(TransplantRules.ValidateReverse(TransplantRules.StatusCompleted, 145, untouched with { Dispatched = 9 }, "x").Ok);
            Assert.False(TransplantRules.ValidateReverse(TransplantRules.StatusCompleted, 145, untouched with { InTransit = 9 }, "x").Ok);
            Assert.False(TransplantRules.ValidateReverse(TransplantRules.StatusCompleted, 145, untouched with { Children = 1 }, "x").Ok);
            Assert.False(TransplantRules.ValidateReverse(TransplantRules.StatusCompleted, 145, untouched with { Quantity = 140 }, "x").Ok);
        }

        // 2026-10-11: business receipt date of a Ready Stock transfer into a Mother Plant Area.
        [Fact]
        public void ReceiptDate_TodayOrEarlierWithAReason_NeverFuture_NeverBeforeTheSend()
        {
            var sent = Today.AddDays(-3);
            Assert.True(ValidateReceiptDate(Today, sent, Today, null).Ok);                          // default: today, no reason needed
            Assert.True(ValidateReceiptDate(sent, sent, Today, "arrived the same day, entered late").Ok);
            Assert.Contains("earlier than today", ValidateReceiptDate(Today.AddDays(-1), sent, Today, " ").Error);
            Assert.Contains("future", ValidateReceiptDate(Today.AddDays(1), sent, Today, "x").Error);
            Assert.Contains("before the transfer was sent", ValidateReceiptDate(sent.AddDays(-1), sent, Today, "x").Error);
            Assert.Contains("too long", ValidateReceiptDate(Today.AddDays(-1), sent, Today, new string('x', MaxReceiptReasonLength + 1)).Error);
        }
    }
}
