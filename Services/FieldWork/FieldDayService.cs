using System.Security.Claims;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Services.FieldWork
{
    // Field Work Phase 1 (2026-11-03): Start Day / End Day / review, independent of Razor Pages -- a page (or a future
    // mobile API endpoint) passes the signed-in user, the submitted values and the photo bytes, and gets a result.
    // The rules are FieldDayRules (pure); the atomic, locked writes are FieldDayRepository; time is the SERVER clock
    // (AppClock: the IST date, the database's SYSUTCDATETIME()) -- the phone's clock is never used.
    public class FieldDayService
    {
        private readonly FieldDayRepository _repo;
        private readonly FieldPhotoStore _photos;
        private readonly FieldWorkOptions _options;

        public FieldDayService(FieldDayRepository repo, FieldPhotoStore photos, FieldWorkOptions options)
        {
            _repo = repo;
            _photos = photos;
            _options = options;
        }

        public FieldWorkOptions Options => _options;

        public sealed record Result(bool Ok, IReadOnlyList<string> Errors, int? DayId = null)
        {
            public static Result Success(int? dayId = null) => new(true, Array.Empty<string>(), dayId);
            public static Result Fail(params string[] errors) => new(false, errors);
            public static Result Fail(IEnumerable<string> errors) => new(false, errors.ToList());
        }

        public sealed record ReadingRequest(decimal? Km, decimal? Latitude, decimal? Longitude, decimal? AccuracyM, string? ExceptionReason,
            byte[]? Photo, string? SubmitToken);

        // A field employee: holds FieldWork.Day as a real permission claim (an administrator's implicit access does not
        // make them one, so administrators are never sent to Start Day).
        public static bool IsFieldEmployee(ClaimsPrincipal user)
            => user.Identity?.IsAuthenticated == true && !user.IsFullAccess()
               && user.HasClaim(MinimumAuthorizationLevelHandler.PermissionClaimType, FieldDayRules.DayPermission);

        public static bool MayManage(ClaimsPrincipal user) => user.HasPermission(FieldDayRules.ManagePermission);

        public Task<bool> SchemaReadyAsync() => _repo.SchemaReadyAsync();

        public Task<bool> HasConsentAsync(int userId) => _repo.HasConsentAsync(userId, _options.ConsentVersion);

        public Task RecordConsentAsync(int userId) => _repo.RecordConsentAsync(userId, _options.ConsentVersion);

        public Task<string?> GetTodayStatusAsync(int userId) => _repo.GetStatusAsync(userId, AppClock.Today);

        public Task<FieldDay?> GetTodayAsync(int userId) => _repo.GetDayAsync(userId, AppClock.Today);

        public async Task<FieldDayRules.GateResult> GateAsync(ClaimsPrincipal user, string pagePath)
        {
            if (!IsFieldEmployee(user) || FieldDayRules.GateFreePages.Contains(pagePath))
                return FieldDayRules.GateResult.Allow;
            var userId = user.GetUserId();
            if (userId == null || !await _repo.SchemaReadyAsync())
                return FieldDayRules.GateResult.Allow;
            var consent = await _repo.HasConsentAsync(userId.Value, _options.ConsentVersion);
            var status = consent ? await _repo.GetStatusAsync(userId.Value, AppClock.Today) : null;
            return FieldDayRules.Gate(true, pagePath, consent, status, user.HasPermission("Dashboard.View"));
        }

        private static Guid? ParseToken(string? token) => Guid.TryParse(token, out var g) && g != Guid.Empty ? g : null;

        private async Task<(FieldDayRepository.PhotoInput? Photo, string? Error)> SavePhotoAsync(byte[]? bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return (null, null);
            var error = _photos.Validate(bytes);
            if (error != null)
                return (null, error);
            var stored = await _photos.SaveAsync(bytes);
            return (new FieldDayRepository.PhotoInput(stored, bytes.Length), null);
        }

        public async Task<Result> StartDayAsync(int userId, string? vehicleType, ReadingRequest request)
        {
            var token = ParseToken(request.SubmitToken);
            if (token == null)
                return Result.Fail("This form has expired. Open Start Day again.");
            if (!await HasConsentAsync(userId))
                return Result.Fail("Accept the Field Work notice first.");

            var hasPhoto = request.Photo is { Length: > 0 };
            var check = FieldDayRules.CheckReading(
                new FieldDayRules.ReadingInput(request.Km, request.Latitude, request.Longitude, request.AccuracyM, hasPhoto, request.ExceptionReason),
                _options.MaxGpsAccuracyMeters, "Start");
            var errors = check.Errors.ToList();
            if (FieldDayRules.ValidateVehicleType(vehicleType) is { } vehicleError)
                errors.Insert(0, vehicleError);
            if (hasPhoto && _photos.Validate(request.Photo) is { } photoError)
                errors.Add(photoError);
            if (errors.Count > 0)
                return Result.Fail(errors);

            var (photo, saveError) = await SavePhotoAsync(request.Photo);
            if (saveError != null)
                return Result.Fail(saveError);
            try
            {
                var dayId = await _repo.StartDayAsync(new FieldDayRepository.StartDayCommand(
                    userId, AppClock.Today, vehicleType!, check.Km, check.Position, check.Reason, FieldDayRules.JoinFlags(check.Flags), photo, token.Value));
                return Result.Success(dayId);
            }
            catch (Exception ex)
            {
                _photos.DeleteQuietly(photo?.StoredFileName);
                return Result.Fail(DbErrorMessage.For(ex));
            }
        }

        public async Task<Result> EndDayAsync(int userId, ReadingRequest request)
        {
            var token = ParseToken(request.SubmitToken);
            if (token == null)
                return Result.Fail("This form has expired. Open End Day again.");
            var today = await _repo.GetDayAsync(userId, AppClock.Today);
            if (today == null || today.Status != FieldDayRules.StatusOpen)
                return Result.Fail("You have no open day today.");

            var hasPhoto = request.Photo is { Length: > 0 };
            var check = FieldDayRules.CheckReading(
                new FieldDayRules.ReadingInput(request.Km, request.Latitude, request.Longitude, request.AccuracyM, hasPhoto, request.ExceptionReason),
                _options.MaxGpsAccuracyMeters, "End");
            var errors = check.Errors.ToList();
            if (check.Ok && FieldDayRules.ValidateEndKm(today.StartKm, check.Km) is { } kmError)
                errors.Add(kmError);
            if (hasPhoto && _photos.Validate(request.Photo) is { } photoError)
                errors.Add(photoError);
            if (errors.Count > 0)
                return Result.Fail(errors);

            var (photo, saveError) = await SavePhotoAsync(request.Photo);
            if (saveError != null)
                return Result.Fail(saveError);
            try
            {
                var outcome = await _repo.EndDayAsync(
                    new FieldDayRepository.EndDayCommand(userId, today.Id, check.Km, check.Position, check.Reason, photo, token.Value),
                    (locked, visits) =>
                    {
                        // re-checked on the LOCKED row: the start reading the decision is based on cannot have changed
                        if (FieldDayRules.ValidateEndKm(locked.StartKm, check.Km) is { } lockedKmError)
                            return new FieldDayRepository.EndDecision(lockedKmError, null, null);
                        var start = locked.StartLatitude.HasValue && locked.StartLongitude.HasValue && locked.StartAccuracyM.HasValue
                            ? new FieldDayRules.Position(locked.StartLatitude.Value, locked.StartLongitude.Value, locked.StartAccuracyM.Value)
                            : null;
                        // GpsDistanceKm stays the straight start -> end line (Phase 1); the 2 km slack rule is judged on the
                        // whole route start -> each visit -> end (Phase 3), so a round trip with far visits is caught too.
                        var gpsKm = FieldDayRules.GpsDistanceKm(start, check.Position);
                        string? mismatch = null;
                        if (start != null && check.Position != null)
                        {
                            var routeKm = FieldRouteRules.RouteKm(visits
                                .Append(new FieldRouteRules.PointInput(FieldRouteRules.PointKind.Start, locked.StartedAtUtc, start.Latitude, start.Longitude, start.AccuracyM, null))
                                .Append(new FieldRouteRules.PointInput(FieldRouteRules.PointKind.End, DateTime.UtcNow, check.Position.Latitude, check.Position.Longitude, check.Position.AccuracyM, null)));
                            mismatch = FieldRouteRules.RouteMismatchFlag(routeKm, check.Km - locked.StartKm, _options.DistanceMismatchSlackKm);
                        }
                        return new FieldDayRepository.EndDecision(null, gpsKm, FieldDayRules.JoinFlags(check.Flags.Append(mismatch)));
                    });
                if (outcome == FieldDayRepository.EndOutcome.AlreadyEnded)
                    _photos.DeleteQuietly(photo?.StoredFileName);
                return Result.Success(today.Id);
            }
            catch (Exception ex)
            {
                _photos.DeleteQuietly(photo?.StoredFileName);
                return Result.Fail(DbErrorMessage.For(ex));
            }
        }

        // The day, only when this user may see it (own day, or FieldWork.Manage / administrator). null = not found or
        // not allowed -- the caller answers 404 either way, so a changed id in the URL reveals nothing.
        public async Task<FieldDay?> GetVisibleDayAsync(ClaimsPrincipal user, int dayId)
        {
            var day = await _repo.GetDayAsync(dayId);
            return day != null && FieldDayRules.MaySeeDay(day.UserId, user.GetUserId(), MayManage(user)) ? day : null;
        }

        public Task<List<FieldDayEvent>> GetEventsAsync(int dayId) => _repo.GetEventsAsync(dayId);

        // The file of one of the day's two photos, for a user who may see the day.
        public async Task<(string Path, string ContentType)?> GetDayPhotoAsync(ClaimsPrincipal user, int dayId, int photoId)
        {
            var day = await GetVisibleDayAsync(user, dayId);
            if (day == null || (day.StartPhotoId != photoId && day.EndPhotoId != photoId))
                return null;
            var photo = await _repo.GetPhotoAsync(photoId);
            if (photo == null || photo.OwnerUserId != day.UserId || photo.ContentType != FieldPhotoStore.JpegContentType)
                return null;
            var path = _photos.PathFor(photo.StoredFileName);
            return path != null && File.Exists(path) ? (path, photo.ContentType) : null;
        }

        public async Task<List<FieldDay>> ListMyDaysAsync(int userId, int days = 45)
            => await _repo.ListDaysAsync(AppClock.Today.AddDays(-days), AppClock.Today, userId, null, null, 200);

        public sealed record DayFilter(DateTime From, DateTime To, int? UserId, string? Status, string? ReviewStatus);

        public const int MaxRangeDays = 92;
        public const int MaxListRows = 300;

        // Normalises a manager's filter: unknown values are dropped, never widened beyond the allowed range.
        public static DayFilter NormalizeFilter(DateTime? from, DateTime? to, int? userId, string? status, string? review,
            IReadOnlyCollection<int> knownEmployeeIds, DateTime todayIst)
        {
            var t = (to ?? todayIst).Date;
            var f = (from ?? t).Date;
            if (f > t)
                (f, t) = (t, f);
            if ((t - f).TotalDays > MaxRangeDays)
                f = t.AddDays(-MaxRangeDays);
            return new DayFilter(f, t,
                userId.HasValue && knownEmployeeIds.Contains(userId.Value) ? userId : null,
                status != null && FieldDayRules.Statuses.Contains(status) ? status : null,
                review != null && FieldDayRules.ReviewStatuses.Contains(review) ? review : null);
        }

        public Task<List<FieldEmployeeOption>> ListFieldEmployeesAsync() => _repo.ListFieldEmployeesAsync();

        public Task<List<FieldDay>> ListDaysAsync(DayFilter filter)
            => _repo.ListDaysAsync(filter.From, filter.To, filter.UserId, filter.Status, filter.ReviewStatus, MaxListRows);

        public async Task<Result> ReviewAsync(ClaimsPrincipal user, int dayId, FieldDayRules.ReviewAction action, string? comment)
        {
            var reviewerId = user.GetUserId();
            if (reviewerId == null || !MayManage(user))
                return Result.Fail("Only a field manager can review field days.");
            var isFull = user.IsFullAccess();
            var today = AppClock.Today;
            var precheck = await _repo.GetDayAsync(dayId);
            if (precheck == null)
                return Result.Fail("Field day not found.");
            var refusal = FieldDayRules.CheckReview(new FieldDayRules.DayFacts(precheck.UserId, precheck.WorkDate, precheck.Status),
                action, reviewerId.Value, isFull, comment, today);
            if (refusal != null)
                return Result.Fail(refusal);
            try
            {
                await _repo.ReviewAsync(dayId, reviewerId.Value, action, comment, today,
                    locked => FieldDayRules.CheckReview(new FieldDayRules.DayFacts(locked.UserId, locked.WorkDate, locked.Status),
                        action, reviewerId.Value, isFull, comment, today));
                return Result.Success(dayId);
            }
            catch (Exception ex)
            {
                return Result.Fail(DbErrorMessage.For(ex));
            }
        }

        public Task<int> AutoCloseStaleAsync() => _repo.AutoCloseStaleAsync(AppClock.Today);
    }
}
