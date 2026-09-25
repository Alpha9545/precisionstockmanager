using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Phase 1:
    //   F1 -- whoever edits a sowing can never make themselves its supervisor
    //         (DirectSowingRules.ValidateSupervisorChange; the repository
    //         re-applies it under lock);
    //   F2 -- only the sowing's assigned supervisor may cancel its approval
    //         (DirectSowingRules.CanCancelApproval, applied by
    //         ReadyConfirmationRepository.CancelAsync under the sowing lock).
    public class SupervisorChangeAndCancelTests
    {
        private const int Creator = 7, Supervisor = 6, OtherApprover = 21, Admin = 1;
        private static readonly int[] Eligible = { Supervisor, OtherApprover, Admin };

        [Fact]
        public void Editor_CannotAssignThemself()
        {
            var (ok, error) = DirectSowingRules.ValidateSupervisorChange(OtherApprover, Creator, OtherApprover, Eligible);
            Assert.False(ok);
            Assert.Equal(DirectSowingRules.EditorCannotBeSupervisorMessage, error);
        }

        [Fact]
        public void Admin_CannotAssignThemself_EitherNoBypass()
            => Assert.False(DirectSowingRules.ValidateSupervisorChange(Admin, Creator, Admin, Eligible).Ok);

        [Fact]
        public void Editor_MayAssignAnotherEligibleSupervisor()
            => Assert.True(DirectSowingRules.ValidateSupervisorChange(Supervisor, Creator, OtherApprover, Eligible).Ok);

        [Fact]
        public void Editor_CannotAssignTheCreator_OrAnIneligibleUser()
        {
            Assert.False(DirectSowingRules.ValidateSupervisorChange(Creator, Creator, OtherApprover, Eligible.Append(Creator).ToArray()).Ok);
            Assert.False(DirectSowingRules.ValidateSupervisorChange(999, Creator, OtherApprover, Eligible).Ok);
        }

        [Fact]
        public void AssignedSupervisor_MayCancelApproval()
        {
            var (ok, error) = DirectSowingRules.CanCancelApproval(Supervisor, Supervisor);
            Assert.True(ok);
            Assert.Null(error);
        }

        [Theory]
        [InlineData(OtherApprover)]
        [InlineData(Admin)]
        [InlineData(Creator)]
        public void AnyoneElse_CannotCancelApproval(int userId)
        {
            var (ok, error) = DirectSowingRules.CanCancelApproval(Supervisor, userId);
            Assert.False(ok);
            Assert.Equal(DirectSowingRules.NotAssignedCancelMessage, error);
        }

        [Fact]
        public void UnknownUser_OrNoSupervisor_CannotCancelApproval()
        {
            Assert.False(DirectSowingRules.CanCancelApproval(Supervisor, null).Ok);
            Assert.False(DirectSowingRules.CanCancelApproval(null, Supervisor).Ok);
        }
    }
}
