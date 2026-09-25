using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using PlantStockManager.Data;
using PlantStockManager.Pages.Production.ReadyConfirmation;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Supervisor Approval by Actual Ready Trays (DirectSowingRules.ComputeTrayApproval):
    //   Actual Ready Seedlings = Actual Ready Trays x the SOWING's cavity
    //   Wastage                = Seeds Used - Actual Ready Seedlings
    //   Wastage %              = Wastage / Seeds Used x 100
    // Example sowing: 14,492 seeds at 102 Cavity -> 142 trays, 14,484 seeds used.
    public class TrayApprovalTests
    {
        private const decimal SeedsUsed = 14484;
        private const int SowingTrays = 142;
        private const string Cavity = "102 Cavity";

        [Theory]
        [InlineData(142, 14484, 0, 0.00)]
        [InlineData(130, 13260, 1224, 8.45)]
        [InlineData(100, 10200, 4284, 29.58)]
        [InlineData(1, 102, 14382, 99.30)]
        public void ActualTrays_GiveSeedlingsWastageAndPercent(int trays, decimal seedlings, decimal wastage, decimal pct)
        {
            var r = DirectSowingRules.ComputeTrayApproval(SeedsUsed, SowingTrays, Cavity, 0, 0, trays, "Germination failure");
            Assert.True(r.Ok, r.Error);
            Assert.Equal(trays, r.ActualTrays);
            Assert.Equal(seedlings, r.ActualSeedlings);
            Assert.Equal(wastage, r.Wastage);
            Assert.Equal(pct, r.WastagePercent);
        }

        [Fact]
        public void NoFixedPercentage_WastageFollowsTheTrays()
        {
            // Every tray count gives a different wastage; nothing is pinned at 30%.
            for (var t = 1; t <= SowingTrays; t++)
            {
                var r = DirectSowingRules.ComputeTrayApproval(SeedsUsed, SowingTrays, Cavity, 0, 0, t, "Disease");
                Assert.True(r.Ok);
                Assert.Equal(SeedsUsed - t * 102m, r.Wastage);
            }
        }

        [Theory]
        [InlineData("9 Cavity", 10, 90, 7, 63)]
        [InlineData("24 Cavity", 166, 3984, 150, 3600)]
        [InlineData("42 Cavity", 3, 126, 2, 84)]
        [InlineData("150 Cavity", 4, 600, 4, 600)]
        public void OtherCavities_UseTheSowingCavity(string cavity, int sowingTrays, decimal seedsUsed, int trays, decimal seedlings)
        {
            var r = DirectSowingRules.ComputeTrayApproval(seedsUsed, sowingTrays, cavity, 0, 0, trays, "Disease");
            Assert.True(r.Ok, r.Error);
            Assert.Equal(seedlings, r.ActualSeedlings);
            Assert.Equal(seedsUsed - seedlings, r.Wastage);
        }

        [Fact]
        public void MoreTraysThanSown_IsRefused()
        {
            var r = DirectSowingRules.ComputeTrayApproval(SeedsUsed, SowingTrays, Cavity, 0, 0, 143, null);
            Assert.False(r.Ok);
            Assert.Contains("cannot exceed the 142 trays sown", r.Error);
        }

        [Theory]
        [InlineData(100.5)]
        [InlineData(0.5)]
        [InlineData(141.99)]
        public void FractionalTrays_AreRefused(decimal trays)
        {
            var r = DirectSowingRules.ComputeTrayApproval(SeedsUsed, SowingTrays, Cavity, 0, 0, trays, "Disease");
            Assert.False(r.Ok);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void ZeroOrNegativeTrays_AreRefused(decimal trays)
        {
            var r = DirectSowingRules.ComputeTrayApproval(SeedsUsed, SowingTrays, Cavity, 0, 0, trays, "Disease");
            Assert.False(r.Ok);
            Assert.Contains("at least 1 complete tray", r.Error);
        }

        [Theory]
        [InlineData("10 Cavity")]
        [InlineData("")]
        [InlineData(null)]
        public void InvalidOrMissingSowingCavity_IsRefused(string? cavity)
            => Assert.False(DirectSowingRules.ComputeTrayApproval(SeedsUsed, SowingTrays, cavity, 0, 0, 100, "Disease").Ok);

        [Fact]
        public void WrongCavity_GivesDifferentSeedlings_CavityComesOnlyFromTheSowing()
        {
            // The cavity is an input of the rule only through the stored sowing;
            // a 150-cavity value would not match the 102-cavity sowing's seeds.
            var right = DirectSowingRules.ComputeTrayApproval(SeedsUsed, SowingTrays, Cavity, 0, 0, 100, "Disease");
            var wrong = DirectSowingRules.ComputeTrayApproval(SeedsUsed, SowingTrays, "150 Cavity", 0, 0, 100, "Disease");
            Assert.Equal(10200, right.ActualSeedlings);
            Assert.NotEqual(right.ActualSeedlings, wrong.ActualSeedlings);
        }

        [Fact]
        public void Wastage_RequiresReason_NoWastage_DoesNot()
        {
            Assert.False(DirectSowingRules.ComputeTrayApproval(SeedsUsed, SowingTrays, Cavity, 0, 0, 100, null).Ok);
            Assert.True(DirectSowingRules.ComputeTrayApproval(SeedsUsed, SowingTrays, Cavity, 0, 0, 142, null).Ok);
        }

        [Fact]
        public void MissingSowingTrays_IsRefused()
            => Assert.False(DirectSowingRules.ComputeTrayApproval(SeedsUsed, null, Cavity, 0, 0, 100, "Disease").Ok);

        [Fact]
        public void AlreadyApproved_IsRefused()
            => Assert.False(DirectSowingRules.ComputeTrayApproval(SeedsUsed, SowingTrays, Cavity, 10200, 4284, 1, "Disease").Ok);

        // ---- The browser cannot supply seedlings, wastage or cavity ---------

        [Fact]
        public void ApprovalPage_BindsOnlyTraysReasonResponsibleAndRemarks()
        {
            var bound = typeof(ConfirmModel).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<BindPropertyAttribute>() != null)
                .Select(p => p.Name).OrderBy(n => n).ToArray();
            Assert.Equal(new[] { "ActualReadyTrays", "Remarks", "ResponsiblePersonId", "SeedSowingId", "WastageReason" }, bound);
        }

        [Fact]
        public void ApprovalPage_ClassLevelBindingIsNotUsed()
            => Assert.Null(typeof(ConfirmModel).GetCustomAttribute<BindPropertiesAttribute>());

        [Fact]
        public void ConfirmAsync_TakesTraysOnly_NoSeedlingsWastageOrCavity()
        {
            var method = typeof(ReadyConfirmationRepository).GetMethod(nameof(ReadyConfirmationRepository.ConfirmAsync))!;
            var names = method.GetParameters().Select(p => p.Name!.ToLowerInvariant()).ToArray();
            Assert.Contains("actualreadytrays", names);
            Assert.DoesNotContain(names, n => n.Contains("seedling") || n.Contains("cavity") || n.Contains("readyquantity")
                                              || (n.Contains("wastage") && n != "wastagereason"));
        }

        [Fact]
        public void TrayPreview_TakesOnlySowingIdAndTrays()
        {
            var method = typeof(ConfirmModel).GetMethod(nameof(ConfirmModel.OnGetTrayPreviewAsync))!;
            Assert.Equal(new[] { "id", "trays" }, method.GetParameters().Select(p => p.Name).ToArray());
        }

        [Theory]
        [InlineData(0, 14484)]
        [InlineData(4284, 14484)]
        [InlineData(1224, 14484)]
        public void WastagePercent_TwoDecimals(decimal wastage, decimal seedsUsed)
            => Assert.Equal(Math.Round(wastage / seedsUsed * 100m, 2, MidpointRounding.AwayFromZero),
                            DirectSowingRules.WastagePercent(wastage, seedsUsed));

        [Fact]
        public void WastagePercent_NoSeeds_IsZero() => Assert.Equal(0m, DirectSowingRules.WastagePercent(0, 0));
    }
}
