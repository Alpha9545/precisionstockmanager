using PlantStockManager.Models;

namespace PlantStockManager.Tests
{
    // Phase 8 (Stock History and Wastage Integration): the display-only
    // classification each unified ledger row gets, per the task's own
    // required movement vocabulary -- Purchase->Stock IN, Issue->Stock
    // OUT, Production->Consumption+Production, Wastage->Wastage
    // transaction, Ready->Ready Stock, Sale/Dispatch->Stock OUT.
    public class UnifiedStockTransactionTests
    {
        private static UnifiedStockTransaction Row(string type, decimal quantity) =>
            new() { TransactionType = type, Quantity = quantity };

        [Theory]
        [InlineData("StockIn", 100, "Purchase / Stock IN")]
        [InlineData("Harvest", 50, "Purchase / Stock IN")]
        [InlineData("Production", 600, "Production")]
        [InlineData("Confirmed", 400, "Ready Stock")]
        [InlineData("Wastage", -20, "Wastage")]
        [InlineData("Dispatch", -30, "Sale / Dispatch (OUT)")]
        [InlineData("Consumption", -10, "Issue (OUT)")]
        public void MovementLabel_MatchesRequiredVocabulary(string type, decimal quantity, string expectedLabel)
        {
            Assert.Equal(expectedLabel, Row(type, quantity).MovementLabel);
        }

        [Fact]
        public void Transfer_IsOutWhenNegative_InWhenPositive()
        {
            Assert.Equal("Issue (OUT)", Row("Transfer", -50).MovementLabel);
            Assert.Equal("Transfer IN", Row("Transfer", 50).MovementLabel);
        }

        [Fact]
        public void ReservationTypes_AreNeverLabeledAsPhysicalMovement()
        {
            // Reservation/ReservationRelease never move PhysicalQuantity --
            // the label must never claim a Stock IN/OUT movement for these.
            Assert.Equal("Reserved (not yet physical movement)", Row("Reservation", 10).MovementLabel);
            Assert.Equal("Reservation Released", Row("ReservationRelease", -10).MovementLabel);
        }
    }
}
