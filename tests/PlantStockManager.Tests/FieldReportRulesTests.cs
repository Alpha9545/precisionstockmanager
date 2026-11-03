using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services.FieldWork;
using static PlantStockManager.Data.FieldReportRepository;
using static PlantStockManager.Services.FieldWork.FieldReportRules;

namespace PlantStockManager.Tests
{
    // Field Work Phase 5 (2026-11-03): the pure report rules (no database).
    public class FieldReportRulesTests
    {
        private static readonly DateTime Today = new(2026, 11, 3);

        private static DayRow Day(int id, int user, DateTime date, string status, decimal startKm, decimal? endKm, string? startFlags = null,
            string? endFlags = null, string review = "Pending", bool endGps = true)
            => new(id, user, "U" + user, date, status, date.AddHours(-2), endKm.HasValue ? date.AddHours(8) : null, startKm, endKm, startFlags, endFlags, review,
                12.97m, 77.59m, 10m, endGps ? 12.97m : null, endGps ? 77.59m : null, endGps ? 10m : null);

        [Fact]
        public void Range_DefaultsToThisMonthSoFar_SwapsAndCaps()
        {
            Assert.Equal(new DateRange(new DateTime(2026, 11, 1), Today), NormalizeRange(null, null, Today));
            Assert.Equal(new DateRange(new DateTime(2026, 10, 5), new DateTime(2026, 10, 9)), NormalizeRange(new DateTime(2026, 10, 9), new DateTime(2026, 10, 5), Today));
            Assert.Equal(Today.AddDays(-MaxRangeDays), NormalizeRange(Today.AddYears(-5), Today, Today).From);
            Assert.Equal(new DateRange(new DateTime(2026, 11, 1), Today), MonthSoFar(null, Today));                 // never past today
            Assert.Equal(new DateRange(new DateTime(2026, 2, 1), new DateTime(2026, 2, 28)), MonthSoFar("2026-02", Today));
        }

        [Fact]
        public void MonthBounds_AreIndiaMidnights_InUtc()
        {
            // 1 Nov 00:00 IST = 31 Oct 18:30 UTC; the range ends before 1 Dec 00:00 IST = 30 Nov 18:30 UTC
            var (fromUtc, toUtc) = UtcBounds(new DateRange(new DateTime(2026, 11, 1), new DateTime(2026, 11, 30)));
            Assert.Equal(new DateTime(2026, 10, 31, 18, 30, 0), fromUtc);
            Assert.Equal(new DateTime(2026, 11, 30, 18, 30, 0), toUtc);
            // a customer created at 23:59 IST on 31 Oct (18:29 UTC) is outside November; 00:01 IST on 1 Nov is inside
            Assert.True(new DateTime(2026, 10, 31, 18, 29, 0) < fromUtc);
            Assert.True(new DateTime(2026, 10, 31, 18, 31, 0) >= fromUtc);
        }

        [Fact]
        public void FlaggedDay_AutomaticFlagOrManagerFlag_UnlessApproved()
        {
            Assert.False(IsFlagged(null, null, "Pending"));
            Assert.True(IsFlagged("GPS reason given", null, "Pending"));
            Assert.True(IsFlagged(null, "Route longer than odometer", "Pending"));
            Assert.True(IsFlagged(null, null, "Flagged"));
            Assert.False(IsFlagged("GPS reason given", "x", "Approved"));   // approved despite the flags
            Assert.False(IsFlagged("  ", "", "Pending"));
        }

        [Fact]
        public void Km_OdometerOnlyWithAnEndReading_RouteIsStartVisitsEnd()
        {
            Assert.Equal(30m, OdometerKm(100m, 130m));
            Assert.Null(OdometerKm(100m, null));
            var day = Day(1, 7, Today, "Closed", 100, 130);
            // start 12.97 -> visit 12.98 -> visit 12.99 -> end 12.97 (0.01 degree latitude = 1.112 km): 4 x 1.112 = 4.45 km
            var visits = new[]
            {
                new VisitPoint(1, 11, Today.AddHours(1), 12.98m, 77.59m, 10m),
                new VisitPoint(1, 12, Today.AddHours(2), 12.99m, 77.59m, 10m),
                new VisitPoint(1, 13, Today.AddHours(3), null, null, null),       // a visit without GPS is skipped
            };
            Assert.Equal(4.45m, RouteKm(day, visits));
        }

        [Fact]
        public void DailyAndSummary_CountWorkedAutoClosedFlaggedDays_AndOdometerOnlyFromEndedDays()
        {
            var d1 = new DateTime(2026, 11, 1);
            var d2 = new DateTime(2026, 11, 2);
            var a = new Activity(
                new List<DayRow>
                {
                    Day(1, 7, d1, "Closed", 100, 140),
                    Day(2, 7, d2, "AutoClosed", 140, null, startFlags: "Low GPS"),
                    Day(3, 8, d2, "Closed", 50, 60, review: "Flagged"),
                },
                new List<VisitPoint> { new(1, 1, d1, 12.98m, 77.59m, 10m), new(1, 2, d1.AddHours(1), 12.98m, 77.59m, 10m), new(3, 3, d2, 12.97m, 77.59m, 10m) },
                new List<UserDateCount> { new(7, "U7", d1, 2), new(9, "U9", d2, 1) },
                new List<UserAmount> { new(7, "U7", "Approved", 300m, 2), new(7, "U7", "Submitted", 50m, 1), new(10, "U10", "Submitted", 20m, 1) },
                true);

            var daily = BuildDaily(a);
            Assert.Equal(3, daily.Count);
            var day1 = daily.Single(r => r.Date == d1);
            Assert.Equal((2, 2, (decimal?)40m, false), (day1.Visits, day1.NewCustomers, day1.OdometerKm, day1.Flagged));
            Assert.Null(daily.Single(r => r.Date == d2 && r.UserId == 7).OdometerKm);

            var summary = BuildSummary(a);
            var u7 = summary.Single(s => s.UserId == 7);
            Assert.Equal((2, 1, 2, 2, 40m, 1, 300m, 50m), (u7.DaysWorked, u7.AutoClosedDays, u7.Visits, u7.NewCustomers, u7.OdometerKm, u7.FlaggedDays, u7.ExpensesApproved, u7.ExpensesPending));
            var u8 = summary.Single(s => s.UserId == 8);
            Assert.Equal((1, 1, 10m), (u8.DaysWorked, u8.FlaggedDays, u8.OdometerKm));
            // employees with only a new customer or only an expense still get a row
            Assert.Equal(1, summary.Single(s => s.UserId == 9).NewCustomers);
            Assert.Equal(20m, summary.Single(s => s.UserId == 10).ExpensesPending);
        }

        [Fact]
        public void WonLost_EachCustomerOncePerOutcome_ForTheEmployeeWhoRecordedIt_DistrictsCaseInsensitive()
        {
            var moves = new List<StatusMove>
            {
                new(1, "Won", 7, "U7", "Kolar"),
                new(1, "Won", 7, "U7", "Kolar"),     // Won -> Negotiation -> Won again: one Won
                new(2, "Lost", 8, "U8", " kolar "),
                new(3, "Won", 8, "U8", null),
            };
            Assert.Equal((2, 1), CountMoves(moves));
            var current = new List<StatusCount>
            {
                new(7, "U7", "Interested", "Kolar", 3),
                new(7, "U7", "Won", "KOLAR", 1),
                new(8, "U8", "New", null, 2),
            };
            var r = BuildLeads(current, moves);
            Assert.Equal((2, 1), (r.MovedToWon, r.MovedToLost));
            Assert.Equal(3, r.ByStatus.Single(g => g.Key == "Interested").CurrentTotal);
            Assert.Equal(2, r.ByStatus.Single(g => g.Key == "Won").MovedToWon);
            var u7 = r.ByEmployee.Single(g => g.Label == "U7");
            Assert.Equal((4, 1, 0), (u7.CurrentTotal, u7.MovedToWon, u7.MovedToLost));
            var u8 = r.ByEmployee.Single(g => g.Label == "U8");
            Assert.Equal((2, 1, 1), (u8.CurrentTotal, u8.MovedToWon, u8.MovedToLost));
            var kolar = Assert.Single(r.ByDistrict, g => g.Key == "KOLAR");
            Assert.Equal((4, 1, 1), (kolar.CurrentTotal, kolar.MovedToWon, kolar.MovedToLost));
            var none = Assert.Single(r.ByDistrict, g => g.Label == NoDistrict);
            Assert.Equal((2, 1), (none.CurrentTotal, none.MovedToWon));
            Assert.Equal(NoDistrict, r.ByDistrict.Last().Label);   // "(none)" last
        }

        [Fact]
        public void Expenses_PerEmployeeAndCategory_ApprovedAndPendingSeparately()
        {
            var rows = BuildExpenses(new[]
            {
                new CategoryAmount(7, "U7", "Petrol", 300m, 50m),
                new CategoryAmount(7, "U7", "Food", 0m, 40m),
            });
            var u7 = Assert.Single(rows);
            Assert.Equal((300m, 90m), (u7.Approved, u7.Pending));
            Assert.Equal((300m, 50m), u7.ByCategory["Petrol"]);
            Assert.Equal((0m, 0m), u7.ByCategory["Toll"]);
        }

        [Fact]
        public void DashboardEmployees_NotStartedFirst_ThenOpen_ThenEnded_WithVisits()
        {
            var a = new Activity(new List<DayRow> { Day(1, 7, Today, "Closed", 1, 2), Day(2, 8, Today, "Open", 1, null), Day(3, 99, Today, "Open", 1, null) },
                new List<VisitPoint> { new(1, 1, Today, 12.9m, 77.5m, 5m), new(2, 2, Today, 12.9m, 77.5m, 5m), new(2, 3, Today, 12.9m, 77.5m, 5m) },
                new(), new(), true);
            var rows = BuildEmployeeDays(new[] { new FieldEmployeeOption(7, "U7"), new FieldEmployeeOption(8, "U8"), new FieldEmployeeOption(9, "U9") }, a);
            Assert.Equal(new[] { 9, 8, 99, 7 }, rows.Select(r => r.UserId));   // 99 worked but is no longer an active field employee: still shown
            Assert.Equal(2, rows.Single(r => r.UserId == 8).Visits);
            Assert.Null(rows.Single(r => r.UserId == 9).Status);
        }

        [Fact]
        public void StartDayGate_LetsMySummaryThroughAfterConsent_NotTheManagerPages()
        {
            Assert.Equal(FieldDayRules.GateResult.Consent, FieldDayRules.Gate(true, "/FieldWork/MySummary", false, null, false));
            Assert.Equal(FieldDayRules.GateResult.Allow, FieldDayRules.Gate(true, "/FieldWork/MySummary", true, null, false));
            Assert.Equal(FieldDayRules.GateResult.StartDay, FieldDayRules.Gate(true, "/FieldWork/Manage/Reports", true, null, false));
            Assert.Equal(FieldDayRules.GateResult.StartDay, FieldDayRules.Gate(true, "/FieldWork/Manage/Dashboard", true, "Closed", false));
        }
    }
}
