namespace PlantStockManager.Services.FieldWork
{
    // Field Work Phase 3 (2026-11-03): the pure day-route rules (no database). A route is the day's start point, each
    // visit check-in in server-time order, and the end point. Points without GPS are skipped and listed. Distances are
    // straight-line (haversine) legs. Nothing here is stored except the End Day flag (FieldDayService).
    public static class FieldRouteRules
    {
        public enum PointKind { Start, Visit, End }

        public sealed record PointInput(PointKind Kind, DateTime AtUtc, decimal? Latitude, decimal? Longitude, decimal? AccuracyM, string? Reason,
            int? VisitId = null, int? CustomerId = null, string? CustomerName = null, string? Discussed = null);

        public sealed record RoutePoint(int Number, PointKind Kind, DateTime AtUtc, decimal Latitude, decimal Longitude, decimal AccuracyM,
            bool LowAccuracy, int? VisitId, int? CustomerId, string? CustomerName, string? Summary);

        public sealed record SkippedPoint(PointKind Kind, DateTime AtUtc, string? CustomerName, string? Reason);

        public sealed record Leg(int From, int To, decimal Km, decimal? Minutes, decimal? SpeedKmh, bool Suspicious, string? Warning);

        public sealed record Settings(decimal MaxGpsAccuracyMeters, decimal SlackKm, decimal SuspiciousSpeedKmh, decimal SuspiciousMinKm,
            decimal OdometerInfoRatio, decimal OdometerInfoExtraKm)
        {
            public static Settings From(FieldWorkOptions o) => new(o.MaxGpsAccuracyMeters, o.DistanceMismatchSlackKm, o.SuspiciousLegSpeedKmh,
                o.SuspiciousLegMinKm, o.OdometerRouteInfoRatio, o.OdometerRouteInfoExtraKm);
        }

        public sealed record Route(IReadOnlyList<RoutePoint> Points, IReadOnlyList<SkippedPoint> Skipped, IReadOnlyList<Leg> Legs, decimal RouteKm,
            decimal? OdometerKm, decimal? DifferenceKm, bool HasEndPoint, IReadOnlyList<string> Flags, IReadOnlyList<string> Warnings,
            string? OdometerInfo, bool OdometerInfoNotable)
        {
            public int VisitCount { get; init; }
        }

        public const int SummaryLength = 120;

        public static string? Summarize(string? text)
        {
            var t = (text ?? "").Trim();
            if (t.Length == 0) return null;
            return t.Length <= SummaryLength ? t : t[..(SummaryLength - 1)] + "…";
        }

        private static int KindOrder(PointKind k) => k == PointKind.Start ? 0 : k == PointKind.End ? 2 : 1;

        // Start first, visits by check-in time (then id), end last.
        public static IReadOnlyList<PointInput> Order(IEnumerable<PointInput> inputs)
            => inputs.OrderBy(p => KindOrder(p.Kind)).ThenBy(p => p.AtUtc).ThenBy(p => p.VisitId ?? 0).ToList();

        public static bool HasPosition(PointInput p) => p.Latitude.HasValue && p.Longitude.HasValue && p.AccuracyM.HasValue;

        public static decimal LegKm(RoutePoint a, RoutePoint b)
            => decimal.Round((decimal)FieldDayRules.HaversineKm((double)a.Latitude, (double)a.Longitude, (double)b.Latitude, (double)b.Longitude), 3);

        // A leg is suspicious when -- after subtracting both GPS accuracies -- it is at least minKm long and was covered
        // faster than maxSpeed (or in no time at all). Low-accuracy points are never judged (their position is unreliable).
        public static Leg CheckLeg(RoutePoint a, RoutePoint b, Settings s)
        {
            var km = LegKm(a, b);
            var minutes = (decimal)(b.AtUtc - a.AtUtc).TotalMinutes;
            decimal? speed = minutes >= 1 ? decimal.Round(km / (minutes / 60m), 1) : null;   // no speed for points less than a minute apart
            if (a.LowAccuracy || b.LowAccuracy)
                return new Leg(a.Number, b.Number, km, decimal.Round(minutes, 1), speed, false, null);
            var effective = km - (a.AccuracyM + b.AccuracyM) / 1000m;
            if (effective < s.SuspiciousMinKm)
                return new Leg(a.Number, b.Number, km, decimal.Round(minutes, 1), speed, false, null);
            var effectiveSpeed = minutes > 0 ? effective / (minutes / 60m) : decimal.MaxValue;
            if (effectiveSpeed <= s.SuspiciousSpeedKmh)
                return new Leg(a.Number, b.Number, km, decimal.Round(minutes, 1), speed, false, null);
            var warning = minutes >= 1
                ? $"Leg {a.Number} → {b.Number}: {km:0.0} km in {minutes:0} min ({effectiveSpeed:0} km/h, more than {s.SuspiciousSpeedKmh:0} km/h)"
                : $"Leg {a.Number} → {b.Number}: {km:0.0} km in under a minute";
            return new Leg(a.Number, b.Number, km, decimal.Round(minutes, 1), speed, true, warning);
        }

        // Route longer than the odometer is physically impossible (a straight line is never longer than the road).
        public static string? RouteMismatchFlag(decimal routeKm, decimal odometerKm, decimal slackKm)
            => routeKm > odometerKm + slackKm
                ? $"GPS distance along the route {routeKm:0.0} km is more than the odometer distance {odometerKm:0.0} km (+{slackKm:0.#} km allowed)"
                : null;

        // Odometer much longer than the straight-line route: INFO only (roads wind). Notable when both thresholds are met.
        public static (string? Text, bool Notable) OdometerInfo(decimal routeKm, decimal? odometerKm, decimal ratio, decimal extraKm)
        {
            if (odometerKm is not { } odo)
                return (null, false);
            var notable = odo - routeKm >= extraKm && (routeKm <= 0 || odo >= routeKm * ratio);
            if (routeKm <= 0)
                return odo > 0 ? ($"Odometer {odo:0.0} km, but the GPS points are all at one place.", notable) : (null, false);
            // an odometer SHORTER than the route is not info: the route rule flags it (when beyond the slack)
            if (odo < routeKm)
                return (null, false);
            return ($"Odometer {odo:0.0} km is {odo / routeKm:0.0}× the straight-line route ({routeKm:0.0} km).", notable);
        }

        public static decimal RouteKm(IEnumerable<PointInput> inputs)
        {
            var pts = Order(inputs).Where(HasPosition).ToList();
            var km = 0m;
            for (var i = 1; i < pts.Count; i++)
                km += (decimal)FieldDayRules.HaversineKm((double)pts[i - 1].Latitude!.Value, (double)pts[i - 1].Longitude!.Value,
                    (double)pts[i].Latitude!.Value, (double)pts[i].Longitude!.Value);
            return decimal.Round(km, 2);
        }

        // dayStatus: Open / Closed / AutoClosed (FieldDayRules).
        public static Route Build(IEnumerable<PointInput> inputs, decimal? odometerKm, string dayStatus, Settings s)
        {
            var ordered = Order(inputs);
            var points = new List<RoutePoint>();
            var skipped = new List<SkippedPoint>();
            foreach (var p in ordered)
            {
                if (!HasPosition(p))
                {
                    skipped.Add(new SkippedPoint(p.Kind, p.AtUtc, p.CustomerName, p.Reason));
                    continue;
                }
                points.Add(new RoutePoint(points.Count + 1, p.Kind, p.AtUtc, p.Latitude!.Value, p.Longitude!.Value, p.AccuracyM!.Value,
                    p.AccuracyM!.Value > s.MaxGpsAccuracyMeters, p.VisitId, p.CustomerId, p.CustomerName, Summarize(p.Discussed)));
            }
            var legs = new List<Leg>();
            for (var i = 1; i < points.Count; i++)
                legs.Add(CheckLeg(points[i - 1], points[i], s));
            var routeKm = decimal.Round(legs.Sum(l => l.Km), 2);

            var flags = new List<string>();
            var hasStart = points.Any(p => p.Kind == PointKind.Start);
            var hasEnd = points.Any(p => p.Kind == PointKind.End);
            if (hasStart && hasEnd && odometerKm.HasValue && RouteMismatchFlag(routeKm, odometerKm.Value, s.SlackKm) is { } mismatch)
                flags.Add(mismatch);
            if (dayStatus == FieldDayRules.StatusAutoClosed)
                flags.Add("No end point: the day was closed automatically (End Day not done).");
            else if (dayStatus == FieldDayRules.StatusClosed && !hasEnd)
                flags.Add("No end point: End Day was saved without GPS.");
            if (skipped.Count > 0)
                flags.Add($"{skipped.Count} point(s) without GPS were skipped.");

            var warnings = legs.Where(l => l.Suspicious).Select(l => l.Warning!).ToList();
            if (dayStatus == FieldDayRules.StatusOpen)
                warnings.Insert(0, "The day is still open: the route ends at the last check-in.");
            var (info, notable) = OdometerInfo(routeKm, odometerKm, s.OdometerInfoRatio, s.OdometerInfoExtraKm);
            return new Route(points, skipped, legs, routeKm, odometerKm, odometerKm.HasValue ? decimal.Round(odometerKm.Value - routeKm, 2) : null,
                hasEnd, flags, warnings, info, notable)
            {
                VisitCount = ordered.Count(p => p.Kind == PointKind.Visit),
            };
        }
    }
}
