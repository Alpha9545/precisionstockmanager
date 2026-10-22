using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using EditValues = PlantStockManager.Services.BedSowingRules.BedEditValues;

namespace PlantStockManager.Tests
{
    // 2026-10-22: Bed Sowing Edit -- the pure rules (who may be edited, what counts as a change, the stale-form guard,
    // validation, the audit text) and the page wiring (permission, buttons, migration and rollback).
    public class BedSowingEditRulesTests
    {
        private static string ReadSource(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
        }

        private static readonly DateTime Today = new(2026, 10, 13);
        private static readonly EditValues Bed = new(new DateTime(2026, 10, 1), 3162, 125, 25000m, "TEST DATA");

        [Theory]
        [InlineData(BedSowing.StatusPending, 0, 0, 0, null, true, null)]
        [InlineData(BedSowing.StatusPending, 0, 0, 1, null, false, BedSowingRules.EditNotUntouchedMessage)]          // a (reversed) operation exists
        [InlineData(BedSowing.StatusPartial, 240, 0, 1, null, false, BedSowingRules.EditNotUntouchedMessage)]
        [InlineData(BedSowing.StatusPending, 0, 10, 1, null, false, BedSowingRules.EditNotUntouchedMessage)]
        [InlineData(BedSowing.StatusCompleted, 1000, 0, 2, null, false, BedSowingRules.EditNotUntouchedMessage)]
        [InlineData(BedSowing.StatusCancelled, 0, 0, 0, null, false, BedSowingRules.EditCancelledMessage)]
        [InlineData(BedSowing.StatusPending, 0, 0, 0, 30, false, BedSowingRules.EditLegacyReadyDateMessage)]
        public void ValidateEditable_UntouchedBedsOnly(string status, double transplanted, double lost, int ops, int? readyDays, bool ok, string? error)
        {
            var r = BedSowingRules.ValidateEditable(status, (decimal)transplanted, (decimal)lost, ops, readyDays);
            Assert.Equal((ok, error), (r.Ok, r.Error));
        }

        [Fact]
        public void Compare_SeparatesSownFieldsFromRemarks_AndIgnoresTimeAndBlankRemarks()
        {
            Assert.False(BedSowingRules.Compare(Bed, Bed with { SowingDate = Bed.SowingDate.AddHours(9), Remarks = " TEST DATA " }).Any);
            var c = BedSowingRules.Compare(Bed, Bed with { Quantity = 24000 });
            Assert.True(c.Quantity && c.Sown && !c.Remarks);
            var r = BedSowingRules.Compare(Bed, Bed with { Remarks = "changed" });
            Assert.True(r.Remarks && r.Any && !r.Sown);
            Assert.True(BedSowingRules.Compare(Bed, Bed with { SeedStockId = 999 }).Lot);
            Assert.True(BedSowingRules.Compare(Bed, Bed with { SpeciesId = 1 }).Variety);
            Assert.True(BedSowingRules.Compare(Bed, Bed with { SowingDate = Bed.SowingDate.AddDays(1) }).Date);
            Assert.False(BedSowingRules.Compare(Bed with { Remarks = null }, Bed with { Remarks = "  " }).Remarks);
        }

        [Fact]
        public void MatchesStored_StaleOrRepeatedFormsAreRefused()
        {
            Assert.True(BedSowingRules.MatchesStored(Bed, Bed));
            Assert.False(BedSowingRules.MatchesStored(Bed with { Quantity = 24000 }, Bed));   // someone saved 24,000 meanwhile
            Assert.False(BedSowingRules.MatchesStored(Bed, Bed with { Remarks = "old" }));
        }

        [Fact]
        public void ValidateEdit_RequiredFieldsDateQuantityAndReasonForSownChanges()
        {
            var sown = BedSowingRules.Compare(Bed, Bed with { Quantity = 1 });
            var remarksOnly = BedSowingRules.Compare(Bed, Bed with { Remarks = "x" });
            Assert.True(BedSowingRules.ValidateEdit(Bed with { Quantity = 1 }, Today, sown, "recount").Ok);
            Assert.True(BedSowingRules.ValidateEdit(Bed with { Remarks = "x" }, Today, remarksOnly, null).Ok);      // no reason for Remarks only
            Assert.Contains("reason is required", BedSowingRules.ValidateEdit(Bed with { Quantity = 1 }, Today, sown, " ").Error);
            Assert.Contains("cannot be in the future", BedSowingRules.ValidateEdit(Bed with { SowingDate = Today.AddDays(1) }, Today, sown, "r").Error);
            Assert.Equal("Quantity must be greater than zero.", BedSowingRules.ValidateEdit(Bed with { Quantity = 0 }, Today, sown, "r").Error);
            Assert.Equal("Quantity must be a whole number.", BedSowingRules.ValidateEdit(Bed with { Quantity = 1.5m }, Today, sown, "r").Error);
            Assert.Equal("Variety is required.", BedSowingRules.ValidateEdit(Bed with { SpeciesId = 0 }, Today, sown, "r").Error);
            Assert.Equal("Main Office seed lot is required.", BedSowingRules.ValidateEdit(Bed with { SeedStockId = 0 }, Today, sown, "r").Error);
            Assert.Contains("at most 500", BedSowingRules.ValidateEdit(Bed with { Remarks = new string('x', 501) }, Today, remarksOnly, null).Error);
        }

        [Fact]
        public void DescribeChanges_ListsOldAndNewValues()
        {
            var to = Bed with { SowingDate = new DateTime(2026, 10, 2), Quantity = 24000, Remarks = "x" };
            var text = BedSowingRules.DescribeChanges(BedSowingRules.Compare(Bed, to), Bed, to, null, null, null, null);
            Assert.Equal("Sowing Date 01/10/2026 -> 02/10/2026; Quantity 25,000 -> 24,000; Remarks changed", text);
        }

        [Fact]
        public void Wiring_Permission_Buttons_Migration()
        {
            Assert.Equal("Sowing.Enter", FeatureAuthorizationConventions.GetRule("/Production/BedSowing/Edit").Read);
            Assert.Equal("Sowing.Enter", FeatureAuthorizationConventions.GetRule("/Production/BedSowing/Create").Read);   // the same as Create
            Assert.Contains("asp-page=\"/Production/BedSowing/Edit\"", ReadSource("Pages", "Production", "BedSowing", "Index.cshtml"));
            Assert.Contains("asp-page=\"/Production/BedSowing/Edit\"", ReadSource("Pages", "Production", "BedSowing", "Details.cshtml"));
            var fwd = ReadSource("Database", "Migrations", "2026-10-22_BedSowingEdit.sql");
            var rb = ReadSource("Database", "Migrations", "2026-10-22_BedSowingEdit_Rollback.sql");
            foreach (var sql in new[] { fwd, rb })
            {
                Assert.Contains("NOT LIKE N'PlantsIMS2[_]Scratch[_]%'", sql);
                Assert.Contains("@@SERVERNAME <> N'$(TARGET_SERVER)'", sql);
                Assert.Contains("N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')", sql);
            }
            Assert.Contains("BedSowingEdit", fwd);
            Assert.Contains("BedSowingEdit", WorkflowCorrectionsSchema.BedSowingEditProbeSql);
            foreach (var code in new[] { "52801", "52802", "52803", "52171", "52172" })
                Assert.Contains("THROW " + code, fwd);
        }
    }
}
