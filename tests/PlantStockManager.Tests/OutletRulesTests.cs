using PlantStockManager.Services;
using Xunit;

namespace PlantStockManager.Tests
{
    public class OutletRulesTests
    {
        // ---- Purchase -------------------------------------------------

        [Fact]
        public void Purchase_ValidQuantityAndSupplier_IsAccepted()
            => Assert.True(OutletRules.ValidatePurchase(500, "ABC Nursery").Ok);

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        [InlineData(10.5)]
        public void Purchase_InvalidQuantity_IsRefused(decimal qty)
            => Assert.False(OutletRules.ValidatePurchase(qty, "ABC Nursery").Ok);

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Purchase_NoSupplier_IsRefused(string? supplier)
            => Assert.False(OutletRules.ValidatePurchase(500, supplier).Ok);

        // ---- Sale / booking items --------------------------------------

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(5.5)]
        public void ItemQuantity_Invalid_IsRefused(decimal qty)
            => Assert.False(OutletRules.ValidateItemQuantity(qty).Ok);

        [Fact]
        public void ItemQuantity_WholeAndPositive_IsAccepted()
            => Assert.True(OutletRules.ValidateItemQuantity(20).Ok);

        [Fact]
        public void SaleWithNoItems_IsRefused()
            => Assert.False(OutletRules.ValidateSaleHasItems(0).Ok);

        [Fact]
        public void SaleWithItems_IsAccepted()
            => Assert.True(OutletRules.ValidateSaleHasItems(4).Ok);

        [Fact]
        public void SaleItem_MoreThanAvailable_IsRefused()
        {
            var (ok, error) = OutletRules.ValidateSaleItemAvailable(25, 20);
            Assert.False(ok);
            Assert.Contains("20", error);
        }

        [Fact]
        public void SaleItem_UpToAvailable_IsAccepted()
            => Assert.True(OutletRules.ValidateSaleItemAvailable(20, 20).Ok);

        // ---- Cross-Outlet stock use -------------------------------------

        [Fact]
        public void StockFromAnotherOutlet_IsRefused()
            => Assert.False(OutletStockOwnershipRules.ValidateSameArea(stockAreaId: 5, outletAreaId: 7).Ok);

        [Fact]
        public void StockFromTheSameOutlet_IsAccepted()
            => Assert.True(OutletStockOwnershipRules.ValidateSameArea(stockAreaId: 5, outletAreaId: 5).Ok);
    }

    public class OutletBookingRulesTests
    {
        [Fact]
        public void Collect_UpToOpenQuantity_IsAccepted()
            => Assert.True(OutletBookingRules.ValidateCollect(OutletBookingRules.Pending, 100, 300).Ok);

        [Fact]
        public void Collect_MoreThanOpen_IsRefused()
        {
            var (ok, error) = OutletBookingRules.ValidateCollect(OutletBookingRules.PartiallyCollected, 250, 200);
            Assert.False(ok);
            Assert.Contains("200", error);
        }

        [Theory]
        [InlineData(OutletBookingRules.Completed)]
        [InlineData(OutletBookingRules.Cancelled)]
        public void Collect_OnAClosedBooking_IsRefused(string status)
            => Assert.False(OutletBookingRules.ValidateCollect(status, 10, 100).Ok);

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        [InlineData(2.5)]
        public void Collect_InvalidQuantity_IsRefused(decimal qty)
            => Assert.False(OutletBookingRules.ValidateCollect(OutletBookingRules.Pending, qty, 100).Ok);

        [Theory]
        [InlineData(OutletBookingRules.Pending)]
        [InlineData(OutletBookingRules.PartiallyCollected)]
        public void Cancel_AnOpenBooking_IsAccepted(string status)
            => Assert.True(OutletBookingRules.ValidateCancel(status).Ok);

        [Theory]
        [InlineData(OutletBookingRules.Completed)]
        [InlineData(OutletBookingRules.Cancelled)]
        public void Cancel_AClosedBooking_IsRefused(string status)
            => Assert.False(OutletBookingRules.ValidateCancel(status).Ok);

        [Fact]
        public void Status_NothingCollectedYet_IsPending()
            => Assert.Equal(OutletBookingRules.Pending, OutletBookingRules.StatusAfterCollect(300, 0));

        [Fact]
        public void Status_SomeCollected_IsPartiallyCollected()
            => Assert.Equal(OutletBookingRules.PartiallyCollected, OutletBookingRules.StatusAfterCollect(300, 100));

        [Fact]
        public void Status_AllCollected_IsCompleted()
            => Assert.Equal(OutletBookingRules.Completed, OutletBookingRules.StatusAfterCollect(300, 300));

        [Fact]
        public void IsClosed_OnlyCompletedOrCancelled()
        {
            Assert.False(OutletBookingRules.IsClosed(OutletBookingRules.Pending));
            Assert.False(OutletBookingRules.IsClosed(OutletBookingRules.PartiallyCollected));
            Assert.True(OutletBookingRules.IsClosed(OutletBookingRules.Completed));
            Assert.True(OutletBookingRules.IsClosed(OutletBookingRules.Cancelled));
        }
    }

    // ---- Ready Tray items (Sale/Booking share OutletRules.ValidateItemQuantity
    // and OutletStockOwnershipRules -- a tray line is validated exactly like a
    // potted line; the only extra rule is Wastage's own availability check) ----

    public class OutletTrayItemTests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(-3)]
        [InlineData(2.5)]
        public void TrayQuantity_FractionalZeroOrNegative_IsRefused(decimal trays)
            => Assert.False(OutletRules.ValidateItemQuantity(trays).Ok);

        [Fact]
        public void TrayQuantity_WholePositive_IsAccepted()
            => Assert.True(OutletRules.ValidateItemQuantity(5).Ok);

        [Fact]
        public void TraySaleItem_MoreThanAvailableTrays_IsRefused()
        {
            // Vinca, Cavity 102, Outlet has 20 trays; requesting 25 must fail.
            var (ok, error) = OutletRules.ValidateSaleItemAvailable(25, 20);
            Assert.False(ok);
            Assert.Contains("20", error);
        }

        [Fact]
        public void TraySaleItem_UpToAvailableTrays_IsAccepted()
            => Assert.True(OutletRules.ValidateSaleItemAvailable(5, 20).Ok);

        [Fact]
        public void TrayStockFromAnotherOutlet_IsRefused()
            => Assert.False(OutletStockOwnershipRules.ValidateSameArea(stockAreaId: 3, outletAreaId: 9).Ok);

        [Fact]
        public void TrayStockFromTheSameOutlet_IsAccepted()
            => Assert.True(OutletStockOwnershipRules.ValidateSameArea(stockAreaId: 9, outletAreaId: 9).Ok);
    }

    // ---- Outlet Wastage -------------------------------------------------

    public class OutletWastageRulesTests
    {
        [Fact]
        public void Reasons_ListsTheFiveAllowedValues()
        {
            Assert.Equal(5, OutletWastageRules.Reasons.Count);
            Assert.Contains("Damaged", OutletWastageRules.Reasons);
            Assert.Contains("Died / Wilted", OutletWastageRules.Reasons);
            Assert.Contains("Pest or Disease", OutletWastageRules.Reasons);
            Assert.Contains("Breakage", OutletWastageRules.Reasons);
            Assert.Contains("Other", OutletWastageRules.Reasons);
        }

        [Theory]
        [InlineData("Damaged", true)]
        [InlineData("Breakage", true)]
        [InlineData(null, false)]
        [InlineData("", false)]
        [InlineData("Because I felt like it", false)]
        public void IsValidReason_OnlyKnownValues(string? reason, bool expected)
            => Assert.Equal(expected, OutletWastageRules.IsValidReason(reason));

        [Fact]
        public void Wastage_PottedWholeQuantityWithinStock_IsAccepted()
            => Assert.True(OutletWastageRules.Validate(quantity: 3, available: 10, reason: "Damaged").Ok);

        [Fact]
        public void Wastage_TrayWholeQuantityWithinStock_IsAccepted()
            => Assert.True(OutletWastageRules.Validate(quantity: 2, available: 13, reason: "Pest or Disease").Ok);

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(1.5)]
        public void Wastage_FractionalZeroOrNegativeQuantity_IsRefused(decimal quantity)
            => Assert.False(OutletWastageRules.Validate(quantity, available: 10, reason: "Damaged").Ok);

        [Fact]
        public void Wastage_ExceedsAvailableStock_IsRefused()
        {
            var (ok, error) = OutletWastageRules.Validate(quantity: 15, available: 13, reason: "Damaged");
            Assert.False(ok);
            Assert.Contains("13", error);
        }

        [Fact]
        public void Wastage_UnknownReason_IsRefused()
        {
            var (ok, error) = OutletWastageRules.Validate(quantity: 2, available: 10, reason: "Because I felt like it");
            Assert.False(ok);
            Assert.Contains("reason", error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Wastage_MissingReason_IsRefused()
            => Assert.False(OutletWastageRules.Validate(quantity: 2, available: 10, reason: null).Ok);

        [Fact]
        public void Wastage_FromAnotherOutletsStock_IsRefused()
            => Assert.False(OutletStockOwnershipRules.ValidateSameArea(stockAreaId: 4, outletAreaId: 6).Ok);
    }
}
