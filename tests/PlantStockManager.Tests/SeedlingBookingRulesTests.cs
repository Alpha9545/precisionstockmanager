using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Regression tests for the (unchanged) Ready Stock -> Booking ->
    // Reservation -> Dispatch rules (Services/SeedlingBookingRules.cs), so the
    // sowing-approval and tray changes cannot silently break fulfilment.
    public class SeedlingBookingRulesTests
    {
        private static ReadyBatchOption Batch(int id, int species, string sown, decimal available, string? firstApproved = null) => new()
        {
            ReadyStockId = id, SpeciesId = species, SowingDate = DateTime.Parse(sown),
            FirstConfirmationDate = firstApproved == null ? null : DateTime.Parse(firstApproved),
            Quantity = available, AvailableQuantity = available
        };

        [Fact]
        public void Reserve_OldestBatchFirst_BookedVarietyOnly()
        {
            var batches = new[]
            {
                Batch(3, 3106, "2026-09-25", 2700),   // the Ready Stock batch already in PlantsIMS2_Test
                Batch(1, 3106, "2026-08-01", 100),
                Batch(2, 3131, "2026-07-01", 999),    // another variety: never used for this booking
            };
            var (ok, plan, error) = SeedlingBookingRules.PlanFifo(batches, 3106, 500);
            Assert.True(ok, error);
            Assert.Equal(new[] { (1, 100m), (3, 400m) }, plan.ToArray());
        }

        [Fact]
        public void Reserve_MoreThanAvailable_IsRefused_AllOrNothing()
        {
            var (ok, plan, error) = SeedlingBookingRules.PlanFifo(new[] { Batch(3, 3106, "2026-09-25", 2700) }, 3106, 2701);
            Assert.False(ok);
            Assert.Empty(plan);
            Assert.Contains(SeedlingBookingRules.InsufficientStockMessage, error);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        [InlineData(10.5)]
        public void Reserve_InvalidQuantity_IsRefused(decimal quantity)
            => Assert.False(SeedlingBookingRules.PlanFifo(new[] { Batch(3, 3106, "2026-09-25", 2700) }, 3106, quantity).Ok);

        [Fact]
        public void Release_NewestAllocationFirst()
        {
            var plan = SeedlingBookingRules.PlanRelease(new[] { (1, 100m), (2, 300m), (3, 0m) }, 350);
            Assert.Equal(new[] { (2, 300m), (1, 50m) }, plan.ToArray());
        }

        [Fact]
        public void Revision_NeverBelowDispatched_AndVarietyFixedAfterDispatch()
        {
            Assert.False(SeedlingBookingRules.ValidateRevision(dispatched: 600, reserved: 0, newQuantity: 500, varietyChanged: false).Ok);
            Assert.False(SeedlingBookingRules.ValidateRevision(600, 0, 1000, varietyChanged: true).Ok);
            var (ok, release, _) = SeedlingBookingRules.ValidateRevision(0, 800, 500, false);
            Assert.True(ok);
            Assert.Equal(300, release);
        }

        [Fact]
        public void BatchChoice_SameVariety_SubstitutionNeedsReason_OtherSpeciesRefused()
        {
            Assert.True(SeedlingBookingRules.ValidateBatchChoice(3106, 4, 3106, 4, null).Ok);
            Assert.False(SeedlingBookingRules.ValidateBatchChoice(3106, 4, 3131, 4, null).Ok);            // substitution without reason
            Assert.True(SeedlingBookingRules.ValidateBatchChoice(3106, 4, 3131, 4, "Customer agreed").IsSubstitution);
            Assert.False(SeedlingBookingRules.ValidateBatchChoice(3106, 4, 9999, 1, "x").Ok);             // other species
        }

        [Theory]
        [InlineData(1000, 1000, null)]
        [InlineData(1000, 1001, "exceeds")]
        [InlineData(1000, 0, "whole numbers")]
        [InlineData(1000, 2.5, "whole numbers")]
        public void DispatchLine_OnlyWholePlants_WithinAllocation(decimal open, decimal quantity, string? errorPart)
        {
            var error = SeedlingBookingRules.ValidateDispatchLine(open, quantity);
            if (errorPart == null) Assert.Null(error);
            else Assert.Contains(errorPart, error);
        }

        [Fact]
        public void Status_CompletedOnlyWhenFullyDispatched()
        {
            Assert.Equal("Completed", SeedlingBookingRules.StatusAfterDispatch(1000, 1000));   // booking 5010 in PlantsIMS2_Test
            Assert.Equal("Pending", SeedlingBookingRules.StatusAfterDispatch(1000, 999));
            Assert.Equal("Reserved", SeedlingBookingRules.Stage("Pending", 1000, 1000, 0, false, true));
            Assert.Equal("Partially dispatched – remainder reserved", SeedlingBookingRules.Stage("Pending", 1000, 400, 600, false, true));
            Assert.Equal("Fully dispatched", SeedlingBookingRules.Stage("Completed", 1000, 0, 1000, false, true));
        }
    }
}
