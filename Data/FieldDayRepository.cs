using System.Data;
using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.Data
{
    // Field Work Phase 1 (2026-11-03): dbo.FieldDays / FieldPhotos / FieldDayEvents / FieldWorkConsents. Every change runs
    // in ONE SqlTransaction and locks the day row it reads (UPDLOCK, HOLDLOCK); the business decision is passed in by
    // FieldDayService and evaluated on the LOCKED row, so two submits / two managers can never both act on a stale copy.
    // Replays are recognised by the durable StartSubmissionToken / EndSubmissionToken columns.
    public class FieldDayRepository
    {
        private readonly DatabaseHelper _db;

        public FieldDayRepository(DatabaseHelper db) => _db = db;

        public Task<bool> SchemaReadyAsync() => FieldWorkSchema.ReadyAsync(_db);

        private const string DaySelect = @"
SELECT d.Id, d.UserId, ISNULL(NULLIF(LTRIM(RTRIM(u.Name)), N''), u.Username) AS EmployeeName, d.WorkDate, d.Status, d.VehicleType,
       d.StartedAtUtc, d.StartLatitude, d.StartLongitude, d.StartAccuracyM, d.StartKm, d.StartPhotoId, d.StartExceptionReason, d.StartFlagReasons,
       d.EndedAtUtc, d.EndLatitude, d.EndLongitude, d.EndAccuracyM, d.EndKm, d.EndPhotoId, d.EndExceptionReason, d.EndFlagReasons,
       d.AutoClosedAtUtc, d.GpsDistanceKm, d.ReviewStatus, d.ReviewedByUserId,
       ISNULL(NULLIF(LTRIM(RTRIM(rv.Name)), N''), rv.Username) AS ReviewedByName, d.ReviewedAtUtc, d.ReviewComment
FROM dbo.FieldDays d
JOIN dbo.IMSUsers u ON u.Id = d.UserId
LEFT JOIN dbo.IMSUsers rv ON rv.Id = d.ReviewedByUserId";

        private static FieldDay MapDay(SqlDataReader r)
        {
            decimal? Dec(int i) => r.IsDBNull(i) ? null : r.GetDecimal(i);
            int? Int(int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
            DateTime? Dt(int i) => r.IsDBNull(i) ? null : r.GetDateTime(i);
            string? Str(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            return new FieldDay
            {
                Id = r.GetInt32(0), UserId = r.GetInt32(1), EmployeeName = r.GetString(2), WorkDate = r.GetDateTime(3),
                Status = r.GetString(4), VehicleType = r.GetString(5),
                StartedAtUtc = r.GetDateTime(6), StartLatitude = Dec(7), StartLongitude = Dec(8), StartAccuracyM = Dec(9),
                StartKm = r.GetDecimal(10), StartPhotoId = Int(11), StartExceptionReason = Str(12), StartFlagReasons = Str(13),
                EndedAtUtc = Dt(14), EndLatitude = Dec(15), EndLongitude = Dec(16), EndAccuracyM = Dec(17), EndKm = Dec(18),
                EndPhotoId = Int(19), EndExceptionReason = Str(20), EndFlagReasons = Str(21),
                AutoClosedAtUtc = Dt(22), GpsDistanceKm = Dec(23), ReviewStatus = r.GetString(24), ReviewedByUserId = Int(25),
                ReviewedByName = Str(26), ReviewedAtUtc = Dt(27), ReviewComment = Str(28),
            };
        }

        private static SqlParameter Decimal(string name, decimal? value, byte precision, byte scale)
            => new(name, SqlDbType.Decimal) { Precision = precision, Scale = scale, Value = (object?)value ?? DBNull.Value };

        private static SqlParameter NText(string name, string? value, int size)
            => new(name, SqlDbType.NVarChar, size) { Value = (object?)value ?? DBNull.Value };

        // ---- reads ---------------------------------------------------------------------------------------------------

        public async Task<string?> GetStatusAsync(int userId, DateTime workDate)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand("SELECT Status FROM dbo.FieldDays WHERE UserId = @U AND WorkDate = @D;", conn);
            cmd.Parameters.Add("@U", SqlDbType.Int).Value = userId;
            cmd.Parameters.Add("@D", SqlDbType.Date).Value = workDate.Date;
            return await cmd.ExecuteScalarAsync() as string;
        }

        public async Task<bool> HasConsentAsync(int userId, int version)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand("SELECT COUNT(*) FROM dbo.FieldWorkConsents WHERE UserId = @U AND ConsentVersion >= @V;", conn);
            cmd.Parameters.Add("@U", SqlDbType.Int).Value = userId;
            cmd.Parameters.Add("@V", SqlDbType.Int).Value = version;
            return (int)(await cmd.ExecuteScalarAsync())! > 0;
        }

        public async Task RecordConsentAsync(int userId, int version)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            const string sql = @"
UPDATE dbo.FieldWorkConsents WITH (UPDLOCK, HOLDLOCK) SET ConsentVersion = @V, AcceptedAtUtc = SYSUTCDATETIME()
WHERE UserId = @U AND ConsentVersion < @V;
IF NOT EXISTS (SELECT 1 FROM dbo.FieldWorkConsents WHERE UserId = @U)
    INSERT INTO dbo.FieldWorkConsents (UserId, ConsentVersion) VALUES (@U, @V);";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.Add("@U", SqlDbType.Int).Value = userId;
            cmd.Parameters.Add("@V", SqlDbType.Int).Value = version;
            try
            {
                await cmd.ExecuteNonQueryAsync();
            }
            catch (SqlException ex) when (ex.Number is 2601 or 2627)
            {
                // a concurrent first acceptance -- already recorded
            }
        }

        public async Task<FieldDay?> GetDayAsync(int dayId)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(DaySelect + " WHERE d.Id = @Id;", conn);
            cmd.Parameters.Add("@Id", SqlDbType.Int).Value = dayId;
            using var r = await cmd.ExecuteReaderAsync();
            return await r.ReadAsync() ? MapDay(r) : null;
        }

        public async Task<FieldDay?> GetDayAsync(int userId, DateTime workDate)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(DaySelect + " WHERE d.UserId = @U AND d.WorkDate = @D;", conn);
            cmd.Parameters.Add("@U", SqlDbType.Int).Value = userId;
            cmd.Parameters.Add("@D", SqlDbType.Date).Value = workDate.Date;
            using var r = await cmd.ExecuteReaderAsync();
            return await r.ReadAsync() ? MapDay(r) : null;
        }

        // Newest first. userId = null: every employee (the caller has already checked FieldWork.Manage).
        public async Task<List<FieldDay>> ListDaysAsync(DateTime from, DateTime to, int? userId, string? status, string? reviewStatus, int max)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            var sql = DaySelect + @"
WHERE d.WorkDate BETWEEN @From AND @To
  AND (@U IS NULL OR d.UserId = @U)
  AND (@S IS NULL OR d.Status = @S)
  AND (@R IS NULL OR d.ReviewStatus = @R)
ORDER BY d.WorkDate DESC, EmployeeName, d.Id DESC
OFFSET 0 ROWS FETCH NEXT @Max ROWS ONLY;";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.Add("@From", SqlDbType.Date).Value = from.Date;
            cmd.Parameters.Add("@To", SqlDbType.Date).Value = to.Date;
            cmd.Parameters.Add("@U", SqlDbType.Int).Value = (object?)userId ?? DBNull.Value;
            cmd.Parameters.Add("@S", SqlDbType.VarChar, 12).Value = (object?)status ?? DBNull.Value;
            cmd.Parameters.Add("@R", SqlDbType.VarChar, 10).Value = (object?)reviewStatus ?? DBNull.Value;
            cmd.Parameters.Add("@Max", SqlDbType.Int).Value = max;
            var list = new List<FieldDay>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(MapDay(r));
            return list;
        }

        public async Task<List<FieldDayEvent>> GetEventsAsync(int dayId)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            const string sql = @"
SELECT e.Id, e.EventType, ISNULL(NULLIF(LTRIM(RTRIM(u.Name)), N''), u.Username), e.AtUtc, e.Details
FROM dbo.FieldDayEvents e LEFT JOIN dbo.IMSUsers u ON u.Id = e.ByUserId
WHERE e.FieldDayId = @Id ORDER BY e.Id;";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.Add("@Id", SqlDbType.Int).Value = dayId;
            var list = new List<FieldDayEvent>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new FieldDayEvent
                {
                    Id = r.GetInt32(0), EventType = r.GetString(1), ByName = r.IsDBNull(2) ? null : r.GetString(2),
                    AtUtc = r.GetDateTime(3), Details = r.IsDBNull(4) ? null : r.GetString(4),
                });
            return list;
        }

        public async Task<FieldPhotoRecord?> GetPhotoAsync(int photoId)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand("SELECT Id, OwnerUserId, StoredFileName, ContentType FROM dbo.FieldPhotos WHERE Id = @Id;", conn);
            cmd.Parameters.Add("@Id", SqlDbType.Int).Value = photoId;
            using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync())
                return null;
            return new FieldPhotoRecord { Id = r.GetInt32(0), OwnerUserId = r.GetInt32(1), StoredFileName = r.GetString(2), ContentType = r.GetString(3) };
        }

        // Active users holding FieldWork.Day through a role, plus anyone who already has a field day (filter options).
        public async Task<List<FieldEmployeeOption>> ListFieldEmployeesAsync()
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            const string sql = @"
SELECT u.Id, ISNULL(NULLIF(LTRIM(RTRIM(u.Name)), N''), u.Username) AS Name
FROM dbo.IMSUsers u
WHERE (u.IsActive = 1 AND EXISTS (SELECT 1 FROM dbo.UserRoles ur
                                  JOIN dbo.RolePermissions rp ON rp.RoleId = ur.RoleId
                                  JOIN dbo.Permissions p ON p.Id = rp.PermissionId
                                  WHERE ur.UserId = u.Id AND p.Code = N'FieldWork.Day'))
   OR EXISTS (SELECT 1 FROM dbo.FieldDays d WHERE d.UserId = u.Id)
ORDER BY Name;";
            using var cmd = new SqlCommand(sql, conn);
            var list = new List<FieldEmployeeOption>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new FieldEmployeeOption(r.GetInt32(0), r.GetString(1)));
            return list;
        }

        // ---- writes --------------------------------------------------------------------------------------------------

        public sealed record PhotoInput(string StoredFileName, int SizeBytes);

        public sealed record StartDayCommand(int UserId, DateTime WorkDate, string VehicleType, decimal Km, FieldDayRules.Position? Position,
            string? ExceptionReason, string? FlagReasons, PhotoInput? Photo, Guid Token);

        // Returns the day id; a replay of the same submission returns the day it already created.
        public async Task<int> StartDayAsync(StartDayCommand c)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            try
            {
                using (var replay = new SqlCommand("SELECT Id, UserId FROM dbo.FieldDays WITH (UPDLOCK, HOLDLOCK) WHERE StartSubmissionToken = @T;", conn, tx))
                {
                    replay.Parameters.Add("@T", SqlDbType.UniqueIdentifier).Value = c.Token;
                    using var r = await replay.ExecuteReaderAsync();
                    if (await r.ReadAsync())
                    {
                        var (id, owner) = (r.GetInt32(0), r.GetInt32(1));
                        await r.CloseAsync();
                        if (owner != c.UserId)
                            throw new InvalidOperationException("This form was already used. Open Start Day again.");
                        await tx.CommitAsync();
                        return id;
                    }
                }

                // a day of an earlier date still open (the job has not run yet) is closed first -- one open day per employee
                await AutoCloseStaleAsync(conn, tx, c.WorkDate, c.UserId);

                using (var existing = new SqlCommand("SELECT Status FROM dbo.FieldDays WITH (UPDLOCK, HOLDLOCK) WHERE UserId = @U AND WorkDate = @D;", conn, tx))
                {
                    existing.Parameters.Add("@U", SqlDbType.Int).Value = c.UserId;
                    existing.Parameters.Add("@D", SqlDbType.Date).Value = c.WorkDate.Date;
                    var status = await existing.ExecuteScalarAsync() as string;
                    if (status != null)
                        throw new InvalidOperationException(status == FieldDayRules.StatusOpen
                            ? "Today's day is already started."
                            : "Today's day is already closed. Ask your manager to reopen it if this is a mistake.");
                }

                var photoId = await InsertPhotoAsync(conn, tx, c.UserId, "DayStart", c.Photo, c.Position);

                // OUTPUT ... INTO: dbo.FieldDays has a trigger (TR_FieldDays_Guard), so a plain OUTPUT clause is not allowed
                const string insert = @"
DECLARE @New TABLE (Id INT);
INSERT INTO dbo.FieldDays (UserId, WorkDate, Status, VehicleType, StartLatitude, StartLongitude, StartAccuracyM, StartKm, StartPhotoId,
                           StartExceptionReason, StartFlagReasons, ReviewStatus, StartSubmissionToken)
OUTPUT inserted.Id INTO @New (Id)
VALUES (@U, @D, 'Open', @Vehicle, @Lat, @Lng, @Acc, @Km, @Photo, @Reason, @Flags, @Review, @T);
SELECT Id FROM @New;";
                int dayId;
                using (var cmd = new SqlCommand(insert, conn, tx))
                {
                    cmd.Parameters.Add("@U", SqlDbType.Int).Value = c.UserId;
                    cmd.Parameters.Add("@D", SqlDbType.Date).Value = c.WorkDate.Date;
                    cmd.Parameters.Add("@Vehicle", SqlDbType.VarChar, 20).Value = c.VehicleType;
                    cmd.Parameters.Add(Decimal("@Lat", c.Position?.Latitude, 9, 6));
                    cmd.Parameters.Add(Decimal("@Lng", c.Position?.Longitude, 9, 6));
                    cmd.Parameters.Add(Decimal("@Acc", c.Position?.AccuracyM, 9, 1));
                    cmd.Parameters.Add(Decimal("@Km", c.Km, 9, 1));
                    cmd.Parameters.Add("@Photo", SqlDbType.Int).Value = (object?)photoId ?? DBNull.Value;
                    cmd.Parameters.Add(NText("@Reason", c.ExceptionReason, 300));
                    cmd.Parameters.Add(NText("@Flags", c.FlagReasons, 500));
                    cmd.Parameters.Add("@Review", SqlDbType.VarChar, 10).Value = c.FlagReasons == null ? FieldDayRules.ReviewPending : FieldDayRules.ReviewFlagged;
                    cmd.Parameters.Add("@T", SqlDbType.UniqueIdentifier).Value = c.Token;
                    dayId = (int)(await cmd.ExecuteScalarAsync())!;
                }

                await InsertEventAsync(conn, tx, dayId, "Started", c.UserId,
                    $"Start {c.Km:0.0} km, {FieldDayRules.VehicleLabel(c.VehicleType)}" + (c.FlagReasons != null ? $"; flagged: {c.FlagReasons}" : ""));
                await tx.CommitAsync();
                return dayId;
            }
            catch (SqlException ex) when (ex.Number is 2601 or 2627)
            {
                await SafeRollbackAsync(tx);
                throw new InvalidOperationException("Today's day is already started (or this form was submitted twice). Open My Day.");
            }
            catch
            {
                await SafeRollbackAsync(tx);
                throw;
            }
        }

        public sealed record EndDayCommand(int UserId, int DayId, decimal Km, FieldDayRules.Position? Position, string? ExceptionReason,
            PhotoInput? Photo, Guid Token);

        // The service's decision on the locked row: an error (refuse) or the GPS distance + end flags to store.
        public sealed record EndDecision(string? Error, decimal? GpsDistanceKm, string? EndFlagReasons);

        public enum EndOutcome { Ended, AlreadyEnded }

        public async Task<EndOutcome> EndDayAsync(EndDayCommand c, Func<FieldDay, EndDecision> decide)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            try
            {
                var (day, endToken) = await LockDayAsync(conn, tx, c.DayId);
                if (day == null || day.UserId != c.UserId)
                    throw new InvalidOperationException("Field day not found.");
                if (endToken == c.Token)
                {
                    await tx.CommitAsync();
                    return EndOutcome.AlreadyEnded;
                }
                if (day.Status != FieldDayRules.StatusOpen)
                    throw new InvalidOperationException(day.Status == FieldDayRules.StatusAutoClosed
                        ? "This day was closed automatically at midnight."
                        : "This day is already ended.");

                var decision = decide(day);
                if (decision.Error != null)
                    throw new InvalidOperationException(decision.Error);

                var photoId = await InsertPhotoAsync(conn, tx, c.UserId, "DayEnd", c.Photo, c.Position);
                const string update = @"
UPDATE dbo.FieldDays
SET Status = 'Closed', EndedAtUtc = SYSUTCDATETIME(), EndLatitude = @Lat, EndLongitude = @Lng, EndAccuracyM = @Acc, EndKm = @Km,
    EndPhotoId = @Photo, EndExceptionReason = @Reason, EndFlagReasons = @Flags, GpsDistanceKm = @Gps, EndSubmissionToken = @T,
    ReviewStatus = CASE WHEN @Flags IS NOT NULL OR StartFlagReasons IS NOT NULL THEN 'Flagged' ELSE 'Pending' END,
    ReviewedByUserId = NULL, ReviewedAtUtc = NULL, ReviewComment = NULL, ModifiedAtUtc = SYSUTCDATETIME()
WHERE Id = @Id AND Status = 'Open';";
                using (var cmd = new SqlCommand(update, conn, tx))
                {
                    cmd.Parameters.Add("@Id", SqlDbType.Int).Value = c.DayId;
                    cmd.Parameters.Add(Decimal("@Lat", c.Position?.Latitude, 9, 6));
                    cmd.Parameters.Add(Decimal("@Lng", c.Position?.Longitude, 9, 6));
                    cmd.Parameters.Add(Decimal("@Acc", c.Position?.AccuracyM, 9, 1));
                    cmd.Parameters.Add(Decimal("@Km", c.Km, 9, 1));
                    cmd.Parameters.Add("@Photo", SqlDbType.Int).Value = (object?)photoId ?? DBNull.Value;
                    cmd.Parameters.Add(NText("@Reason", c.ExceptionReason, 300));
                    cmd.Parameters.Add(NText("@Flags", decision.EndFlagReasons, 500));
                    cmd.Parameters.Add(Decimal("@Gps", decision.GpsDistanceKm, 9, 2));
                    cmd.Parameters.Add("@T", SqlDbType.UniqueIdentifier).Value = c.Token;
                    if (await cmd.ExecuteNonQueryAsync() != 1)
                        throw new InvalidOperationException("This day is already ended.");
                }

                await InsertEventAsync(conn, tx, c.DayId, "Ended", c.UserId,
                    $"End {c.Km:0.0} km (odometer {c.Km - day.StartKm:0.0} km"
                    + (decision.GpsDistanceKm.HasValue ? $", GPS {decision.GpsDistanceKm:0.0} km" : "") + ")"
                    + (decision.EndFlagReasons != null ? $"; flagged: {decision.EndFlagReasons}" : ""));
                await tx.CommitAsync();
                return EndOutcome.Ended;
            }
            catch (SqlException ex) when (ex.Number is 2601 or 2627)
            {
                await SafeRollbackAsync(tx);
                throw new InvalidOperationException("This form was already submitted. Open My Day.");
            }
            catch
            {
                await SafeRollbackAsync(tx);
                throw;
            }
        }

        // Approve / Flag / Reopen by a manager. check runs on the locked row and returns null (allowed) or the refusal.
        public async Task ReviewAsync(int dayId, int reviewerUserId, FieldDayRules.ReviewAction action, string? comment, DateTime todayIst,
            Func<FieldDay, string?> check)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            try
            {
                var (day, _) = await LockDayAsync(conn, tx, dayId);
                if (day == null)
                    throw new InvalidOperationException("Field day not found.");
                var refusal = check(day);
                if (refusal != null)
                    throw new InvalidOperationException(refusal);
                var text = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();

                string sql;
                string details;
                switch (action)
                {
                    case FieldDayRules.ReviewAction.Approve:
                    case FieldDayRules.ReviewAction.Flag:
                        sql = @"
UPDATE dbo.FieldDays SET ReviewStatus = @Review, ReviewedByUserId = @By, ReviewedAtUtc = SYSUTCDATETIME(), ReviewComment = @Comment,
       ModifiedAtUtc = SYSUTCDATETIME()
WHERE Id = @Id;";
                        details = text ?? "";
                        break;
                    case FieldDayRules.ReviewAction.Reopen:
                        // auto-close of an earlier still-open day of the same employee first (one open day per employee)
                        await AutoCloseStaleAsync(conn, tx, todayIst, day.UserId);
                        sql = @"
UPDATE dbo.FieldDays
SET Status = 'Open', EndedAtUtc = NULL, EndLatitude = NULL, EndLongitude = NULL, EndAccuracyM = NULL, EndKm = NULL, EndPhotoId = NULL,
    EndExceptionReason = NULL, EndFlagReasons = NULL, AutoClosedAtUtc = NULL, GpsDistanceKm = NULL, EndSubmissionToken = NULL,
    ReviewStatus = CASE WHEN StartFlagReasons IS NOT NULL THEN 'Flagged' ELSE 'Pending' END,
    ReviewedByUserId = NULL, ReviewedAtUtc = NULL, ReviewComment = NULL, ModifiedAtUtc = SYSUTCDATETIME()
WHERE Id = @Id AND Status IN ('Closed', 'AutoClosed');";
                        details = $"Reason: {text}. Previous: {FieldDayRules.StatusLabel(day.Status)}"
                            + (day.EndKm.HasValue ? $", end {day.EndKm:0.0} km" : "")
                            + (day.EndPhotoId.HasValue ? $", end photo #{day.EndPhotoId}" : "")
                            + (day.GpsDistanceKm.HasValue ? $", GPS {day.GpsDistanceKm:0.0} km" : "")
                            + (day.EndFlagReasons != null ? $", flags: {day.EndFlagReasons}" : "")
                            + (day.ReviewStatus != FieldDayRules.ReviewPending ? $", review {day.ReviewStatus}" : "");
                        break;
                    default:
                        throw new InvalidOperationException("Unknown action.");
                }

                using (var cmd = new SqlCommand(sql, conn, tx))
                {
                    cmd.Parameters.Add("@Id", SqlDbType.Int).Value = dayId;
                    cmd.Parameters.Add("@By", SqlDbType.Int).Value = reviewerUserId;
                    cmd.Parameters.Add("@Review", SqlDbType.VarChar, 10).Value =
                        action == FieldDayRules.ReviewAction.Approve ? FieldDayRules.ReviewApproved : FieldDayRules.ReviewFlagged;
                    cmd.Parameters.Add(NText("@Comment", text, 300));
                    if (await cmd.ExecuteNonQueryAsync() != 1)
                        throw new InvalidOperationException("The day changed meanwhile. Open it again.");
                }

                var eventType = action switch
                {
                    FieldDayRules.ReviewAction.Approve => "Approved",
                    FieldDayRules.ReviewAction.Flag => "Flagged",
                    _ => "Reopened",
                };
                await InsertEventAsync(conn, tx, dayId, eventType, reviewerUserId, details.Length > 1000 ? details[..1000] : details);
                await tx.CommitAsync();
            }
            catch (SqlException ex) when (ex.Number is 2601 or 2627)
            {
                await SafeRollbackAsync(tx);
                throw new InvalidOperationException("The employee already has another open day, so this day cannot be reopened.");
            }
            catch
            {
                await SafeRollbackAsync(tx);
                throw;
            }
        }

        // Closes every day still Open whose WorkDate is before today (IST) -- the midnight job and the start of a new day.
        // Idempotent: only rows that are Open at the moment of the UPDATE change, so a re-run (or a second app instance
        // running it at the same time -- the UPDATE locks each row and re-checks Status) closes nothing twice.
        public async Task<int> AutoCloseStaleAsync(DateTime todayIst)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            try
            {
                var n = await AutoCloseStaleAsync(conn, tx, todayIst, null);
                await tx.CommitAsync();
                return n;
            }
            catch
            {
                await SafeRollbackAsync(tx);
                throw;
            }
        }

        private static async Task<int> AutoCloseStaleAsync(SqlConnection conn, SqlTransaction tx, DateTime todayIst, int? userId)
        {
            const string sql = @"
DECLARE @Closed TABLE (Id INT PRIMARY KEY, WorkDate DATE);
UPDATE dbo.FieldDays
SET Status = 'AutoClosed', AutoClosedAtUtc = SYSUTCDATETIME(), EndFlagReasons = @Flag, ReviewStatus = 'Flagged',
    ReviewedByUserId = NULL, ReviewedAtUtc = NULL, ReviewComment = NULL, ModifiedAtUtc = SYSUTCDATETIME()
OUTPUT inserted.Id, inserted.WorkDate INTO @Closed (Id, WorkDate)
WHERE Status = 'Open' AND WorkDate < @Today AND (@U IS NULL OR UserId = @U);
INSERT INTO dbo.FieldDayEvents (FieldDayId, EventType, ByUserId, Details)
SELECT Id, 'AutoClosed', NULL, N'Open day of ' + CONVERT(NVARCHAR(10), WorkDate, 23) + N' closed automatically (End Day not done)' FROM @Closed;
SELECT COUNT(*) FROM @Closed;";
            using var cmd = new SqlCommand(sql, conn, tx);
            cmd.Parameters.Add("@Today", SqlDbType.Date).Value = todayIst.Date;
            cmd.Parameters.Add("@U", SqlDbType.Int).Value = (object?)userId ?? DBNull.Value;
            cmd.Parameters.Add(NText("@Flag", FieldDayRules.AutoClosedFlag, 500));
            return (int)(await cmd.ExecuteScalarAsync())!;
        }

        // ---- helpers -------------------------------------------------------------------------------------------------

        private static async Task<(FieldDay? Day, Guid? EndToken)> LockDayAsync(SqlConnection conn, SqlTransaction tx, int dayId)
        {
            using (var lockCmd = new SqlCommand("SELECT EndSubmissionToken FROM dbo.FieldDays WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id;", conn, tx))
            {
                lockCmd.Parameters.Add("@Id", SqlDbType.Int).Value = dayId;
                using var r = await lockCmd.ExecuteReaderAsync();
                if (!await r.ReadAsync())
                    return (null, null);
                var token = r.IsDBNull(0) ? (Guid?)null : r.GetGuid(0);
                await r.CloseAsync();
                using var cmd = new SqlCommand(DaySelect + " WHERE d.Id = @Id;", conn, tx);
                cmd.Parameters.Add("@Id", SqlDbType.Int).Value = dayId;
                using var r2 = await cmd.ExecuteReaderAsync();
                await r2.ReadAsync();
                return (MapDay(r2), token);
            }
        }

        private static async Task<int?> InsertPhotoAsync(SqlConnection conn, SqlTransaction tx, int userId, string purpose, PhotoInput? photo,
            FieldDayRules.Position? position)
        {
            if (photo == null)
                return null;
            const string sql = @"
INSERT INTO dbo.FieldPhotos (OwnerUserId, Purpose, StoredFileName, ContentType, SizeBytes, Latitude, Longitude, AccuracyM)
OUTPUT inserted.Id
VALUES (@U, @Purpose, @File, 'image/jpeg', @Size, @Lat, @Lng, @Acc);";
            using var cmd = new SqlCommand(sql, conn, tx);
            cmd.Parameters.Add("@U", SqlDbType.Int).Value = userId;
            cmd.Parameters.Add("@Purpose", SqlDbType.VarChar, 20).Value = purpose;
            cmd.Parameters.Add("@File", SqlDbType.VarChar, 64).Value = photo.StoredFileName;
            cmd.Parameters.Add("@Size", SqlDbType.Int).Value = photo.SizeBytes;
            cmd.Parameters.Add(Decimal("@Lat", position?.Latitude, 9, 6));
            cmd.Parameters.Add(Decimal("@Lng", position?.Longitude, 9, 6));
            cmd.Parameters.Add(Decimal("@Acc", position?.AccuracyM, 9, 1));
            return (int)(await cmd.ExecuteScalarAsync())!;
        }

        private static async Task InsertEventAsync(SqlConnection conn, SqlTransaction tx, int dayId, string type, int? byUserId, string? details)
        {
            using var cmd = new SqlCommand(
                "INSERT INTO dbo.FieldDayEvents (FieldDayId, EventType, ByUserId, Details) VALUES (@D, @T, @By, @Details);", conn, tx);
            cmd.Parameters.Add("@D", SqlDbType.Int).Value = dayId;
            cmd.Parameters.Add("@T", SqlDbType.VarChar, 12).Value = type;
            cmd.Parameters.Add("@By", SqlDbType.Int).Value = (object?)byUserId ?? DBNull.Value;
            cmd.Parameters.Add(NText("@Details", details, 1000));
            await cmd.ExecuteNonQueryAsync();
        }

        private static async Task SafeRollbackAsync(SqlTransaction tx)
        {
            try
            {
                if (tx.Connection != null)
                    await tx.RollbackAsync();
            }
            catch (InvalidOperationException) { }
            catch (SqlException) { }
        }
    }
}
