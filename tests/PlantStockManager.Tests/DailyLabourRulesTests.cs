using System.IO;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using PlantStockManager.Authorization;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Daily Labour Count (2026-10-04) -- pure rules and wiring. The database
    // behaviour (authorization, duplicates, concurrency, filters) is
    // DailyLabourE2ETests, against a scratch copy.
    public class DailyLabourRulesTests
    {
        private static string ReadSource(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
        }

        private static readonly Area MainOffice = new() { Id = 2, Name = "Main-Office-Areas", AreaType = "MainOffice", IsActive = true };
        private static readonly Area GreenBless = new() { Id = 127, Name = "Green Bless Nursery", AreaType = "MotherPlant", IsActive = true };
        private static readonly Area Outlet = new() { Id = 124, Name = "Outlet", AreaType = "Outlet", IsActive = true };

        // ---- calculations ---------------------------------------------------------------------------

        [Theory]
        [InlineData(10, 2, 8, 1, 21, 19.5)]     // Main Office example
        [InlineData(15, 3, 12, 2, 32, 29.5)]    // Green Bless example
        [InlineData(20, 3, 15, 2, 40, 37.5)]    // Facility-5 example
        [InlineData(0, 0, 0, 0, 0, 0)]
        [InlineData(0, 1, 0, 0, 1, 0.5)]
        [InlineData(500, 500, 500, 500, 2000, 1500)]
        public void TotalWorkers_And_LabourDays(int mf, int mh, int ff, int fh, int workers, decimal days)
        {
            Assert.Equal(workers, DailyLabourRules.TotalWorkers(mf, mh, ff, fh));
            Assert.Equal(days, DailyLabourRules.LabourDays(mf, mh, ff, fh));
            var row = new DailyLabourCount { MaleFullDay = mf, MaleHalfDay = mh, FemaleFullDay = ff, FemaleHalfDay = fh };
            Assert.Equal(mf + mh, row.TotalMale);
            Assert.Equal(ff + fh, row.TotalFemale);
            Assert.Equal(workers, row.TotalWorkers);
            Assert.Equal(days, row.LabourDays);
        }

        [Fact]
        public void Summary_UsesTheSameFormulas()
        {
            var s = new DailyLabourSummary { MaleFullDay = 25, MaleHalfDay = 5, FemaleFullDay = 20, FemaleHalfDay = 3 };
            Assert.Equal(30, s.TotalMale);
            Assert.Equal(23, s.TotalFemale);
            Assert.Equal(53, s.TotalWorkers);
            Assert.Equal(49m, s.LabourDays);
        }

        // ---- counts / date ----------------------------------------------------------------------------

        [Theory]
        [InlineData(0, true)]
        [InlineData(10, true)]
        [InlineData(500, true)]
        [InlineData(-1, false)]
        [InlineData(501, false)]
        [InlineData(null, false)]
        public void ValidateCount(int? value, bool ok)
            => Assert.Equal(ok, DailyLabourRules.ValidateCount(value, "Male Full Day") == null);

        [Fact]
        public void ValidateDate_TodayAndPastOk_FutureAndMissingRejected()
        {
            var today = new DateTime(2026, 10, 4);
            Assert.Null(DailyLabourRules.ValidateDate(today, today));
            Assert.Null(DailyLabourRules.ValidateDate(today.AddDays(-30), today));
            Assert.Equal(DailyLabourRules.FutureDateMessage, DailyLabourRules.ValidateDate(today.AddDays(1), today));
            Assert.NotNull(DailyLabourRules.ValidateDate(null, today));
        }

        // ---- location rule ----------------------------------------------------------------------------

        [Theory]
        [InlineData("MainOffice", true)]
        [InlineData("MotherPlant", false)]
        [InlineData("Outlet", false)]
        [InlineData(null, false)]
        [InlineData("mainoffice", false)]    // exact project constant, no fuzzy match
        public void RequiresPolyhouse_OnlyForMainOfficeType(string? areaType, bool expected)
            => Assert.Equal(expected, DailyLabourRules.RequiresPolyhouse(areaType));

        [Fact]
        public void RequiresPolyhouse_UsesTheExistingProjectConstant()
            => Assert.True(DailyLabourRules.RequiresPolyhouse(DirectSowingRules.MainOfficeAreaType));

        [Fact]
        public void ValidateLocation_MainOffice()
        {
            var f5 = new Polyhouse { Id = 2, Name = "Facility-5", AreaId = 2 };
            var gbP1 = new Polyhouse { Id = 9, Name = "P-1", AreaId = 127 };
            Assert.Null(DailyLabourRules.ValidateLocation(MainOffice, true, 2, f5));
            Assert.Equal(DailyLabourRules.PolyhouseRequiredMessage, DailyLabourRules.ValidateLocation(MainOffice, true, null, null)!.Value.Message);
            Assert.Equal(DailyLabourRules.PolyhouseNotInAreaMessage, DailyLabourRules.ValidateLocation(MainOffice, true, 9, gbP1)!.Value.Message);
            Assert.Equal("PolyhouseId", DailyLabourRules.ValidateLocation(MainOffice, true, 999, null)!.Value.Field);
        }

        [Fact]
        public void ValidateLocation_OtherArea_IsAreaWise()
        {
            Assert.Null(DailyLabourRules.ValidateLocation(GreenBless, true, null, null));
            var gbP1 = new Polyhouse { Id = 9, Name = "P-1", AreaId = 127 };
            Assert.Equal(DailyLabourRules.PolyhouseNotAllowedMessage, DailyLabourRules.ValidateLocation(GreenBless, true, 9, gbP1)!.Value.Message);
        }

        [Fact]
        public void Outlet_IsExcludedFromDailyLabour()
        {
            Assert.False(DailyLabourRules.IsLabourArea(OutletRules.AreaType));
            Assert.True(DailyLabourRules.IsLabourArea("MainOffice"));
            Assert.True(DailyLabourRules.IsLabourArea("MotherPlant"));
            Assert.True(DailyLabourRules.IsLabourArea(null));
            // rejected even for an authorized (e.g. full-access) user
            Assert.Equal(DailyLabourRules.OutletNotAllowedMessage, DailyLabourRules.ValidateLocation(Outlet, true, null, null)!.Value.Message);
        }

        [Theory]
        [InlineData(null, true)]
        [InlineData("", true)]
        [InlineData("Weeding", true)]
        [InlineData(500, true)]
        [InlineData(501, false)]
        public void ValidateRemarks_OptionalUpTo500(object? remark, bool ok)
        {
            var text = remark is int n ? new string('x', n) : (string?)remark;
            Assert.Equal(ok, DailyLabourRules.ValidateRemarks(text) == null);
        }

        [Fact]
        public void ValidateLocation_UnauthorizedInactiveOrMissingArea_Rejected()
        {
            Assert.Equal(DailyLabourRules.NotAuthorizedMessage, DailyLabourRules.ValidateLocation(GreenBless, false, null, null)!.Value.Message);
            var inactive = new Area { Id = 5, Name = "Old", AreaType = "MotherPlant", IsActive = false };
            Assert.Equal("AreaId", DailyLabourRules.ValidateLocation(inactive, true, null, null)!.Value.Field);
            Assert.Equal("AreaId", DailyLabourRules.ValidateLocation(null, true, null, null)!.Value.Field);
        }

        // ---- wiring -------------------------------------------------------------------------------------

        [Fact]
        public void Pages_AreMappedToLabourPermissions()
        {
            Assert.Equal("Labour.View", FeatureAuthorizationConventions.GetRule("/Production/Labour/Index").Read);
            Assert.Equal("Labour.Enter", FeatureAuthorizationConventions.GetRule("/Production/Labour/Entry").Read);
            var layout = ReadSource("Pages", "Shared", "_Layout.cshtml");
            Assert.Contains("nav-page=\"/Production/Labour/Entry\"", layout);
            Assert.Contains("nav-page=\"/Production/Labour/Index\"", layout);
        }

        [Fact]
        public void EntryForm_HasOnlyDateLocationAndFourCounts_NoWorkerOrWageFields()
        {
            var bound = typeof(PlantStockManager.Pages.Production.Labour.EntryModel)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<BindPropertyAttribute>() != null)
                .Select(p => p.Name).OrderBy(x => x).ToList();
            Assert.Equal(new[] { "AreaId", "FemaleFullDay", "FemaleHalfDay", "Id", "LabourDate", "MaleFullDay", "MaleHalfDay", "PolyhouseId", "Remarks" }, bound);
            // Whole numbers only: the counts are int, never decimal.
            foreach (var name in new[] { "MaleFullDay", "MaleHalfDay", "FemaleFullDay", "FemaleHalfDay" })
                Assert.Equal(typeof(int?), typeof(PlantStockManager.Pages.Production.Labour.EntryModel).GetProperty(name)!.PropertyType);

            var model = string.Join(",", typeof(DailyLabourCount).GetProperties().Select(p => p.Name));
            Assert.DoesNotContain("Worker", model.Replace("TotalWorkers", ""));
            Assert.DoesNotContain("Wage", model);
            Assert.DoesNotContain("Employee", model);
        }

        [Fact]
        public void EntryAndRecordsPages_MatchTheApprovedLayout()
        {
            var entry = ReadSource("Pages", "Production", "Labour", "Entry.cshtml");
            Assert.Contains(">Save Labour Entry<", entry);
            foreach (var id in new[] { "totalMale", "totalFemale", "totalWorkers", "labourDays", "remarks" })
                Assert.Contains($"id=\"{id}\"", entry);
            var list = ReadSource("Pages", "Production", "Labour", "Index.cshtml");
            Assert.DoesNotContain("Last Updated", list);
            Assert.Contains("<th>Remark</th><th>Entered By</th><th>Edit</th>", list);
            Assert.DoesNotContain("asp-page-handler=\"Delete\"", list);
            Assert.DoesNotContain("OnPostDelete", ReadSource("Pages", "Production", "Labour", "Entry.cshtml.cs"));
        }

        [Fact]
        public void Migration_IsGuarded_AndHasTheDatabaseRules()
        {
            var sql = ReadSource("Database", "Migrations", "2026-10-04_DailyLabourCounts.sql");
            Assert.Contains("NOT LIKE N'PlantsIMS2[_]Scratch[_]%'", sql);
            Assert.Contains("N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')", sql);
            Assert.Contains("WHERE PolyhouseId IS NOT NULL", sql);
            Assert.Contains("WHERE PolyhouseId IS NULL", sql);
            Assert.Contains("BETWEEN 0 AND 500", sql);
            Assert.Contains("TR_DailyLabourCounts_Location", sql);
            Assert.DoesNotContain("ALTER TABLE dbo.LabourLogs", sql);
            Assert.DoesNotContain("DROP TABLE", sql);
            Assert.Equal(DailyLabourRules.MaxCount, 500);
            Assert.Contains("Remarks       NVARCHAR(500) NULL", sql);
            Assert.Contains("IN (N'Sowing Supervisor', N'Mother Plant Supervisor')", sql);
            Assert.DoesNotContain("N'Outlet Sales')", sql);
            Assert.Contains("Outlet is not part of Daily Labour.", sql);
        }

        [Fact]
        public void FollowUpMigration_AddsRemarks_RemovesOnlyOutletSalesLabourGrants_AndIsGuarded()
        {
            var sql = ReadSource("Database", "Migrations", "2026-10-04_DailyLabourCounts_RemarksNoOutlet.sql");
            Assert.Contains("NOT LIKE N'PlantsIMS2[_]Scratch[_]%'", sql);
            Assert.Contains("N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')", sql);
            Assert.Contains("ADD Remarks NVARCHAR(500) NULL", sql);
            Assert.Contains("= N'Outlet Sales'", sql);
            Assert.Contains("p.Code IN (N'Labour.View', N'Labour.Enter')", sql);
            Assert.Contains("THROW 51724", sql);                       // refuses if Outlet labour rows exist
            Assert.DoesNotContain("DELETE FROM dbo.DailyLabourCounts", sql);
            Assert.DoesNotContain("ALTER TABLE dbo.LabourLogs", sql);
            Assert.DoesNotContain("DELETE FROM dbo.LabourLogs", sql);
        }

        [Fact]
        public void DailyLabourCounts_BlockAreaDeletion()
            => Assert.Contains(DeletionRules.AreaDependencies, d => d.Table == "DailyLabourCounts" && d.Column == "AreaId");
    }
}
