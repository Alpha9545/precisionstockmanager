using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // 2026-11-07 Bed Sowing Alerts -- the pure rules (no database): Due Date = bed Sowing Date + Bed Transplant Days;
    // Overdue / Due Today / Due Soon through the SAME ClassifyReadyAlert as Ready Alerts; open beds only; no days = listed
    // under No schedule, never dated. Plus: the Ready Alerts (Direct Seed Sowing) classification is unchanged.
    public class BedTransplantAlertRulesTests
    {
        private static readonly DateTime Today = new(2026, 11, 7);
        private const int Window = SeedSowingRepository.DefaultReadySoonWindowDays;   // 3

        private static BedTransplantAlert Bed(int id, int daysAgo, int? transplantDays, string status = BedSowing.StatusPending,
            decimal qty = 1000, decimal transplanted = 0, decimal lost = 0)
            => new()
            {
                BedSowingId = id, BedSowingCode = "BS-" + id, SpeciesName = "V" + id, SowingDate = Today.AddDays(-daysAgo),
                Quantity = qty, TransplantedQuantity = transplanted, LostQuantity = lost, Status = status, BedTransplantDays = transplantDays,
            };

        [Fact]
        public void DueDate_IsBedSowingDatePlusDays_AndNullWithoutDays()
        {
            Assert.Equal(new DateTime(2026, 11, 2), BedTransplantAlertRules.DueDate(new DateTime(2026, 10, 8, 15, 30, 0), 25));
            Assert.Null(BedTransplantAlertRules.DueDate(Today, null));
            Assert.Null(BedTransplantAlertRules.DueDate(Today, 0));     // never a same-day guess from an invalid value
            Assert.Null(BedTransplantAlertRules.DueDate(Today, -5));
            Assert.Null(BedTransplantAlertRules.DueDate(Today, 366));
            Assert.Equal(Today.AddDays(365), BedTransplantAlertRules.DueDate(Today, 365));
        }

        [Theory]
        [InlineData(30, 20, BedTransplantAlertRules.Overdue)]     // due 10 days ago
        [InlineData(21, 20, BedTransplantAlertRules.Overdue)]     // due yesterday
        [InlineData(20, 20, BedTransplantAlertRules.DueToday)]    // due today
        [InlineData(19, 20, BedTransplantAlertRules.DueSoon)]     // due tomorrow
        [InlineData(17, 20, BedTransplantAlertRules.DueSoon)]     // due in 3 days = the window's last day
        [InlineData(16, 20, BedTransplantAlertRules.None)]        // due in 4 days: outside the window
        public void Classify_OpenBed_ByDueDate(int sownDaysAgo, int transplantDays, string expected)
        {
            var bed = Bed(1, sownDaysAgo, transplantDays);
            Assert.Equal(expected, BedTransplantAlertRules.Classify(bed.Status, bed.RemainingQuantity, bed.DueDate, Today, Window));
        }

        [Fact]
        public void Classify_UsesTheSameDateRuleAsReadyAlerts()
        {
            for (var offset = -5; offset <= 6; offset++)
            {
                var due = Today.AddDays(offset);
                Assert.Equal(SeedSowingRepository.ClassifyReadyAlert("Sown", due, Today, Window),
                    BedTransplantAlertRules.Classify(BedSowing.StatusPending, 10, due, Today, Window));
            }
        }

        [Fact]
        public void Classify_NoDays_IsNoSchedule_NotADate()
        {
            Assert.Equal(BedTransplantAlertRules.NoSchedule, BedTransplantAlertRules.Classify(BedSowing.StatusPending, 500, null, Today, Window));
            Assert.Equal(BedTransplantAlertRules.NoSchedule, BedTransplantAlertRules.Classify(BedSowing.StatusPartial, 500, null, Today, Window));
        }

        [Theory]
        [InlineData(BedSowing.StatusCompleted, 0)]
        [InlineData(BedSowing.StatusCancelled, 1000)]
        [InlineData(BedSowing.StatusPending, 0)]       // nothing left in the bed
        [InlineData("Unknown", 1000)]
        [InlineData(null, 1000)]
        public void Classify_ClosedOrEmptyBed_NeverAlerts(string? status, decimal remaining)
        {
            Assert.Equal(BedTransplantAlertRules.None, BedTransplantAlertRules.Classify(status, remaining, Today.AddDays(-10), Today, Window));
            Assert.Equal(BedTransplantAlertRules.None, BedTransplantAlertRules.Classify(status, remaining, null, Today, Window));
        }

        [Fact]
        public void PartiallyTransplantedBed_StillAlerts_ForWhatIsLeft()
        {
            var bed = Bed(1, 30, 20, BedSowing.StatusPartial, qty: 1000, transplanted: 600, lost: 100);
            Assert.Equal(300, bed.RemainingQuantity);
            var groups = BedTransplantAlertRules.Group(new[] { bed }, Today, Window);
            Assert.Single(groups.Overdue);
            Assert.Equal(300, groups.OverdueQuantity);
        }

        [Fact]
        public void Group_SortsIntoTheFourLists_AndCountsEqualTheLists()
        {
            var beds = new[]
            {
                Bed(1, 40, 20), Bed(2, 25, 20),                      // overdue (20 and 5 days)
                Bed(3, 20, 20),                                       // due today
                Bed(4, 18, 20), Bed(5, 17, 20),                       // due soon (2 and 3 days)
                Bed(6, 5, 20),                                        // not yet (15 days): not listed
                Bed(7, 10, null), Bed(8, 2, null),                   // no schedule
                Bed(9, 40, 20, BedSowing.StatusCompleted, transplanted: 1000), Bed(10, 40, 20, BedSowing.StatusCancelled),   // closed
            };
            var g = BedTransplantAlertRules.Group(beds, Today, Window);
            Assert.Equal(new[] { 1, 2 }, g.Overdue.Select(b => b.BedSowingId));          // most overdue first
            Assert.Equal(new[] { 3 }, g.DueToday.Select(b => b.BedSowingId));
            Assert.Equal(new[] { 4, 5 }, g.DueSoon.Select(b => b.BedSowingId));
            Assert.Equal(new[] { 7, 8 }, g.NoSchedule.Select(b => b.BedSowingId));        // oldest sowing first
            Assert.Equal(2000, g.OverdueQuantity);
            Assert.Equal(1000, g.DueTodayQuantity);
            Assert.Equal(2000, g.DueSoonQuantity);
            Assert.Equal(2000, g.NoScheduleQuantity);
            // every open bed lands in exactly one list
            Assert.Equal(7, g.Overdue.Count + g.DueToday.Count + g.DueSoon.Count + g.NoSchedule.Count);
        }

        [Fact]
        public void Group_WiderWindow_MovesLaterBedsIntoDueSoon()
        {
            var beds = new[] { Bed(1, 10, 20) };   // due in 10 days
            Assert.Empty(BedTransplantAlertRules.Group(beds, Today, 3).DueSoon);
            Assert.Single(BedTransplantAlertRules.Group(beds, Today, 10).DueSoon);
        }

        [Theory]
        [InlineData(0, Window)]
        [InlineData(-4, Window)]
        [InlineData(7, 7)]
        [InlineData(90, 90)]
        [InlineData(5000, 90)]
        public void NormalizeWindow_DefaultsAndCaps(int input, int expected)
            => Assert.Equal(expected, BedTransplantAlertRules.NormalizeWindow(input));

        [Theory]
        [InlineData(null, true)]
        [InlineData(1, true)]
        [InlineData(25, true)]
        [InlineData(365, true)]
        [InlineData(0, false)]
        [InlineData(-1, false)]
        [InlineData(366, false)]
        public void ValidateDays_NullOr1To365(int? days, bool ok)
        {
            var (valid, error) = BedTransplantAlertRules.ValidateDays(days);
            Assert.Equal(ok, valid);
            Assert.Equal(ok ? null : BedTransplantAlertRules.DaysRangeMessage, error);
        }

        // ---- Ready Alerts (Direct Seed Sowing) stay exactly as they were ----

        [Theory]
        [InlineData("Sown", -1, "Overdue")]
        [InlineData("Sown", 0, "ReadyToday")]
        [InlineData("Sown", 3, "ReadySoon")]
        [InlineData("Sown", 4, "None")]
        [InlineData("Completed", -1, "None")]
        [InlineData("Cancelled", 0, "None")]
        public void ReadyAlerts_Classification_Unchanged(string status, int offset, string expected)
        {
            Assert.Equal(expected, SeedSowingRepository.ClassifyReadyAlert(status, Today.AddDays(offset), Today, Window));
            Assert.Equal("None", SeedSowingRepository.ClassifyReadyAlert("Sown", null, Today, Window));
            Assert.Equal(3, SeedSowingRepository.DefaultReadySoonWindowDays);
        }

        [Fact]
        public void ReadyAlerts_CandidateQuery_Unchanged()
        {
            // The tray-batch alert query keeps its filter; bed alerts never feed into it.
            var source = File.ReadAllText(Path.Combine(RepoRoot(), "Data", "SeedSowingRepository.cs"));
            Assert.Contains("WHERE sw.Status = 'Sown' AND sw.ExpectedReadyDate IS NOT NULL AND sw.ExpectedReadyDate <= @Horizon", source);
            Assert.DoesNotContain("BedTransplantDays", source);
            Assert.DoesNotContain("BedTransplantSchedule", source);
        }

        internal static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.sln")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new DirectoryNotFoundException("PlantStockManager.sln not found above the test output folder.");
        }
    }
}
