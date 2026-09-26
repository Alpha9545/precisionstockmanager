using PlantStockManager.Services;
using Xunit;

namespace PlantStockManager.Tests
{
    // Direct customer sale: not restricted to a particular Area type -- only
    // readiness (available quantity) and the Area being active are checked.
    public class DispatchRulesTests
    {
        [Theory]
        [InlineData(100, true, true, 200)]   // production/MotherPlant-type Area: sale IS allowed
        [InlineData(100, true, true, 100)]   // exactly the available quantity
        public void ValidSale_FromAnyActiveArea_IsAccepted(decimal qty, bool areaExists, bool areaActive, decimal available)
            => Assert.True(DispatchRules.ValidateDirectSale(qty, "Customer", areaExists, areaActive, available).Ok);

        [Fact]
        public void Sale_FromAnInactiveArea_IsRefused()
            => Assert.False(DispatchRules.ValidateDirectSale(10, "Customer", true, false, 100).Ok);

        [Fact]
        public void Sale_WithNoArea_IsRefused()
            => Assert.False(DispatchRules.ValidateDirectSale(10, "Customer", false, false, 100).Ok);

        [Fact]
        public void Sale_MoreThanAvailable_IsRefused()
        {
            var (ok, error) = DispatchRules.ValidateDirectSale(150, "Customer", true, true, 100);
            Assert.False(ok);
            Assert.Contains("100", error);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        [InlineData(10.5)]
        public void Sale_InvalidQuantity_IsRefused(decimal qty)
            => Assert.False(DispatchRules.ValidateDirectSale(qty, "Customer", true, true, 100).Ok);

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Sale_WithNoCustomerName_IsRefused(string? name)
            => Assert.False(DispatchRules.ValidateDirectSale(10, name, true, true, 100).Ok);
    }
}
