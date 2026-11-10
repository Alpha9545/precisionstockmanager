using System.Text.RegularExpressions;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // 2026-11-09 (owner): Daily Labour Attendance dates -- a non-administrator enters TODAY only (India business date);
    // a System Administrator may use any date. Pure rule, India-date boundary and source-guard tests (no database).
    // The page / repository behaviour is in DailyLabourAttendanceE2ETests.Dates_*, the HTTP pipeline in
    // LabourAttendanceDateAuthorizationTests.
    public class LabourAttendanceDateRuleTests
    {
        private static readonly DateTime Today = new(2026, 11, 9);

        [Fact]
        public void Message_IsTheOwnersWording()
            => Assert.Equal("Attendance can only be entered for today's date. Contact the System Administrator for entries on other dates.",
                DailyLabourRules.TodayOnlyMessage);

        [Fact]
        public void NonAdministrator_TodayOnly_PastAndFutureRejected()
        {
            Assert.Null(DailyLabourRules.ValidateAttendanceDate(Today, Today, isAdministrator: false));
            Assert.Null(DailyLabourRules.ValidateAttendanceDate(Today.AddHours(15), Today, isAdministrator: false));   // a time part is ignored
            foreach (var other in new[] { Today.AddDays(-1), Today.AddDays(1), Today.AddDays(-365), Today.AddDays(30) })
                Assert.Equal(DailyLabourRules.TodayOnlyMessage, DailyLabourRules.ValidateAttendanceDate(other, Today, isAdministrator: false));
        }

        [Fact]
        public void Administrator_AnyDate_PastTodayAndFuture()
        {
            foreach (var any in new[] { Today, Today.AddDays(-1), Today.AddDays(-400), Today.AddDays(1), Today.AddDays(60) })
                Assert.Null(DailyLabourRules.ValidateAttendanceDate(any, Today, isAdministrator: true));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void MissingDate_IsRejected_ForEveryone(bool isAdministrator)
        {
            Assert.Equal("Date is required.", DailyLabourRules.ValidateAttendanceDate(null, Today, isAdministrator));
            Assert.Equal("Date is required.", DailyLabourRules.ValidateAttendanceDate(default(DateTime), Today, isAdministrator));
        }

        [Fact]
        public void LabourOvertime_KeepsItsOwnRule_TodayAndEarlier()
        {
            // Overtime is a separate entry (not attendance) and is unchanged by this requirement.
            Assert.Null(DailyLabourRules.ValidateDate(Today.AddDays(-3), Today));
            Assert.Equal(DailyLabourRules.FutureDateMessage, DailyLabourRules.ValidateDate(Today.AddDays(1), Today));
        }

        // ---- India business date around midnight -------------------------------------------------------------------

        [Fact]
        public void IndiaDate_ChangesAtMidnightIndiaTime_NotAtMidnightUtc()
        {
            // 23:59:59 IST on 9 Nov = 18:29:59 UTC; 00:00:00 IST on 10 Nov = 18:30:00 UTC.
            Assert.Equal(new DateTime(2026, 11, 9), AppClock.IndiaDateOf(new DateTime(2026, 11, 9, 18, 29, 59, DateTimeKind.Utc)));
            Assert.Equal(new DateTime(2026, 11, 10), AppClock.IndiaDateOf(new DateTime(2026, 11, 9, 18, 30, 0, DateTimeKind.Utc)));
            // Between 00:00 and 05:30 IST the UTC date is still the previous day -- the India date wins.
            Assert.Equal(new DateTime(2026, 11, 10), AppClock.IndiaDateOf(new DateTime(2026, 11, 9, 20, 0, 0, DateTimeKind.Utc)));
            Assert.Equal(new DateTime(2026, 11, 10), AppClock.IndiaDateOf(new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc)));
        }

        [Fact]
        public void BusinessClock_OneSecondBeforeAndAtMidnightIndia_DecidesWhichDayANonAdministratorMayEnter()
        {
            var clock = new TestBusinessClock();
            clock.AtIndia(new DateTime(2026, 11, 9, 23, 59, 59));
            Assert.Equal(new DateTime(2026, 11, 9), clock.Today);
            Assert.Null(DailyLabourRules.ValidateAttendanceDate(new DateTime(2026, 11, 9), clock.Today, false));
            Assert.Equal(DailyLabourRules.TodayOnlyMessage, DailyLabourRules.ValidateAttendanceDate(new DateTime(2026, 11, 10), clock.Today, false));

            clock.AtIndia(new DateTime(2026, 11, 10, 0, 0, 0));
            Assert.Equal(new DateTime(2026, 11, 10), clock.Today);
            Assert.Equal(DailyLabourRules.TodayOnlyMessage, DailyLabourRules.ValidateAttendanceDate(new DateTime(2026, 11, 9), clock.Today, false));
            Assert.Null(DailyLabourRules.ValidateAttendanceDate(new DateTime(2026, 11, 10), clock.Today, false));
        }

        [Fact]
        public void BusinessClock_DefaultsToTheRealIndiaDate()
            => Assert.Equal(AppClock.Today, new BusinessClock().Today);

        // ---- source guards ---------------------------------------------------------------------------------------

        private static string ReadSource(params string[] parts)
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "PlantStockManager.csproj")))
                dir = Path.GetDirectoryName(dir);
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir! }.Concat(parts).ToArray()));
        }

        [Fact]
        public void AttendancePage_ChecksTheDateRuleOnPost_WithTheServerSideAdministratorClaim_AndTheBusinessClock()
        {
            var code = ReadSource("Pages", "Production", "Labour", "Attendance.cshtml.cs");
            Assert.Contains("DailyLabourRules.ValidateAttendanceDate(LabourDate, Today, CanUseAnyDate)", code);
            Assert.Contains("public bool CanUseAnyDate => User.IsFullAccess();", code);
            Assert.Contains("public DateTime Today => _clock.Today;", code);
            Assert.DoesNotContain("DailyLabourRules.ValidateDate(", code);   // the old "today and earlier" rule
            Assert.DoesNotContain("AppClock.Today", code);

            // The date check comes before the save in OnPostAsync.
            var post = code[code.IndexOf("public async Task<IActionResult> OnPostAsync()", StringComparison.Ordinal)..];
            Assert.True(post.IndexOf("ValidateAttendanceDate", StringComparison.Ordinal) < post.IndexOf("_attendanceRepo.SaveAsync", StringComparison.Ordinal));
        }

        [Fact]
        public void AttendanceView_NonAdministratorDatePicker_IsLockedToToday_AdministratorHasNoLimit()
        {
            var view = ReadSource("Pages", "Production", "Labour", "Attendance.cshtml");
            var inputs = Regex.Matches(view, "<input type=\"date\" name=\"labourDate\"[^>]*>").Select(m => m.Value).ToList();
            Assert.Equal(2, inputs.Count);
            var admin = Assert.Single(inputs, i => !i.Contains("readonly"));
            Assert.DoesNotContain("min=", admin);
            Assert.DoesNotContain("max=", admin);
            var others = Assert.Single(inputs, i => i.Contains("readonly"));
            Assert.Contains("value=\"@Model.Today.ToString(\"yyyy-MM-dd\")\"", others);
            Assert.Contains("min=\"@Model.Today.ToString(\"yyyy-MM-dd\")\"", others);
            Assert.Contains("max=\"@Model.Today.ToString(\"yyyy-MM-dd\")\"", others);
            Assert.Contains("@if (Model.CanUseAnyDate)", view);
        }

        // Every way attendance can be written goes through the Attendance page's POST: the repository's SaveAsync has
        // no other caller, and the legacy Daily Labour Entry page cannot create records and edits only as an administrator.
        [Fact]
        public void EveryAttendanceWritePath_GoesThroughTheCheckedPost()
        {
            var root = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory)))!;
            while (!File.Exists(Path.Combine(root, "PlantStockManager.csproj")))
                root = Path.GetDirectoryName(root)!;
            var sources = new[] { "Pages", "Endpoints", "Services", "Data" }
                .Select(d => Path.Combine(root, d)).Where(Directory.Exists)
                .SelectMany(d => Directory.GetFiles(d, "*.cs", SearchOption.AllDirectories))
                .ToDictionary(f => Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllText);

            var inserts = sources.Where(s => Regex.IsMatch(s.Value, @"(INSERT\s+INTO|UPDATE)\s+dbo\.DailyLabourAttendance\b")).Select(s => s.Key).ToList();
            Assert.Equal(new[] { "Data/DailyLabourAttendanceRepository.cs" }, inserts);

            var savers = sources.Where(s => s.Value.Contains("_attendanceRepo.SaveAsync(")).Select(s => s.Key).ToList();
            Assert.Equal(new[] { "Pages/Production/Labour/Attendance.cshtml.cs" }, savers);

            var entry = sources["Pages/Production/Labour/Entry.cshtml.cs"];
            Assert.Contains("return RetiredForNewRecords();", entry);
            Assert.Contains("if (!User.IsFullAccess())", entry);
        }
    }
}
