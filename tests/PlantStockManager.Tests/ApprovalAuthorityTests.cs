using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Sowing approval authority (Services/DirectSowingRules.cs): ONLY the
    // supervisor assigned to the sowing may approve it, and never the person
    // who recorded it. ReadyConfirmationRepository.ConfirmAsync applies the
    // same function under the sowing's row lock.
    public class ApprovalAuthorityTests
    {
        private const int Akshay = 6, Pratik = 7, Rohit = 1, Prajwal = 21;

        [Fact]
        public void AssignedSupervisor_MayApprove()
        {
            var (ok, error) = DirectSowingRules.CanApprove(assignedSupervisorId: Akshay, createdById: Pratik, createdBy: "Pratik",
                                                           approverId: Akshay, approverName: "Akshay");
            Assert.True(ok);
            Assert.Null(error);
        }

        [Fact]
        public void AnotherSupervisor_IsRefused_PermissionAloneIsNotEnough()
        {
            var (ok, error) = DirectSowingRules.CanApprove(Akshay, Pratik, "Pratik", Prajwal, "Prajwal");
            Assert.False(ok);
            Assert.Equal(DirectSowingRules.NotAssignedMessage, error);
        }

        [Fact]
        public void SystemAdministrator_NotAssigned_IsRefused()
        {
            var (ok, _) = DirectSowingRules.CanApprove(Akshay, Pratik, "Pratik", Rohit, "Rohit");
            Assert.False(ok);
        }

        [Fact]
        public void SelfApproval_IsRefused_EvenWhenAssigned()
        {
            // The sowing already in PlantsIMS2_Test was recorded by Prajwal with
            // Prajwal as its own supervisor: he could never approve it.
            var (ok, error) = DirectSowingRules.CanApprove(Prajwal, Prajwal, "Prajwal", Prajwal, "Prajwal");
            Assert.False(ok);
            Assert.Equal(DirectSowingRules.OwnSowingMessage, error);
        }

        [Fact]
        public void NoSupervisorAssigned_NobodyMayApprove()
        {
            var (ok, error) = DirectSowingRules.CanApprove(null, Pratik, "Pratik", Akshay, "Akshay");
            Assert.False(ok);
            Assert.Equal(DirectSowingRules.NoSupervisorMessage, error);
        }

        [Fact]
        public void UnidentifiedApprover_IsRefused()
            => Assert.False(DirectSowingRules.CanApprove(Akshay, Pratik, "Pratik", null, "Akshay").Ok);

        // ---- assigning the supervisor on the sowing ---------------------

        private static readonly int[] Approvers = { Akshay, Rohit, Prajwal };

        [Fact]
        public void Assignment_EligibleSupervisor_IsAccepted()
            => Assert.True(DirectSowingRules.ValidateSupervisorAssignment(Akshay, Pratik, Approvers).Ok);

        [Fact]
        public void Assignment_IsRequired()
        {
            Assert.False(DirectSowingRules.ValidateSupervisorAssignment(null, Pratik, Approvers).Ok);
            Assert.False(DirectSowingRules.ValidateSupervisorAssignment(0, Pratik, Approvers).Ok);
        }

        [Fact]
        public void Assignment_ToYourself_IsRefused()
        {
            // Akshay records a sowing: he cannot be its supervisor (another
            // approver, e.g. a System Administrator, must be chosen).
            var (ok, error) = DirectSowingRules.ValidateSupervisorAssignment(Akshay, Akshay, Approvers);
            Assert.False(ok);
            Assert.Contains("cannot assign yourself", error);
            Assert.True(DirectSowingRules.ValidateSupervisorAssignment(Rohit, Akshay, Approvers).Ok);
        }

        [Fact]
        public void Assignment_NonApprover_IsRefused()
        {
            var (ok, error) = DirectSowingRules.ValidateSupervisorAssignment(Pratik /* Sowing Operator */, Rohit, Approvers);
            Assert.False(ok);
            Assert.Contains("not an active user who can approve", error);
        }

        [Theory]
        [InlineData("Sown", 0, 0, true)]
        [InlineData("Sown", 10, 0, false)]
        [InlineData("Sown", 0, 5, false)]
        [InlineData("Completed", 450, 50, false)]
        [InlineData("Cancelled", 0, 0, false)]
        public void SupervisorChange_OnlyBeforeApproval(string status, decimal ready, decimal wastage, bool allowed)
            => Assert.Equal(allowed, DirectSowingRules.CanChangeSupervisor(status, ready, wastage));

        // ---- quantities at approval (duplicate / invalid / wastage) ------

        [Fact]
        public void ReadyStockQuantity_IsTheApprovedReady_AndWastageBalances()
        {
            var (ok, wastage, _) = DirectSowingRules.ComputeApproval(quantitySown: 4000, alreadyReady: 0, alreadyWasted: 0, readyNow: 3700, wastageReason: "Disease");
            Assert.True(ok);
            Assert.Equal(300, wastage);          // 3700 ready + 300 wastage = 4000 sown
        }

        [Fact]
        public void DuplicateApproval_IsRefused()
            => Assert.False(DirectSowingRules.ComputeApproval(4000, 3700, 300, 1, null).Ok);

        [Theory]
        [InlineData(4001, "Disease")]   // more than sown
        [InlineData(-1, null)]          // negative
        [InlineData(3700.5, "Disease")] // fractional plants
        [InlineData(3700, null)]        // wastage 300 without a reason
        [InlineData(3700, "Rain")]      // unknown reason
        public void InvalidApprovalQuantities_AreRefused(decimal ready, string? reason)
            => Assert.False(DirectSowingRules.ComputeApproval(4000, 0, 0, ready, reason).Ok);
    }
}
