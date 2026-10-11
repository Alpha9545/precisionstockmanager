using System.IO;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using PlantStockManager.Authorization;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Labour Overtime (2026-10-06) -- pure rules and wiring: duration, worker-hours, validation,
    // summaries. The database behaviour (authorization, duplicates, corrections + audit, the
    // computed columns) is LabourOvertimeE2ETests, against a scratch copy.
    public class LabourOvertimeRulesTests
    {
        private static string ReadSource(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
        }

        private static TimeOnly T(string hhmm) => TimeOnly.ParseExact(hhmm, "HH:mm");

        // ---- duration and worker-hours -----------------------------------------------------------

        [Theory]
        [InlineData("17:30", "20:00", 150)]   // approved example: 2h 30m
        [InlineData("18:00", "19:00", 60)]
        [InlineData("17:30", "17:31", 1)]
        [InlineData("00:00", "23:59", 1439)]
        [InlineData("17:30", "17:30", 0)]     // zero -> invalid
        [InlineData("20:00", "17:30", -150)]  // end before start -> invalid
        [InlineData("23:00", "01:00", -1320)] // never wraps past midnight (TimeOnly subtraction would give +120)
        public void DurationMinutes_IsEndMinusStart_OnTheSameDay(string start, string end, int minutes)
            => Assert.Equal(minutes, LabourOvertimeRules.DurationMinutes(T(start), T(end)));

        [Fact]
        public void ApprovedExample_TenAndFiveWorkerHours()
        {
            var minutes = LabourOvertimeRules.DurationMinutes(T("17:30"), T("20:00"));
            Assert.Equal("2h 30m", LabourOvertimeRules.FormatDuration(minutes));
            Assert.Equal(10m, LabourOvertimeRules.WorkerHours(4, minutes));
            Assert.Equal(5m, LabourOvertimeRules.WorkerHours(2, minutes));
            Assert.Equal(15m, LabourOvertimeRules.WorkerHours(6, minutes));
        }

        [Theory]
        [InlineData(150, "2h 30m")]
        [InlineData(180, "3h")]
        [InlineData(45, "45m")]
        [InlineData(3680, "61h 20m")]       // approved monthly example
        [InlineData(0, "0m")]
        public void FormatDuration(int minutes, string text) => Assert.Equal(text, LabourOvertimeRules.FormatDuration(minutes));

        [Theory]
        [InlineData(10, "10")]
        [InlineData(145.5, "145.5")]
        [InlineData(227.5, "227.5")]
        [InlineData(2.3333, "2.33")]
        public void FormatHours(decimal hours, string text) => Assert.Equal(text, LabourOvertimeRules.FormatHours(hours));

        [Fact]
        public void Times_AreKeptToTheMinute()
            => Assert.Equal(T("17:30"), LabourOvertimeRules.ToMinute(new TimeOnly(17, 30, 45)));

        // ---- validation ----------------------------------------------------------------------------

        private static List<(string Field, string Message)> V(int? male = 4, int? female = 2, string? start = "17:30", string? end = "20:00", string? work = "Polyhouse cleaning", string? remarks = null)
            => LabourOvertimeRules.Validate(male, female, start == null ? null : T(start), end == null ? null : T(end), work, remarks);

        [Fact]
        public void ValidOvertime_HasNoErrors()
        {
            Assert.Empty(V());
            Assert.Empty(V(male: 0, female: 1));
            Assert.Empty(V(male: 500, female: 500));
        }

        [Fact]
        public void EndNotAfterStart_IsRejected()
        {
            Assert.Equal(new[] { ("EndTime", LabourOvertimeRules.EndAfterStartMessage) }, V(start: "20:00", end: "17:30"));
            Assert.Equal(new[] { ("EndTime", LabourOvertimeRules.EndAfterStartMessage) }, V(start: "17:30", end: "17:30"));
            Assert.Equal(new[] { ("StartTime", LabourOvertimeRules.TimesRequiredMessage) }, V(end: null));
        }

        [Fact]
        public void Workers_AtLeastOne_NeverNegative_AtMost500_Required()
        {
            Assert.Equal(new[] { ("MaleWorkers", LabourOvertimeRules.AtLeastOneWorkerMessage) }, V(male: 0, female: 0));
            Assert.Equal(new[] { ("MaleWorkers", "Male Workers cannot be negative.") }, V(male: -1));
            Assert.Equal(new[] { ("FemaleWorkers", "Female Workers cannot be more than 500.") }, V(female: 501));
            Assert.Equal(new[] { ("FemaleWorkers", "Female Workers is required (enter 0 if none).") }, V(female: null));
        }

        [Fact]
        public void WorkDescription_Required_TextLimited()
        {
            Assert.Equal(new[] { ("WorkDescription", LabourOvertimeRules.DescriptionRequiredMessage) }, V(work: "   "));
            Assert.Equal("WorkDescription", Assert.Single(V(work: new string('x', 501))).Field);
            Assert.Equal("Remarks", Assert.Single(V(remarks: new string('x', 501))).Field);
            Assert.Empty(V(work: new string('x', 500), remarks: new string('x', 500)));
        }

        [Fact]
        public void FutureDate_IsRejected_InIndiaTime()
        {
            Assert.Equal(DailyLabourRules.FutureDateMessage, DailyLabourRules.ValidateDate(AppClock.Today.AddDays(1), AppClock.Today));
            Assert.Null(DailyLabourRules.ValidateDate(AppClock.Today, AppClock.Today));
        }

        // ---- summaries -----------------------------------------------------------------------------

        private static LabourOvertimeEntry E(string date, int area, int? polyhouse, int male, int female, string start, string end)
        {
            var d = LabourOvertimeRules.DurationMinutes(T(start), T(end));
            return new LabourOvertimeEntry
            {
                LabourDate = DateTime.Parse(date), AreaId = area, AreaName = $"A{area}", PolyhouseId = polyhouse, PolyhouseName = polyhouse.HasValue ? $"P{polyhouse}" : null,
                MaleWorkers = male, FemaleWorkers = female, StartTime = T(start), EndTime = T(end),
                DurationMinutes = d, MaleWorkerMinutes = d * male, FemaleWorkerMinutes = d * female,
            };
        }

        [Fact]
        public void Summary_AddsEveryEntrySeparately_NeverMerges()
        {
            var entries = new[]
            {
                E("2026-10-06", 2, 2, 4, 2, "17:30", "20:00"),   // 150 min: 10 / 5
                E("2026-10-06", 2, 2, 3, 0, "18:00", "19:00"),   // overlapping, 60 min: 3 / 0
                E("2026-10-05", 127, null, 1, 1, "17:00", "19:30"), // 150 min: 2.5 / 2.5
            };
            var total = new LabourOvertimeSummary();
            foreach (var e in entries) total.Add(e);
            Assert.Equal((3, 360, "6h", 8, 3), (total.Entries, total.DurationMinutes, total.Duration, total.MaleWorkers, total.FemaleWorkers));
            Assert.Equal((15.5m, 7.5m, 23m), (total.MaleWorkerHours, total.FemaleWorkerHours, total.TotalWorkerHours));

            var byDate = LabourOvertimeSummary.GroupBy(entries, e => e.LabourDate.ToString("yyyy-MM-dd")).ToDictionary(s => s.Key);
            Assert.Equal((2, 13m, 5m), (byDate["2026-10-06"].Entries, byDate["2026-10-06"].MaleWorkerHours, byDate["2026-10-06"].FemaleWorkerHours));
            var byArea = LabourOvertimeSummary.GroupBy(entries, e => e.AreaName!).ToDictionary(s => s.Key);
            Assert.Equal((2, 1), (byArea["A2"].Entries, byArea["A127"].Entries));
        }

        // ---- wiring --------------------------------------------------------------------------------

        [Fact]
        public void Pages_UseTheExistingLabourPermissions_AndAreInTheMenu()
        {
            Assert.Equal("Labour.Enter", FeatureAuthorizationConventions.GetRule("/Production/Labour/Overtime").Read);
            Assert.Equal("Labour.View", FeatureAuthorizationConventions.GetRule("/Production/Labour/OvertimeHistory").Read);
            Assert.Equal("Labour.View", FeatureAuthorizationConventions.GetRule("/Production/Labour/OvertimeDetails").Read);
            var layout = ReadSource("Pages", "Shared", "_Layout.cshtml");
            var order = new[] { ">Daily Attendance<", ">Attendance History<", ">Overtime<", ">Overtime History<" }.Select(x => layout.IndexOf(x)).ToArray();
            Assert.All(order, i => Assert.True(i > 0));
            Assert.Equal(order.OrderBy(i => i), order);   // menu order as approved
        }

        [Fact]
        public void Form_HasNoDurationOrWorkerHourFields_OnlyWhatTheUserEnters()
        {
            var type = typeof(PlantStockManager.Pages.Production.Labour.OvertimeModel);
            var bound = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<BindPropertyAttribute>() != null)
                .Select(p => p.Name).OrderBy(x => x).ToList();
            Assert.Equal(new[] { "AreaId", "EndTime", "FemaleWorkers", "Id", "LabourDate", "MaleWorkers", "PolyhouseId", "Remarks", "StartTime", "SubmitToken", "WorkDescription" }, bound);
            Assert.Equal(typeof(int?), type.GetProperty("MaleWorkers")!.PropertyType);
            Assert.Equal(typeof(TimeOnly?), type.GetProperty("StartTime")!.PropertyType);
        }

        [Fact]
        public void HistoryPage_ShowsTheApprovedColumnsFiltersAndSummaries()
        {
            var page = ReadSource("Pages", "Production", "Labour", "OvertimeHistory.cshtml");
            foreach (var text in new[] { "<th>Date</th>", "<th>Area</th>", "<th>Polyhouse</th>", ">Male<", ">Female<", "<th>Start</th>", "<th>End</th>", ">Duration<",
                                         ">Male Worker-Hours<", ">Female Worker-Hours<", ">Total Worker-Hours<", "<th>Work Description</th>", ">Entries<", ">Total Duration<" })
                Assert.Contains(text, page);
            foreach (var filter in new[] { "name=\"date\"", "name=\"fromDate\"", "name=\"toDate\"", "name=\"areaId\"", "name=\"polyhouseId\"", "name=\"groupBy\"" })
                Assert.Contains(filter, page);
            Assert.Equal(new[] { "Date", "Month", "Area", "Polyhouse" }, PlantStockManager.Pages.Production.Labour.OvertimeHistoryModel.Groupings);
        }

        [Fact]
        public void Migration_IsGuarded_ComputesInTheDatabase_AndAudits()
        {
            var sql = ReadSource("Database", "Migrations", "2026-10-06_LabourOvertime.sql");
            Assert.Contains("NOT LIKE N'PlantsIMS2[_]Scratch[_]%'", sql);
            Assert.Contains("N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')", sql);
            Assert.Contains("@@SERVERNAME <> N'$(TARGET_SERVER)'", sql);
            Assert.Contains("DurationMinutes     AS (DATEDIFF(MINUTE, StartTime, EndTime)) PERSISTED", sql);
            Assert.Contains("AS (DATEDIFF(MINUTE, StartTime, EndTime) * MaleWorkers) PERSISTED", sql);
            Assert.Contains("AS (DATEDIFF(MINUTE, StartTime, EndTime) * FemaleWorkers) PERSISTED", sql);
            Assert.Contains("CHECK (EndTime > StartTime)", sql);
            Assert.Contains("CHECK (MaleWorkers + FemaleWorkers > 0)", sql);
            Assert.Contains("BETWEEN 0 AND 500", sql);
            Assert.Contains("DATEADD(MINUTE, 330, SYSUTCDATETIME())", sql);   // not in the future, India date
            Assert.Contains("TR_LabourOvertime_Location", sql);
            Assert.Contains("TR_LabourOvertime_Audit", sql);
            Assert.Contains("TR_LabourOvertimeHistory_Protect", sql);
            Assert.DoesNotContain("UNIQUE", sql.ToUpperInvariant().Replace("-- ", ""));   // separate entries are never merged or blocked
            Assert.DoesNotContain("INSERT INTO dbo.RolePermissions", sql);
            Assert.DoesNotContain("DROP TABLE", sql);

            var rollback = ReadSource("Database", "Migrations", "2026-10-06_LabourOvertime_Rollback.sql");
            Assert.Contains("THROW 51754", rollback);   // refuses while overtime rows exist
        }

        [Fact]
        public void LabourOvertime_BlocksAreaDeletion()
            => Assert.Contains(DeletionRules.AreaDependencies, d => d.Table == "LabourOvertime" && d.Column == "AreaId");
    }
}
