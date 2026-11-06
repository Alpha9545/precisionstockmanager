using System.Security.Claims;
using PlantStockManager.Authorization;
using PlantStockManager.Services.FieldWork;
using static PlantStockManager.Services.FieldWork.FieldDayRules;

namespace PlantStockManager.Tests
{
    // Field Work Phase 1 (2026-11-03): the pure Start Day / End Day / review / gate rules (no database).
    public class FieldDayRulesTests
    {
        private static ReadingCheck Check(decimal? km = 1000m, decimal? lat = 12.97m, decimal? lng = 77.59m, decimal? acc = 15m,
            bool photo = true, string? reason = null)
            => CheckReading(new ReadingInput(km, lat, lng, acc, photo, reason), 100m, "Start");

        [Fact]
        public void CompleteReading_IsAccepted_WithoutFlags()
        {
            var c = Check();
            Assert.True(c.Ok);
            Assert.Empty(c.Flags);
            Assert.Equal(new Position(12.97m, 77.59m, 15m), c.Position);
            Assert.Equal(1000m, c.Km);
        }

        [Theory]
        [InlineData(null)]
        [InlineData(-1.0)]
        [InlineData(10000000.0)]
        [InlineData(12.34)]
        public void InvalidKm_IsRefused(double? km)
            => Assert.False(Check(km: km.HasValue ? (decimal)km.Value : null).Ok);

        [Fact]
        public void NoGps_NeedsReason_ThenFlags()
        {
            Assert.False(Check(lat: null, lng: null, acc: null).Ok);
            var c = Check(lat: null, lng: null, acc: null, reason: "No signal in basement");
            Assert.True(c.Ok);
            Assert.Null(c.Position);
            Assert.Contains(c.Flags, f => f.Contains("no GPS") && f.Contains("No signal in basement"));
        }

        [Fact]
        public void PoorAccuracy_IsRefusedWithoutReason_FlaggedWithReason()
        {
            var refused = Check(acc: 250m);
            Assert.False(refused.Ok);
            Assert.Contains(refused.Errors, e => e.Contains("too poor"));
            var flagged = Check(acc: 250m, reason: "Cloudy, phone GPS weak");
            Assert.True(flagged.Ok);
            Assert.Contains(flagged.Flags, f => f.Contains("poor GPS accuracy 250 m"));
            Assert.True(Check(acc: 100m).Ok);   // exactly the limit is accepted
        }

        [Fact]
        public void IncompleteOrOutOfRangePosition_IsRefused()
        {
            Assert.False(Check(acc: null).Ok);
            Assert.False(Check(lat: 91m).Ok);
            Assert.False(Check(lng: -181m).Ok);
            Assert.False(Check(acc: 0m).Ok);
        }

        [Fact]
        public void NoPhoto_NeedsReason_ThenFlags()
        {
            Assert.False(Check(photo: false).Ok);
            var c = Check(photo: false, reason: "Camera broken");
            Assert.True(c.Ok);
            Assert.Contains(c.Flags, f => f.Contains("no meter photo"));
        }

        [Fact]
        public void Reason_IsTrimmed_AndLimited()
        {
            Assert.Null(Check(photo: false, reason: "   ").Reason);
            Assert.False(Check(photo: false, reason: "   ").Ok);
            Assert.False(Check(photo: false, reason: new string('x', MaxReasonLength + 1)).Ok);
        }

        [Fact]
        public void VehicleType_MustBeKnown()
        {
            Assert.Null(ValidateVehicleType("OwnBike"));
            Assert.Null(ValidateVehicleType("CompanyVehicle"));
            Assert.NotNull(ValidateVehicleType("Tractor"));
            Assert.NotNull(ValidateVehicleType(null));
        }

        [Fact]
        public void EndKm_MustNotBeBelowStartKm()
        {
            Assert.Null(ValidateEndKm(1000m, 1000m));
            Assert.Null(ValidateEndKm(1000m, 1050.5m));
            Assert.NotNull(ValidateEndKm(1000m, 999.9m));
        }

        [Fact]
        public void Haversine_OneDegreeOfLatitude_IsAbout111Km()
        {
            Assert.InRange(HaversineKm(0, 0, 1, 0), 111.1, 111.3);
            Assert.Equal(0, HaversineKm(12.97, 77.59, 12.97, 77.59), 6);
            // Bengaluru (MG Road) -> Mysuru (Palace): about 128 km straight line
            Assert.InRange(HaversineKm(12.9758, 77.6064, 12.3052, 76.6552), 120, 135);
        }

        [Fact]
        public void GpsDistance_NeedsBothPositions()
        {
            Assert.Null(GpsDistanceKm(null, new Position(1, 1, 5)));
            Assert.Equal(0m, GpsDistanceKm(new Position(1, 1, 5), new Position(1, 1, 5)));
        }

        [Fact]
        public void DistanceMismatch_FlagsOnlyWhenGpsIsLongerThanOdometerPlusSlack()
        {
            Assert.NotNull(DistanceMismatchFlag(10m, 5m, 2m));
            Assert.Null(DistanceMismatchFlag(7m, 5m, 2m));            // within slack
            Assert.Null(DistanceMismatchFlag(0.2m, 40m, 2m));         // round trip: short straight line is normal
            Assert.Null(DistanceMismatchFlag(null, 0m, 2m));          // no GPS: nothing to compare
        }

        [Fact]
        public void JoinFlags_SkipsEmpty_AndLimitsLength()
        {
            Assert.Null(JoinFlags(new string?[] { null, " " }));
            Assert.Equal("a; b", JoinFlags(new[] { "a", null, "b" }));
            Assert.Equal(MaxFlagLength, JoinFlags(new[] { new string('x', 600) })!.Length);
        }

        // ---- review -------------------------------------------------------------------------------------------------

        private static readonly DateTime Today = new(2026, 11, 3);

        [Fact]
        public void Review_OwnDay_IsRefused_ExceptForAdministrator()
        {
            var day = new DayFacts(7, Today, StatusClosed);
            Assert.NotNull(CheckReview(day, ReviewAction.Approve, 7, false, null, Today));
            Assert.Null(CheckReview(day, ReviewAction.Approve, 7, true, null, Today));
            Assert.Null(CheckReview(day, ReviewAction.Approve, 8, false, null, Today));
        }

        [Fact]
        public void Approve_OpenDay_IsRefused_FlagNeedsComment()
        {
            Assert.NotNull(CheckReview(new DayFacts(7, Today, StatusOpen), ReviewAction.Approve, 8, false, null, Today));
            Assert.NotNull(CheckReview(new DayFacts(7, Today, StatusClosed), ReviewAction.Flag, 8, false, "  ", Today));
            Assert.Null(CheckReview(new DayFacts(7, Today, StatusOpen), ReviewAction.Flag, 8, false, "Photo blurred", Today));
            Assert.NotNull(CheckReview(new DayFacts(7, Today, StatusClosed), ReviewAction.Flag, 8, false, new string('x', 301), Today));
        }

        [Fact]
        public void RepeatedReview_IsRefused_ButAFlaggedDayMayBeApproved_AndAnAutoFlagFlaggedAgain()
        {
            // a second Approve (double submit / concurrent manager) is refused
            Assert.NotNull(CheckReview(new DayFacts(7, Today, StatusClosed, ReviewApproved, 9), ReviewAction.Approve, 8, false, null, Today));
            Assert.Null(CheckReview(new DayFacts(7, Today, StatusClosed, ReviewFlagged, 9, "Blurred"), ReviewAction.Approve, 8, false, null, Today));
            // the same manager flag with the same reason is a duplicate; a new reason or an automatic flag is not
            Assert.NotNull(CheckReview(new DayFacts(7, Today, StatusClosed, ReviewFlagged, 9, "Blurred"), ReviewAction.Flag, 8, false, " Blurred ", Today));
            Assert.Null(CheckReview(new DayFacts(7, Today, StatusClosed, ReviewFlagged, 9, "Blurred"), ReviewAction.Flag, 8, false, "Wrong km", Today));
            Assert.Null(CheckReview(new DayFacts(7, Today, StatusClosed, ReviewFlagged, null, null), ReviewAction.Flag, 8, false, "Wrong km", Today));
            Assert.Null(CheckReview(new DayFacts(7, Today, StatusClosed, ReviewApproved, 9, "Blurred"), ReviewAction.Flag, 8, false, "Blurred", Today));
            // an approved day of today may still be reopened
            Assert.Null(CheckReview(new DayFacts(7, Today, StatusClosed, ReviewApproved, 9), ReviewAction.Reopen, 8, false, "x", Today));
        }

        [Fact]
        public void Reopen_OnlyTodaysClosedDay_WithReason()
        {
            Assert.Null(CheckReview(new DayFacts(7, Today, StatusClosed), ReviewAction.Reopen, 8, false, "Ended by mistake", Today));
            Assert.NotNull(CheckReview(new DayFacts(7, Today, StatusClosed), ReviewAction.Reopen, 8, false, null, Today));
            Assert.NotNull(CheckReview(new DayFacts(7, Today, StatusOpen), ReviewAction.Reopen, 8, false, "x", Today));
            Assert.NotNull(CheckReview(new DayFacts(7, Today.AddDays(-1), StatusAutoClosed), ReviewAction.Reopen, 8, false, "x", Today));
        }

        [Fact]
        public void MaySeeDay_OwnerOrManagerOnly()
        {
            Assert.True(MaySeeDay(7, 7, false));
            Assert.False(MaySeeDay(7, 8, false));
            Assert.False(MaySeeDay(7, null, false));
            Assert.True(MaySeeDay(7, 8, true));
        }

        // ---- gate ---------------------------------------------------------------------------------------------------

        [Fact]
        public void Gate_LetsNonFieldUsersAndFreePagesThrough()
        {
            Assert.Equal(GateResult.Allow, Gate(false, "/Production/ReadyStock/Index", false, null, true));
            foreach (var page in new[] { "/FieldWork/StartDay", "/FieldWork/Consent", "/Account/Logout" })
                Assert.Equal(GateResult.Allow, Gate(true, page, false, null, false));
        }

        [Fact]
        public void Gate_ForcesConsentThenStartDay_UntilTodayIsOpen()
        {
            Assert.Equal(GateResult.Consent, Gate(true, "/FieldWork/Index", false, null, false));
            Assert.Equal(GateResult.StartDay, Gate(true, "/FieldWork/Index", true, null, false));
            Assert.Equal(GateResult.StartDay, Gate(true, "/Bookings/Book", true, StatusClosed, true));
            Assert.Equal(GateResult.StartDay, Gate(true, "/FieldWork/EndDay", true, StatusAutoClosed, true));
            Assert.Equal(GateResult.Allow, Gate(true, "/Bookings/Book", true, StatusOpen, true));
            Assert.Equal(GateResult.FieldHome, Gate(true, "/Index", true, StatusOpen, false));
            Assert.Equal(GateResult.Allow, Gate(true, "/Index", true, StatusOpen, true));
        }

        [Fact]
        public void IsFieldEmployee_NeedsTheRealPermissionClaim_NotFullAccess()
        {
            ClaimsPrincipal P(params Claim[] claims) => new(new ClaimsIdentity(claims, "Test"));
            var perm = new Claim(MinimumAuthorizationLevelHandler.PermissionClaimType, DayPermission);
            var full = new Claim(ClaimsPrincipalSecurityExtensions.FullAccessClaimType, "true");
            Assert.True(FieldDayService.IsFieldEmployee(P(perm)));
            Assert.False(FieldDayService.IsFieldEmployee(P(full)));
            Assert.False(FieldDayService.IsFieldEmployee(P(full, perm)));
            Assert.False(FieldDayService.IsFieldEmployee(P(new Claim(MinimumAuthorizationLevelHandler.PermissionClaimType, ManagePermission))));
            Assert.False(FieldDayService.IsFieldEmployee(new ClaimsPrincipal(new ClaimsIdentity())));   // anonymous
            Assert.True(FieldDayService.MayManage(P(full)));
        }

        // ---- manager filter -----------------------------------------------------------------------------------------

        [Fact]
        public void NormalizeFilter_DropsUnknownValues_AndCapsTheRange()
        {
            var known = new[] { 5, 6 };
            var f = FieldDayService.NormalizeFilter(null, null, 99, "Hacked", "x' OR 1=1", known, Today);
            Assert.Equal(Today, f.From);
            Assert.Equal(Today, f.To);
            Assert.Null(f.UserId);
            Assert.Null(f.Status);
            Assert.Null(f.ReviewStatus);

            var g = FieldDayService.NormalizeFilter(Today, Today.AddDays(-400), 5, StatusAutoClosed, ReviewFlagged, known, Today);
            Assert.Equal(Today, g.To);                                       // swapped
            Assert.Equal(Today.AddDays(-FieldDayService.MaxRangeDays), g.From); // capped
            Assert.Equal(5, g.UserId);
            Assert.Equal(StatusAutoClosed, g.Status);
            Assert.Equal(ReviewFlagged, g.ReviewStatus);
        }

        // ---- photo store --------------------------------------------------------------------------------------------

        private static FieldPhotoStore Store() => new(new FieldWorkOptions { PhotoRoot = Path.Combine(Path.GetTempPath(), "psm-fw-tests"), MaxPhotoBytes = 1024 });

        private static byte[] Jpeg(int size)
        {
            var b = new byte[size];
            b[0] = 0xFF; b[1] = 0xD8; b[2] = 0xFF; b[3] = 0xE0;
            b[^2] = 0xFF; b[^1] = 0xD9;
            return b;
        }

        [Fact]
        public void PhotoStore_AcceptsOnlyCompleteJpegWithinTheLimit()
        {
            var store = Store();
            Assert.Null(store.Validate(Jpeg(500)));
            Assert.NotNull(store.Validate(Jpeg(2000)));          // too large
            Assert.NotNull(store.Validate(null));
            Assert.NotNull(store.Validate(Array.Empty<byte>()));
            var truncated = Jpeg(500); truncated[^1] = 0;
            Assert.NotNull(store.Validate(truncated));
            var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0xFF, 0xD9 };
            Assert.NotNull(store.Validate(png));
            var html = System.Text.Encoding.ASCII.GetBytes("<html><script>alert(1)</script>\xFF\xD9");
            Assert.NotNull(store.Validate(html));
        }

        [Fact]
        public void PhotoStore_PathFor_RejectsAnythingButItsOwnNames()
        {
            var store = Store();
            Assert.NotNull(store.PathFor("0123456789abcdef0123456789abcdef.jpg"));
            Assert.Null(store.PathFor("../appsettings.json"));
            Assert.Null(store.PathFor("..\\..\\x.jpg"));
            Assert.Null(store.PathFor("0123456789abcdef0123456789abcdef.png"));
            Assert.Null(store.PathFor(null));
        }

        [Fact]
        public async Task PhotoStore_Save_WritesANewGuidFile()
        {
            var store = Store();
            var name = await store.SaveAsync(Jpeg(300));
            var path = store.PathFor(name);
            Assert.NotNull(path);
            Assert.Equal(300, new FileInfo(path!).Length);
            store.DeleteQuietly(name);
            Assert.False(File.Exists(path));
        }
    }
}
