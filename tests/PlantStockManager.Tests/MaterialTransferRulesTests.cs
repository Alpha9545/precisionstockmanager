using PlantStockManager.Services;
using Location = PlantStockManager.Services.MaterialTransferRules.Location;

namespace PlantStockManager.Tests
{
    // 2026-10-23: the pure rules of Cocopeat / Pitmos transfers (Services/MaterialTransferRules.cs).
    public class MaterialTransferRulesTests
    {
        private static readonly DateTime Today = new(2026, 10, 23);
        private static Location Main(int polyhouse) => new(2, "MainOffice", true, polyhouse, 2);
        private static Location MotherPlant(int polyhouse) => new(127, "MotherPlant", true, polyhouse, 127);

        [Fact]
        public void Locations_MainOfficeAndMotherPlantPolyhouses_AreEligible()
        {
            Assert.Null(MaterialTransferRules.ValidateLocations(Main(3), Main(4)));
            Assert.Null(MaterialTransferRules.ValidateLocations(Main(3), MotherPlant(14)));
            Assert.Null(MaterialTransferRules.ValidateLocations(MotherPlant(14), Main(3)));
            Assert.Null(MaterialTransferRules.ValidateLocations(MotherPlant(14), MotherPlant(15)));
        }

        [Fact]
        public void Locations_Outlet_Inactive_Unknown_Mismatched_OrSamePolyhouse_AreRefused()
        {
            Assert.Contains("destination must be", MaterialTransferRules.ValidateLocations(Main(3), new Location(124, "Outlet", true, 18, 124)));
            Assert.Contains("source must be", MaterialTransferRules.ValidateLocations(new Location(5, "MainOffice", false, 9, 5), Main(3)));
            Assert.Contains("destination must be", MaterialTransferRules.ValidateLocations(Main(3), new Location(7, null, true, 9, 7)));
            Assert.Contains("does not belong", MaterialTransferRules.ValidateLocations(Main(3), new Location(127, "MotherPlant", true, 4, 2)));
            Assert.Equal(MaterialTransferRules.SamePolyhouseMessage, MaterialTransferRules.ValidateLocations(Main(3), Main(3)));
            Assert.NotNull(MaterialTransferRules.ValidateLocations(null, Main(3)));
        }

        [Theory]
        [InlineData(0, "greater than zero")]
        [InlineData(-5, "greater than zero")]
        [InlineData(1.0001, "decimal places")]
        [InlineData(1000001, "too large")]
        public void Request_BadQuantity_IsRefused(double quantity, string expected)
            => Assert.Contains(expected, MaterialTransferRules.ValidateRequest((decimal)quantity, null, Today, Today));

        [Fact]
        public void Request_DateAndRemark()
        {
            Assert.Null(MaterialTransferRules.ValidateRequest(12.345m, "ok", Today, Today));
            Assert.Null(MaterialTransferRules.ValidateRequest(1m, null, Today.AddDays(-3), Today));
            Assert.Equal(LedgerDates.FutureDateMessage, MaterialTransferRules.ValidateRequest(1m, null, Today.AddDays(1), Today));
            Assert.Equal("Date is required.", MaterialTransferRules.ValidateRequest(1m, null, default, Today));
            Assert.Contains("too long", MaterialTransferRules.ValidateRequest(1m, new string('x', 501), Today, Today));
        }

        [Fact]
        public void LockOrder_IsTheSameForBothDirectionsOfOnePair()
        {
            Assert.True(MaterialTransferRules.LockSourceFirst(2, 3, 2, 4));
            Assert.False(MaterialTransferRules.LockSourceFirst(2, 4, 2, 3));     // B -> A locks A first too
            Assert.True(MaterialTransferRules.LockSourceFirst(2, 6, 127, 14));
            Assert.False(MaterialTransferRules.LockSourceFirst(127, 14, 2, 6));
        }

        [Fact]
        public void LedgerLabels_AndHistoryCategory()
        {
            Assert.Equal("Transferred out", MaterialStockRules.TypeLabel(MaterialTransferRules.TransferOut));
            Assert.Equal("Transferred in", MaterialStockRules.TypeLabel(MaterialTransferRules.TransferIn));
            Assert.Equal(MaterialStockRules.Transfer, MaterialStockRules.MaterialCategory(MaterialTransferRules.TransferOut));
            Assert.Equal(MaterialStockRules.Transfer, MaterialStockRules.MaterialCategory(MaterialTransferRules.TransferIn));
            Assert.Equal(MaterialStockRules.Consume, MaterialStockRules.MaterialCategory(MaterialStockRules.Consume));
            Assert.Equal("Transfer MT-2026-00001 to Main / Facility-1", MaterialTransferRules.OutRemark("MT-2026-00001", "Main", "Facility-1"));
        }
    }
}
