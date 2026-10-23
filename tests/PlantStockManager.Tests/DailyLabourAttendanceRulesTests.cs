using System.IO;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using PlantStockManager.Authorization;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Daily Labour Attendance (2026-10-06) -- pure rules and wiring: the 9:00 AM / 2:00 PM
    // estimate, Incomplete days, validation, the India-time clock. The database behaviour
    // (authorization, duplicates, corrections + audit, legacy records) is
    // DailyLabourAttendanceE2ETests, against a scratch copy.
    public class DailyLabourAttendanceRulesTests
    {
        private static string ReadSource(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
        }

        // ---- the estimate ------------------------------------------------------------------------------

        [Theory]
        [InlineData(20, 17, 17, 3)]     // approved example, male
        [InlineData(15, 12, 12, 3)]     // approved example, female
        [InlineData(10, 10, 10, 0)]     // everybody stayed
        [InlineData(10, 14, 10, 4)]     // more at 2 PM: the extra 4 were present at one time only
        [InlineData(0, 0, 0, 0)]
        [InlineData(7, 0, 0, 7)]        // nobody at 2 PM: all half day
        [InlineData(0, 7, 0, 7)]
        [InlineData(500, 500, 500, 0)]
        public void Classify_FullDayIsTheSmallerCount_HalfDayIsTheDifference(int at9, int at2, int full, int half)
            => Assert.Equal((full, half), DailyLabourRules.Classify(at9, at2));

        [Fact]
        public void ApprovedExample_GivesTheApprovedResult()
        {
            var day = new DailyLabourDay { Male9 = 20, Female9 = 15, Male2 = 17, Female2 = 12 };
            Assert.True(day.IsComplete);
            Assert.Equal((17, 3, 12, 3), (day.MaleFullDay!.Value, day.MaleHalfDay!.Value, day.FemaleFullDay!.Value, day.FemaleHalfDay!.Value));
            Assert.Equal((29, 6), (day.TotalFullDay!.Value, day.TotalHalfDay!.Value));
            Assert.Equal(32m, day.LabourDays);
        }

        [Fact]
        public void MaleAndFemale_AreClassifiedSeparately()
        {
            // Male count drops, female count rises: never netted against each other.
            var day = new DailyLabourDay { Male9 = 10, Female9 = 5, Male2 = 6, Female2 = 9 };
            Assert.Equal((6, 4, 5, 4), (day.MaleFullDay!.Value, day.MaleHalfDay!.Value, day.FemaleFullDay!.Value, day.FemaleHalfDay!.Value));
            Assert.Equal((11, 8), (day.TotalFullDay!.Value, day.TotalHalfDay!.Value));
        }

        [Fact]
        public void OneTimeOnly_IsIncomplete_AndHasNoEstimate()
        {
            foreach (var day in new[]
            {
                new DailyLabourDay { Male9 = 20, Female9 = 15 },
                new DailyLabourDay { Male2 = 17, Female2 = 12 },
            })
            {
                Assert.False(day.IsComplete);
                Assert.Null(day.MaleFullDay);
                Assert.Null(day.MaleHalfDay);
                Assert.Null(day.FemaleFullDay);
                Assert.Null(day.FemaleHalfDay);
                Assert.Null(day.TotalFullDay);
                Assert.Null(day.TotalHalfDay);
                Assert.Null(day.LabourDays);
            }
        }

        [Fact]
        public void ManualRecord_KeepsItsEnteredCounts()
        {
            var day = new DailyLabourDay { Source = DailyLabourDay.ManualSource, ManualMaleFullDay = 10, ManualMaleHalfDay = 2, ManualFemaleFullDay = 8, ManualFemaleHalfDay = 1 };
            Assert.True(day.IsManual);
            Assert.True(day.IsComplete);
            Assert.Equal((10, 2, 8, 1), (day.MaleFullDay!.Value, day.MaleHalfDay!.Value, day.FemaleFullDay!.Value, day.FemaleHalfDay!.Value));
            Assert.Equal((18, 3), (day.TotalFullDay!.Value, day.TotalHalfDay!.Value));
        }

        [Fact]
        public void Summary_CountsIncompleteDays_ButLeavesThemOutOfTheTotals()
        {
            var s = new DailyLabourDaySummary();
            s.Add(new DailyLabourDay { Male9 = 20, Female9 = 15, Male2 = 17, Female2 = 12 });     // 17/3, 12/3
            s.Add(new DailyLabourDay { Male9 = 99, Female9 = 99 });                                 // incomplete
            s.Add(new DailyLabourDay { Source = DailyLabourDay.ManualSource, ManualMaleFullDay = 10, ManualMaleHalfDay = 2, ManualFemaleFullDay = 8, ManualFemaleHalfDay = 1 });
            Assert.Equal((3, 2, 1, 1), (s.Days, s.CompleteDays, s.IncompleteDays, s.ManualDays));
            Assert.Equal((27, 5, 20, 4), (s.MaleFullDay, s.MaleHalfDay, s.FemaleFullDay, s.FemaleHalfDay));
            Assert.Equal((47, 9), (s.TotalFullDay, s.TotalHalfDay));
            Assert.Equal(51.5m, s.LabourDays);
        }

        // ---- validation --------------------------------------------------------------------------------

        [Fact]
        public void AttendanceTime_BlankIsAllowedOnlyWhenNotYetRecorded()
        {
            Assert.Empty(DailyLabourRules.ValidateAttendanceTime(null, null, DailyLabourRules.Afternoon, alreadyRecorded: false));
            var errors = DailyLabourRules.ValidateAttendanceTime(null, null, DailyLabourRules.Afternoon, alreadyRecorded: true);
            Assert.Single(errors);
            Assert.StartsWith("2:00 PM attendance is already recorded", errors[0]);
        }

        [Fact]
        public void AttendanceTime_NeedsBothCounts_EachZeroTo500()
        {
            Assert.Empty(DailyLabourRules.ValidateAttendanceTime(0, 0, DailyLabourRules.Morning, false));
            Assert.Empty(DailyLabourRules.ValidateAttendanceTime(500, 500, DailyLabourRules.Morning, false));
            Assert.Equal(new[] { "9:00 AM Female is required (enter 0 if none)." }, DailyLabourRules.ValidateAttendanceTime(20, null, DailyLabourRules.Morning, false));
            Assert.Equal(new[] { "2:00 PM Male is required (enter 0 if none)." }, DailyLabourRules.ValidateAttendanceTime(null, 4, DailyLabourRules.Afternoon, false));
            Assert.Equal(new[] { "9:00 AM Male cannot be negative." }, DailyLabourRules.ValidateAttendanceTime(-1, 0, DailyLabourRules.Morning, false));
            Assert.Equal(new[] { "2:00 PM Female cannot be more than 500." }, DailyLabourRules.ValidateAttendanceTime(0, 501, DailyLabourRules.Afternoon, true));
        }

        [Fact]
        public void OnlyTheTwoAttendanceTimesExist()
        {
            Assert.Equal(new[] { "AM09", "PM14" }, DailyLabourRules.AttendanceTimes);
            Assert.True(DailyLabourRules.IsAttendanceTime("AM09"));
            Assert.True(DailyLabourRules.IsAttendanceTime("PM14"));
            Assert.False(DailyLabourRules.IsAttendanceTime("AM10"));
            Assert.False(DailyLabourRules.IsAttendanceTime(null));
            Assert.Equal("9:00 AM", DailyLabourRules.AttendanceTimeLabel("AM09"));
            Assert.Equal("2:00 PM", DailyLabourRules.AttendanceTimeLabel("PM14"));
        }

        // ---- India time ------------------------------------------------------------------------------

        [Fact]
        public void AppClock_IsIndiaTime_UtcPlus0530()
        {
            Assert.Equal(TimeSpan.FromHours(5.5), AppClock.IndiaTimeZone.GetUtcOffset(new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc)));
            Assert.Equal(TimeSpan.FromHours(5.5), AppClock.IndiaTimeZone.GetUtcOffset(new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc)));   // no daylight saving
            // 20:00 UTC on 5 Oct is already 6 Oct in India -- the date a UTC server would get wrong.
            Assert.Equal(new DateTime(2026, 10, 6, 1, 30, 0), AppClock.ToIndiaTime(new DateTime(2026, 10, 5, 20, 0, 0)));
            var drift = (AppClock.Now - TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, AppClock.IndiaTimeZone)).Duration();
            Assert.True(drift < TimeSpan.FromSeconds(5));
            Assert.Equal(AppClock.Now.Date, AppClock.Today);
        }

        // ---- wiring ----------------------------------------------------------------------------------

        [Fact]
        public void Pages_UseTheExistingLabourPermissions_AndAreInTheMenu()
        {
            // 2026-10-23: + Labour.Attendance (attendance only, Outlet Sales -- 2026-10-26_LabourAttendanceOutlet.sql)
            Assert.Equal("Labour.Enter|Labour.Attendance", FeatureAuthorizationConventions.GetRule("/Production/Labour/Attendance").Read);
            // 2026-10-20: Attendance History is a System Administrator report (menu: Reports)
            Assert.Same(FeatureAuthorizationConventions.FullAccessOnlyRule, FeatureAuthorizationConventions.GetRule("/Production/Labour/AttendanceHistory"));
            var layout = ReadSource("Pages", "Shared", "_Layout.cshtml");
            Assert.Contains("<a asp-page=\"/Production/Labour/Attendance\">Daily Attendance</a>", layout);
            Assert.Contains("<a asp-page=\"/Production/Labour/AttendanceHistory\">Attendance History</a>", layout);
        }

        [Fact]
        public void AttendanceForm_HasOnlyDateLocationAndTheTwoTimes_NoFullHalfDayOrWorkerFields()
        {
            var type = typeof(PlantStockManager.Pages.Production.Labour.AttendanceModel);
            var bound = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<BindPropertyAttribute>() != null)
                .Select(p => p.Name).OrderBy(x => x).ToList();
            Assert.Equal(new[] { "AreaId", "Female2", "Female9", "LabourDate", "Male2", "Male9", "PolyhouseId", "Remarks2", "Remarks9" }, bound);
            foreach (var name in new[] { "Male9", "Female9", "Male2", "Female2" })
                Assert.Equal(typeof(int?), type.GetProperty(name)!.PropertyType);   // whole numbers only

            var page = ReadSource("Pages", "Production", "Labour", "Attendance.cshtml");
            foreach (var id in new[] { "male9", "female9", "male2", "female2", "maleFull", "maleHalf", "femaleFull", "femaleHalf", "totalFull", "totalHalf", "incompleteNotice" })
                Assert.Contains($"id=\"{id}\"", page);
            Assert.Contains("Estimated", page);
            Assert.DoesNotContain("asp-page-handler=\"Delete\"", page);
        }

        [Fact]
        public void HistoryPage_ShowsTheApprovedColumns()
        {
            var page = ReadSource("Pages", "Production", "Labour", "AttendanceHistory.cshtml");
            foreach (var text in new[] { ">9:00 AM<", ">2:00 PM<", ">Male<", ">Female<", ">Full Day<", ">Half Day<", ">Total<", "Incomplete", "Manual", "date-total" })
                Assert.Contains(text, page);
            foreach (var filter in new[] { "name=\"fromDate\"", "name=\"toDate\"", "name=\"areaId\"", "name=\"polyhouseId\"" })
                Assert.Contains(filter, page);
        }

        [Fact]
        public void Migration_IsGuarded_KeepsTheOldTable_AndAuditsInTheDatabase()
        {
            var sql = ReadSource("Database", "Migrations", "2026-10-06_DailyLabourAttendance.sql");
            Assert.Contains("NOT LIKE N'PlantsIMS2[_]Scratch[_]%'", sql);
            Assert.Contains("N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')", sql);
            Assert.Contains("@@SERVERNAME <> N'$(TARGET_SERVER)'", sql);
            Assert.Contains("CHECK (AttendanceTime IN ('AM09', 'PM14'))", sql);
            Assert.Contains("BETWEEN 0 AND 500", sql);
            Assert.Contains("WHERE PolyhouseId IS NOT NULL", sql);
            Assert.Contains("WHERE PolyhouseId IS NULL", sql);
            Assert.Contains("TR_DailyLabourAttendance_Location", sql);
            Assert.Contains("TR_DailyLabourAttendance_Audit", sql);
            Assert.Contains("TR_DailyLabourAttendanceHistory_Protect", sql);
            Assert.Contains("THROW 51725", sql);   // no attendance where a manual record exists
            // The old manual table is read, never changed.
            Assert.DoesNotContain("ALTER TABLE dbo.DailyLabourCounts", sql);
            Assert.DoesNotContain("DELETE FROM dbo.DailyLabourCounts", sql);
            Assert.DoesNotContain("UPDATE dbo.DailyLabourCounts", sql);
            Assert.DoesNotContain("DROP TABLE", sql);
            Assert.DoesNotContain("INSERT INTO dbo.RolePermissions", sql);   // existing Labour permissions are reused

            var rollback = ReadSource("Database", "Migrations", "2026-10-06_DailyLabourAttendance_Rollback.sql");
            Assert.Contains("THROW 51734", rollback);   // refuses while attendance rows exist
            Assert.DoesNotContain("DailyLabourCounts (", rollback);
        }

        [Fact]
        public void DailyLabourAttendance_BlocksAreaDeletion()
            => Assert.Contains(DeletionRules.AreaDependencies, d => d.Table == "DailyLabourAttendance" && d.Column == "AreaId");
    }
}
