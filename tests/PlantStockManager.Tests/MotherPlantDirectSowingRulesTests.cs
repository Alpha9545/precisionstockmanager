using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // 2026-10-21: Mother Plant & Cutting > Direct Sowing -- the pure rules (no database): menu + page permission, the Area
    // resolution, who may approve, the approval category / visibility, and that the Seed Production rules are unchanged.
    public class MotherPlantDirectSowingRulesTests
    {
        private static string ReadSource(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
        }

        private const int Area = 127, Supervisor = 110, Recorder = 110, OtherUser = 101;

        [Fact]
        public void Menu_DirectSowing_IsUnderMotherPlantAndCutting_MotherPlantEnter()
        {
            var layout = ReadSource("Pages", "Shared", "_Layout.cshtml");
            var mp = layout.IndexOf(">Mother Plant &amp; Cutting<", StringComparison.Ordinal);
            var pot = layout.IndexOf("POT PRODUCTION", StringComparison.Ordinal);
            var item = layout.IndexOf("nav-page=\"/Production/MotherPlant/DirectSowing\"", StringComparison.Ordinal);
            Assert.True(mp > 0 && item > mp && item < pot, "Direct Sowing must be a Mother Plant & Cutting menu item.");
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(layout, "/Production/MotherPlant/DirectSowing\"").Count);   // nav-page + asp-page, one item
            Assert.Contains(">Direct Sowing</a>", layout[item..layout.IndexOf("</li>", item, StringComparison.Ordinal)]);
            // Seed Production keeps its own Direct Seed Sowing entry
            Assert.Contains("nav-page=\"/Production/SeedSowing/Index\"><a asp-page=\"/Production/SeedSowing/Index\">Direct Seed Sowing</a>", layout);

            var rule = FeatureAuthorizationConventions.GetRule("/Production/MotherPlant/DirectSowing");
            Assert.Equal("MotherPlant.Enter", rule.Read);
            Assert.Equal("Sowing.Enter", FeatureAuthorizationConventions.GetRule("/Production/SeedSowing/Create").Read);   // unchanged
        }

        [Theory]
        [InlineData(null, null, true, "MotherPlant", true, DirectSowingRules.MotherPlantDirectAreaRequiredMessage)]
        [InlineData(127, 4, true, "MotherPlant", true, DirectSowingRules.MotherPlantDirectNoPolyhouseMessage)]
        [InlineData(127, null, false, null, false, DirectSowingRules.MotherPlantDirectAreaNotFoundMessage)]
        [InlineData(2, null, true, "MainOffice", true, DirectSowingRules.MotherPlantDirectNotMotherPlantAreaMessage)]
        [InlineData(124, null, true, "Outlet", true, DirectSowingRules.MotherPlantDirectNotMotherPlantAreaMessage)]
        [InlineData(127, null, true, "MotherPlant", false, DirectSowingRules.MotherPlantDirectNotMotherPlantAreaMessage)]   // inactive
        [InlineData(127, null, true, "MotherPlant", true, null)]
        public void ResolveMotherPlantDirectSowingArea(int? area, int? polyhouse, bool found, string? type, bool active, string? error)
        {
            var (ok, message) = DirectSowingRules.ResolveMotherPlantDirectSowingArea(area, polyhouse, found, type, active);
            Assert.Equal(error == null, ok);
            Assert.Equal(error, message);
            Assert.Equal(MotherPlantAreaSchema.MotherPlantAreaType, DirectSowingRules.MotherPlantAreaType);
        }

        [Fact]
        public void CanApprove_MotherPlantDirect_OnlyAnAreaSupervisorOfItsArea_RecorderMay_SafeByDefault()
        {
            // the recorder, who IS an Area Supervisor of the Area: allowed (no maker-checker for this workflow)
            Assert.True(DirectSowingRules.CanApprove(null, Recorder, "rec", Recorder, "rec", SeedSowing.SourceSeed, null, true, true, "Green Bless").Ok);
            // anyone who is not an Area Supervisor of the batch's Area (another Area, Main Office, ...): refused
            var (ok, error) = DirectSowingRules.CanApprove(null, Recorder, "rec", OtherUser, "other", SeedSowing.SourceSeed, null, true, false, "Green Bless");
            Assert.False(ok);
            Assert.Equal(DirectSowingRules.NotMotherPlantDirectApproverMessage("Green Bless"), error);
            // a caller that did not check the Area Supervisor rule gets a refusal, never an approval
            Assert.False(DirectSowingRules.CanApprove(null, Recorder, "rec", Supervisor, "sup", SeedSowing.SourceSeed, null, motherPlantDirect: true).Ok);
            Assert.False(DirectSowingRules.CanApprove(null, Recorder, "rec", null, null, SeedSowing.SourceSeed, null, true, true).Ok);   // unidentified

            // Seed Production Direct Sowing unchanged: the assigned supervisor only, never the recorder
            Assert.True(DirectSowingRules.CanApprove(9, 21, "a", 9, "b", SeedSowing.SourceSeed).Ok);
            Assert.Equal(DirectSowingRules.NotAssignedMessage, DirectSowingRules.CanApprove(9, 21, "a", 10, "c", SeedSowing.SourceSeed).Error);
            Assert.Equal(DirectSowingRules.OwnSowingMessage, DirectSowingRules.CanApprove(21, 21, "a", 21, "a", SeedSowing.SourceSeed).Error);
            Assert.Equal(DirectSowingRules.NoSupervisorMessage, DirectSowingRules.CanApprove(null, 21, "a", 10, "c", SeedSowing.SourceSeed).Error);
            // ... and Area-Supervisor status grants nothing for it
            Assert.False(DirectSowingRules.CanApprove(9, 21, "a", 10, "c", SeedSowing.SourceSeed, null, false, true).Ok);
        }

        private static SeedSowing Batch(bool mpDirect, string source = SeedSowing.SourceSeed) => new()
        {
            Id = 1, SourceType = source, IsMotherPlantDirectSowing = mpDirect, AreaId = Area, AreaName = "Green Bless Nursery",
            CreatedById = Recorder, CreatedBy = "rec", SupervisorId = mpDirect ? null : 9, ExpectedReadyDate = AppClock.Today.AddDays(30)
        };

        private static readonly SowingApprovalRules.Settings Settings = new(DirectSowingRules.BedApprover.NotConfigured, true, true, true);

        [Fact]
        public void Category_Visibility_Eligibility_MotherPlantDirect()
        {
            Assert.Equal(SowingApprovalRules.CategoryMotherPlantDirectSowing, SowingApprovalRules.CategoryOfBatch(Batch(true)));
            Assert.Equal(SowingApprovalRules.CategorySowing, SowingApprovalRules.CategoryOfBatch(Batch(false)));
            Assert.Equal(SowingApprovalRules.CategoryMotherPlantTransplant, SowingApprovalRules.CategoryOf(SeedSowing.SourceTransplant));
            Assert.Equal(SowingApprovalRules.CategoryBedSowing, SowingApprovalRules.CategoryOf(SeedSowing.SourceBedSowing));
            Assert.Contains(SowingApprovalRules.CategoryMotherPlantDirectSowing, SowingApprovalRules.Categories);
            Assert.True(SowingApprovalRules.IsMotherPlantCategory(SowingApprovalRules.CategoryMotherPlantDirectSowing));
            Assert.True(SowingApprovalRules.IsMotherPlantCategory(SowingApprovalRules.CategoryMotherPlantTransplant));
            Assert.False(SowingApprovalRules.IsMotherPlantCategory(SowingApprovalRules.CategorySowing));
            Assert.Equal("Mother Plant Area Direct Sowings", SowingApprovalRules.CategoryLabel(SowingApprovalRules.CategoryMotherPlantDirectSowing));

            // visibility: the strict Area rule (as all Mother Plant work), never the seedling scope
            Assert.True(SowingApprovalRules.IsVisible(SowingApprovalRules.CategoryMotherPlantDirectSowing, Area, _ => false, a => a == Area));
            Assert.False(SowingApprovalRules.IsVisible(SowingApprovalRules.CategoryMotherPlantDirectSowing, Area, _ => true, _ => false));

            // eligibility: Area Supervisor of its Area -> Approve now (Direct Sowing has no Ready Date gate); otherwise not authorised
            var mine = SowingApprovalRules.Evaluate(Batch(true), Recorder, "rec", true, true, Settings, AppClock.Today);
            Assert.True(mine.CanApprove, mine.Reason);
            Assert.Equal("Area Supervisor of Green Bless Nursery", mine.Approver);
            var theirs = SowingApprovalRules.Evaluate(Batch(true), OtherUser, "other", true, false, Settings, AppClock.Today);
            Assert.False(theirs.Authorized);
            var noPermission = SowingApprovalRules.Evaluate(Batch(true), Recorder, "rec", false, true, Settings, AppClock.Today);
            Assert.False(noPermission.Authorized);
            // a Seed Production sowing: still the assigned supervisor, whatever the Area
            Assert.False(SowingApprovalRules.Evaluate(Batch(false), OtherUser, "other", true, true, Settings, AppClock.Today).Authorized);
            Assert.True(SowingApprovalRules.Evaluate(Batch(false), 9, "sup", true, false, Settings, AppClock.Today).CanApprove);
        }

        [Fact]
        public void Model_OriginLabel_TellsTheWorkflowsApart()
        {
            Assert.Equal("Direct Sowing (Mother Plant Area)", Batch(true).OriginLabel);
            Assert.Equal("Direct Seed Sowing", Batch(false).OriginLabel);
            Assert.Equal("Transplanted Ready Stock", Batch(false, SeedSowing.SourceTransplant).OriginLabel);
        }

        [Fact]
        public void Migration_And_Rollback_AreSupplied_Guarded_AndMatchTheProbe()
        {
            var fwd = ReadSource("Database", "Migrations", "2026-10-21_MotherPlantDirectSowing.sql");
            var rb = ReadSource("Database", "Migrations", "2026-10-21_MotherPlantDirectSowing_Rollback.sql");
            foreach (var sql in new[] { fwd, rb })
            {
                Assert.Contains("NOT LIKE N'PlantsIMS2[_]Scratch[_]%'", sql);
                Assert.Contains("@@SERVERNAME <> N'$(TARGET_SERVER)'", sql);
                Assert.Contains("N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')", sql);
            }
            Assert.Contains("IsMotherPlantDirectSowing BIT NOT NULL", fwd);
            Assert.Contains("TR_SeedSowings_MotherPlantDirectSowing", fwd);
            Assert.Contains("Mother Plant Area Direct Sowings exist", rb);   // refused once data exists
            // every probed object is created by the forward script
            foreach (var marker in new[] { "IsMotherPlantDirectSowing", "TR_SeedSowings_MotherPlantDirectSowing", "MotherPlantDirectSowing" })
                Assert.Contains(marker, WorkflowCorrectionsSchema.MotherPlantDirectSowingProbeSql);
        }
    }
}
