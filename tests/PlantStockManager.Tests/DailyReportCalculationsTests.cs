using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Daily Report (Step 3A): the one pure calculation involved in
    // assembling a DailyReport. Everything else DailyReportService does is
    // a direct pass-through of an existing repository's own aggregate
    // query -- nothing to unit-test there without a live database (see
    // DailyReportServiceE2ETests for that, gated behind PSM_SCRATCH_CONNECTION
    // like every other DB-backed E2E test in this project).
    public class DailyReportCalculationsTests
    {
        private static SeedlingDispatchLine Line(int dispatchId, decimal quantity)
            => new() { SeedlingDispatchId = dispatchId, Quantity = quantity };

        [Fact]
        public void NoLines_ReturnsZeroCountAndQuantity()
        {
            var (count, quantity) = DailyReportCalculations.SummarizeDispatchLines(new List<SeedlingDispatchLine>());
            Assert.Equal(0, count);
            Assert.Equal(0, quantity);
        }

        [Fact]
        public void SingleDispatch_SingleLine_CountsAsOneEvent()
        {
            var (count, quantity) = DailyReportCalculations.SummarizeDispatchLines(new[] { Line(101, 500) });
            Assert.Equal(1, count);
            Assert.Equal(500, quantity);
        }

        [Fact]
        public void SingleDispatch_MultipleLines_CountsAsOneEvent_QuantitiesSummed()
        {
            // one dispatch, split across 3 Ready Stock batches (substitution)
            var lines = new[] { Line(101, 200), Line(101, 150), Line(101, 50) };
            var (count, quantity) = DailyReportCalculations.SummarizeDispatchLines(lines);
            Assert.Equal(1, count);
            Assert.Equal(400, quantity);
        }

        [Fact]
        public void MultipleDispatches_EachCountedSeparately()
        {
            var lines = new[] { Line(101, 200), Line(102, 300), Line(103, 100) };
            var (count, quantity) = DailyReportCalculations.SummarizeDispatchLines(lines);
            Assert.Equal(3, count);
            Assert.Equal(600, quantity);
        }

        [Fact]
        public void MixOfSingleAndMultiLineDispatches_CountsEventsNotLines()
        {
            var lines = new[]
            {
                Line(101, 200), Line(101, 100), // dispatch 101: 2 lines, 1 event
                Line(102, 500),                 // dispatch 102: 1 line, 1 event
                Line(103, 50), Line(103, 50), Line(103, 50), // dispatch 103: 3 lines, 1 event
            };
            var (count, quantity) = DailyReportCalculations.SummarizeDispatchLines(lines);
            Assert.Equal(3, count);   // events, not the 6 lines
            Assert.Equal(950, quantity);
        }

        // ---- Step 5: employee activity partitioning ------------------------

        private static EmployeeLoginActivity Employee(int userId, string name, DateTime? firstLoginAtToday, bool hasActivityToday = false)
            => new() { UserId = userId, Name = name, FirstLoginAtToday = firstLoginAtToday, HasActivityToday = hasActivityToday };

        [Fact]
        public void PartitionEmployeeActivity_EmptyInput_ReturnsTwoEmptyLists()
        {
            var (loggedIn, notLoggedIn) = DailyReportCalculations.PartitionEmployeeActivity(new List<EmployeeLoginActivity>());
            Assert.Empty(loggedIn);
            Assert.Empty(notLoggedIn);
        }

        [Fact]
        public void PartitionEmployeeActivity_AllLoggedIn_NoneInNotLoggedIn()
        {
            var activity = new[]
            {
                Employee(1, "Rohit", new DateTime(2026, 9, 30, 9, 0, 0)),
                Employee(2, "Reshma", new DateTime(2026, 9, 30, 9, 15, 0)),
            };
            var (loggedIn, notLoggedIn) = DailyReportCalculations.PartitionEmployeeActivity(activity);
            Assert.Equal(2, loggedIn.Count);
            Assert.Empty(notLoggedIn);
        }

        [Fact]
        public void PartitionEmployeeActivity_NoneLoggedIn_AllInNotLoggedIn()
        {
            var activity = new[] { Employee(1, "Rohit", null), Employee(2, "Reshma", null) };
            var (loggedIn, notLoggedIn) = DailyReportCalculations.PartitionEmployeeActivity(activity);
            Assert.Empty(loggedIn);
            Assert.Equal(2, notLoggedIn.Count);
        }

        [Fact]
        public void PartitionEmployeeActivity_Mixed_SplitsCorrectly_PreservesFirstLoginAt()
        {
            var loginTime = new DateTime(2026, 9, 30, 9, 30, 0);
            var activity = new[]
            {
                Employee(1, "Rohit", loginTime),
                Employee(2, "Reshma", null),
                Employee(3, "Sarika", null),
            };
            var (loggedIn, notLoggedIn) = DailyReportCalculations.PartitionEmployeeActivity(activity);

            var only = Assert.Single(loggedIn);
            Assert.Equal(1, only.UserId);
            Assert.Equal(loginTime, only.FirstLoginAtToday);
            Assert.True(only.LoggedInToday);

            Assert.Equal(2, notLoggedIn.Count);
            Assert.All(notLoggedIn, e => Assert.False(e.LoggedInToday));
            Assert.All(notLoggedIn, e => Assert.Null(e.FirstLoginAtToday));
        }

        [Fact]
        public void PartitionEmployeeActivity_EveryEmployeeAccountedForExactlyOnce()
        {
            var activity = new[]
            {
                Employee(1, "A", new DateTime(2026, 9, 30, 8, 0, 0)),
                Employee(2, "B", null),
                Employee(3, "C", new DateTime(2026, 9, 30, 10, 0, 0)),
                Employee(4, "D", null),
                Employee(5, "E", null),
            };
            var (loggedIn, notLoggedIn) = DailyReportCalculations.PartitionEmployeeActivity(activity);

            Assert.Equal(activity.Length, loggedIn.Count + notLoggedIn.Count); // no one lost, no one duplicated
            Assert.Empty(loggedIn.Select(e => e.UserId).Intersect(notLoggedIn.Select(e => e.UserId))); // no overlap
        }

        // ---- Step 7: EmployeeLoginActivity.IsActiveUser (pure computed property) ----

        [Fact]
        public void IsActiveUser_LoggedInAndHasActivity_IsTrue()
        {
            var e = Employee(1, "A", new DateTime(2026, 9, 30, 9, 0, 0), hasActivityToday: true);
            Assert.True(e.IsActiveUser);
        }

        [Fact]
        public void IsActiveUser_LoggedInButNoActivity_IsFalse()
        {
            var e = Employee(1, "A", new DateTime(2026, 9, 30, 9, 0, 0), hasActivityToday: false);
            Assert.False(e.IsActiveUser);
        }

        [Fact]
        public void IsActiveUser_ActivityButNoLogin_IsFalse()
        {
            // Approved rule #8: activity alone, without a login, never counts as an Active User.
            var e = Employee(1, "A", firstLoginAtToday: null, hasActivityToday: true);
            Assert.False(e.LoggedInToday);
            Assert.True(e.HasActivityToday);
            Assert.False(e.IsActiveUser);
        }

        [Fact]
        public void IsActiveUser_NeitherLoginNorActivity_IsFalse()
        {
            var e = Employee(1, "A", firstLoginAtToday: null, hasActivityToday: false);
            Assert.False(e.IsActiveUser);
        }

        // ---- Step 7: SummarizeActiveUsers (pure) ----------------------------

        [Fact]
        public void SummarizeActiveUsers_EmptyInput_ReturnsZeroZero()
        {
            var (active, noActivity) = DailyReportCalculations.SummarizeActiveUsers(new List<EmployeeLoginActivity>());
            Assert.Equal(0, active);
            Assert.Equal(0, noActivity);
        }

        [Fact]
        public void SummarizeActiveUsers_AllLoggedInHaveActivity_AllActive_NoneNoActivity()
        {
            var loggedIn = new[]
            {
                Employee(1, "A", new DateTime(2026, 9, 30, 9, 0, 0), hasActivityToday: true),
                Employee(2, "B", new DateTime(2026, 9, 30, 9, 5, 0), hasActivityToday: true),
            };
            var (active, noActivity) = DailyReportCalculations.SummarizeActiveUsers(loggedIn);
            Assert.Equal(2, active);
            Assert.Equal(0, noActivity);
        }

        [Fact]
        public void SummarizeActiveUsers_NoneHaveActivity_ZeroActive_AllNoActivity()
        {
            var loggedIn = new[]
            {
                Employee(1, "A", new DateTime(2026, 9, 30, 9, 0, 0), hasActivityToday: false),
                Employee(2, "B", new DateTime(2026, 9, 30, 9, 5, 0), hasActivityToday: false),
            };
            var (active, noActivity) = DailyReportCalculations.SummarizeActiveUsers(loggedIn);
            Assert.Equal(0, active);
            Assert.Equal(2, noActivity);
        }

        [Fact]
        public void SummarizeActiveUsers_Mixed_SplitsCorrectly_NoActivityEqualsLoggedInMinusActive()
        {
            var loggedIn = new[]
            {
                Employee(1, "A", new DateTime(2026, 9, 30, 9, 0, 0), hasActivityToday: true),
                Employee(2, "B", new DateTime(2026, 9, 30, 9, 5, 0), hasActivityToday: false),
                Employee(3, "C", new DateTime(2026, 9, 30, 9, 10, 0), hasActivityToday: true),
                Employee(4, "D", new DateTime(2026, 9, 30, 9, 15, 0), hasActivityToday: false),
                Employee(5, "E", new DateTime(2026, 9, 30, 9, 20, 0), hasActivityToday: false),
            };
            var (active, noActivity) = DailyReportCalculations.SummarizeActiveUsers(loggedIn);

            Assert.Equal(2, active);      // A, C
            Assert.Equal(3, noActivity);  // B, D, E
            Assert.Equal(loggedIn.Length, active + noActivity); // approved formula: NoActivity = LoggedIn - Active
        }

        // ---- Step 8B: GetNoActivityRecordedNames (pure) ---------------------

        [Fact]
        public void GetNoActivityRecordedNames_EmptyInput_ReturnsEmptyList()
        {
            var names = DailyReportCalculations.GetNoActivityRecordedNames(new List<EmployeeLoginActivity>());
            Assert.Empty(names);
        }

        [Fact]
        public void GetNoActivityRecordedNames_AllHaveActivity_ReturnsEmptyList()
        {
            var loggedIn = new[]
            {
                Employee(1, "A", new DateTime(2026, 9, 30, 9, 0, 0), hasActivityToday: true),
                Employee(2, "B", new DateTime(2026, 9, 30, 9, 5, 0), hasActivityToday: true),
            };
            var names = DailyReportCalculations.GetNoActivityRecordedNames(loggedIn);
            Assert.Empty(names);
        }

        [Fact]
        public void GetNoActivityRecordedNames_SomeHaveNoActivity_ReturnsOnlyThoseNames_PreservesOrder()
        {
            var loggedIn = new[]
            {
                Employee(1, "Amit Patil", new DateTime(2026, 9, 30, 9, 0, 0), hasActivityToday: false),
                Employee(2, "Active Employee", new DateTime(2026, 9, 30, 9, 5, 0), hasActivityToday: true),
                Employee(3, "Rahul Shinde", new DateTime(2026, 9, 30, 9, 10, 0), hasActivityToday: false),
            };
            var names = DailyReportCalculations.GetNoActivityRecordedNames(loggedIn);
            Assert.Equal(new[] { "Amit Patil", "Rahul Shinde" }, names);
        }

        // ---- Step 8B: GetNotLoggedInNames (pure) ----------------------------

        [Fact]
        public void GetNotLoggedInNames_EmptyInput_ReturnsEmptyList()
        {
            var names = DailyReportCalculations.GetNotLoggedInNames(new List<EmployeeLoginActivity>());
            Assert.Empty(names);
        }

        [Fact]
        public void GetNotLoggedInNames_ReturnsNamesInGivenOrder()
        {
            var notLoggedIn = new[]
            {
                Employee(1, "Kavita Nair", null),
                Employee(2, "Rohan Deshpande", null),
            };
            var names = DailyReportCalculations.GetNotLoggedInNames(notLoggedIn);
            Assert.Equal(new[] { "Kavita Nair", "Rohan Deshpande" }, names);
        }

        [Fact]
        public void GetNotLoggedInNames_DoesNotDeduplicateDistinctEmployeesSharingTheSameName()
        {
            // Two different employees can legitimately share a display name --
            // this must never silently collapse into one entry.
            var notLoggedIn = new[]
            {
                Employee(1, "Rahul Shinde", null),
                Employee(2, "Rahul Shinde", null),
            };
            var names = DailyReportCalculations.GetNotLoggedInNames(notLoggedIn);
            Assert.Equal(new[] { "Rahul Shinde", "Rahul Shinde" }, names);
        }
    }
}
