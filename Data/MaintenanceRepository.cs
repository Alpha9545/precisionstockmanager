using System.Text;
using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Data
{
    // dbo.MaintenanceTickets / Events / Photos / Categories
    // (Database/Migrations/2026-10-06_MaintenanceTickets.sql).
    //
    // Every workflow step is ONE conditional UPDATE -- "... WHERE Id = @Id AND Status = <expected>"
    // plus the step's own condition (the assigned worker, NOT the assigned worker, a worker of the
    // ticket's trade) -- followed by its MaintenanceTicketEvents row, in one transaction. Two
    // people can therefore never both perform the same step (first one wins; the other gets
    // false = "changed by someone else"), and the event history always matches the ticket.
    // Every timestamp is the database's SYSUTCDATETIME(), never a value from the browser.
    // WHO may call a step is decided by the page (MaintenanceAuthorization) BEFORE calling here;
    // the database's CHECK constraints repeat the data rules.
    public class MaintenanceRepository
    {
        private readonly DatabaseHelper _dbHelper;

        public MaintenanceRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        public sealed record NewTicket(int CategoryId, int AreaId, int? PolyhouseId, string Title, string Description, string Priority);

        // ---- categories / workers ------------------------------------------------------------

        public async Task<List<MaintenanceCategory>> GetCategoriesAsync(bool activeOnly = true)
        {
            var list = new List<MaintenanceCategory>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
SELECT c.Id, c.Name, c.WorkerRoleId, COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName), c.IsActive
FROM dbo.MaintenanceCategories c JOIN dbo.Roles r ON r.Id = c.WorkerRoleId
WHERE (@ActiveOnly = 0 OR c.IsActive = 1)
ORDER BY c.SortOrder, c.Name", conn);
            cmd.Parameters.AddWithValue("@ActiveOnly", activeOnly);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new MaintenanceCategory { Id = r.GetInt32(0), Name = r.GetString(1), WorkerRoleId = r.GetInt32(2), WorkerRoleName = r.GetString(3), IsActive = r.GetBoolean(4) });
            return list;
        }

        // Active users holding the worker role of a category (assign / reassign targets).
        public async Task<List<MaintenanceWorker>> GetWorkersForCategoryAsync(int categoryId)
        {
            var list = new List<MaintenanceWorker>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
SELECT DISTINCT u.Id, u.Name
FROM dbo.MaintenanceCategories c
JOIN dbo.UserRoles ur ON ur.RoleId = c.WorkerRoleId
JOIN dbo.IMSUsers u ON u.Id = ur.UserId AND u.IsActive = 1
WHERE c.Id = @CategoryId
ORDER BY u.Name", conn);
            cmd.Parameters.AddWithValue("@CategoryId", categoryId);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new MaintenanceWorker(r.GetInt32(0), r.IsDBNull(1) ? $"User {r.GetInt32(0)}" : r.GetString(1)));
            return list;
        }

        // ---- reading -------------------------------------------------------------------------

        private const string BaseSelect = @"
SELECT t.Id, t.TicketNo, t.CategoryId, c.Name, COALESCE(NULLIF(LTRIM(RTRIM(wr.Name)), N''), wr.RoleName),
       t.AreaId, a.Name, t.PolyhouseId, p.Name, t.Title, t.Description, t.Priority, t.Status,
       t.RequestedById, ru.Name, t.RequestedAt, t.AssignedWorkerId, wu.Name, t.AcceptedAt, t.ExpectedVisitDate, t.ExpectedVisitRemarks,
       t.ArrivedAt, t.ArrivalConfirmedById, acu.Name, t.ArrivalConfirmedAt, t.WorkStartedAt, t.ProblemFound, t.WorkPerformed,
       t.WorkEndedAt, t.WorkDone, t.Resolution, t.MaterialsUsed, t.WorkerRemarks,
       t.CompletionConfirmedById, ccu.Name, t.CompletionConfirmedAt, t.ClosedAt, t.CancelledById, cu.Name, t.CancelledAt, t.CancelReason
FROM dbo.MaintenanceTickets t
JOIN dbo.MaintenanceCategories c ON c.Id = t.CategoryId
JOIN dbo.Roles wr ON wr.Id = c.WorkerRoleId
JOIN dbo.Area a ON a.Id = t.AreaId
LEFT JOIN dbo.Polyhouses p ON p.Id = t.PolyhouseId
LEFT JOIN dbo.IMSUsers ru ON ru.Id = t.RequestedById
LEFT JOIN dbo.IMSUsers wu ON wu.Id = t.AssignedWorkerId
LEFT JOIN dbo.IMSUsers acu ON acu.Id = t.ArrivalConfirmedById
LEFT JOIN dbo.IMSUsers ccu ON ccu.Id = t.CompletionConfirmedById
LEFT JOIN dbo.IMSUsers cu ON cu.Id = t.CancelledById";

        public async Task<MaintenanceTicket?> GetByIdAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(BaseSelect + " WHERE t.Id = @Id", conn);
            cmd.Parameters.AddWithValue("@Id", id);
            using var r = await cmd.ExecuteReaderAsync();
            return await r.ReadAsync() ? Map(r) : null;
        }

        // areaScope: null = every Area; otherwise only these Area ids (empty = nothing).
        // Newest first.
        public async Task<List<MaintenanceTicket>> SearchAsync(MaintenanceTicketFilter filter, IReadOnlyCollection<int>? areaScope)
        {
            var list = new List<MaintenanceTicket>();
            if (areaScope != null && areaScope.Count == 0)
                return list;
            if (filter.CategoryIds != null && filter.CategoryIds.Count == 0)
                return list;
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand { Connection = conn };
            var sb = new StringBuilder(" WHERE 1 = 1");
            if (filter.FromDate.HasValue)
            {
                // RequestedAt is UTC; the filter is an India date (UTC+05:30).
                sb.Append(" AND t.RequestedAt >= @FromUtc");
                cmd.Parameters.AddWithValue("@FromUtc", filter.FromDate.Value.Date.AddMinutes(-330));
            }
            if (filter.ToDate.HasValue)
            {
                sb.Append(" AND t.RequestedAt < @ToUtc");
                cmd.Parameters.AddWithValue("@ToUtc", filter.ToDate.Value.Date.AddDays(1).AddMinutes(-330));
            }
            void Eq(string column, string name, object? value)
            {
                if (value == null) return;
                sb.Append($" AND {column} = {name}");
                cmd.Parameters.AddWithValue(name, value);
            }
            Eq("t.CategoryId", "@CategoryId", filter.CategoryId);
            Eq("t.AssignedWorkerId", "@WorkerId", filter.WorkerId);
            Eq("t.AreaId", "@AreaId", filter.AreaId);
            Eq("t.PolyhouseId", "@PolyhouseId", filter.PolyhouseId);
            Eq("t.Status", "@Status", string.IsNullOrWhiteSpace(filter.Status) ? null : filter.Status);
            Eq("t.Priority", "@Priority", string.IsNullOrWhiteSpace(filter.Priority) ? null : filter.Priority);
            Eq("t.RequestedById", "@RequestedById", filter.RequestedById);
            if (filter.ActiveOnly)
                sb.Append(" AND t.Status NOT IN ('Closed', 'Cancelled')");
            if (filter.CategoryIds != null)
                sb.Append(" AND t.CategoryId IN (").Append(string.Join(", ", filter.CategoryIds.Select((id, i) => { cmd.Parameters.AddWithValue("@Cat" + i, id); return "@Cat" + i; }))).Append(')');
            if (areaScope != null)
                sb.Append(" AND t.AreaId IN (").Append(string.Join(", ", areaScope.Select((id, i) => { cmd.Parameters.AddWithValue("@Scope" + i, id); return "@Scope" + i; }))).Append(')');
            cmd.CommandText = BaseSelect + sb + " ORDER BY t.RequestedAt DESC, t.Id DESC";
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(Map(r));
            return list;
        }

        public async Task<List<MaintenanceEvent>> GetEventsAsync(int ticketId)
        {
            var list = new List<MaintenanceEvent>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
SELECT e.Id, e.EventType, e.FromStatus, e.ToStatus, e.ActorId, u.Name, e.EventAt, e.Note
FROM dbo.MaintenanceTicketEvents e LEFT JOIN dbo.IMSUsers u ON u.Id = e.ActorId
WHERE e.TicketId = @Id ORDER BY e.Id", conn);
            cmd.Parameters.AddWithValue("@Id", ticketId);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new MaintenanceEvent
                {
                    Id = r.GetInt32(0), EventType = r.GetString(1),
                    FromStatus = r.IsDBNull(2) ? null : r.GetString(2), ToStatus = r.IsDBNull(3) ? null : r.GetString(3),
                    ActorId = r.IsDBNull(4) ? null : r.GetInt32(4), ActorName = r.IsDBNull(5) ? null : r.GetString(5),
                    EventAt = DateTime.SpecifyKind(r.GetDateTime(6), DateTimeKind.Utc), Note = r.IsDBNull(7) ? null : r.GetString(7),
                });
            return list;
        }

        public async Task<List<MaintenancePhoto>> GetPhotosAsync(int ticketId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            return await ReadPhotosAsync(conn, "WHERE ph.TicketId = @Id ORDER BY ph.Stage DESC, ph.Id", ticketId);
        }

        public async Task<MaintenancePhoto?> GetPhotoAsync(int photoId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            return (await ReadPhotosAsync(conn, "WHERE ph.Id = @Id", photoId)).FirstOrDefault();
        }

        private static async Task<List<MaintenancePhoto>> ReadPhotosAsync(SqlConnection conn, string where, int id)
        {
            var list = new List<MaintenancePhoto>();
            using var cmd = new SqlCommand($@"
SELECT ph.Id, ph.TicketId, ph.Stage, ph.StoredFileName, ph.OriginalFileName, ph.ContentType, ph.SizeBytes, u.Name, ph.UploadedAt
FROM dbo.MaintenanceTicketPhotos ph LEFT JOIN dbo.IMSUsers u ON u.Id = ph.UploadedById {where}", conn);
            cmd.Parameters.AddWithValue("@Id", id);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new MaintenancePhoto
                {
                    Id = r.GetInt32(0), TicketId = r.GetInt32(1), Stage = r.GetString(2), StoredFileName = r.GetString(3),
                    OriginalFileName = r.GetString(4), ContentType = r.GetString(5), SizeBytes = r.GetInt32(6),
                    UploadedByName = r.IsDBNull(7) ? null : r.GetString(7), UploadedAt = DateTime.SpecifyKind(r.GetDateTime(8), DateTimeKind.Utc),
                });
            return list;
        }

        // ---- creating ------------------------------------------------------------------------

        public async Task<int> CreateAsync(NewTicket t, int requestedById, IReadOnlyList<MaintenancePhotoStore.SavedPhoto> photos)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                using var cmd = new SqlCommand(@"
INSERT INTO dbo.MaintenanceTickets (CategoryId, AreaId, PolyhouseId, Title, Description, Priority, Status, RequestedById, RequestedAt)
VALUES (@CategoryId, @AreaId, @PolyhouseId, @Title, @Description, @Priority, 'Open', @RequestedById, SYSUTCDATETIME());
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn, tx);   // not OUTPUT: the table has a trigger
                cmd.Parameters.AddWithValue("@CategoryId", t.CategoryId);
                cmd.Parameters.AddWithValue("@AreaId", t.AreaId);
                cmd.Parameters.AddWithValue("@PolyhouseId", (object?)t.PolyhouseId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Title", t.Title.Trim());
                cmd.Parameters.AddWithValue("@Description", t.Description.Trim());
                cmd.Parameters.AddWithValue("@Priority", t.Priority);
                cmd.Parameters.AddWithValue("@RequestedById", requestedById);
                var id = (int)(await cmd.ExecuteScalarAsync())!;
                await AddEventAsync(conn, tx, id, "Created", null, MaintenanceRules.Open, requestedById, $"Priority {t.Priority}");
                await AddPhotosAsync(conn, tx, id, "Problem", photos, requestedById);
                tx.Commit();
                return id;
            }
            catch
            {
                if (tx.Connection != null) tx.Rollback();
                throw;
            }
        }

        // ---- workflow steps (true = done; false = the ticket was not in the expected state) ---

        // First worker of the ticket's trade to accept wins. The database itself re-checks the
        // trade: the worker must hold the category's worker role.
        public Task<bool> AcceptAsync(int id, int workerId, DateTime expectedVisitDate, string? remarks)
            => StepAsync(id, new[] { MaintenanceRules.Open }, MaintenanceRules.Accepted, "Accepted", workerId,
                $"Expected visit {expectedVisitDate:dd-MM-yyyy}" + (string.IsNullOrWhiteSpace(remarks) ? "" : $" -- {remarks.Trim()}"),
                @"AssignedWorkerId = @Actor, AcceptedAt = SYSUTCDATETIME(), ExpectedVisitDate = @Expected, ExpectedVisitRemarks = @Remarks",
                @" AND AssignedWorkerId IS NULL AND EXISTS (SELECT 1 FROM dbo.UserRoles ur JOIN dbo.MaintenanceCategories c ON c.WorkerRoleId = ur.RoleId
                                                              WHERE ur.UserId = @Actor AND c.Id = t.CategoryId)",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@Expected", expectedVisitDate.Date);
                    cmd.Parameters.AddWithValue("@Remarks", (object?)MaintenanceRules.Trimmed(remarks) ?? DBNull.Value);
                });

        // Administrator: assign an Open ticket (-> Accepted) or change the worker of an Accepted
        // one (before arrival). The new worker must hold the ticket's trade role and be active.
        public Task<bool> AssignAsync(int id, int adminId, int workerId, string workerName, string? previousWorkerName, DateTime expectedVisitDate, string? remarks, bool reassign)
            => StepAsync(id, new[] { reassign ? MaintenanceRules.Accepted : MaintenanceRules.Open }, MaintenanceRules.Accepted,
                reassign ? "Reassigned" : "Assigned", adminId,
                (reassign ? $"Worker changed from {previousWorkerName} to {workerName}" : $"Assigned to {workerName}") + $"; expected visit {expectedVisitDate:dd-MM-yyyy}"
                    + (string.IsNullOrWhiteSpace(remarks) ? "" : $" -- {remarks.Trim()}"),
                @"AssignedWorkerId = @Worker, AcceptedAt = ISNULL(AcceptedAt, SYSUTCDATETIME()), ExpectedVisitDate = @Expected, ExpectedVisitRemarks = @Remarks",
                @" AND EXISTS (SELECT 1 FROM dbo.UserRoles ur JOIN dbo.MaintenanceCategories c ON c.WorkerRoleId = ur.RoleId
                               JOIN dbo.IMSUsers u ON u.Id = ur.UserId AND u.IsActive = 1
                               WHERE ur.UserId = @Worker AND c.Id = t.CategoryId)"
                    + (reassign ? " AND AssignedWorkerId <> @Worker" : " AND AssignedWorkerId IS NULL"),
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@Worker", workerId);
                    cmd.Parameters.AddWithValue("@Expected", expectedVisitDate.Date);
                    cmd.Parameters.AddWithValue("@Remarks", (object?)MaintenanceRules.Trimmed(remarks) ?? DBNull.Value);
                });

        public Task<bool> ArriveAsync(int id, int workerId)
            => StepAsync(id, new[] { MaintenanceRules.Accepted }, MaintenanceRules.Arrived, "Arrived", workerId, null,
                "ArrivedAt = SYSUTCDATETIME()", " AND AssignedWorkerId = @Actor");

        public Task<bool> ConfirmArrivalAsync(int id, int supervisorId)
            => StepAsync(id, new[] { MaintenanceRules.Arrived }, MaintenanceRules.ArrivalConfirmed, "ArrivalConfirmed", supervisorId, "Worker has arrived.",
                "ArrivalConfirmedById = @Actor, ArrivalConfirmedAt = SYSUTCDATETIME()", " AND AssignedWorkerId <> @Actor");

        public Task<bool> StartWorkAsync(int id, int workerId, string problemFound, string workPerformed)
            => StepAsync(id, new[] { MaintenanceRules.ArrivalConfirmed }, MaintenanceRules.InProgress, "WorkStarted", workerId, problemFound.Trim(),
                "WorkStartedAt = SYSUTCDATETIME(), ProblemFound = @Problem, WorkPerformed = @Work", " AND AssignedWorkerId = @Actor",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@Problem", problemFound.Trim());
                    cmd.Parameters.AddWithValue("@Work", workPerformed.Trim());
                });

        public Task<bool> ResolveAsync(int id, int workerId, string workDone, string resolution, string? materials, string? remarks, IReadOnlyList<MaintenancePhotoStore.SavedPhoto> photos)
            => StepAsync(id, new[] { MaintenanceRules.InProgress }, MaintenanceRules.Resolved, "Resolved", workerId, resolution.Trim(),
                "WorkEndedAt = SYSUTCDATETIME(), WorkDone = @WorkDone, Resolution = @Resolution, MaterialsUsed = @Materials, WorkerRemarks = @WorkerRemarks",
                " AND AssignedWorkerId = @Actor",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@WorkDone", workDone.Trim());
                    cmd.Parameters.AddWithValue("@Resolution", resolution.Trim());
                    cmd.Parameters.AddWithValue("@Materials", (object?)MaintenanceRules.Trimmed(materials) ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@WorkerRemarks", (object?)MaintenanceRules.Trimmed(remarks) ?? DBNull.Value);
                }, photos, "Completion");

        public Task<bool> ConfirmCompletionAsync(int id, int supervisorId)
            => StepAsync(id, new[] { MaintenanceRules.Resolved }, MaintenanceRules.Closed, "CompletionConfirmed", supervisorId, "Problem solved / work completed.",
                "CompletionConfirmedById = @Actor, CompletionConfirmedAt = SYSUTCDATETIME(), ClosedAt = SYSUTCDATETIME()", " AND AssignedWorkerId <> @Actor");

        // Back to In Progress: the work end is cleared and recorded again when the worker
        // resolves the ticket the next time (the event keeps the rejection and its reason).
        public Task<bool> RejectCompletionAsync(int id, int supervisorId, string reason)
            => StepAsync(id, new[] { MaintenanceRules.Resolved }, MaintenanceRules.InProgress, "CompletionRejected", supervisorId, reason.Trim(),
                "WorkEndedAt = NULL", " AND AssignedWorkerId <> @Actor");

        public Task<bool> CancelAsync(int id, int actorId, bool isAdministrator, string reason)
            => StepAsync(id, new[] { MaintenanceRules.Open, MaintenanceRules.Accepted }, MaintenanceRules.Cancelled, "Cancelled", actorId, reason.Trim(),
                "CancelledById = @Actor, CancelledAt = SYSUTCDATETIME(), CancelReason = @Reason",
                " AND (RequestedById = @Actor OR @IsAdmin = 1)",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@Reason", reason.Trim());
                    cmd.Parameters.AddWithValue("@IsAdmin", isAdministrator);
                });

        private async Task<bool> StepAsync(int id, string[] fromStatuses, string toStatus, string eventType, int actorId, string? note,
            string set, string extraWhere, Action<SqlCommand>? addParameters = null,
            IReadOnlyList<MaintenancePhotoStore.SavedPhoto>? photos = null, string? photoStage = null)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                var from = string.Join(", ", fromStatuses.Select((s, i) => "@From" + i));
                using var cmd = new SqlCommand($@"
DECLARE @Previous TABLE (Status VARCHAR(20));
UPDATE t SET Status = @To, {set}, UpdatedAt = SYSUTCDATETIME(), UpdatedById = @Actor
OUTPUT deleted.Status INTO @Previous
FROM dbo.MaintenanceTickets t
WHERE t.Id = @Id AND t.Status IN ({from}){extraWhere};
SELECT TOP (1) Status FROM @Previous;", conn, tx);
                cmd.Parameters.AddWithValue("@Id", id);
                cmd.Parameters.AddWithValue("@To", toStatus);
                cmd.Parameters.AddWithValue("@Actor", actorId);
                for (var i = 0; i < fromStatuses.Length; i++)
                    cmd.Parameters.AddWithValue("@From" + i, fromStatuses[i]);
                addParameters?.Invoke(cmd);
                var previous = await cmd.ExecuteScalarAsync() as string;
                if (previous == null)
                {
                    tx.Rollback();
                    return false;
                }
                await AddEventAsync(conn, tx, id, eventType, previous, toStatus, actorId, note);
                if (photos != null && photos.Count > 0)
                    await AddPhotosAsync(conn, tx, id, photoStage!, photos, actorId);
                tx.Commit();
                return true;
            }
            catch
            {
                if (tx.Connection != null) tx.Rollback();
                throw;
            }
        }

        private static async Task AddEventAsync(SqlConnection conn, SqlTransaction tx, int ticketId, string eventType, string? from, string? to, int? actorId, string? note)
        {
            using var cmd = new SqlCommand(@"
INSERT INTO dbo.MaintenanceTicketEvents (TicketId, EventType, FromStatus, ToStatus, ActorId, EventAt, Note)
VALUES (@TicketId, @EventType, @From, @To, @Actor, SYSUTCDATETIME(), @Note)", conn, tx);
            cmd.Parameters.AddWithValue("@TicketId", ticketId);
            cmd.Parameters.AddWithValue("@EventType", eventType);
            cmd.Parameters.AddWithValue("@From", (object?)from ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@To", (object?)to ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Actor", (object?)actorId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Note", note == null ? DBNull.Value : note.Length > 1000 ? note[..1000] : note);
            await cmd.ExecuteNonQueryAsync();
        }

        // Photo rows + one PhotoAdded event. The per-stage limit is checked under the ticket's
        // row lock, so two simultaneous uploads can never exceed it.
        private static async Task AddPhotosAsync(SqlConnection conn, SqlTransaction tx, int ticketId, string stage, IReadOnlyList<MaintenancePhotoStore.SavedPhoto> photos, int uploadedById)
        {
            if (photos.Count == 0)
                return;
            using (var count = new SqlCommand(@"
SELECT COUNT(*) FROM dbo.MaintenanceTicketPhotos ph
JOIN dbo.MaintenanceTickets t WITH (UPDLOCK, HOLDLOCK) ON t.Id = ph.TicketId
WHERE ph.TicketId = @TicketId AND ph.Stage = @Stage", conn, tx))
            {
                count.Parameters.AddWithValue("@TicketId", ticketId);
                count.Parameters.AddWithValue("@Stage", stage);
                if ((int)(await count.ExecuteScalarAsync())! + photos.Count > MaintenanceRules.MaxPhotosPerStage)
                    throw new InvalidOperationException($"At most {MaintenanceRules.MaxPhotosPerStage} {stage.ToLowerInvariant()} photos per ticket.");
            }
            foreach (var p in photos)
            {
                using var cmd = new SqlCommand(@"
INSERT INTO dbo.MaintenanceTicketPhotos (TicketId, Stage, StoredFileName, OriginalFileName, ContentType, SizeBytes, UploadedById, UploadedAt)
VALUES (@TicketId, @Stage, @Stored, @Original, @ContentType, @Size, @UploadedById, SYSUTCDATETIME())", conn, tx);
                cmd.Parameters.AddWithValue("@TicketId", ticketId);
                cmd.Parameters.AddWithValue("@Stage", stage);
                cmd.Parameters.AddWithValue("@Stored", p.StoredFileName);
                cmd.Parameters.AddWithValue("@Original", p.OriginalFileName);
                cmd.Parameters.AddWithValue("@ContentType", p.ContentType);
                cmd.Parameters.AddWithValue("@Size", p.SizeBytes);
                cmd.Parameters.AddWithValue("@UploadedById", uploadedById);
                await cmd.ExecuteNonQueryAsync();
            }
            await AddEventAsync(conn, tx, ticketId, "PhotoAdded", null, null, uploadedById, $"{photos.Count} {stage.ToLowerInvariant()} photo(s)");
        }

        private static DateTime? Utc(SqlDataReader r, int i) => r.IsDBNull(i) ? null : DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Utc);
        private static string? S(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
        private static int? I(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);

        private static MaintenanceTicket Map(SqlDataReader r) => new()
        {
            Id = r.GetInt32(0), TicketNo = r.GetString(1), CategoryId = r.GetInt32(2), CategoryName = r.GetString(3), WorkerRoleName = r.GetString(4),
            AreaId = r.GetInt32(5), AreaName = S(r, 6), PolyhouseId = I(r, 7), PolyhouseName = S(r, 8),
            Title = r.GetString(9), Description = r.GetString(10), Priority = r.GetString(11), Status = r.GetString(12),
            RequestedById = r.GetInt32(13), RequestedByName = S(r, 14), RequestedAt = Utc(r, 15)!.Value,
            AssignedWorkerId = I(r, 16), AssignedWorkerName = S(r, 17), AcceptedAt = Utc(r, 18),
            ExpectedVisitDate = r.IsDBNull(19) ? null : r.GetDateTime(19), ExpectedVisitRemarks = S(r, 20),
            ArrivedAt = Utc(r, 21), ArrivalConfirmedById = I(r, 22), ArrivalConfirmedByName = S(r, 23), ArrivalConfirmedAt = Utc(r, 24),
            WorkStartedAt = Utc(r, 25), ProblemFound = S(r, 26), WorkPerformed = S(r, 27),
            WorkEndedAt = Utc(r, 28), WorkDone = S(r, 29), Resolution = S(r, 30), MaterialsUsed = S(r, 31), WorkerRemarks = S(r, 32),
            CompletionConfirmedById = I(r, 33), CompletionConfirmedByName = S(r, 34), CompletionConfirmedAt = Utc(r, 35), ClosedAt = Utc(r, 36),
            CancelledById = I(r, 37), CancelledByName = S(r, 38), CancelledAt = Utc(r, 39), CancelReason = S(r, 40),
        };
    }
}
