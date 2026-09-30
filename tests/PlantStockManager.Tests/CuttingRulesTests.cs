using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Mother Plant -> Cutting -> Delivery -> Main Office confirmation.
    public class CuttingRulesTests
    {
        [Theory]
        [InlineData(1)]
        [InlineData(8000)]
        public void CuttingProduction_WholePositive_IsAccepted(decimal q) => Assert.True(CuttingRules.ValidateProductionQuantity(q).Ok);

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        [InlineData(10.5)]
        public void CuttingProduction_ZeroNegativeFractional_IsRefused(decimal q) => Assert.False(CuttingRules.ValidateProductionQuantity(q).Ok);

        [Fact]
        public void Delivery_MoreThanAvailable_IsRefused()
        {
            var (ok, error) = CuttingRules.ValidateDelivery(1001, 1000);
            Assert.False(ok);
            Assert.Contains("1,000", error);
        }

        [Fact]
        public void Confirmation_AllReceived_NoTransitLoss()
        {
            var (ok, loss, _) = CuttingRules.ConfirmDelivery(sent: 1000, received: 1000, shortfallReason: null);
            Assert.True(ok);
            Assert.Equal(0, loss);
        }

        [Fact]
        public void Confirmation_Shortfall_IsTransitLoss_WithReason()
        {
            var (ok, loss, _) = CuttingRules.ConfirmDelivery(1000, 950, "Damaged in transport");
            Assert.True(ok);
            Assert.Equal(50, loss);   // delivered 1000 -> received 950 -> 50 wastage
        }

        [Fact]
        public void Confirmation_Shortfall_WithoutReason_IsRefused()
            => Assert.False(CuttingRules.ConfirmDelivery(1000, 950, null).Ok);

        [Theory]
        [InlineData(1001)]   // more than sent
        [InlineData(-1)]
        [InlineData(950.5)]
        public void Confirmation_InvalidReceived_IsRefused(decimal received)
            => Assert.False(CuttingRules.ConfirmDelivery(1000, received, "x").Ok);

        // Correction #3 -- AREA ISOLATION: a pool is usable only by users who may access the pool's OWN Area
        // (Main Office stock is the Main Office Area's stock, not every Area's).
        [Theory]
        [InlineData(true, true)]     // the user may access the pool's Area (their own Area, or Main Office for a Main Office user)
        [InlineData(false, false)]   // another Area's stock -- including Main Office stock for a production Area's user
        public void CuttingSource_OnlyThePoolsOwnAreaUsers(bool canAccessPoolArea, bool expected)
            => Assert.Equal(expected, CuttingRules.CanUseAsSource(canAccessPoolArea));
    }
}
