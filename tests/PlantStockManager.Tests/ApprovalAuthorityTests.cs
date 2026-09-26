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
        public void OwnSowingMessage_NamesOnlyTheAssignedSupervisor()
        {
            // Phase D: the refusal no longer suggests that another supervisor
            // or an administrator may approve instead.
            Assert.DoesNotContain("Administrator", DirectSowingRules.OwnSowingMessage);
            Assert.DoesNotContain("Another", DirectSowingRules.OwnSowingMessage);
            Assert.Contains("assigned", DirectSowingRules.OwnSowingMessage);
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
        // Phase D: the eligible list holds Sowing Supervisors only
        // (UserRoleRepository.GetSowingApproversAsync) -- here Akshay and a
        // second Sowing Supervisor (id 30); System Administrators are not in it.

        private const int SecondSowingSupervisor = 30;
        private static readonly int[] Approvers = { Akshay, SecondSowingSupervisor };

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
            Assert.True(DirectSowingRules.ValidateSupervisorAssignment(SecondSowingSupervisor, Akshay, Approvers).Ok);
        }

        [Fact]
        public void Assignment_SystemAdministrator_IsRefused()
        {
            // An administrator is not a Sowing Supervisor, so cannot be the
            // assigned approver (no bypass through assignment either).
            var (ok, error) = DirectSowingRules.ValidateSupervisorAssignment(Rohit, Pratik, Approvers);
            Assert.False(ok);
            Assert.Contains("not an active Sowing Supervisor", error);
        }

        [Fact]
        public void Assignment_NonApprover_IsRefused()
        {
            var (ok, error) = DirectSowingRules.ValidateSupervisorAssignment(Pratik /* Sowing Operator */, Rohit, Approvers);
            Assert.False(ok);
            Assert.Contains("not an active Sowing Supervisor", error);
        }

        // Phase D: a saved sowing's supervisor can never be changed (so
        // nobody can re-assign a sowing to themselves and then approve it).
        // The Edit page accepts only the sowing id and its Remarks.
        [Fact]
        public void EditPage_BindsOnlyIdAndRemarks()
        {
            var bound = typeof(PlantStockManager.Pages.Production.SeedSowing.EditModel)
                .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Where(p => System.Reflection.CustomAttributeExtensions.GetCustomAttribute<Microsoft.AspNetCore.Mvc.BindPropertyAttribute>(p) != null)
                .Select(p => p.Name).OrderBy(n => n).ToArray();
            Assert.Equal(new[] { "Id", "Remarks" }, bound);
        }

        [Fact]
        public void Repository_HasNoSupervisorUpdate()
        {
            // UpdateDetailsAsync (which could change SupervisorId) is gone;
            // only the Remarks can be updated.
            var methods = typeof(PlantStockManager.Data.SeedSowingRepository).GetMethods().Select(m => m.Name).ToList();
            Assert.DoesNotContain("UpdateDetailsAsync", methods);
            Assert.Contains("UpdateRemarksAsync", methods);
            var p = typeof(PlantStockManager.Data.SeedSowingRepository).GetMethod("UpdateRemarksAsync")!.GetParameters().Select(x => x.Name).ToArray();
            Assert.DoesNotContain(p, n => n!.Contains("upervisor", StringComparison.OrdinalIgnoreCase));
        }

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
