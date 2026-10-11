using PlantStockManager.Services;
using static PlantStockManager.Services.TrayTransferRules;

namespace PlantStockManager.Tests
{
    // Pure rules of the 2026-10-10 changes: business-dated ledger rows (LedgerDates), tray
    // adjustments and tray transfers (TrayTransferRules), and the tray history categories.
    public class TrayTransferRulesTests
    {
        private static readonly DateTime NowIndia = new(2026, 10, 10, 15, 30, 0);
        private static readonly DateTime Today = NowIndia.Date;
        private static Location Main(int polyhouse = 2) => new(1, "MainOffice", true, polyhouse, 1);
        private static Location Mp(int polyhouse = 14) => new(127, "MotherPlant", true, polyhouse, 127);

        // ---- LedgerDates ------------------------------------------------------------------

        [Fact]
        public void LedgerDates_TodayOrNoDate_IsNow_PastKeepsTheTimeOfDay_FutureIsRefused()
        {
            Assert.Equal((true, (DateTime?)null, (string?)null), LedgerDates.Instant(null, NowIndia));
            Assert.Equal((true, (DateTime?)null, (string?)null), LedgerDates.Instant(Today, NowIndia));

            var (ok, utc, error) = LedgerDates.Instant(Today.AddDays(-3), NowIndia);
            Assert.True(ok, error);
            Assert.Equal(AppClock.IndiaToUtc(new DateTime(2026, 10, 7, 15, 30, 0)), utc);
            Assert.Equal(new DateTime(2026, 10, 7), AppClock.ToIndiaTime(utc!.Value).Date);   // falls on its business day

            var future = LedgerDates.Instant(Today.AddDays(1), NowIndia);
            Assert.False(future.Ok);
            Assert.Equal(LedgerDates.FutureDateMessage, future.Error);
        }

        [Fact]
        public void LedgerDates_ReservationRelease_IsNeverBeforeTheReservation()
        {
            var dispatch = new DateTime(2026, 10, 5, 10, 0, 0, DateTimeKind.Utc);
            Assert.Null(LedgerDates.ReleaseInstant(null, dispatch));
            Assert.Null(LedgerDates.ReleaseInstant(dispatch, null));
            Assert.Equal(dispatch, LedgerDates.ReleaseInstant(dispatch, dispatch.AddDays(-2)));
            Assert.Equal(dispatch.AddDays(1), LedgerDates.ReleaseInstant(dispatch, dispatch.AddDays(1)));
        }

        // ---- Adjustment ---------------------------------------------------------------------

        [Theory]
        [InlineData(5, 10, "Physical count", true)]
        [InlineData(-10, 10, "All broken", true)]
        [InlineData(-11, 10, "Too many", false)]       // never below zero
        [InlineData(0, 10, "Nothing", false)]          // zero changes nothing
        [InlineData(1.5, 10, "Half tray", false)]      // whole trays only
        [InlineData(3, 10, "   ", false)]             // reason required
        [InlineData(3, 10, null, false)]
        public void Adjustment_WholeTrays_WithAReason_NeverBelowZero(double delta, double balance, string? reason, bool expected)
        {
            var (ok, error) = ValidateAdjustment((decimal)delta, (decimal)balance, reason, "24 Cavity");
            Assert.Equal(expected, ok);
            if (!ok) Assert.False(string.IsNullOrWhiteSpace(error));
        }

        [Fact]
        public void Adjustment_RejectsUnknownCavity_AndOverlongReason()
        {
            Assert.False(ValidateAdjustment(1, 0, "x", "7 Cavity").Ok);
            Assert.False(ValidateAdjustment(1, 0, new string('x', MaxReasonLength + 1), "24 Cavity").Ok);
            Assert.True(ValidateAdjustment(1, 0, new string('x', MaxReasonLength), "24 Cavity").Ok);
        }

        // ---- Transfer -----------------------------------------------------------------------

        [Fact]
        public void Transfer_WholeTraysBetweenTwoPolyhouses_UpToTheSourceBalance()
        {
            Assert.True(ValidateTransfer(Main(), Mp(), "24 Cavity", 40, 40, Today, Today).Ok);
            Assert.True(ValidateTransfer(Main(2), Main(3), "24 Cavity", 1, 40, Today.AddDays(-2), Today).Ok);

            Assert.Contains("Only 40", ValidateTransfer(Main(), Mp(), "24 Cavity", 41, 40, Today, Today).Error);
            Assert.False(ValidateTransfer(Main(), Mp(), "24 Cavity", 0, 40, Today, Today).Ok);
            Assert.False(ValidateTransfer(Main(), Mp(), "24 Cavity", 2.5m, 40, Today, Today).Ok);
            Assert.False(ValidateTransfer(Main(), Mp(), "11 Cavity", 1, 40, Today, Today).Ok);
            Assert.Contains("different Polyhouses", ValidateTransfer(Main(2), Main(2), "24 Cavity", 1, 40, Today, Today).Error);
            Assert.Equal(LedgerDates.FutureDateMessage, ValidateTransfer(Main(), Mp(), "24 Cavity", 1, 40, Today.AddDays(1), Today).Error);
        }

        [Fact]
        public void Transfer_RefusesOutletInactiveAndMismatchedLocations()
        {
            var outlet = new Location(9, OutletRules.AreaType, true, 30, 9);
            var inactive = new Location(8, "MainOffice", false, 31, 8);
            var mismatched = new Location(127, "MotherPlant", true, 14, 1);   // Polyhouse 14 is not in Area 127
            Assert.Contains("destination", ValidateTransfer(Main(), outlet, "24 Cavity", 1, 40, Today, Today).Error);
            Assert.Contains("source", ValidateTransfer(inactive, Mp(), "24 Cavity", 1, 40, Today, Today).Error);
            Assert.Contains("does not belong", ValidateTransfer(Main(), mismatched, "24 Cavity", 1, 40, Today, Today).Error);
        }

        [Fact]
        public void Close_OnlyAPendingTransfer_AndOnlyWithAReason()
        {
            Assert.True(ValidateClose(StatusPending, "Wrong cavity", "rejected").Ok);
            Assert.False(ValidateClose(StatusPending, " ", "rejected").Ok);
            foreach (var closed in new[] { StatusReceived, StatusRejected, StatusCancelled })
                Assert.Contains("already", ValidateClose(closed, "x", "cancelled").Error);
            Assert.Equal("In transit", StatusLabel(StatusPending));
        }

        // ---- location-aware receiving (2026-10-10) ------------------------------------------

        private static System.Security.Claims.ClaimsPrincipal With(params string[] permissions)
            => new(new System.Security.Claims.ClaimsIdentity(permissions.Select(p =>
                new System.Security.Claims.Claim(PlantStockManager.Authorization.MinimumAuthorizationLevelHandler.PermissionClaimType, p)), "test"));

        private static PlantStockManager.Models.Area AreaOf(int id, string type, bool active = true) => new() { Id = id, Name = "A" + id, AreaType = type, IsActive = active };

        [Fact]
        public void Receiving_DependsOnTheDestinationAreaType_AndTheUsersOwnAreas()
        {
            Assert.Equal(ReceivePermission, ReceivePermissionFor("MotherPlant"));
            Assert.Equal(ReceiveMainAreaPermission, ReceivePermissionFor("MainOffice"));
            Assert.Null(ReceivePermissionFor(OutletRules.AreaType));
            Assert.Null(ReceivePermissionFor(null));

            var mp = AreaOf(127, "MotherPlant");
            var main = AreaOf(2, "MainOffice");
            var outlet = AreaOf(3, OutletRules.AreaType);
            bool Own(int id) => id is 127 or 2;   // assigned to both, for the sake of the test

            var mpSupervisor = With(ReceivePermission);
            Assert.True(PlantStockManager.Authorization.TrayAreaScope.IsReceivableBy(mpSupervisor, mp, Own, false));
            Assert.False(PlantStockManager.Authorization.TrayAreaScope.IsReceivableBy(mpSupervisor, main, Own, false));   // wrong location type
            Assert.False(PlantStockManager.Authorization.TrayAreaScope.IsReceivableBy(mpSupervisor, mp, _ => false, false)); // not its Area

            var sowingSupervisor = With(ReceiveMainAreaPermission);
            Assert.True(PlantStockManager.Authorization.TrayAreaScope.IsReceivableBy(sowingSupervisor, main, Own, false));
            Assert.False(PlantStockManager.Authorization.TrayAreaScope.IsReceivableBy(sowingSupervisor, mp, Own, false));

            Assert.False(PlantStockManager.Authorization.TrayAreaScope.IsReceivableBy(With(ReceivePermission, ReceiveMainAreaPermission), outlet, _ => true, false));
            Assert.False(PlantStockManager.Authorization.TrayAreaScope.IsReceivableBy(sowingSupervisor, AreaOf(2, "MainOffice", active: false), Own, false));
            Assert.True(PlantStockManager.Authorization.TrayAreaScope.IsReceivableBy(With(), main, _ => false, fullAreaAccess: true));     // admin
            Assert.False(PlantStockManager.Authorization.TrayAreaScope.IsReceivableBy(With(), outlet, _ => true, fullAreaAccess: true));   // never an Outlet
        }

        // ---- tray history by business date (2026-10-10) -------------------------------------

        [Fact]
        public void TrayHistory_FlagsEveryStretchBelowZero_InBusinessDateOrder_NeverOtherwise()
        {
            DateTime At(int day, int hour = 12) => AppClock.IndiaToUtc(new DateTime(2026, 10, day, hour, 0, 0));
            var ok = new[]
            {
                new TrayHistoryRules.Movement(1, "P-1 / 9", At(1), 1, 10),
                new TrayHistoryRules.Movement(1, "P-1 / 9", At(2), 2, -10),
                new TrayHistoryRules.Movement(1, "P-1 / 9", At(3), 3, 5),
            };
            Assert.Empty(TrayHistoryRules.NegativeStretches(ok));

            // a transplant dated the 9th (entered later, Id 5) of trays received on the 10th (Id 4)
            var bad = new[]
            {
                new TrayHistoryRules.Movement(2, "P-1 / 42", At(10), 4, 30),
                new TrayHistoryRules.Movement(2, "P-1 / 42", At(9), 5, -5),
                new TrayHistoryRules.Movement(3, "P-2 / 42", At(9), 6, -2),    // never recovers
            };
            var stretches = TrayHistoryRules.NegativeStretches(bad).OrderBy(s => s.PoolId).ToList();
            Assert.Equal(2, stretches.Count);
            Assert.Equal((2, new DateTime(2026, 10, 9), (DateTime?)new DateTime(2026, 10, 10), -5m), (stretches[0].PoolId, stretches[0].FromIndia, stretches[0].UntilIndia, stretches[0].Lowest));
            Assert.Equal((3, (DateTime?)null, -2m), (stretches[1].PoolId, stretches[1].UntilIndia, stretches[1].Lowest));
        }

        [Fact]
        public void Transplant_TrayMessage_NamesTheDate_WhenBackDated()
        {
            Assert.True(PlantStockManager.Services.TransplantRules.ValidateTrays(3, 3, "9 Cavity", new DateTime(2026, 10, 5)).Ok);
            var (ok, error) = PlantStockManager.Services.TransplantRules.ValidateTrays(3, 2, "9 Cavity", new DateTime(2026, 10, 5));
            Assert.False(ok);
            Assert.Contains("2 available from that date onward", error);
            Assert.Contains("Add Tray Stock first", PlantStockManager.Services.TransplantRules.ValidateTrays(3, 2, "9 Cavity").Error);
        }

        // ---- Material Stock history categories ---------------------------------------------

        [Theory]
        [InlineData("Allocation", MaterialStockRules.Receive)]
        [InlineData("Sowing", MaterialStockRules.Consume)]
        [InlineData("Transplant", MaterialStockRules.Consume)]          // was shown as an Adjustment before 2026-10-10
        [InlineData("ReversalReturn", MaterialStockRules.Reversal)]
        [InlineData("Adjustment", MaterialStockRules.Adjustment)]
        [InlineData("TransferOut", MaterialStockRules.Transfer)]
        [InlineData("TransferIn", MaterialStockRules.Transfer)]
        [InlineData("TransferReturn", MaterialStockRules.Transfer)]
        public void TrayHistoryCategory(string trayType, string category)
            => Assert.Equal(category, MaterialStockRules.TrayCategory(trayType));
    }
}
