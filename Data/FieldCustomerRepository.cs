using System.Data;
using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services.FieldWork;
using static PlantStockManager.Data.FieldSql;

namespace PlantStockManager.Data
{
    // Field Work Phase 2 (2026-11-03): customers, calls / notes, follow-ups, reassign, history. Every change runs in ONE
    // SqlTransaction, locks the customer row it changes (UPDLOCK, HOLDLOCK) and evaluates the service's decision on the
    // LOCKED row. Creations are replay-safe through durable SubmissionToken columns. Visibility is decided by the service
    // (FieldCustomerRules); list queries take the viewer and "sees all" explicitly.
    public class FieldCustomerRepository
    {
        private readonly DatabaseHelper _db;

        public FieldCustomerRepository(DatabaseHelper db) => _db = db;

        public Task<bool> SchemaReadyAsync() => FieldCustomerSchema.ReadyAsync(_db);

        private static readonly string CustomerSelect = $@"
SELECT c.Id, c.Name, c.ContactPerson, c.Phone, c.AlternatePhone, c.CustomerType, c.VillageCity, c.District, c.Address,
       c.Latitude, c.Longitude, c.AccuracyM, c.GpsExceptionReason, c.FlagReasons, c.PhotoId, c.LeadStatus,
       c.AssignedUserId, {NameOf("a")}, CAST(ISNULL(a.IsActive, 0) AS BIT), c.CreatedByUserId, {NameOf("cb")}, c.CreatedAtUtc,
       c.ModifiedAtUtc, c.IsActive, c.ArchivedAtUtc, {NameOf("ab")}, c.ArchiveReason
FROM dbo.FieldCustomers c
JOIN dbo.IMSUsers a ON a.Id = c.AssignedUserId
JOIN dbo.IMSUsers cb ON cb.Id = c.CreatedByUserId
LEFT JOIN dbo.IMSUsers ab ON ab.Id = c.ArchivedByUserId";

        private static FieldCustomer MapCustomer(SqlDataReader r) => new()
        {
            Id = r.GetInt32(0), Name = r.GetString(1), ContactPerson = Str(r, 2), Phone = r.GetString(3), AlternatePhone = Str(r, 4),
            CustomerType = r.GetString(5), VillageCity = r.GetString(6), District = Str(r, 7), Address = Str(r, 8),
            Latitude = Dec(r, 9), Longitude = Dec(r, 10), AccuracyM = Dec(r, 11), GpsExceptionReason = Str(r, 12), FlagReasons = Str(r, 13),
            PhotoId = IntOrNull(r, 14), LeadStatus = r.GetString(15), AssignedUserId = r.GetInt32(16), AssignedName = r.GetString(17),
            AssignedUserActive = r.GetBoolean(18), CreatedByUserId = r.GetInt32(19), CreatedByName = r.GetString(20), CreatedAtUtc = r.GetDateTime(21),
            ModifiedAtUtc = Dt(r, 22), IsActive = r.GetBoolean(23), ArchivedAtUtc = Dt(r, 24), ArchivedByName = Str(r, 25), ArchiveReason = Str(r, 26),
        };

        // ---- reads ---------------------------------------------------------------------------------------------------

        public async Task<FieldCustomer?> GetAsync(int id)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            return await GetAsync(conn, null, id, false);
        }

        private static async Task<FieldCustomer?> GetAsync(SqlConnection conn, SqlTransaction? tx, int id, bool lockRow)
        {
            if (lockRow)
            {
                using var lockCmd = new SqlCommand("SELECT Id FROM dbo.FieldCustomers WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id;", conn, tx);
                lockCmd.Parameters.Add(Int("@Id", id));
                if (await lockCmd.ExecuteScalarAsync() == null)
                    return null;
            }
            using var cmd = new SqlCommand(CustomerSelect + " WHERE c.Id = @Id;", conn, tx);
            cmd.Parameters.Add(Int("@Id", id));
            using var r = await cmd.ExecuteReaderAsync();
            return await r.ReadAsync() ? MapCustomer(r) : null;
        }

        public sealed record ListQuery(int ViewerUserId, bool SeeAll, string? Search, string? Status, string? Type, string? District,
            int? EmployeeId, DateTime? NoVisitSinceUtc, bool NoFollowUp, bool NeedsReassign, string Archived, int Page, int PageSize);

        public async Task<(List<FieldCustomerListRow> Rows, int Total)> ListAsync(ListQuery q)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            var sql = $@"
SELECT c.Id, c.Name, c.ContactPerson, c.Phone, c.CustomerType, c.VillageCity, c.District, c.LeadStatus, c.AssignedUserId, {NameOf("a")},
       CAST(ISNULL(a.IsActive, 0) AS BIT), c.IsActive, lv.LastVisit, fu.DueDate, COUNT(*) OVER () AS Total
FROM dbo.FieldCustomers c
JOIN dbo.IMSUsers a ON a.Id = c.AssignedUserId
OUTER APPLY (SELECT MAX(v.CheckInAtUtc) AS LastVisit FROM dbo.FieldVisits v WHERE v.CustomerId = c.Id) lv
LEFT JOIN dbo.FieldFollowUps fu ON fu.CustomerId = c.Id AND fu.Status = 'Pending'
WHERE (@All = 1 OR c.AssignedUserId = @V OR c.CreatedByUserId = @V)
  AND (@Archived = 'All' OR c.IsActive = CASE WHEN @Archived = 'Archived' THEN 0 ELSE 1 END)
  AND (@Q IS NULL OR c.Name LIKE @Q ESCAPE '\' OR c.VillageCity LIKE @Q ESCAPE '\' OR c.ContactPerson LIKE @Q ESCAPE '\'
       OR (@QDigits IS NOT NULL AND (@All = 1 OR c.AssignedUserId = @V) AND (c.Phone LIKE @QDigits OR c.AlternatePhone LIKE @QDigits)))
  AND (@Status IS NULL OR c.LeadStatus = @Status)
  AND (@Type IS NULL OR c.CustomerType = @Type)
  AND (@District IS NULL OR c.District = @District)
  AND (@Emp IS NULL OR c.AssignedUserId = @Emp)
  AND (@NoVisitSince IS NULL OR COALESCE(lv.LastVisit, c.CreatedAtUtc) < @NoVisitSince)
  AND (@NoFollowUp = 0 OR (c.LeadStatus IN ('Interested', 'SampleGiven', 'Negotiation') AND fu.Id IS NULL))
  AND (@NeedsReassign = 0 OR (ISNULL(a.IsActive, 0) = 0 AND c.IsActive = 1))
ORDER BY c.Name, c.Id
OFFSET @Skip ROWS FETCH NEXT @Size ROWS ONLY;";
            using var cmd = new SqlCommand(sql, conn);
            var search = FieldCustomerRules.Clean(q.Search);
            var digits = search == null ? "" : new string(search.Where(char.IsAsciiDigit).ToArray());
            cmd.Parameters.Add(Int("@V", q.ViewerUserId));
            cmd.Parameters.Add(new SqlParameter("@All", SqlDbType.Bit) { Value = q.SeeAll });
            cmd.Parameters.Add(Text("@Archived", q.Archived, 10));
            cmd.Parameters.Add(NText("@Q", search == null ? null : "%" + EscapeLike(search) + "%", 210));
            cmd.Parameters.Add(Text("@QDigits", digits.Length >= 3 && digits.Length == search!.Replace(" ", "").Replace("+", "").Replace("-", "").Length
                ? "%" + digits + "%" : null, 20));
            cmd.Parameters.Add(Text("@Status", q.Status, 15));
            cmd.Parameters.Add(Text("@Type", q.Type, 15));
            cmd.Parameters.Add(NText("@District", q.District, 100));
            cmd.Parameters.Add(Int("@Emp", q.EmployeeId));
            cmd.Parameters.Add(new SqlParameter("@NoVisitSince", SqlDbType.DateTime2) { Value = (object?)q.NoVisitSinceUtc ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@NoFollowUp", SqlDbType.Bit) { Value = q.NoFollowUp });
            cmd.Parameters.Add(new SqlParameter("@NeedsReassign", SqlDbType.Bit) { Value = q.NeedsReassign });
            cmd.Parameters.Add(Int("@Skip", (Math.Max(1, q.Page) - 1) * q.PageSize));
            cmd.Parameters.Add(Int("@Size", q.PageSize));
            var rows = new List<FieldCustomerListRow>();
            var total = 0;
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var assigned = r.GetInt32(8);
                var phone = r.GetString(3);
                rows.Add(new FieldCustomerListRow
                {
                    Id = r.GetInt32(0), Name = r.GetString(1), ContactPerson = Str(r, 2),
                    PhoneShown = FieldCustomerRules.MaySeePhone(assigned, q.ViewerUserId, q.SeeAll) ? phone : FieldCustomerRules.MaskPhone(phone),
                    CustomerType = r.GetString(4), VillageCity = r.GetString(5), District = Str(r, 6), LeadStatus = r.GetString(7),
                    AssignedUserId = assigned, AssignedName = r.GetString(9), AssignedUserActive = r.GetBoolean(10), IsActive = r.GetBoolean(11),
                    LastVisitAtUtc = Dt(r, 12), PendingFollowUpDate = Dt(r, 13),
                });
                total = r.GetInt32(14);
            }
            return (rows, total);
        }

        private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");

        // Districts of the customers this viewer can see (filter options).
        public async Task<List<string>> VisibleDistrictsAsync(int viewerUserId, bool seeAll)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            const string sql = @"
SELECT DISTINCT c.District FROM dbo.FieldCustomers c
WHERE c.District IS NOT NULL AND (@All = 1 OR c.AssignedUserId = @V OR c.CreatedByUserId = @V)
ORDER BY c.District;";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.Add(Int("@V", viewerUserId));
            cmd.Parameters.Add(new SqlParameter("@All", SqlDbType.Bit) { Value = seeAll });
            var list = new List<string>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(r.GetString(0));
            return list;
        }

        // Every district already entered (suggestions and case-insensitive reuse of the existing spelling).
        public async Task<List<string>> AllDistrictsAsync()
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand("SELECT DISTINCT District FROM dbo.FieldCustomers WHERE District IS NOT NULL ORDER BY District;", conn);
            var list = new List<string>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(r.GetString(0));
            return list;
        }

        public async Task<List<FieldCustomerRules.DuplicateMatch>> FindByPhoneKeysAsync(IReadOnlyCollection<string> keys, int? excludeCustomerId)
        {
            var list = new List<FieldCustomerRules.DuplicateMatch>();
            if (keys.Count == 0)
                return list;
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            var sql = $@"
SELECT TOP 10 c.Id, c.Name, c.VillageCity, {NameOf("a")}, c.IsActive, c.AssignedUserId
FROM dbo.FieldCustomers c JOIN dbo.IMSUsers a ON a.Id = c.AssignedUserId
WHERE (c.PhoneKey IN (SELECT value FROM STRING_SPLIT(@Keys, ',')) OR c.AlternatePhoneKey IN (SELECT value FROM STRING_SPLIT(@Keys, ',')))
  AND (@Ex IS NULL OR c.Id <> @Ex)
ORDER BY c.IsActive DESC, c.Id;";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.Add(Text("@Keys", string.Join(",", keys), 100));
            cmd.Parameters.Add(Int("@Ex", excludeCustomerId));
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new FieldCustomerRules.DuplicateMatch(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4), r.GetInt32(5)));
            return list;
        }

        // Active users holding FieldWork.Day through a role (assignment targets).
        public async Task<List<FieldEmployeeOption>> ListAssignableEmployeesAsync()
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            var sql = $@"
SELECT u.Id, {NameOf("u")} AS Name FROM dbo.IMSUsers u
WHERE u.IsActive = 1 AND EXISTS (SELECT 1 FROM dbo.UserRoles ur JOIN dbo.RolePermissions rp ON rp.RoleId = ur.RoleId
                                  JOIN dbo.Permissions p ON p.Id = rp.PermissionId WHERE ur.UserId = u.Id AND p.Code = N'FieldWork.Day')
ORDER BY Name;";
            using var cmd = new SqlCommand(sql, conn);
            var list = new List<FieldEmployeeOption>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new FieldEmployeeOption(r.GetInt32(0), r.GetString(1)));
            return list;
        }

        public sealed record AssigneeSummary(int UserId, string Name, bool IsActive, int ActiveCustomers);

        // Everyone who has active customers assigned (bulk reassign sources; deactivated employees first).
        public async Task<List<AssigneeSummary>> ListAssigneesAsync()
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            var sql = $@"
SELECT u.Id, {NameOf("u")}, CAST(ISNULL(u.IsActive, 0) AS BIT), COUNT(*)
FROM dbo.FieldCustomers c JOIN dbo.IMSUsers u ON u.Id = c.AssignedUserId
WHERE c.IsActive = 1
GROUP BY u.Id, u.Name, u.Username, u.IsActive
ORDER BY CAST(ISNULL(u.IsActive, 0) AS INT), 2;";
            using var cmd = new SqlCommand(sql, conn);
            var list = new List<AssigneeSummary>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new AssigneeSummary(r.GetInt32(0), r.GetString(1), r.GetBoolean(2), r.GetInt32(3)));
            return list;
        }

        public async Task<int> CountNeedingReassignAsync()
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"SELECT COUNT(*) FROM dbo.FieldCustomers c JOIN dbo.IMSUsers u ON u.Id = c.AssignedUserId
WHERE c.IsActive = 1 AND ISNULL(u.IsActive, 0) = 0;", conn);
            return (int)(await cmd.ExecuteScalarAsync())!;
        }

        // ---- follow-ups ----------------------------------------------------------------------------------------------

        private static readonly string FollowUpSelect = $@"
SELECT f.Id, f.CustomerId, c.Name, c.VillageCity, c.LeadStatus, c.AssignedUserId, {NameOf("a")}, f.DueDate, f.Note, f.Status,
       {NameOf("cb")}, f.CreatedAtUtc, f.ClosedAtUtc, {NameOf("cl")}, f.ClosedByVisitId, f.ClosedByNoteId, f.CancelReason
FROM dbo.FieldFollowUps f
JOIN dbo.FieldCustomers c ON c.Id = f.CustomerId
JOIN dbo.IMSUsers a ON a.Id = c.AssignedUserId
JOIN dbo.IMSUsers cb ON cb.Id = f.CreatedByUserId
LEFT JOIN dbo.IMSUsers cl ON cl.Id = f.ClosedByUserId";

        private static FieldFollowUp MapFollowUp(SqlDataReader r) => new()
        {
            Id = r.GetInt32(0), CustomerId = r.GetInt32(1), CustomerName = r.GetString(2), VillageCity = r.GetString(3), LeadStatus = r.GetString(4),
            AssignedUserId = r.GetInt32(5), AssignedName = r.GetString(6), DueDate = r.GetDateTime(7), Note = Str(r, 8), Status = r.GetString(9),
            CreatedByName = r.GetString(10), CreatedAtUtc = r.GetDateTime(11), ClosedAtUtc = Dt(r, 12), ClosedByName = Str(r, 13),
            ClosedByVisitId = IntOrNull(r, 14), ClosedByNoteId = IntOrNull(r, 15), CancelReason = Str(r, 16),
        };

        public async Task<FieldFollowUp?> GetFollowUpAsync(int id)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(FollowUpSelect + " WHERE f.Id = @Id;", conn);
            cmd.Parameters.Add(Int("@Id", id));
            using var r = await cmd.ExecuteReaderAsync();
            return await r.ReadAsync() ? MapFollowUp(r) : null;
        }

        // Pending follow-ups of ACTIVE customers: the viewer's own (assigned) customers, or everyone's (managers),
        // optionally one employee, optionally only those due by @DueBy (overdue + today).
        public async Task<List<FieldFollowUp>> ListPendingAsync(int viewerUserId, bool seeAll, int? employeeId, DateTime? dueBy, int max)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(FollowUpSelect + @"
WHERE f.Status = 'Pending' AND c.IsActive = 1
  AND (@All = 1 OR c.AssignedUserId = @V)
  AND (@Emp IS NULL OR c.AssignedUserId = @Emp)
  AND (@DueBy IS NULL OR f.DueDate <= @DueBy)
ORDER BY f.DueDate, c.Name
OFFSET 0 ROWS FETCH NEXT @Max ROWS ONLY;", conn);
            cmd.Parameters.Add(Int("@V", viewerUserId));
            cmd.Parameters.Add(new SqlParameter("@All", SqlDbType.Bit) { Value = seeAll });
            cmd.Parameters.Add(Int("@Emp", employeeId));
            cmd.Parameters.Add(Date("@DueBy", dueBy));
            cmd.Parameters.Add(Int("@Max", max));
            var list = new List<FieldFollowUp>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(MapFollowUp(r));
            return list;
        }

        // Badge / My Day card: pending follow-ups of the user's ACTIVE assigned customers, due today and overdue.
        public async Task<FieldFollowUpCounts> CountDueAsync(int userId, DateTime todayIst)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            const string sql = @"
SELECT ISNULL(SUM(CASE WHEN f.DueDate = @Today THEN 1 ELSE 0 END), 0), ISNULL(SUM(CASE WHEN f.DueDate < @Today THEN 1 ELSE 0 END), 0)
FROM dbo.FieldFollowUps f JOIN dbo.FieldCustomers c ON c.Id = f.CustomerId
WHERE f.Status = 'Pending' AND f.DueDate <= @Today AND c.IsActive = 1 AND c.AssignedUserId = @U;";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.Add(Int("@U", userId));
            cmd.Parameters.Add(Date("@Today", todayIst));
            using var r = await cmd.ExecuteReaderAsync();
            await r.ReadAsync();
            return new FieldFollowUpCounts(r.GetInt32(0), r.GetInt32(1));
        }

        // ---- history -------------------------------------------------------------------------------------------------

        public async Task<FieldCustomerTimeline> GetTimelineAsync(int customerId)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            var t = new FieldCustomerTimeline { Visits = await FieldVisitRepository.ListForCustomerAsync(conn, customerId) };

            using (var cmd = new SqlCommand($@"
SELECT n.Id, n.CustomerId, n.VisitId, n.UserId, {NameOf("u")}, n.NoteType, n.Text, n.LeadStatusBefore, n.LeadStatusAfter, n.CreatedAtUtc
FROM dbo.FieldCustomerNotes n JOIN dbo.IMSUsers u ON u.Id = n.UserId WHERE n.CustomerId = @C ORDER BY n.CreatedAtUtc DESC, n.Id DESC;", conn))
            {
                cmd.Parameters.Add(Int("@C", customerId));
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    t.Notes.Add(new FieldCustomerNote
                    {
                        Id = r.GetInt32(0), CustomerId = r.GetInt32(1), VisitId = IntOrNull(r, 2), UserId = r.GetInt32(3), UserName = r.GetString(4),
                        NoteType = r.GetString(5), Text = r.GetString(6), LeadStatusBefore = Str(r, 7), LeadStatusAfter = Str(r, 8), CreatedAtUtc = r.GetDateTime(9),
                    });
            }
            using (var cmd = new SqlCommand(FollowUpSelect + " WHERE f.CustomerId = @C ORDER BY f.Id DESC;", conn))
            {
                cmd.Parameters.Add(Int("@C", customerId));
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    t.FollowUps.Add(MapFollowUp(r));
            }
            using (var cmd = new SqlCommand($@"
SELECT e.Id, e.VisitId, e.FollowUpId, e.EventType, e.FromStatus, e.ToStatus, {NameOf("fu")}, {NameOf("tu")}, {NameOf("b")}, e.AtUtc, e.Details
FROM dbo.FieldCustomerEvents e JOIN dbo.IMSUsers b ON b.Id = e.ByUserId
LEFT JOIN dbo.IMSUsers fu ON fu.Id = e.FromUserId LEFT JOIN dbo.IMSUsers tu ON tu.Id = e.ToUserId
WHERE e.CustomerId = @C ORDER BY e.Id DESC;", conn))
            {
                cmd.Parameters.Add(Int("@C", customerId));
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    t.Events.Add(new FieldCustomerEvent
                    {
                        Id = r.GetInt32(0), VisitId = IntOrNull(r, 1), FollowUpId = IntOrNull(r, 2), EventType = r.GetString(3), FromStatus = Str(r, 4),
                        ToStatus = Str(r, 5), FromUserName = Str(r, 6), ToUserName = Str(r, 7), ByName = r.GetString(8), AtUtc = r.GetDateTime(9), Details = Str(r, 10),
                    });
            }
            return t;
        }

        // The stored file of a photo that belongs to THIS customer: its profile photo or a photo of one of its visits.
        public async Task<FieldPhotoRecord?> GetCustomerPhotoAsync(int customerId, int photoId)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            const string sql = @"
SELECT p.Id, p.OwnerUserId, p.StoredFileName, p.ContentType FROM dbo.FieldPhotos p
WHERE p.Id = @P AND (
      (p.Purpose = 'CustomerPhoto' AND EXISTS (SELECT 1 FROM dbo.FieldCustomers c WHERE c.Id = @C AND c.PhotoId = p.Id))
   OR (p.Purpose = 'VisitPhoto' AND EXISTS (SELECT 1 FROM dbo.FieldVisitPhotos vp JOIN dbo.FieldVisits v ON v.Id = vp.VisitId
                                          WHERE vp.PhotoId = p.Id AND v.CustomerId = @C)));";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.Add(Int("@C", customerId));
            cmd.Parameters.Add(Int("@P", photoId));
            using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync())
                return null;
            return new FieldPhotoRecord { Id = r.GetInt32(0), OwnerUserId = r.GetInt32(1), StoredFileName = r.GetString(2), ContentType = r.GetString(3) };
        }

        // ---- writes --------------------------------------------------------------------------------------------------

        public sealed record PhotoInput(string StoredFileName, int SizeBytes, int ConsentReminderVersion);

        public sealed record CreateCommand(FieldCustomerRules.CleanCustomer Customer, int AssignedUserId, int CreatedByUserId,
            FieldDayRules.Position? Position, string? GpsExceptionReason, string? FlagReasons, PhotoInput? Photo, Guid Token);

        // The customer an earlier submit of this form created (id, creator), or null.
        public async Task<(int Id, int CreatedByUserId)?> FindByTokenAsync(Guid token)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand("SELECT Id, CreatedByUserId FROM dbo.FieldCustomers WHERE SubmissionToken = @T;", conn);
            cmd.Parameters.Add("@T", SqlDbType.UniqueIdentifier).Value = token;
            using var r = await cmd.ExecuteReaderAsync();
            return await r.ReadAsync() ? (r.GetInt32(0), r.GetInt32(1)) : null;
        }

        // Returns (id, replay). A replay of the same submission by the same user returns the customer it already created.
        public async Task<(int Id, bool Replay)> CreateAsync(CreateCommand c)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            try
            {
                using (var replay = new SqlCommand("SELECT Id, CreatedByUserId FROM dbo.FieldCustomers WITH (UPDLOCK, HOLDLOCK) WHERE SubmissionToken = @T;", conn, tx))
                {
                    replay.Parameters.Add("@T", SqlDbType.UniqueIdentifier).Value = c.Token;
                    using var r = await replay.ExecuteReaderAsync();
                    if (await r.ReadAsync())
                    {
                        var (id, by) = (r.GetInt32(0), r.GetInt32(1));
                        await r.CloseAsync();
                        if (by != c.CreatedByUserId)
                            throw new InvalidOperationException("This form was already used. Open Add Customer again.");
                        await tx.CommitAsync();
                        return (id, true);
                    }
                }

                var newId = await InsertCustomerAsync(conn, tx, c);
                await tx.CommitAsync();
                return (newId, false);
            }
            catch (SqlException ex) when (ex.Number is 2601 or 2627)
            {
                await SafeRollbackAsync(tx);
                throw new InvalidOperationException("This form was already submitted. Open the customer list.");
            }
            catch
            {
                await SafeRollbackAsync(tx);
                throw;
            }
        }

        // Inserts the customer (its photo first, when given) and its 'Created' event inside the caller's transaction. Shared
        // by Add Customer and the one-step New Visit (FieldVisitRepository.CreateWithNewCustomerAsync).
        internal static async Task<int> InsertCustomerAsync(SqlConnection conn, SqlTransaction tx, CreateCommand c)
        {
            int? photoId = null;
            if (c.Photo != null)
                photoId = await InsertPhotoAsync(conn, tx, c.CreatedByUserId, "CustomerPhoto", c.Photo.StoredFileName, c.Photo.SizeBytes,
                    c.Position?.Latitude, c.Position?.Longitude, c.Position?.AccuracyM, c.Photo.ConsentReminderVersion);

            const string insert = @"
DECLARE @New TABLE (Id INT);
INSERT INTO dbo.FieldCustomers (Name, ContactPerson, Phone, AlternatePhone, CustomerType, VillageCity, District, Address,
       Latitude, Longitude, AccuracyM, GpsExceptionReason, FlagReasons, PhotoId, LeadStatus, AssignedUserId, CreatedByUserId, SubmissionToken)
OUTPUT inserted.Id INTO @New (Id)
VALUES (@Name, @Contact, @Phone, @Alt, @Type, @Village, @District, @Address, @Lat, @Lng, @Acc, @Reason, @Flags, @Photo, 'New', @Assigned, @By, @T);
SELECT Id FROM @New;";
            int newId;
            using (var cmd = new SqlCommand(insert, conn, tx))
            {
                var x = c.Customer;
                cmd.Parameters.Add(NText("@Name", x.Name, 150));
                cmd.Parameters.Add(NText("@Contact", x.ContactPerson, 150));
                cmd.Parameters.Add(Text("@Phone", x.Phone, 15));
                cmd.Parameters.Add(Text("@Alt", x.AlternatePhone, 15));
                cmd.Parameters.Add(Text("@Type", x.CustomerType, 15));
                cmd.Parameters.Add(NText("@Village", x.VillageCity, 100));
                cmd.Parameters.Add(NText("@District", x.District, 100));
                cmd.Parameters.Add(NText("@Address", x.Address, 400));
                cmd.Parameters.Add(Decimal("@Lat", c.Position?.Latitude, 9, 6));
                cmd.Parameters.Add(Decimal("@Lng", c.Position?.Longitude, 9, 6));
                cmd.Parameters.Add(Decimal("@Acc", c.Position?.AccuracyM, 9, 1));
                cmd.Parameters.Add(NText("@Reason", c.GpsExceptionReason, 300));
                cmd.Parameters.Add(NText("@Flags", c.FlagReasons, 500));
                cmd.Parameters.Add(Int("@Photo", photoId));
                cmd.Parameters.Add(Int("@Assigned", c.AssignedUserId));
                cmd.Parameters.Add(Int("@By", c.CreatedByUserId));
                cmd.Parameters.Add("@T", SqlDbType.UniqueIdentifier).Value = c.Token;
                newId = (int)(await cmd.ExecuteScalarAsync())!;
            }
            await InsertEventAsync(conn, tx, newId, "Created", c.CreatedByUserId,
                $"{c.Customer.Name}, {c.Customer.VillageCity}" + (c.FlagReasons != null ? $"; flagged: {c.FlagReasons}" : ""),
                toStatus: "New", toUserId: c.AssignedUserId);
            return newId;
        }

        // Active customers ASSIGNED to this user (the ones they may log a visit for), matching name / village / contact or
        // phone digits -- the New Visit form's "Existing customer" search.
        public async Task<List<FieldCustomerListRow>> SearchWorkableAsync(int userId, string? search, int max)
        {
            var q = FieldCustomerRules.Clean(search);
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            var sql = $@"
SELECT TOP (@Max) c.Id, c.Name, c.ContactPerson, c.Phone, c.CustomerType, c.VillageCity, c.District, c.LeadStatus, fu.DueDate
FROM dbo.FieldCustomers c
LEFT JOIN dbo.FieldFollowUps fu ON fu.CustomerId = c.Id AND fu.Status = 'Pending'
WHERE c.IsActive = 1 AND c.AssignedUserId = @U
  AND (@Q IS NULL OR c.Name LIKE @Q ESCAPE '\' OR c.VillageCity LIKE @Q ESCAPE '\' OR c.ContactPerson LIKE @Q ESCAPE '\'
       OR (@QDigits IS NOT NULL AND (c.Phone LIKE @QDigits OR c.AlternatePhone LIKE @QDigits)))
ORDER BY c.Name, c.Id;";
            using var cmd = new SqlCommand(sql, conn);
            var digits = q == null ? "" : new string(q.Where(char.IsAsciiDigit).ToArray());
            cmd.Parameters.Add(Int("@Max", max));
            cmd.Parameters.Add(Int("@U", userId));
            cmd.Parameters.Add(NText("@Q", q == null ? null : "%" + EscapeLike(q) + "%", 210));
            cmd.Parameters.Add(Text("@QDigits", digits.Length >= 3 ? "%" + digits + "%" : null, 20));
            var list = new List<FieldCustomerListRow>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new FieldCustomerListRow
                {
                    Id = r.GetInt32(0), Name = r.GetString(1), ContactPerson = Str(r, 2), PhoneShown = r.GetString(3), CustomerType = r.GetString(4),
                    VillageCity = r.GetString(5), District = Str(r, 6), LeadStatus = r.GetString(7), PendingFollowUpDate = Dt(r, 8),
                    AssignedUserId = userId, IsActive = true, AssignedUserActive = true,
                });
            return list;
        }

        // Runs act on the LOCKED customer after check(customer) returned null (else that refusal is thrown).
        private async Task<T> MutateAsync<T>(int customerId, Func<FieldCustomer, string?> check,
            Func<SqlConnection, SqlTransaction, FieldCustomer, Task<T>> act)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            try
            {
                var customer = await GetAsync(conn, tx, customerId, lockRow: true)
                    ?? throw new InvalidOperationException("Customer not found.");
                var refusal = check(customer);
                if (refusal != null)
                    throw new InvalidOperationException(refusal);
                var result = await act(conn, tx, customer);
                await tx.CommitAsync();
                return result;
            }
            catch (SqlException ex) when (ex.Number is 2601 or 2627)
            {
                await SafeRollbackAsync(tx);
                throw new InvalidOperationException("This was already saved, or the customer changed meanwhile. Open the customer again.");
            }
            catch
            {
                await SafeRollbackAsync(tx);
                throw;
            }
        }

        public Task UpdateProfileAsync(int customerId, int byUserId, FieldCustomerRules.CleanCustomer x, Func<FieldCustomer, string?> check,
            Func<FieldCustomer, string> describeChanges)
            => MutateAsync(customerId, check, async (conn, tx, old) =>
            {
                var changes = describeChanges(old);
                if (changes.Length == 0)
                    return 0;
                const string sql = @"
UPDATE dbo.FieldCustomers SET Name = @Name, ContactPerson = @Contact, Phone = @Phone, AlternatePhone = @Alt, CustomerType = @Type,
       VillageCity = @Village, District = @District, Address = @Address, ModifiedAtUtc = SYSUTCDATETIME()
WHERE Id = @Id;";
                using (var cmd = new SqlCommand(sql, conn, tx))
                {
                    cmd.Parameters.Add(Int("@Id", customerId));
                    cmd.Parameters.Add(NText("@Name", x.Name, 150));
                    cmd.Parameters.Add(NText("@Contact", x.ContactPerson, 150));
                    cmd.Parameters.Add(Text("@Phone", x.Phone, 15));
                    cmd.Parameters.Add(Text("@Alt", x.AlternatePhone, 15));
                    cmd.Parameters.Add(Text("@Type", x.CustomerType, 15));
                    cmd.Parameters.Add(NText("@Village", x.VillageCity, 100));
                    cmd.Parameters.Add(NText("@District", x.District, 100));
                    cmd.Parameters.Add(NText("@Address", x.Address, 400));
                    await cmd.ExecuteNonQueryAsync();
                }
                await InsertEventAsync(conn, tx, customerId, "Updated", byUserId, changes);
                return 1;
            });

        public Task SetStatusAsync(int customerId, int byUserId, string status, string? comment, Func<FieldCustomer, string?> check)
            => MutateAsync(customerId, check, async (conn, tx, old) =>
            {
                if (old.LeadStatus == status)
                    return 0;
                await UpdateStatusAsync(conn, tx, customerId, status);
                await InsertEventAsync(conn, tx, customerId, "StatusChanged", byUserId, comment, fromStatus: old.LeadStatus, toStatus: status);
                return 1;
            });

        internal static async Task UpdateStatusAsync(SqlConnection conn, SqlTransaction tx, int customerId, string status)
        {
            using var cmd = new SqlCommand("UPDATE dbo.FieldCustomers SET LeadStatus = @S, ModifiedAtUtc = SYSUTCDATETIME() WHERE Id = @Id;", conn, tx);
            cmd.Parameters.Add(Int("@Id", customerId));
            cmd.Parameters.Add(Text("@S", status, 15));
            await cmd.ExecuteNonQueryAsync();
        }

        public Task ArchiveAsync(int customerId, int byUserId, string reason, Func<FieldCustomer, string?> check)
            => MutateAsync(customerId, check, async (conn, tx, _) =>
            {
                using (var cmd = new SqlCommand(@"UPDATE dbo.FieldCustomers SET IsActive = 0, ArchivedByUserId = @By, ArchivedAtUtc = SYSUTCDATETIME(),
       ArchiveReason = @R, ModifiedAtUtc = SYSUTCDATETIME() WHERE Id = @Id AND IsActive = 1;", conn, tx))
                {
                    cmd.Parameters.Add(Int("@Id", customerId));
                    cmd.Parameters.Add(Int("@By", byUserId));
                    cmd.Parameters.Add(NText("@R", reason, 300));
                    if (await cmd.ExecuteNonQueryAsync() != 1)
                        throw new InvalidOperationException("The customer is already archived.");
                }
                await InsertEventAsync(conn, tx, customerId, "Archived", byUserId, reason);
                return 1;
            });

        public Task RestoreAsync(int customerId, int byUserId, Func<FieldCustomer, string?> check)
            => MutateAsync(customerId, check, async (conn, tx, old) =>
            {
                using (var cmd = new SqlCommand(@"UPDATE dbo.FieldCustomers SET IsActive = 1, ArchivedByUserId = NULL, ArchivedAtUtc = NULL,
       ArchiveReason = NULL, ModifiedAtUtc = SYSUTCDATETIME() WHERE Id = @Id AND IsActive = 0;", conn, tx))
                {
                    cmd.Parameters.Add(Int("@Id", customerId));
                    if (await cmd.ExecuteNonQueryAsync() != 1)
                        throw new InvalidOperationException("The customer is not archived.");
                }
                await InsertEventAsync(conn, tx, customerId, "Restored", byUserId, $"Was archived: {old.ArchiveReason}");
                return 1;
            });

        // Moves the given customers to toUserId in ONE transaction (their pending follow-ups follow, because a follow-up
        // belongs to the customer's assigned employee). Every customer must still be assigned to expectedFromUserId (when
        // given) or already to toUserId (a repeated submit: nothing to do); otherwise NOTHING moves.
        public async Task<int> ReassignAsync(IReadOnlyCollection<int> customerIds, int? expectedFromUserId, int toUserId, int byUserId, string? reason)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            try
            {
                var ids = string.Join(",", customerIds.Distinct());
                var current = new List<(int Id, int Assigned, bool Active)>();
                using (var cmd = new SqlCommand(@"SELECT c.Id, c.AssignedUserId, c.IsActive FROM dbo.FieldCustomers c WITH (UPDLOCK, HOLDLOCK)
WHERE c.Id IN (SELECT CAST(value AS INT) FROM STRING_SPLIT(@Ids, ','));", conn, tx))
                {
                    cmd.Parameters.Add(Text("@Ids", ids, 8000));
                    using var r = await cmd.ExecuteReaderAsync();
                    while (await r.ReadAsync())
                        current.Add((r.GetInt32(0), r.GetInt32(1), r.GetBoolean(2)));
                }
                if (current.Count != customerIds.Distinct().Count())
                    throw new InvalidOperationException("Some customers were not found. Open the list again.");
                if (current.Any(c => !c.Active))
                    throw new InvalidOperationException("Archived customers cannot be reassigned. Restore them first.");
                if (expectedFromUserId.HasValue && current.Any(c => c.Assigned != expectedFromUserId.Value && c.Assigned != toUserId))
                    throw new InvalidOperationException("Some customers were reassigned by someone else meanwhile. Nothing was moved; open the list again.");

                var moved = 0;
                foreach (var c in current.Where(c => c.Assigned != toUserId))
                {
                    using (var cmd = new SqlCommand("UPDATE dbo.FieldCustomers SET AssignedUserId = @To, ModifiedAtUtc = SYSUTCDATETIME() WHERE Id = @Id;", conn, tx))
                    {
                        cmd.Parameters.Add(Int("@Id", c.Id));
                        cmd.Parameters.Add(Int("@To", toUserId));
                        await cmd.ExecuteNonQueryAsync();
                    }
                    await InsertEventAsync(conn, tx, c.Id, "Reassigned", byUserId, reason, fromUserId: c.Assigned, toUserId: toUserId);
                    moved++;
                }
                await tx.CommitAsync();
                return moved;
            }
            catch
            {
                await SafeRollbackAsync(tx);
                throw;
            }
        }

        // ---- calls / notes / follow-ups ------------------------------------------------------------------------------

        public sealed record NoteCommand(int CustomerId, int UserId, string NoteType, string Text, int? VisitId, string? NewLeadStatus,
            bool CompletesFollowUp, DateTime? NextFollowUpDate, string? NextFollowUpNote, Guid Token);

        public sealed record EntryResult(int Id, bool Replay, bool CompletedFollowUp, bool CreatedFollowUp);

        // check(customer, pendingFollowUp, visitOwnerUserId) on the LOCKED customer.
        public async Task<EntryResult> AddNoteAsync(NoteCommand c, Func<FieldCustomer, FieldFollowUp?, int?, string?> check)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            try
            {
                using (var replay = new SqlCommand("SELECT Id, UserId FROM dbo.FieldCustomerNotes WITH (UPDLOCK, HOLDLOCK) WHERE SubmissionToken = @T;", conn, tx))
                {
                    replay.Parameters.Add("@T", SqlDbType.UniqueIdentifier).Value = c.Token;
                    using var r = await replay.ExecuteReaderAsync();
                    if (await r.ReadAsync())
                    {
                        var (id, by) = (r.GetInt32(0), r.GetInt32(1));
                        await r.CloseAsync();
                        if (by != c.UserId)
                            throw new InvalidOperationException("This form was already used.");
                        await tx.CommitAsync();
                        return new EntryResult(id, true, false, false);
                    }
                }

                var customer = await GetAsync(conn, tx, c.CustomerId, lockRow: true) ?? throw new InvalidOperationException("Customer not found.");
                var pending = await GetPendingLockedAsync(conn, tx, c.CustomerId);
                int? visitOwner = null;
                if (c.VisitId.HasValue)
                {
                    using var vc = new SqlCommand("SELECT UserId FROM dbo.FieldVisits WHERE Id = @V AND CustomerId = @C;", conn, tx);
                    vc.Parameters.Add(Int("@V", c.VisitId));
                    vc.Parameters.Add(Int("@C", c.CustomerId));
                    visitOwner = (int?)await vc.ExecuteScalarAsync() ?? throw new InvalidOperationException("Visit not found.");
                }
                var refusal = check(customer, pending, visitOwner);
                if (refusal != null)
                    throw new InvalidOperationException(refusal);

                var statusChanges = c.NewLeadStatus != null && c.NewLeadStatus != customer.LeadStatus;
                int noteId;
                // OUTPUT ... INTO: the table has a trigger
                using (var cmd = new SqlCommand(@"
DECLARE @New TABLE (Id INT);
INSERT INTO dbo.FieldCustomerNotes (CustomerId, VisitId, UserId, NoteType, Text, LeadStatusBefore, LeadStatusAfter, SubmissionToken)
OUTPUT inserted.Id INTO @New (Id)
VALUES (@C, @V, @U, @Type, @Text, @Before, @After, @T);
SELECT Id FROM @New;", conn, tx))
                {
                    cmd.Parameters.Add(Int("@C", c.CustomerId));
                    cmd.Parameters.Add(Int("@V", c.VisitId));
                    cmd.Parameters.Add(Int("@U", c.UserId));
                    cmd.Parameters.Add(Text("@Type", c.NoteType, 12));
                    cmd.Parameters.Add(NText("@Text", c.Text, 1000));
                    cmd.Parameters.Add(Text("@Before", statusChanges ? customer.LeadStatus : null, 15));
                    cmd.Parameters.Add(Text("@After", statusChanges ? c.NewLeadStatus : null, 15));
                    cmd.Parameters.Add("@T", SqlDbType.UniqueIdentifier).Value = c.Token;
                    noteId = (int)(await cmd.ExecuteScalarAsync())!;
                }
                if (statusChanges)
                {
                    await UpdateStatusAsync(conn, tx, c.CustomerId, c.NewLeadStatus!);
                    await InsertEventAsync(conn, tx, c.CustomerId, "StatusChanged", c.UserId, $"{c.NoteType}: {Short(c.Text)}",
                        fromStatus: customer.LeadStatus, toStatus: c.NewLeadStatus);
                }
                var completed = pending != null && c.CompletesFollowUp;
                if (completed)
                    await CloseFollowUpAsync(conn, tx, pending!, c.UserId, null, noteId, $"{c.NoteType}: {Short(c.Text)}");
                var created = false;
                if (c.NextFollowUpDate.HasValue)
                {
                    await InsertFollowUpAsync(conn, tx, c.CustomerId, c.NextFollowUpDate.Value, c.NextFollowUpNote, c.UserId, null, noteId);
                    created = true;
                }
                await tx.CommitAsync();
                return new EntryResult(noteId, false, completed, created);
            }
            catch (SqlException ex) when (ex.Number is 2601 or 2627)
            {
                await SafeRollbackAsync(tx);
                throw new InvalidOperationException("This customer already has a pending follow-up, or the form was submitted twice. Open the customer again.");
            }
            catch
            {
                await SafeRollbackAsync(tx);
                throw;
            }
        }

        private static string Short(string text) => text.Length <= 120 ? text : text[..119] + "…";

        internal static async Task<FieldFollowUp?> GetPendingLockedAsync(SqlConnection conn, SqlTransaction tx, int customerId)
        {
            using (var lockCmd = new SqlCommand("SELECT Id FROM dbo.FieldFollowUps WITH (UPDLOCK, HOLDLOCK) WHERE CustomerId = @C AND Status = 'Pending';", conn, tx))
            {
                lockCmd.Parameters.Add(Int("@C", customerId));
                if (await lockCmd.ExecuteScalarAsync() == null)
                    return null;
            }
            using var cmd = new SqlCommand(FollowUpSelect + " WHERE f.CustomerId = @C AND f.Status = 'Pending';", conn, tx);
            cmd.Parameters.Add(Int("@C", customerId));
            using var r = await cmd.ExecuteReaderAsync();
            return await r.ReadAsync() ? MapFollowUp(r) : null;
        }

        internal static async Task CloseFollowUpAsync(SqlConnection conn, SqlTransaction tx, FieldFollowUp pending, int byUserId, int? visitId, int? noteId,
            string details)
        {
            using (var cmd = new SqlCommand(@"UPDATE dbo.FieldFollowUps SET Status = 'Done', ClosedAtUtc = SYSUTCDATETIME(), ClosedByUserId = @By,
       ClosedByVisitId = @V, ClosedByNoteId = @N WHERE Id = @Id AND Status = 'Pending';", conn, tx))
            {
                cmd.Parameters.Add(Int("@Id", pending.Id));
                cmd.Parameters.Add(Int("@By", byUserId));
                cmd.Parameters.Add(Int("@V", visitId));
                cmd.Parameters.Add(Int("@N", noteId));
                if (await cmd.ExecuteNonQueryAsync() != 1)
                    throw new InvalidOperationException("The follow-up changed meanwhile. Open the customer again.");
            }
            await InsertEventAsync(conn, tx, pending.CustomerId, "FollowUpDone", byUserId, $"Due {pending.DueDate:dd-MMM-yyyy}. {details}",
                visitId: visitId, followUpId: pending.Id);
        }

        internal static async Task<int> InsertFollowUpAsync(SqlConnection conn, SqlTransaction tx, int customerId, DateTime dueDate, string? note,
            int byUserId, int? fromVisitId, int? fromNoteId)
        {
            int id;
            using (var cmd = new SqlCommand(@"
DECLARE @New TABLE (Id INT);
INSERT INTO dbo.FieldFollowUps (CustomerId, DueDate, Note, Status, CreatedFromVisitId, CreatedFromNoteId, CreatedByUserId)
OUTPUT inserted.Id INTO @New (Id)
VALUES (@C, @D, @Note, 'Pending', @V, @N, @By);
SELECT Id FROM @New;", conn, tx))
            {
                cmd.Parameters.Add(Int("@C", customerId));
                cmd.Parameters.Add(Date("@D", dueDate));
                cmd.Parameters.Add(NText("@Note", note, 500));
                cmd.Parameters.Add(Int("@V", fromVisitId));
                cmd.Parameters.Add(Int("@N", fromNoteId));
                cmd.Parameters.Add(Int("@By", byUserId));
                id = (int)(await cmd.ExecuteScalarAsync())!;
            }
            await InsertEventAsync(conn, tx, customerId, "FollowUpSet", byUserId, $"Due {dueDate:dd-MMM-yyyy}" + (note != null ? $": {note}" : ""),
                visitId: fromVisitId, followUpId: id);
            return id;
        }

        // A follow-up without a visit / note (the "set the next date" prompt on the customer page).
        public Task<int> SetFollowUpAsync(int customerId, int byUserId, DateTime dueDate, string? note, Func<FieldCustomer, FieldFollowUp?, string?> check)
            => MutateAsync(customerId, _ => null, async (conn, tx, customer) =>
            {
                var pending = await GetPendingLockedAsync(conn, tx, customerId);
                var refusal = check(customer, pending);
                if (refusal != null)
                    throw new InvalidOperationException(refusal);
                return await InsertFollowUpAsync(conn, tx, customerId, dueDate, note, byUserId, null, null);
            });

        public Task CancelFollowUpAsync(int followUpId, int customerId, int byUserId, string reason, Func<FieldCustomer, FieldFollowUp?, string?> check)
            => MutateAsync(customerId, _ => null, async (conn, tx, customer) =>
            {
                var pending = await GetPendingLockedAsync(conn, tx, customerId);
                var refusal = check(customer, pending?.Id == followUpId ? pending : null);
                if (refusal != null)
                    throw new InvalidOperationException(refusal);
                using (var cmd = new SqlCommand(@"UPDATE dbo.FieldFollowUps SET Status = 'Cancelled', ClosedAtUtc = SYSUTCDATETIME(), ClosedByUserId = @By,
       CancelReason = @R WHERE Id = @Id AND Status = 'Pending';", conn, tx))
                {
                    cmd.Parameters.Add(Int("@Id", followUpId));
                    cmd.Parameters.Add(Int("@By", byUserId));
                    cmd.Parameters.Add(NText("@R", reason, 300));
                    if (await cmd.ExecuteNonQueryAsync() != 1)
                        throw new InvalidOperationException("The follow-up is no longer pending.");
                }
                await InsertEventAsync(conn, tx, customerId, "FollowUpCancelled", byUserId, reason, followUpId: followUpId);
                return 1;
            });
    }
}
