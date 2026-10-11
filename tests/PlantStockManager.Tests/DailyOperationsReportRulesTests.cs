using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using R = PlantStockManager.Services.DailyOperationsReportRules;

namespace PlantStockManager.Tests
{
    // Daily Operations Report -- the pure rules (Services/DailyOperationsReportRules.cs):
    // dates, Indian number grouping, "0" vs "not available", historical Ready Stock,
    // employee categories and the plain-text layout. Database behaviour is
    // DailyOperationsReportE2ETests; HTTP authorization is DailyOperationsReportAuthorizationTests.
    public class DailyOperationsReportRulesTests
    {
        private static readonly DateTime Today = new(2026, 10, 9);
        private static readonly DateTime Now = new(2026, 10, 9, 15, 45, 0);
        private static readonly DateTime D = new(2026, 10, 1);

        // ---- Indian number formatting --------------------------------------

        [Theory]
        [InlineData(0, "0")]
        [InlineData(7, "7")]
        [InlineData(999, "999")]
        [InlineData(1000, "1,000")]
        [InlineData(24585, "24,585")]
        [InlineData(102546, "1,02,546")]
        [InlineData(1234567, "12,34,567")]
        [InlineData(12345678, "1,23,45,678")]
        [InlineData(123456789, "12,34,56,789")]
        [InlineData(-102546, "-1,02,546")]
        public void Indian_GroupsDigitsTheIndianWay(int value, string expected)
            => Assert.Equal(expected, R.Indian(value));

        [Fact]
        public void Indian_DropsTrailingZeroes_KeepsRealFractions()
        {
            Assert.Equal("100", R.Indian(100.00m));
            Assert.Equal("1,02,546.5", R.Indian(102546.50m));
            Assert.Equal("12.125", R.Indian(12.125m, 3));
            Assert.Equal("12.13", R.Indian(12.125m));
            Assert.Equal("0.5", R.Indian(0.5m));
        }

        // ---- Dates -----------------------------------------------------------

        [Fact]
        public void ResolveDate_DefaultsToToday_KeepsPastDates_RefusesFutureDates()
        {
            Assert.Equal((Today, (string?)null), R.ResolveDate(null, Today));
            Assert.Equal((D, (string?)null), R.ResolveDate(D, Today));
            Assert.Equal(new DateTime(2019, 3, 4), R.ResolveDate(new DateTime(2019, 3, 4, 13, 0, 0), Today).Date);

            var (date, notice) = R.ResolveDate(Today.AddDays(1), Today);
            Assert.Equal(Today, date);
            Assert.Contains("future", notice);

            Assert.Equal(Today, R.ResolveDate(new DateTime(1990, 1, 1), Today).Date);
        }

        [Fact]
        public void PreviousAndNextDay_StepOneDay_AndNeverIntoTheFuture()
        {
            Assert.Equal(D.AddDays(-1), R.PreviousDay(D));
            Assert.Equal(D.AddDays(1), R.NextDay(D, Today));
            Assert.Equal(Today, R.NextDay(Today.AddDays(-1), Today));
            Assert.Null(R.NextDay(Today, Today));
            Assert.Null(R.PreviousDay(R.EarliestSelectableDate));
        }

        [Fact]
        public void ResolveRange_Defaults14Days_ClampsFuture_SwapsAndCaps()
        {
            var (f, t, n) = R.ResolveRange(null, null, D, Today);
            Assert.Equal((D.AddDays(-13), D, (string?)null), (f, t, n));

            (f, t, _) = R.ResolveRange(D, Today.AddDays(5), D, Today);
            Assert.Equal(Today, t);

            (f, t, _) = R.ResolveRange(D, D.AddDays(-3), D, Today);
            Assert.Equal((D.AddDays(-3), D), (f, t));

            (f, t, n) = R.ResolveRange(new DateTime(2026, 1, 1), Today, D, Today);
            Assert.Equal(R.MaxHistoryDays, (t - f).Days + 1);
            Assert.NotNull(n);
        }

        // ---- Building a day ----------------------------------------------------

        private static DailyOperationsSourceData Data(DateTime from, DateTime to, Action<DailyOperationsSourceData>? fill = null)
        {
            var d = new DailyOperationsSourceData { From = from, To = to };
            d.Coverage = new DailyOperationsCoverage
            {
                SowingFrom = new(2026, 9, 1), SeedlingBookingsFrom = new(2026, 9, 1), PottedBookingsFrom = new(2026, 9, 1),
                SeedlingDispatchFrom = new(2026, 9, 1), PottedDispatchFrom = new(2026, 9, 1), OutletSalesFrom = new(2026, 9, 1),
                FertilizerFrom = new(2026, 9, 1), WastageFrom = new(2026, 9, 1), ReadyStockFrom = new(2026, 9, 1),
                LoginTrackingStartUtc = new DateTime(2026, 9, 1, 3, 0, 0),
            };
            d.PlantTypes.AddRange(new[] { new NamedId(1, "MERI GOLD"), new NamedId(3, "ZINNIA"), new NamedId(4, "SEASONAL VARITIES") });
            fill?.Invoke(d);
            return d;
        }

        [Fact]
        public void Build_TakesOnlyTheSelectedDaysRows()
        {
            var data = Data(D.AddDays(-1), D.AddDays(1), d =>
            {
                d.Sowing.Add(new DayFigure(D.AddDays(-1), "Seed", 1, 500));
                d.Sowing.Add(new DayFigure(D, "Seed", 2, 1200));
                d.Sowing.Add(new DayFigure(D, "Cutting", 1, 300));
                d.Sowing.Add(new DayFigure(D.AddDays(1), "Cutting", 4, 9999));
                d.SeedlingDispatches.Add(new DayFigure(D, null, 1, 450));
                d.SeedlingDispatches.Add(new DayFigure(D.AddDays(1), null, 7, 1));
            });
            var r = R.Build(data, D, Today, Now);
            Assert.Equal(1500m, r.Sowing.Quantity);
            Assert.Equal(3, r.Sowing.Count);
            Assert.Equal(1200m, r.SeedSowingQuantity);
            Assert.Equal(300m, r.CuttingSowingQuantity);
            Assert.Equal(1, r.SeedlingDispatch.Count);
            Assert.Throws<ArgumentOutOfRangeException>(() => R.Build(data, D.AddDays(2), Today, Now));
        }

        [Fact]
        public void NoTransactions_IsZero_ButNoRecordsYet_IsNotAvailable()
        {
            var data = Data(D, D, d =>
            {
                d.Coverage.OutletSalesFrom = D.AddDays(1);   // outlet sales only begin the next day
                d.Coverage.PottedDispatchFrom = null;        // never recorded at all
            });
            var r = R.Build(data, D, Today, Now);

            Assert.True(r.Sowing.Available);
            Assert.Equal(0m, r.Sowing.Quantity);
            Assert.True(r.Wastage.Available);
            Assert.False(r.OutletSales.Available);
            Assert.Contains(D.AddDays(1).ToString("yyyy-MM-dd"), r.OutletSales.UnavailableReason);
            Assert.False(r.PottedDispatch.Available);
            Assert.Contains("yet", r.PottedDispatch.UnavailableReason);

            var text = R.ToPlainText(r);
            Assert.Contains("Sowing: 0\n", text);
            Assert.Contains("Outlet Sales: Not available\n", text);
            Assert.Contains("Dispatch: Seedling: 0, Potted: Not available\n", text);
            Assert.Contains("- Outlet sales: No outlet sale records exist before", text);
        }

        [Fact]
        public void EmptyDatabase_EverythingIsNotAvailable_NothingIsInventedOrThrown()
        {
            var data = new DailyOperationsSourceData { From = D, To = D };
            var r = R.Build(data, D, Today, Now);
            Assert.False(r.Sowing.Available);
            Assert.False(r.ReadyStockAvailable);
            Assert.False(r.SeedlingBookings.Available);
            Assert.False(r.Fertilizer.Available);
            Assert.False(r.LoginDataAvailable);
            Assert.Equal(0, r.TotalEmployees);
            var text = R.ToPlainText(r);
            Assert.StartsWith("Date: 2026-10-01\nDaily Operations\nSowing: Not available\nReady Stock: Not available\n", text);
            Assert.EndsWith("Thank You!", text);
        }

        // ---- Ready Stock as of a past date -----------------------------------

        [Fact]
        public void ReadyStock_PastDate_IsTheLedgerBalanceAtTheEndOfThatDay_NotTodaysStock()
        {
            var data = Data(D, Today, d =>
            {
                d.ReadyStockMovements.Add(new PlantTypeMovement(4, null, 100000));      // opening before the range
                d.ReadyStockMovements.Add(new PlantTypeMovement(4, D, 2546));
                d.ReadyStockMovements.Add(new PlantTypeMovement(3, D, 7500));
                d.ReadyStockMovements.Add(new PlantTypeMovement(3, D.AddDays(2), -7500)); // all dispatched later
                d.ReadyStockMovements.Add(new PlantTypeMovement(1, Today, 900));
                d.ReadyStockCurrent.AddRange(new[] { new ReadyStockBalance(4, 102546m, 0, 0), new ReadyStockBalance(3, 0m, 0, 0), new ReadyStockBalance(1, 900m, 0, 0) });
            });

            var past = R.Build(data, D, Today, Now);
            Assert.True(past.ReadyStockAvailable);
            Assert.Equal(new[] { new ReadyStockLine("SEASONAL VARITIES", 102546), new ReadyStockLine("ZINNIA", 7500) }, past.ReadyStock);
            Assert.Contains("end of 2026-10-01", past.ReadyStockBasis);

            var later = R.Build(data, D.AddDays(2), Today, Now);
            Assert.Equal(new[] { new ReadyStockLine("SEASONAL VARITIES", 102546) }, later.ReadyStock);

            var today = R.Build(data, Today, Today, Now);
            Assert.Equal(new[] { new ReadyStockLine("SEASONAL VARITIES", 102546), new ReadyStockLine("MERI GOLD", 900) }, today.ReadyStock);
        }

        [Fact]
        public void ReadyStock_WhenTheLedgerDoesNotReconcile_PastIsNotAvailable_TodayUsesTheCurrentTable()
        {
            var data = Data(D, Today, d =>
            {
                d.Coverage.ReadyStockUnreconciledBatches = 2;
                d.ReadyStockMovements.Add(new PlantTypeMovement(4, D, 10));
                d.ReadyStockCurrent.Add(new ReadyStockBalance(4, 55m, 0, 0));
            });
            var past = R.Build(data, D, Today, Now);
            Assert.False(past.ReadyStockAvailable);
            Assert.Contains("cannot be reconstructed", past.ReadyStockUnavailableReason);
            Assert.Empty(past.ReadyStock);
            Assert.Contains("Ready Stock: Not available\n", R.ToPlainText(past));

            var today = R.Build(data, Today, Today, Now);
            Assert.True(today.ReadyStockAvailable);
            Assert.Equal(new[] { new ReadyStockLine("SEASONAL VARITIES", 55) }, today.ReadyStock);
            Assert.Contains("Current balance", today.ReadyStockBasis);
        }

        // 2026-10-10 definitions: owned = approved - dispatched; physical = owned - in transit;
        // available = physical - reserved. A transfer in transit is counted once (owned, not physical).
        [Fact]
        public void ReadyStock_SplitsOwnedReservedAndInTransit_AndShowsAvailable()
        {
            var data = Data(D, Today, d =>
            {
                d.ReadyStockMovements.Add(new PlantTypeMovement(4, null, 1000));
                d.ReadyStockMovements.Add(new PlantTypeMovement(4, D, 300, StockKind.Reserved));
                d.ReadyStockMovements.Add(new PlantTypeMovement(4, D, 200, StockKind.InTransit));
                d.ReadyStockMovements.Add(new PlantTypeMovement(4, D.AddDays(1), -200, StockKind.InTransit)); // received next day
                d.ReadyStockMovements.Add(new PlantTypeMovement(4, D.AddDays(1), -200));                      // ... and leaves the source
            });

            var sent = R.Build(data, D, Today, Now);
            var line = Assert.Single(sent.ReadyStock);
            Assert.Equal((1000m, 300m, 200m, 800m, 500m), (line.Owned, line.Reserved, line.InTransit, line.Physical, line.Available));
            Assert.Equal(500m, sent.ReadyStockAvailableTotal);
            Assert.Equal(800m, sent.ReadyStockPhysicalTotal);
            Assert.Equal(1000m, sent.ReadyStockOwnedTotal);
            Assert.Contains("SEASONAL VARITIES: 500\nReady Stock totals: physical 800, reserved 300, in transit 200\n", R.ToPlainText(sent));

            var received = Assert.Single(R.Build(data, D.AddDays(1), Today, Now).ReadyStock);
            Assert.Equal((800m, 300m, 0m, 800m, 500m), (received.Owned, received.Reserved, received.InTransit, received.Physical, received.Available));
        }

        [Fact]
        public void ReadyStock_LegacyRecordingLag_IsStatedInTheNotes_NeverMoved()
        {
            var lag = new RecordingLag("seedling dispatches", D, D.AddDays(2), 1200);
            var onBusinessDay = R.RecordingLagNotes(new[] { lag }, D);
            Assert.Contains(onBusinessDay, n => n.Contains("1,200 plants of seedling dispatches dated 2026-10-01 were recorded later (2026-10-03)"));
            var onRecordedDay = R.RecordingLagNotes(new[] { lag }, D.AddDays(2));
            Assert.Contains(onRecordedDay, n => n.Contains("recorded on this date belong to earlier dates (2026-10-01)"));
            Assert.Empty(R.RecordingLagNotes(new[] { lag }, D.AddDays(1)));
            Assert.Empty(R.RecordingLagNotes(new[] { new RecordingLag("transplanting", D, D, 5) }, D));
        }

        // 2026-10-11: a past day whose movements are dated before their batch existed is flagged;
        // so is any figure that rebuilds to a negative number. The figures are never replaced.
        [Fact]
        public void ReadyStock_UnreliableHistory_IsFlagged_NeverReplacedByTodaysStock()
        {
            var data = Data(D, Today, d =>
            {
                d.ReadyStockMovements.Add(new PlantTypeMovement(4, D, -24));                        // dispatch dated D ...
                d.ReadyStockMovements.Add(new PlantTypeMovement(4, D.AddDays(2), 480));             // ... batch approved D+2
                d.ReadyStockDatingIssues.Add(new ReadyStockDatingIssue(4, D, D.AddDays(2), 24));
            });
            var first = R.Build(data, D, Today, Now);
            Assert.True(first.ReadyStockAvailable);
            Assert.Equal(-24m, Assert.Single(first.ReadyStock).Owned);                                   // the ledger's figure, not today's
            Assert.Contains(first.Notes, n => n.Contains("SEASONAL VARITIES on this date cannot be reliably reconstructed") && n.Contains("2026-10-03"));
            Assert.Contains(first.Notes, n => n.Contains("rebuilds to a negative figure"));
            Assert.Contains(R.Build(data, D.AddDays(1), Today, Now).Notes, n => n.Contains("cannot be reliably reconstructed"));
            var approved = R.Build(data, D.AddDays(2), Today, Now);
            Assert.DoesNotContain(approved.Notes, n => n.Contains("cannot be reliably reconstructed"));
            Assert.Equal(456m, Assert.Single(approved.ReadyStock).Owned);
        }

        [Fact]
        public void ReadyStock_BeforeTheLedgerBegins_IsNotAvailable()
        {
            var data = Data(D, D, d => d.Coverage.ReadyStockFrom = D.AddDays(1));
            Assert.False(R.Build(data, D, Today, Now).ReadyStockAvailable);
        }

        // ---- Fertilizer / wastage units -----------------------------------------

        [Fact]
        public void Fertilizer_IsReportedPerUnit_NeverSummedAcrossUnits()
        {
            var data = Data(D, D, d =>
            {
                d.Fertilizer.Add(new DayFigure(D, "KG", 2, 12.125m));
                d.Fertilizer.Add(new DayFigure(D, "25 Kg Bag", 1, 3m));
                d.Fertilizer.Add(new DayFigure(D, "kg", 1, 1m));
            });
            var r = R.Build(data, D, Today, Now);
            Assert.Equal(4, r.Fertilizer.Count);
            Assert.Equal("13.125 KG, 3 25 Kg Bag", R.FertilizerText(r));
            Assert.Equal("0", R.FertilizerText(R.Build(Data(D, D), D, Today, Now)));
        }

        [Fact]
        public void Wastage_SeedLotsAreShownInTheirOwnUnit()
        {
            var data = Data(D, D, d =>
            {
                d.Wastage.Add(new DayFigure(D, null, 3, 574));
                d.SeedLotWastage.Add(new DayFigure(D, "Seeds", 2, 188));
            });
            Assert.Equal("574 (Seed lots: 188 Seeds)", R.WastageText(R.Build(data, D, Today, Now)));
        }

        // ---- Software activity --------------------------------------------------

        private static DailyOperationsSourceData People(DateTime day, Action<DailyOperationsSourceData> fill) => Data(day, day, d =>
        {
            d.Employees.AddRange(new[] { new NamedId(1, "Abhijeet Gaikwad"), new NamedId(2, "Akshay Shinde"), new NamedId(3, "Rohit Shinde"), new NamedId(4, "Sarika Kolhe"), new NamedId(5, "Rahul Kamble"), new NamedId(6, "Rahul Kamble") });
            fill(d);
        });

        [Fact]
        public void Employees_ArePartitioned_AndTheCategoriesAddUp()
        {
            var data = People(D, d =>
            {
                d.Logins.Add(new EmployeeLogin(2, D, D.AddHours(-2), 3));   // logged in + active
                d.Logins.Add(new EmployeeLogin(3, D, D.AddHours(-1), 1));   // logged in, nothing recorded
                d.Logins.Add(new EmployeeLogin(5, D, D.AddHours(1), 1));    // logged in + active
                d.Logins.Add(new EmployeeLogin(99, D, D, 4));               // not an active employee -> ignored
                d.Logins.Add(new EmployeeLogin(1, D.AddDays(-1), D, 1));    // another day -> ignored
                d.Activity.Add(new EmployeeActivityDay(2, D));
                d.Activity.Add(new EmployeeActivityDay(5, D));
                d.Activity.Add(new EmployeeActivityDay(4, D));              // worked without a new login
            });
            var r = R.Build(data, D, Today, Now);

            Assert.True(r.LoginDataAvailable);
            Assert.Equal(6, r.TotalEmployees);
            Assert.Equal(new[] { "Akshay Shinde", "Rohit Shinde", "Rahul Kamble" }, r.LoggedIn);
            Assert.Equal(new[] { "Akshay Shinde", "Rahul Kamble" }, r.ActiveUsers);
            Assert.Equal(new[] { "Rohit Shinde" }, r.NoActivityRecorded);
            Assert.Equal(new[] { "Abhijeet Gaikwad", "Sarika Kolhe", "Rahul Kamble" }, r.NotLoggedIn); // two people, same name: both listed
            Assert.Equal(new[] { "Sarika Kolhe" }, r.ActivityWithoutLogin);
            Assert.Equal(5, r.LoginEvents);
            Assert.Equal(r.LoggedIn.Count, r.ActiveUsers.Count + r.NoActivityRecorded.Count);
            Assert.Equal(r.TotalEmployees, r.LoggedIn.Count + r.NotLoggedIn.Count);
            Assert.Single(r.LoggedIn.Intersect(r.NotLoggedIn)); // only the shared name "Rahul Kamble": two different people
        }

        [Fact]
        public void Logins_BeforeTrackingBegan_AreNotAvailable_NotNotLoggedIn()
        {
            var data = People(D, d => d.Coverage.LoginTrackingStartUtc = new DateTime(2026, 10, 1, 4, 44, 0));
            var before = R.Build(Data(D.AddDays(-1), D.AddDays(-1), x => { x.Coverage = data.Coverage; x.Employees.AddRange(data.Employees); }), D.AddDays(-1), Today, Now);
            Assert.False(before.LoginDataAvailable);
            Assert.Empty(before.NotLoggedIn);
            Assert.Equal(6, before.TotalEmployees);
            var text = R.ToPlainText(before);
            Assert.Contains("Logged In: Not available\nActive Users: Not available\nNo Activity Recorded: Not available\nNot Logged In: Not available\n", text);
            Assert.Contains("Login tracking began on 2026-10-01", text);

            // The first tracked day (04:44 UTC = 10:14 IST) is reported, with a partial-day note.
            var first = R.Build(data, D, Today, Now);
            Assert.True(first.LoginDataAvailable);
            Assert.Contains("10:14 IST", first.LoginPartialDayNote);
        }

        [Fact]
        public void LoginTrackingStart_IsJudgedOnItsIndiaDay()
        {
            // 2026-09-30 19:00 UTC is 2026-10-01 00:30 IST: 30 September is NOT tracked.
            var data = People(D.AddDays(-1), d => d.Coverage.LoginTrackingStartUtc = new DateTime(2026, 9, 30, 19, 0, 0));
            Assert.False(R.Build(data, D.AddDays(-1), Today, Now).LoginDataAvailable);
        }

        // ---- Plain text ------------------------------------------------------

        [Fact]
        public void PlainText_HasTheRequestedLayout()
        {
            var day = D;
            var data = Data(day, day, d =>
            {
                d.Sowing.Add(new DayFigure(day, "Seed", 3, 24585));
                d.ReadyStockMovements.Add(new PlantTypeMovement(4, null, 102546));
                d.ReadyStockMovements.Add(new PlantTypeMovement(3, null, 7500));
                d.SeedlingBookings.Add(new DayFigure(day, null, 2, 900, 3));
                d.SeedlingDispatches.Add(new DayFigure(day, null, 1, 400));
                d.Wastage.Add(new DayFigure(day, null, 4, 574));
                d.Employees.AddRange(new[] { new NamedId(1, "Abhijeet Gaikwad"), new NamedId(2, "Achyut Kunjir"), new NamedId(3, "Rohit Shinde"), new NamedId(4, "Sarika Kolhe") });
                d.Logins.Add(new EmployeeLogin(3, day, day, 1));
                d.Logins.Add(new EmployeeLogin(4, day, day, 2));
                d.Activity.Add(new EmployeeActivityDay(4, day));
            });
            var text = R.ToPlainText(R.Build(data, day, Today, Now));

            Assert.Equal(
                "Date: 2026-10-01\n" +
                "Daily Operations\n" +
                "Sowing: 24,585\n" +
                "Ready Stock (available for booking/dispatch):\n" +
                "SEASONAL VARITIES: 1,02,546\n" +
                "ZINNIA: 7,500\n" +
                "Ready Stock totals: physical 1,10,046, reserved 0, in transit 0\n" +
                "Bookings: Seedling: 2, Potted: 0\n" +
                "Dispatch: Seedling: 1, Potted: 0\n" +
                "Outlet Sales: 0\n" +
                "Fertilizer Usage: 0\n" +
                "Wastage: 574\n" +
                "Software Activity\n" +
                "Total Employees: 4\n" +
                "Logged In: 2\n" +
                "Active Users: 1\n" +
                "No Activity Recorded: Rohit Shinde\n" +
                "Not Logged In: Abhijeet Gaikwad, Achyut Kunjir\n" +
                "Thank You!", text);
        }

        [Fact]
        public void PlainText_ForToday_SaysItIsLive()
        {
            var text = R.ToPlainText(R.Build(Data(Today, Today), Today, Today, Now));
            Assert.Contains("- Today's report is live: figures up to 15:45 IST", text);
        }

        [Fact]
        public void PlainText_EmptyNameLists_SayNone()
        {
            var data = People(D, d => d.Logins.AddRange(Enumerable.Range(1, 6).Select(i => new EmployeeLogin(i, D, D, 1))));
            var text = R.ToPlainText(R.Build(data, D, Today, Now));
            Assert.Contains("No Activity Recorded: Abhijeet Gaikwad, Akshay Shinde, Rohit Shinde, Sarika Kolhe, Rahul Kamble, Rahul Kamble\nNot Logged In: None\n", text);
            Assert.DoesNotContain("Activity Without New Login", text);
        }

        [Fact]
        public void PlainText_SeveralDays_AreSeparatedOldestFirst()
        {
            var data = Data(D, D.AddDays(1));
            var text = R.ToPlainText(R.BuildAll(data, Today, Now));
            Assert.True(text.IndexOf("Date: 2026-10-01", StringComparison.Ordinal) < text.IndexOf("Date: 2026-10-02", StringComparison.Ordinal));
            Assert.Contains("Thank You!\n\n----------\n\nDate: 2026-10-02", text);
        }

        // ---- Guards on shared code ----------------------------------------------

        [Fact]
        public void Authorization_ThePageIsFullAccessOnly()
        {
            var rule = FeatureAuthorizationConventions.GetRule("/ManagementDashboard/DailyOperationsReport");
            Assert.Same(FeatureAuthorizationConventions.FullAccessOnlyRule, rule);
            Assert.True(FeatureAuthorizationConventions.IsMapped("/ManagementDashboard/DailyOperationsReport"));
            Assert.Equal("Admin.ManageAreas", FeatureAuthorizationConventions.GetRule("/ManagementDashboard/Index").Read); // dashboard unchanged
        }

        // WastageRepository's SQL was refactored so this report can reuse its loss
        // sources by India day; the Wasted Stock report's own query text must be
        // byte-for-byte what it was (SHA-256 of the committed text, line endings normalised).
        [Fact]
        public void WastageReport_OwnQuery_IsUnchanged()
        {
            var sql = (string)typeof(WastageRepository).GetField("Sql", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql.Replace("\r", "")))).ToLowerInvariant();
            Assert.Equal("06d4f3f65b7fb49a8e727b429c46aed074f478446ef4453a77532a95f1704a38", hash);
            Assert.DoesNotContain("@IndiaOffsetMinutes", sql);
            Assert.Contains("@IndiaOffsetMinutes", WastageRepository.SourcesSql(indiaDays: true));
        }
    }
}
