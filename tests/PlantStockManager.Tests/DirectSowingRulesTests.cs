using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Seed Stock -> Direct Sowing -> Supervisor Approval -> Ready Stock rules
    // (Services/DirectSowingRules.cs). The repositories apply the same
    // functions inside their locked SQL transactions.
    public class DirectSowingRulesTests
    {
        // ---- whole plants only -------------------------------------------

        [Theory]
        [InlineData(0, true)]
        [InlineData(1, true)]
        [InlineData(500, true)]
        [InlineData(-3, true)]
        [InlineData(0.5, false)]
        [InlineData(10.25, false)]
        [InlineData(-3.5, false)]
        public void IsWholeNumber(decimal quantity, bool expected)
            => Assert.Equal(expected, DirectSowingRules.IsWholeNumber(quantity));

        [Fact]
        public void Sowing_WholeQuantityWithinStock_IsAllowed()
        {
            var (ok, remaining, error) = DirectSowingRules.CheckSeedAvailability(1000, 0, 500);
            Assert.True(ok);
            Assert.Equal(500, remaining);
            Assert.Null(error);
        }

        [Fact]
        public void Sowing_FractionalQuantity_IsRefused()
        {
            var (ok, _, error) = DirectSowingRules.CheckSeedAvailability(1000, 0, 10.5m);
            Assert.False(ok);
            Assert.Contains("whole number", error);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public void Sowing_ZeroOrNegativeQuantity_IsRefused(decimal quantity)
        {
            var (ok, _, _) = DirectSowingRules.CheckSeedAvailability(1000, 0, quantity);
            Assert.False(ok);
        }

        [Fact]
        public void Sowing_MoreThanAvailable_IsRefused_InTransitCountsAsUnavailable()
        {
            var (ok, remaining, error) = DirectSowingRules.CheckSeedAvailability(1000, 300, 701);
            Assert.False(ok);
            Assert.Equal(700, remaining);
            Assert.Contains("Insufficient", error);
        }

        [Fact]
        public void Approval_FractionalReadyQuantity_IsRefused()
        {
            var (ok, _, error) = DirectSowingRules.ComputeApproval(500, 0, 0, 449.5m, "Disease");
            Assert.False(ok);
            Assert.Contains("whole number", error);
        }

        // ---- approval arithmetic -----------------------------------------

        [Fact]
        public void Approval_WastageIsSownMinusReady_AndNeedsAReason()
        {
            var withReason = DirectSowingRules.ComputeApproval(500, 0, 0, 450, "Germination failure");
            Assert.True(withReason.Ok);
            Assert.Equal(50, withReason.Wastage);

            var withoutReason = DirectSowingRules.ComputeApproval(500, 0, 0, 450, null);
            Assert.False(withoutReason.Ok);
            Assert.Contains("Wastage Reason is required", withoutReason.Error);
        }

        [Fact]
        public void Approval_NoWastage_NeedsNoReason()
        {
            var (ok, wastage, _) = DirectSowingRules.ComputeApproval(500, 0, 0, 500, null);
            Assert.True(ok);
            Assert.Equal(0, wastage);
        }

        [Fact]
        public void Approval_ReadyAboveSown_IsRefused()
        {
            var (ok, _, error) = DirectSowingRules.ComputeApproval(500, 0, 0, 501, null);
            Assert.False(ok);
            Assert.Contains("cannot exceed", error);
        }

        [Fact]
        public void Approval_AlreadyFullyApproved_IsRefused_NoDoubleApproval()
        {
            var (ok, _, error) = DirectSowingRules.ComputeApproval(500, 450, 50, 0, null);
            Assert.False(ok);
            Assert.Contains("already been fully approved", error);
        }

        [Fact]
        public void Approval_UnknownWastageReason_IsRefused()
        {
            var (ok, _, _) = DirectSowingRules.ComputeApproval(500, 0, 0, 400, "Rain");
            Assert.False(ok);
        }

        [Fact]
        public void ExpectedReadyDate_IsSowingDatePlusGrowingDays_OrNullWithoutGrowingDays()
        {
            var sown = new DateTime(2026, 9, 25);
            Assert.Equal(new DateTime(2026, 12, 24), DirectSowingRules.ExpectedReadyDate(sown, 90));   // Celosia
            Assert.Null(DirectSowingRules.ExpectedReadyDate(sown, null));
            Assert.Null(DirectSowingRules.ExpectedReadyDate(sown, 0));
        }

        // ---- optional Polyhouse / Area -----------------------------------

        [Fact]
        public void Location_NothingChosen_UsesSeedLotArea_AndNoPolyhouse()
        {
            var (ok, areaId, polyhouseId, _) = DirectSowingRules.ResolveGrowingLocation(seedLotAreaId: 2, requestedAreaId: null, polyhouseId: null, polyhouseAreaId: null);
            Assert.True(ok);
            Assert.Equal(2, areaId);
            Assert.Null(polyhouseId);   // nothing invented
        }

        [Fact]
        public void Location_PolyhouseWithoutArea_IsRecorded_AtSeedLotArea()
        {
            var (ok, areaId, polyhouseId, _) = DirectSowingRules.ResolveGrowingLocation(2, null, 6, null);
            Assert.True(ok);
            Assert.Equal(2, areaId);
            Assert.Equal(6, polyhouseId);
        }

        [Fact]
        public void Location_PolyhouseWithArea_UsesThatArea_WhenNoAreaChosen()
        {
            var (ok, areaId, polyhouseId, _) = DirectSowingRules.ResolveGrowingLocation(2, null, 6, 5);
            Assert.True(ok);
            Assert.Equal(5, areaId);
            Assert.Equal(6, polyhouseId);
        }

        [Fact]
        public void Location_ChosenArea_Wins()
        {
            var (ok, areaId, polyhouseId, _) = DirectSowingRules.ResolveGrowingLocation(2, 1, null, null);
            Assert.True(ok);
            Assert.Equal(1, areaId);
            Assert.Null(polyhouseId);
        }

        [Fact]
        public void Location_PolyhouseAssignedToAnotherArea_IsRefused()
        {
            var (ok, _, _, error) = DirectSowingRules.ResolveGrowingLocation(2, 1, 6, 5);
            Assert.False(ok);
            Assert.Contains("different Area", error);
        }

        [Fact]
        public void Location_ZeroIdsMeanNotChosen()
        {
            var (ok, areaId, polyhouseId, _) = DirectSowingRules.ResolveGrowingLocation(2, 0, 0, null);
            Assert.True(ok);
            Assert.Equal(2, areaId);
            Assert.Null(polyhouseId);
        }

        // ---- self-approval -----------------------------------------------

        [Fact]
        public void SelfApproval_SameUserId_IsOwnSowing()
            => Assert.True(DirectSowingRules.IsOwnSowing(createdById: 6, createdBy: "Akshay", approverId: 6, approverName: "Akshay"));

        [Fact]
        public void SelfApproval_OtherSupervisorOrAdministrator_MayApprove()
        {
            Assert.False(DirectSowingRules.IsOwnSowing(6, "Akshay", 1, "Rohit"));   // System Administrator
            Assert.False(DirectSowingRules.IsOwnSowing(7, "Pratik", 6, "Akshay"));  // Akshay approves another operator's sowing
        }

        [Fact]
        public void SelfApproval_IdWins_OverName()
            => Assert.False(DirectSowingRules.IsOwnSowing(6, "Akshay", 21, "akshay"));

        [Fact]
        public void SelfApproval_WithoutId_FallsBackToUsername_CaseInsensitive()
        {
            Assert.True(DirectSowingRules.IsOwnSowing(null, " Akshay ", 6, "akshay"));
            Assert.False(DirectSowingRules.IsOwnSowing(null, "Pratik", 6, "Akshay"));
            Assert.False(DirectSowingRules.IsOwnSowing(null, null, 6, "Akshay"));
        }
    }
}
