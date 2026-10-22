using PlantStockManager.Models;
using PlantStockManager.Pages.Production.SeedSowing;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // The trays the availability panel says an entry needs -- the same rules the saves use: Seed / Cutting Sowing take
    // every started tray (CEILING, DirectSowingRules.PhysicalTraysRequired); a whole-tray transplant takes only its
    // complete trays (FLOOR; the remainder is waste and takes no tray). No database needed.
    public class TrayAvailabilityRulesTests
    {
        [Theory]
        [InlineData(170, "150 Cavity", 1)]
        [InlineData(168, "9 Cavity", 18)]
        [InlineData(149, "150 Cavity", 0)]
        [InlineData(0, "24 Cavity", 0)]
        [InlineData(500, "10 Cavity", 0)]
        [InlineData(500, null, 0)]
        public void Transplant_NeedsCompleteTraysOnly(decimal quantity, string? cavity, decimal trays)
        {
            Assert.Equal(trays, TrayAvailabilityForm.CompleteTrays(quantity, cavity));
            var (ok, plan, _) = TransplantRules.PlanProcessing(quantity, quantity, cavity, false, "w");
            if (ok && plan!.MakesTrayBatch)
                Assert.Equal(plan.Trays, TrayAvailabilityForm.CompleteTrays(quantity, cavity));                      // agrees with the save's plan
        }

        [Theory]
        [InlineData(103, "102 Cavity", 2)]
        [InlineData(950, "24 Cavity", 40)]
        public void Sowing_NeedsEveryStartedTray_AsTheSave(decimal quantity, string cavity, decimal trays)
            => Assert.Equal(trays, DirectSowingRules.PhysicalTraysRequired(quantity, cavity));

        [Fact]
        public void Rows_NameTheirLocation_AreaPoolByArea_PolyhouseWithItsArea()
        {
            Assert.Equal("Mother Plant A (Area)", new TrayAvailabilityRow("a5", 5, "Mother Plant A", null, null, true, "150 Cavity", 0).Location);
            Assert.Equal("Polyhouse A (Main Office)", new TrayAvailabilityRow("p4", 2, "Main Office", 4, "Polyhouse A", false, "150 Cavity", 3).Location);
        }
    }
}
