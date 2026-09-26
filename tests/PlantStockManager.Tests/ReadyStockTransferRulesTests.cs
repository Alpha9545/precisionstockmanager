using PlantStockManager.Services;
using Xunit;

namespace PlantStockManager.Tests
{
    // Sending Ready Stock (seedling trays) to Main Office or an Outlet: the
    // whole batch moves together, only before anything is reserved/dispatched.
    public class ReadyStockTransferRulesTests
    {
        [Fact]
        public void WholeBatch_NoBookingsYet_IsAccepted()
            => Assert.True(ReadyStockTransferRules.ValidateTransfer(1000, 1000, 0, 0, sourceAreaId: 1, destinationAreaId: 2).Ok);

        [Theory]
        [InlineData(50, 0)]
        [InlineData(0, 50)]
        [InlineData(20, 30)]
        public void AnyReservationOrDispatch_BlocksTheMove(decimal reserved, decimal dispatched)
        {
            var (ok, error) = ReadyStockTransferRules.ValidateTransfer(1000, 1000, reserved, dispatched, 1, 2);
            Assert.False(ok);
            Assert.Contains("cannot be moved", error);
        }

        [Fact]
        public void PartialQuantity_CannotSplitTheBatch()
        {
            var (ok, error) = ReadyStockTransferRules.ValidateTransfer(600, 1000, 0, 0, 1, 2);
            Assert.False(ok);
            Assert.Contains("whole batch", error);
        }

        [Fact]
        public void SameSourceAndDestination_IsRefused()
            => Assert.False(ReadyStockTransferRules.ValidateTransfer(1000, 1000, 0, 0, 1, 1).Ok);

        [Fact]
        public void NoDestinationChosen_IsRefused()
            => Assert.False(ReadyStockTransferRules.ValidateTransfer(1000, 1000, 0, 0, 1, null).Ok);

        [Theory]
        [InlineData(0)]
        [InlineData(-10)]
        [InlineData(10.5)]
        public void InvalidQuantity_IsRefused(decimal qty)
            => Assert.False(ReadyStockTransferRules.ValidateTransfer(qty, 1000, 0, 0, 1, 2).Ok);
    }
}
