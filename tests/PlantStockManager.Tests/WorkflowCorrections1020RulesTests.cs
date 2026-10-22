using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using MpApprovals = PlantStockManager.Pages.Production.MotherPlant.SowingApprovalsModel;

namespace PlantStockManager.Tests
{
    // 2026-10-20 -- the pure rules (no database):
    //   Mother Plant Area TRANSPLANT batches: any date once 2026-10-20_TransplantApprovalAnyDate.sql is applied (still need a
    //   Ready Date; still only an Area Supervisor of their Area); every other workflow keeps its date rule.
    //   "Approved by me": the page binds no identity -- only the tab.
    public class WorkflowCorrections1020RulesTests
    {
        private static readonly DateTime Today = new(2026, 10, 20);
        private static readonly DirectSowingRules.BedApprover Maya = new(true, 9, "Maya Shinde");

        [Theory]
        [InlineData(-5)]   // before the Ready Date
        [InlineData(0)]    // on it
        [InlineData(7)]    // after it
        public void Transplant_AnyDate_ApprovableBeforeOnAndAfterTheReadyDate(int daysPast)
            => Assert.True(BedSowingRules.ValidateApprovalReadyDate(SeedSowing.SourceTransplant, Today.AddDays(-daysPast), Today,
                bedAnyDate: false, transplantAnyDate: true).Ok);

        [Fact]
        public void Transplant_AnyDate_StillNeedsAReadyDate_AndWithoutTheMigrationWaitsForIt()
        {
            Assert.False(BedSowingRules.ValidateApprovalReadyDate(SeedSowing.SourceTransplant, null, Today, false, true).Ok);
            var early = BedSowingRules.ValidateApprovalReadyDate(SeedSowing.SourceTransplant, Today.AddDays(5), Today);
            Assert.Equal((false, string.Format(BedSowingRules.TransplantNotReadyYetMessageFormat, Today.AddDays(5))), early);
            Assert.True(BedSowingRules.ValidateApprovalReadyDate(SeedSowing.SourceTransplant, Today, Today).Ok);
            Assert.True(BedSowingRules.ValidateApprovalReadyDate(SeedSowing.SourceTransplant, Today.AddDays(-1), Today).Ok);
        }

        [Fact]
        public void OtherWorkflows_KeepTheirDateRules_TheTransplantFlagChangesNothingForThem()
        {
            var future = Today.AddDays(5);
            // Bed Sowing: only its own BedAnyDate switch decides
            Assert.False(BedSowingRules.ValidateApprovalReadyDate(SeedSowing.SourceBedSowing, future, Today, bedAnyDate: false, transplantAnyDate: true).Ok);
            Assert.True(BedSowingRules.ValidateApprovalReadyDate(SeedSowing.SourceBedSowing, future, Today, bedAnyDate: true, transplantAnyDate: false).Ok);
            // Seed / Cutting: no date rule, either way
            foreach (var source in new[] { SeedSowing.SourceSeed, SeedSowing.SourceCutting, null })
                foreach (var flag in new[] { false, true })
                    Assert.True(BedSowingRules.ValidateApprovalReadyDate(source, future, Today, false, flag).Ok);
        }

        private static SeedSowing Transplant(DateTime ready) => new()
        {
            Id = 1, SourceType = SeedSowing.SourceTransplant, AreaId = 1, AreaName = "Shree Swami Samarth Agro", CreatedById = 21, CreatedBy = "rec",
            ExpectedReadyDate = ready, CavityType = "24 Cavity", NumberOfTrays = 2, QuantitySown = 48
        };

        [Fact]
        public void ApprovalList_AnyDate_OnlyTheAreasSupervisor_NeverAnotherArea_NeverWithoutPermission()
        {
            var settings = new SowingApprovalRules.Settings(Maya, BedAnyDate: true, TransplantAreaScope: true, TransplantAnyDate: true);
            var early = Transplant(Today.AddDays(10));
            var mine = SowingApprovalRules.Evaluate(early, 113, "a", true, isAreaApprover: true, settings, Today);
            Assert.True(mine.CanApprove, mine.Reason);
            Assert.False(mine.WaitingForReadyDate);
            Assert.True(SowingApprovalRules.Evaluate(early, 115, "b", true, true, settings, Today).CanApprove);                  // second Area Supervisor
            Assert.Equal(string.Format(SowingApprovalRules.NotAreaSupervisorMessageFormat, early.AreaName),
                SowingApprovalRules.Evaluate(early, 110, "other", true, isAreaApprover: false, settings, Today).Reason);   // another Area
            Assert.Equal(SowingApprovalRules.NoApprovePermissionMessage, SowingApprovalRules.Evaluate(early, 3, "v", false, false, settings, Today).Reason);
            // without the migration: waits for the Ready Date (earlier rule)
            Assert.True(SowingApprovalRules.Evaluate(early, 113, "a", true, true, settings with { TransplantAnyDate = false }, Today).WaitingForReadyDate);
            // Bed Sowing is untouched by the transplant flag: still Maya only
            var bed = new SeedSowing { SourceType = SeedSowing.SourceBedSowing, AreaId = 2, AreaName = "Main", CreatedById = 9, ExpectedReadyDate = Today.AddDays(10),
                CavityType = "24 Cavity", NumberOfTrays = 1, QuantitySown = 24 };
            Assert.True(SowingApprovalRules.Evaluate(bed, 9, "maya", true, false, settings, Today).CanApprove);
            Assert.Equal(DirectSowingRules.NotBedApproverMessage("Maya Shinde"), SowingApprovalRules.Evaluate(bed, 113, "a", true, true, settings, Today).Reason);
        }

        // 2026-10-20: the Waiting-for-approval filter -- Authorized = passed every authority check (only the date may remain).
        [Fact]
        public void Authorized_IsTrueOnlyWhenEveryAuthorityCheckPasses()
        {
            var anyDate = new SowingApprovalRules.Settings(Maya, true, true, TransplantAnyDate: true);
            var dated = anyDate with { TransplantAnyDate = false };
            var early = Transplant(Today.AddDays(10));
            Assert.True(SowingApprovalRules.Evaluate(early, 113, "a", true, true, anyDate, Today).Authorized);           // approvable now
            var waits = SowingApprovalRules.Evaluate(early, 113, "a", true, true, dated, Today);
            Assert.True(waits.Authorized && waits.WaitingForReadyDate && !waits.CanApprove);                              // theirs, from the date
            Assert.False(SowingApprovalRules.Evaluate(early, 110, "o", true, false, anyDate, Today).Authorized);         // another Area
            Assert.False(SowingApprovalRules.Evaluate(early, 110, "o", true, false, dated, Today).Authorized);           // ... whatever the date rule
            Assert.False(SowingApprovalRules.Evaluate(early, 3, "v", false, false, anyDate, Today).Authorized);          // no permission
            Assert.False(SowingApprovalRules.Evaluate(early, null, null, true, true, anyDate, Today).Authorized);        // unidentified user
            var bed = new SeedSowing { SourceType = SeedSowing.SourceBedSowing, AreaId = 2, AreaName = "Main", CreatedById = 9, ExpectedReadyDate = Today.AddDays(10),
                CavityType = "24 Cavity", NumberOfTrays = 1, QuantitySown = 24 };
            Assert.True(SowingApprovalRules.Evaluate(bed, 9, "maya", true, false, anyDate, Today).Authorized);
            Assert.False(SowingApprovalRules.Evaluate(bed, 13, "s", true, false, anyDate, Today).Authorized);
            Assert.False(SowingApprovalRules.Evaluate(bed, 21, "admin", true, true, anyDate, Today).Authorized);         // Bed Sowing: not even admin
        }

        [Fact]
        public void ApprovedByMePage_BindsOnlyTheTab_NeverAnIdentity()
        {
            var bound = typeof(MpApprovals).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<BindPropertyAttribute>() != null).Select(p => p.Name).ToArray();
            Assert.Equal(new[] { "Tab" }, bound);
            Assert.Null(typeof(MpApprovals).GetCustomAttribute<BindPropertiesAttribute>());
            Assert.Equal(typeof(string), typeof(MpApprovals).GetProperty("Tab")!.PropertyType);
            // the history read takes an explicit scope (2026-10-20) built from the page's principal by the shared policy
            var m = typeof(SowingApprovalQueueRepository).GetMethod(nameof(SowingApprovalQueueRepository.GetApprovalHistoryAsync))!;
            Assert.Equal(new[] { typeof(HistoryScope), typeof(bool) }, m.GetParameters().Select(x => x.ParameterType).ToArray());
            foreach (var page in new[] { new[] { "MotherPlant", "SowingApprovals.cshtml.cs" }, new[] { "ReadyConfirmation", "Index.cshtml.cs" } })
            {
                var src = File.ReadAllText(Path.Combine(new[] { RepoRoot(), "Pages", "Production" }.Concat(page).ToArray()));
                Assert.Contains("GetApprovalHistoryAsync(SowingApprovalVisibility.History(User)", src);
                Assert.Contains("approvableOnly: SowingApprovalVisibility.PendingApprovableOnly(User)", src);
                Assert.DoesNotContain("Request.", src);
            }
            var sql = File.ReadAllText(Path.Combine(RepoRoot(), "Data", "SowingApprovalQueueRepository.cs"));
            Assert.Contains("WHERE (@All = 1 OR rc.ApprovedById = @Approver)", sql);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.csproj")))
                dir = dir.Parent;
            return dir!.FullName;
        }
    }
}
