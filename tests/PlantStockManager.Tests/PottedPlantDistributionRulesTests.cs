using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Phase 7 (Potted Plant Distribution): Ready Potted Plant Stock may
    // leave a production/Growing Partner Area toward exactly three
    // destinations -- Main Office, Outlet, or a Direct Customer Sale.
    // These pure predicates are what InternalTransferRepository
    // ('GrowingPartnerToMainOffice'/'GrowingPartnerToOutlet' branches) and
    // PottedPlantBookingRepository (a Customer Booking's source stock) all
    // re-check under their own row locks -- tested here directly so the
    // "exactly these Area types" rule can never silently drift apart
    // between the transfer side and the booking side.
    public class PottedPlantDistributionRulesTests
    {
        // ---- Destination #1: Main Office ---------------------------------

        [Fact]
        public void MainOfficeDestination_Valid_WhenActiveMainOfficeArea()
        {
            Assert.True(PottedPlantDistributionRules.IsValidMainOfficeDestination("MainOffice", isActive: true));
        }

        [Theory]
        [InlineData("MainOffice", false)] // inactive Main Office Area
        [InlineData("Outlet", true)]       // wrong Area type
        [InlineData("MotherPlant", true)]  // a production Area, never a valid destination
        [InlineData(null, true)]           // unset Area type
        public void MainOfficeDestination_Invalid(string? areaType, bool isActive)
        {
            Assert.False(PottedPlantDistributionRules.IsValidMainOfficeDestination(areaType, isActive));
        }

        // ---- Destination #2: Outlet ---------------------------------------

        [Fact]
        public void OutletDestination_Valid_WhenActiveOutletArea()
        {
            Assert.True(PottedPlantDistributionRules.IsValidOutletDestination("Outlet", isActive: true));
        }

        [Theory]
        [InlineData("Outlet", false)]      // inactive Outlet Area
        [InlineData("MainOffice", true)]   // wrong Area type
        [InlineData("Kunjir", true)]       // a production Area, never a valid destination
        [InlineData(null, true)]
        public void OutletDestination_Invalid(string? areaType, bool isActive)
        {
            Assert.False(PottedPlantDistributionRules.IsValidOutletDestination(areaType, isActive));
        }

        // ---- Destination #3: Direct Customer Sale --------------------------
        // May happen from stock physically held at EITHER Main Office or
        // Outlet (both customer-facing once stock has arrived via
        // destinations #1/#2) -- never directly from a production/Growing
        // Partner Area.

        [Theory]
        [InlineData("Outlet")]
        [InlineData("MainOffice")]
        public void CustomerSaleSource_Valid_ForEitherCustomerFacingAreaType(string areaType)
        {
            Assert.True(PottedPlantDistributionRules.IsValidCustomerSaleSourceArea(areaType, isActive: true));
        }

        [Theory]
        [InlineData("Outlet", false)]        // inactive
        [InlineData("MainOffice", false)]    // inactive
        [InlineData("MotherPlant", true)]    // a production Area -- never a direct sale source
        [InlineData("Kiran", true)]
        [InlineData("Kunjir", true)]
        [InlineData(null, true)]
        public void CustomerSaleSource_Invalid(string? areaType, bool isActive)
        {
            Assert.False(PottedPlantDistributionRules.IsValidCustomerSaleSourceArea(areaType, isActive));
        }

        // ---- The three destinations never overlap with a bare production
        //      Area, and Main Office/Outlet are mutually exclusive
        //      destinations for the SAME transfer (never both true). ------

        [Theory]
        [InlineData("MotherPlant")]
        [InlineData("Kunjir")]
        [InlineData("Kiran")]
        public void ProductionAreaType_IsNeverAValidDestinationOrSaleSource(string productionAreaType)
        {
            Assert.False(PottedPlantDistributionRules.IsValidMainOfficeDestination(productionAreaType, true));
            Assert.False(PottedPlantDistributionRules.IsValidOutletDestination(productionAreaType, true));
            Assert.False(PottedPlantDistributionRules.IsValidCustomerSaleSourceArea(productionAreaType, true));
        }

        [Fact]
        public void MainOfficeAndOutletDestinations_AreMutuallyExclusive()
        {
            Assert.True(PottedPlantDistributionRules.IsValidMainOfficeDestination("MainOffice", true));
            Assert.False(PottedPlantDistributionRules.IsValidOutletDestination("MainOffice", true));

            Assert.True(PottedPlantDistributionRules.IsValidOutletDestination("Outlet", true));
            Assert.False(PottedPlantDistributionRules.IsValidMainOfficeDestination("Outlet", true));
        }
    }
}
