namespace PlantStockManager.Services.FieldWork
{
    // Field Work Phase 1 (2026-11-03): the pure Start Day / End Day / review rules. No database, no HTTP -- the same
    // rules serve the Razor Pages now and a mobile API later. The database re-checks the shape of every row
    // (2026-11-03_FieldWorkDays.sql CHECK constraints + TR_FieldDays_Guard).
    public static class FieldDayRules
    {
        public const string DayPermission = "FieldWork.Day";
        public const string ManagePermission = "FieldWork.Manage";

        public const string StatusOpen = "Open";
        public const string StatusClosed = "Closed";
        public const string StatusAutoClosed = "AutoClosed";

        public const string ReviewPending = "Pending";
        public const string ReviewApproved = "Approved";
        public const string ReviewFlagged = "Flagged";

        public const decimal MaxKm = 9_999_999.9m;
        public const int MaxReasonLength = 300;
        public const int MaxCommentLength = 300;
        public const int MaxFlagLength = 500;

        public const string AutoClosedFlag = "Auto-closed at midnight: End Day was not done";

        public static readonly IReadOnlyList<string> Statuses = new[] { StatusOpen, StatusClosed, StatusAutoClosed };
        public static readonly IReadOnlyList<string> ReviewStatuses = new[] { ReviewPending, ReviewApproved, ReviewFlagged };

        public static readonly IReadOnlyDictionary<string, string> VehicleTypes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OwnBike"] = "Own bike",
            ["OwnCar"] = "Own car",
            ["CompanyVehicle"] = "Company vehicle",
        };

        public static string VehicleLabel(string? code) => code != null && VehicleTypes.TryGetValue(code, out var label) ? label : code ?? "";

        public static string StatusLabel(string? status) => status == StatusAutoClosed ? "Auto-closed" : status ?? "";

        public sealed record Position(decimal Latitude, decimal Longitude, decimal AccuracyM);

        // What one Start Day or End Day submission carries, before any database check.
        public sealed record ReadingInput(decimal? Km, decimal? Latitude, decimal? Longitude, decimal? AccuracyM, bool HasPhoto, string? ExceptionReason);

        // Errors: refuse the submission. Flags: accept it and mark the day for manager review.
        public sealed record ReadingCheck(IReadOnlyList<string> Errors, IReadOnlyList<string> Flags, Position? Position, string? Reason, decimal Km)
        {
            public bool Ok => Errors.Count == 0;
        }

        public static string? NormalizeReason(string? reason)
        {
            var r = (reason ?? "").Trim();
            return r.Length == 0 ? null : r;
        }

        public static ReadingCheck CheckReading(ReadingInput input, decimal maxAccuracyMeters, string step)
        {
            var errors = new List<string>();
            var flags = new List<string>();
            var reason = NormalizeReason(input.ExceptionReason);
            if (reason != null && reason.Length > MaxReasonLength)
                errors.Add($"The reason can be at most {MaxReasonLength} characters.");

            var km = input.Km ?? -1m;
            if (input.Km == null)
                errors.Add("Enter the meter reading (km).");
            else if (km < 0 || km > MaxKm)
                errors.Add($"The meter reading must be between 0 and {MaxKm:0.0} km.");
            else if (decimal.Round(km, 1) != km)
                errors.Add("The meter reading can have at most one decimal place.");

            Position? position = null;
            var given = new[] { input.Latitude.HasValue, input.Longitude.HasValue, input.AccuracyM.HasValue };
            if (given.Any(g => g) && !given.All(g => g))
                errors.Add("The GPS position is incomplete. Tap \"Retry GPS\".");
            else if (given.All(g => g))
            {
                var lat = input.Latitude!.Value;
                var lng = input.Longitude!.Value;
                var acc = input.AccuracyM!.Value;
                if (lat < -90 || lat > 90 || lng < -180 || lng > 180 || acc <= 0 || acc > 100_000)
                    errors.Add("The GPS position is not valid. Tap \"Retry GPS\".");
                else
                {
                    position = new Position(decimal.Round(lat, 6), decimal.Round(lng, 6), decimal.Round(acc, 1));
                    if (acc > maxAccuracyMeters)
                    {
                        if (reason == null)
                            errors.Add($"GPS accuracy is too poor ({acc:0} m; at most {maxAccuracyMeters:0} m is accepted). Move to open sky and tap \"Retry GPS\", or give a reason.");
                        else
                            flags.Add($"{step}: poor GPS accuracy {acc:0} m ({reason})");
                    }
                }
            }
            else if (reason == null)
                errors.Add("Location is required. Allow location access and tap \"Retry GPS\", or give a reason why GPS is not working.");
            else
                flags.Add($"{step}: no GPS ({reason})");

            if (!input.HasPhoto)
            {
                if (reason == null)
                    errors.Add("Take a live photo of the meter, or give a reason why the camera is not working.");
                else
                    flags.Add($"{step}: no meter photo ({reason})");
            }

            return new ReadingCheck(errors, flags, position, reason, km);
        }

        public static string? ValidateVehicleType(string? vehicleType)
            => vehicleType != null && VehicleTypes.ContainsKey(vehicleType) ? null : "Select the vehicle type.";

        public static string? ValidateEndKm(decimal startKm, decimal endKm)
            => endKm < startKm ? $"The end reading ({endKm:0.0} km) cannot be less than the start reading ({startKm:0.0} km)." : null;

        // Great-circle distance in km (haversine, mean Earth radius 6371.0088 km).
        public static double HaversineKm(double lat1, double lng1, double lat2, double lng2)
        {
            const double R = 6371.0088;
            static double Rad(double d) => d * Math.PI / 180.0;
            var dLat = Rad(lat2 - lat1);
            var dLng = Rad(lng2 - lng1);
            var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                  + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
            return 2 * R * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
        }

        public static decimal? GpsDistanceKm(Position? start, Position? end)
            => start == null || end == null
                ? null
                : decimal.Round((decimal)HaversineKm((double)start.Latitude, (double)start.Longitude, (double)end.Latitude, (double)end.Longitude), 2);

        // Flag when the straight line between the two GPS points is longer than the odometer distance + slack.
        public static string? DistanceMismatchFlag(decimal? gpsKm, decimal odometerKm, decimal slackKm)
            => gpsKm.HasValue && gpsKm.Value > odometerKm + slackKm
                ? $"GPS distance {gpsKm.Value:0.0} km is more than the odometer distance {odometerKm:0.0} km (+{slackKm:0.#} km allowed)"
                : null;

        public static string? JoinFlags(IEnumerable<string?> flags)
        {
            var text = string.Join("; ", flags.Where(f => !string.IsNullOrWhiteSpace(f)));
            if (text.Length == 0)
                return null;
            return text.Length <= MaxFlagLength ? text : text[..(MaxFlagLength - 1)] + "…";
        }

        // ---- review -------------------------------------------------------------------------------------------------

        public enum ReviewAction { Approve, Flag, Reopen }

        public sealed record DayFacts(int OwnerUserId, DateTime WorkDate, string Status);

        // null = allowed, otherwise the reason it is refused. A manager never reviews their own day (an administrator may).
        public static string? CheckReview(DayFacts day, ReviewAction action, int reviewerUserId, bool reviewerIsFullAccess,
            string? comment, DateTime todayIst)
        {
            if (day.OwnerUserId == reviewerUserId && !reviewerIsFullAccess)
                return "You cannot review your own field day.";
            var text = (comment ?? "").Trim();
            if (text.Length > MaxCommentLength)
                return $"The comment can be at most {MaxCommentLength} characters.";
            switch (action)
            {
                case ReviewAction.Approve:
                    return day.Status == StatusOpen ? "The day is still open; approve it after End Day." : null;
                case ReviewAction.Flag:
                    return text.Length == 0 ? "Write why the day is flagged." : null;
                case ReviewAction.Reopen:
                    if (day.Status == StatusOpen)
                        return "The day is already open.";
                    if (day.WorkDate.Date != todayIst.Date)
                        return "Only today's day can be reopened (past days stay closed).";
                    return text.Length == 0 ? "Write why the day is reopened." : null;
                default:
                    return "Unknown action.";
            }
        }

        // Who may see a day: its employee, or a field manager / administrator.
        public static bool MaySeeDay(int ownerUserId, int? viewerUserId, bool viewerMayManage)
            => viewerMayManage || (viewerUserId.HasValue && viewerUserId.Value == ownerUserId);

        // ---- Start Day gate -----------------------------------------------------------------------------------------

        public enum GateResult { Allow, Consent, StartDay, FieldHome }

        // Pages a field employee may open before today's day is started.
        public static readonly IReadOnlySet<string> GateFreePages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "/FieldWork/StartDay", "/FieldWork/Consent", "/Account/Logout", "/Account/Login", "/Account/AccessDenied", "/Error",
        };

        // isFieldEmployee: holds FieldWork.Day itself (an administrator's implicit access does not count).
        public static GateResult Gate(bool isFieldEmployee, string pagePath, bool hasConsent, string? todayStatus, bool mayOpenDashboard)
        {
            if (!isFieldEmployee || GateFreePages.Contains(pagePath))
                return GateResult.Allow;
            if (!hasConsent)
                return GateResult.Consent;
            if (todayStatus != StatusOpen)
                return GateResult.StartDay;
            if (string.Equals(pagePath, "/Index", StringComparison.OrdinalIgnoreCase) && !mayOpenDashboard)
                return GateResult.FieldHome;
            return GateResult.Allow;
        }
    }
}
