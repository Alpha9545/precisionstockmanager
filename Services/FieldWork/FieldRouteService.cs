using System.Security.Claims;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Services.FieldWork
{
    // Field Work Phase 3 (2026-11-03): day routes and the team view, independent of Razor Pages. An employee sees only their
    // own days; a field manager / administrator sees everyone's. Anything else is "not found" (pages answer 404). Routes are
    // computed on request from the stored start / end / check-in points (FieldRouteRules); nothing new is stored.
    public class FieldRouteService
    {
        private readonly FieldDayRepository _days;
        private readonly FieldRouteRepository _routes;
        private readonly FieldWorkOptions _options;

        public FieldRouteService(FieldDayRepository days, FieldRouteRepository routes, FieldWorkOptions options)
        {
            _days = days;
            _routes = routes;
            _options = options;
        }

        public FieldWorkOptions Options => _options;

        public sealed record RouteView(FieldDay Day, FieldRouteRules.Route Route);

        public Task<bool> SchemaReadyAsync() => _days.SchemaReadyAsync();

        // The start / end points of a day (the end only once End Day was saved; an auto-closed day has none).
        public static IEnumerable<FieldRouteRules.PointInput> DayPoints(FieldDay d)
        {
            yield return new FieldRouteRules.PointInput(FieldRouteRules.PointKind.Start, d.StartedAtUtc, d.StartLatitude, d.StartLongitude,
                d.StartAccuracyM, d.StartExceptionReason);
            if (d.Status == FieldDayRules.StatusClosed && d.EndedAtUtc.HasValue)
                yield return new FieldRouteRules.PointInput(FieldRouteRules.PointKind.End, d.EndedAtUtc.Value, d.EndLatitude, d.EndLongitude,
                    d.EndAccuracyM, d.EndExceptionReason);
        }

        public FieldRouteRules.Route Build(FieldDay day, IEnumerable<FieldRouteRules.PointInput> visits)
            => FieldRouteRules.Build(DayPoints(day).Concat(visits), day.OdometerKm, day.Status, FieldRouteRules.Settings.From(_options));

        private async Task<RouteView> ViewAsync(FieldDay day)
            => new(day, Build(day, await _routes.VisitsReadyAsync() ? await _routes.GetDayVisitsAsync(day.Id) : new()));

        public async Task<RouteView?> GetRouteAsync(ClaimsPrincipal user, int dayId)
        {
            var day = await _days.GetDayAsync(dayId);
            if (day == null || !FieldDayRules.MaySeeDay(day.UserId, user.GetUserId(), FieldDayService.MayManage(user)))
                return null;
            return await ViewAsync(day);
        }

        // Allowed = false: the user may not look at that employee (the page answers 404). View = null: no day on that date.
        public async Task<(bool Allowed, RouteView? View)> GetRouteForDateAsync(ClaimsPrincipal user, int? employeeId, DateTime date)
        {
            var me = user.GetUserId();
            if (me == null)
                return (false, null);
            var target = employeeId ?? me.Value;
            if (target != me.Value && !FieldDayService.MayManage(user))
                return (false, null);
            var day = await _days.GetDayAsync(target, date.Date);
            return (true, day == null ? null : await ViewAsync(day));
        }

        public sealed record TeamRow(int UserId, string Name, bool IsActive, string Status, int? DayId, DateTime? StartedAtUtc, DateTime? EndedAtUtc,
            int Visits, decimal? RouteKm, decimal? OdometerKm, decimal? DifferenceKm, string? ReviewStatus, IReadOnlyList<string> Flags,
            int SuspiciousLegs, bool OdometerInfoNotable)
        {
            public bool NotStarted => DayId == null;
        }

        public const string NotStarted = "NotStarted";

        // Managers only (anyone else gets an empty list). "Not started" first, then by name.
        public async Task<List<TeamRow>> GetTeamAsync(ClaimsPrincipal user, DateTime date)
        {
            if (!FieldDayService.MayManage(user))
                return new List<TeamRow>();
            var members = await _routes.GetTeamAsync(date.Date);
            var visits = await _routes.VisitsReadyAsync() ? await _routes.GetVisitsOnDateAsync(date.Date) : new();
            return members.Select(m =>
            {
                if (m.Day == null)
                    return new TeamRow(m.UserId, m.Name, m.IsActive, NotStarted, null, null, null, 0, null, null, null, null, Array.Empty<string>(), 0, false);
                var route = Build(m.Day, visits.TryGetValue(m.Day.Id, out var v) ? v : new());
                // the route's flags plus the flags stored on the day (Start / End Day, auto-close)
                var flags = route.Flags
                    .Concat(new[] { m.Day.StartFlagReasons, m.Day.EndFlagReasons }.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f!))
                    .Distinct().ToList();
                return new TeamRow(m.UserId, m.Name, m.IsActive, m.Day.Status, m.Day.Id, m.Day.StartedAtUtc, m.Day.EndedAtUtc ?? m.Day.AutoClosedAtUtc,
                    route.VisitCount, route.RouteKm, route.OdometerKm, route.DifferenceKm, m.Day.ReviewStatus, flags,
                    route.Legs.Count(l => l.Suspicious), route.OdometerInfoNotable);
            })
            .OrderBy(r => r.NotStarted ? 0 : 1).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        }

        // Map data for the page (serialised to JSON; the script builds DOM text nodes, never HTML).
        public static object MapData(FieldRouteRules.Route r, Func<int, string?> customerUrl) => new
        {
            points = r.Points.Select(p => new
            {
                n = p.Number,
                kind = p.Kind.ToString(),
                lat = p.Latitude,
                lng = p.Longitude,
                acc = p.AccuracyM,
                low = p.LowAccuracy,
                time = AppClock.ToIndiaTime(p.AtUtc).ToString("hh:mm tt", System.Globalization.CultureInfo.InvariantCulture),
                customer = p.CustomerName,
                summary = p.Summary,
                url = p.CustomerId.HasValue ? customerUrl(p.CustomerId.Value) : null,
            }),
            legs = r.Legs.Select(l => new { from = l.From, to = l.To, suspicious = l.Suspicious }),
        };
    }
}
