using PlantStockManager.Services.FieldWork;
using static PlantStockManager.Services.FieldWork.FieldRouteRules;

namespace PlantStockManager.Tests
{
    // Field Work Phase 3 (2026-11-03): the pure day-route rules (no database).
    public class FieldRouteRulesTests
    {
        private static readonly Settings S = new(MaxGpsAccuracyMeters: 100, SlackKm: 2, SuspiciousSpeedKmh: 80, SuspiciousMinKm: 2,
            OdometerInfoRatio: 3, OdometerInfoExtraKm: 20);
        private static readonly DateTime T0 = new(2026, 11, 3, 4, 0, 0, DateTimeKind.Utc);

        // ~0.009 degrees of latitude = ~1 km
        private static PointInput Start(double km = 0, int minutes = 0, decimal acc = 10)
            => new(PointKind.Start, T0.AddMinutes(minutes), 12.9716m + (decimal)(km * 0.008993), 77.5946m, acc, null);
        private static PointInput End(double km = 0, int minutes = 480, decimal acc = 10)
            => new(PointKind.End, T0.AddMinutes(minutes), 12.9716m + (decimal)(km * 0.008993), 77.5946m, acc, null);
        private static PointInput Visit(int id, double km, int minutes, decimal acc = 10, string? name = null, string? discussed = null)
            => new(PointKind.Visit, T0.AddMinutes(minutes), 12.9716m + (decimal)(km * 0.008993), 77.5946m, acc, null, id, 100 + id, name ?? $"Customer {id}", discussed);
        private static PointInput NoGps(PointKind kind, int minutes, string reason, int? id = null)
            => new(kind, T0.AddMinutes(minutes), null, null, null, reason, id, id.HasValue ? 100 + id : null, id.HasValue ? $"Customer {id}" : null);

        [Fact]
        public void Points_AreStartThenVisitsByTimeThenEnd_AndNumbered()
        {
            var r = Build(new[] { End(), Visit(2, 5, 120), Visit(1, 3, 60), Start() }, 20m, FieldDayRules.StatusClosed, S);
            Assert.Equal(new[] { PointKind.Start, PointKind.Visit, PointKind.Visit, PointKind.End }, r.Points.Select(p => p.Kind));
            Assert.Equal(new int?[] { null, 1, 2, null }, r.Points.Select(p => p.VisitId));
            Assert.Equal(new[] { 1, 2, 3, 4 }, r.Points.Select(p => p.Number));
            Assert.Equal(2, r.VisitCount);
            Assert.True(r.HasEndPoint);
        }

        [Fact]
        public void Distance_IsTheSumOfStraightLegs_WithOdometerDifference()
        {
            // 0 -> 3 -> 5 -> 0 km along one meridian = 3 + 2 + 5 = 10 km
            var r = Build(new[] { Start(), Visit(1, 3, 60), Visit(2, 5, 120), End() }, 25m, FieldDayRules.StatusClosed, S);
            Assert.Equal(3, r.Legs.Count);
            Assert.InRange(r.RouteKm, 9.9m, 10.1m);
            Assert.Equal(25m, r.OdometerKm);
            Assert.InRange(r.DifferenceKm!.Value, 14.9m, 15.1m);
            Assert.Empty(r.Flags);
            Assert.Equal(r.RouteKm, RouteKm(new[] { Start(), Visit(1, 3, 60), Visit(2, 5, 120), End() }));
        }

        [Fact]
        public void PointsWithoutGps_AreSkippedAndListed_LegsJoinTheNeighbours()
        {
            var r = Build(new[] { Start(), Visit(1, 3, 60), NoGps(PointKind.Visit, 90, "No signal", 2), Visit(3, 5, 120), End() }, 30m, FieldDayRules.StatusClosed, S);
            Assert.Equal(4, r.Points.Count);
            var skipped = Assert.Single(r.Skipped);
            Assert.Equal("Customer 2", skipped.CustomerName);
            Assert.Equal("No signal", skipped.Reason);
            Assert.Equal(3, r.Legs.Count);
            Assert.Contains(r.Legs, l => l.From == 2 && l.To == 3);   // visit 1 -> visit 3
            Assert.Contains(r.Flags, f => f.Contains("1 point(s) without GPS"));
            Assert.Equal(3, r.VisitCount);
        }

        [Fact]
        public void RouteLongerThanOdometerPlusSlack_IsFlagged_RoundTripUnderOdometerIsNot()
        {
            // round trip 0 -> 10 -> 0 = 20 km route; odometer 15 -> impossible -> flagged
            var flagged = Build(new[] { Start(), Visit(1, 10, 60), End() }, 15m, FieldDayRules.StatusClosed, S);
            Assert.Contains(flagged.Flags, f => f.Contains("GPS distance along the route 20.0 km") && f.Contains("odometer distance 15.0 km"));
            // within the 2 km slack: not flagged
            Assert.Empty(Build(new[] { Start(), Visit(1, 10, 60), End() }, 18.5m, FieldDayRules.StatusClosed, S).Flags);
            // a real round trip with a longer odometer: not flagged (Phase 1's start -> end line would be ~0 km)
            Assert.Empty(Build(new[] { Start(), Visit(1, 10, 60), End() }, 26m, FieldDayRules.StatusClosed, S).Flags);
            Assert.NotNull(RouteMismatchFlag(20m, 15m, 2m));
            Assert.Null(RouteMismatchFlag(17m, 15m, 2m));
        }

        [Fact]
        public void MissingEndPoint_OpenAutoClosedAndClosedWithoutGps()
        {
            var open = Build(new[] { Start(), Visit(1, 3, 60) }, null, FieldDayRules.StatusOpen, S);
            Assert.False(open.HasEndPoint);
            Assert.Empty(open.Flags);
            Assert.Contains(open.Warnings, w => w.Contains("still open"));
            Assert.Null(open.OdometerKm);

            var auto = Build(new[] { Start(), Visit(1, 3, 60) }, null, FieldDayRules.StatusAutoClosed, S);
            Assert.Contains(auto.Flags, f => f.Contains("closed automatically"));

            var noEndGps = Build(new[] { Start(), Visit(1, 3, 60), NoGps(PointKind.End, 480, "Phone off") }, 10m, FieldDayRules.StatusClosed, S);
            Assert.Contains(noEndGps.Flags, f => f.Contains("End Day was saved without GPS"));
            Assert.Contains(noEndGps.Skipped, s => s.Kind == PointKind.End && s.Reason == "Phone off");
            Assert.DoesNotContain(noEndGps.Flags, f => f.Contains("GPS distance along the route"));   // nothing to compare without an end point
        }

        [Fact]
        public void SuspiciousLeg_FastJump_IsWarned_SlowOrShortIsNot()
        {
            // 30 km in 10 minutes = 180 km/h
            var fast = Build(new[] { Start(), Visit(1, 30, 10), End(30, 600) }, 100m, FieldDayRules.StatusClosed, S);
            var leg = fast.Legs.First();
            Assert.True(leg.Suspicious);
            Assert.Contains("Leg 1 → 2", leg.Warning);
            Assert.Contains(fast.Warnings, w => w.Contains("Leg 1 → 2"));
            // 30 km in 60 minutes = 30 km/h: fine
            Assert.False(Build(new[] { Start(), Visit(1, 30, 60) }, null, FieldDayRules.StatusOpen, S).Legs.Single().Suspicious);
            // 1.5 km in 0 minutes: below the 2 km minimum, ignored
            Assert.False(Build(new[] { Start(), Visit(1, 1.5, 0) }, null, FieldDayRules.StatusOpen, S).Legs.Single().Suspicious);
            // 5 km at the same instant: suspicious, worded without a meaningless speed
            var instant = Build(new[] { Start(), Visit(1, 5, 0) }, null, FieldDayRules.StatusOpen, S).Legs.Single();
            Assert.True(instant.Suspicious);
            Assert.EndsWith("in under a minute", instant.Warning);
        }

        [Fact]
        public void SuspiciousLeg_SubtractsGpsAccuracy_AndIgnoresLowAccuracyPoints()
        {
            // 2.5 km apart in 1 minute, but each point is +/- 400 m: effective 1.7 km < 2 km -> not suspicious
            Assert.False(Build(new[] { Start(acc: 400), Visit(1, 2.5, 1, acc: 400) }, null, FieldDayRules.StatusOpen,
                S with { MaxGpsAccuracyMeters = 1000 }).Legs.Single().Suspicious);
            // a low-accuracy point (worse than the GPS limit) is never judged, but still counts in the distance and is marked
            var r = Build(new[] { Start(), Visit(1, 20, 1, acc: 500) }, null, FieldDayRules.StatusOpen, S);
            Assert.False(r.Legs.Single().Suspicious);
            Assert.True(r.Points[1].LowAccuracy);
            Assert.InRange(r.RouteKm, 19.9m, 20.1m);
        }

        [Fact]
        public void OdometerFarAboveRoute_IsInfoOnly_NotableAboveBothThresholds()
        {
            var r = Build(new[] { Start(), Visit(1, 5, 60), End() }, 60m, FieldDayRules.StatusClosed, S);   // route 10, odo 60
            Assert.Empty(r.Flags);
            Assert.True(r.OdometerInfoNotable);
            Assert.Contains("6.0× the straight-line route", r.OdometerInfo);

            var (text, notable) = OdometerInfo(10m, 25m, 3m, 20m);     // 2.5x: below the ratio
            Assert.NotNull(text);
            Assert.False(notable);
            Assert.False(OdometerInfo(5m, 16m, 3m, 20m).Notable);       // 3.2x but only +11 km
            Assert.True(OdometerInfo(0m, 30m, 3m, 20m).Notable);        // all points at one place, 30 km driven
            Assert.Equal((null, false), OdometerInfo(10m, null, 3m, 20m));
            Assert.Equal((null, false), OdometerInfo(24m, 5m, 3m, 20m));   // odometer shorter than the route: the flag covers it, no info
        }

        [Fact]
        public void Popup_Summary_IsTruncated()
        {
            Assert.Null(Summarize("  "));
            Assert.Equal("Short", Summarize(" Short "));
            Assert.Equal(SummaryLength, Summarize(new string('x', 500))!.Length);
            var r = Build(new[] { Start(), Visit(1, 1, 30, discussed: new string('y', 300)) }, null, FieldDayRules.StatusOpen, S);
            Assert.EndsWith("…", r.Points[1].Summary);
        }

        [Fact]
        public void Settings_ComeFromConfiguration()
        {
            var s = Settings.From(new FieldWorkOptions { SuspiciousLegSpeedKmh = 60, SuspiciousLegMinKm = 1, OdometerRouteInfoRatio = 4, OdometerRouteInfoExtraKm = 10 });
            Assert.Equal(60, s.SuspiciousSpeedKmh);
            Assert.Equal(1, s.SuspiciousMinKm);
            Assert.Equal(4, s.OdometerInfoRatio);
            Assert.Equal(10, s.OdometerInfoExtraKm);
            Assert.Equal(2, s.SlackKm);
        }
    }
}
