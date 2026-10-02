using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Pure (DB-free) coverage of the Cutting Tray Sowing destination rule:
    // the Sowing Supervisor's confirmation screen must offer, and the
    // repository must accept, a real, active Polyhouse -- never Outlet (a
    // customer-facing stock location, never a sowing destination), never an
    // inactive Area. 2026-10-02: a real Polyhouse filed under a Main
    // Office-type Area (e.g. "Facility-5") IS a valid destination -- only
    // that Area's own Cutting Stock depot concept was ever meant to be
    // excluded, never its named physical Polyhouses.
    public class CuttingSowingDestinationRulesTests
    {
        [Fact]
        public void ARealPolyhouseUnderAMainOfficeAreaIsAValidDestination()
        {
            var (ok, error) = CuttingSowingDestinationRules.ValidateDestination(2, true, "MainOffice");
            Assert.True(ok, error);
            Assert.Null(error);
        }

        [Fact]
        public void Outlet_IsNeverAValidDestination()
        {
            var (ok, error) = CuttingSowingDestinationRules.ValidateDestination(5, true, OutletRules.AreaType);
            Assert.False(ok);
            Assert.Equal(CuttingSowingDestinationRules.InvalidDestinationMessage, error);
        }

        [Fact]
        public void InactiveArea_IsNeverAValidDestination()
        {
            var (ok, error) = CuttingSowingDestinationRules.ValidateDestination(1, false, "MotherPlant");
            Assert.False(ok);
            Assert.Equal(CuttingSowingDestinationRules.InvalidDestinationMessage, error);
        }

        [Fact]
        public void MissingArea_IsNeverAValidDestination()
        {
            var (ok, error) = CuttingSowingDestinationRules.ValidateDestination(null, true, "MotherPlant");
            Assert.False(ok);
            Assert.Equal(CuttingSowingDestinationRules.InvalidDestinationMessage, error);
        }

        [Theory]
        [InlineData("MotherPlant")]
        [InlineData("Kunjir")]
        [InlineData("Kiran")]
        [InlineData(null)]
        public void AnyOtherActiveArea_IsAValidGrowingDestination(string? areaType)
        {
            var (ok, error) = CuttingSowingDestinationRules.ValidateDestination(1, true, areaType);
            Assert.True(ok, error);
            Assert.Null(error);
        }
    }
}
