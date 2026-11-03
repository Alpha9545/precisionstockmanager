using System.Data;
using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services.FieldWork;
using static PlantStockManager.Data.FieldSql;

namespace PlantStockManager.Data
{
    // Field Work Phase 2 (2026-11-03): visits (GPS check-ins on the employee's OPEN Field Day), their items and photos, and
    // manager edits. One SqlTransaction per change; the customer row, today's Field Day and the pending follow-up are locked
    // (UPDLOCK, HOLDLOCK) before the service's decision is applied. TR_FieldVisits_Guard re-checks the open day.
    public class FieldVisitRepository
    {
        private readonly DatabaseHelper _db;

        public FieldVisitRepository(DatabaseHelper db) => _db = db;

        private static readonly string VisitSelect = $@"
SELECT v.Id, v.CustomerId, v.UserId, {NameOf("u")}, v.FieldDayId, v.CheckInAtUtc, v.Latitude, v.Longitude, v.AccuracyM,
       v.GpsExceptionReason, v.FlagReasons, v.Discussed, v.Notes, v.LeadStatusBefore, v.LeadStatusAfter, {NameOf("m")}, v.ModifiedAtUtc
FROM dbo.FieldVisits v JOIN dbo.IMSUsers u ON u.Id = v.UserId LEFT JOIN dbo.IMSUsers m ON m.Id = v.ModifiedByUserId";

        private static FieldVisit MapVisit(SqlDataReader r) => new()
        {
            Id = r.GetInt32(0), CustomerId = r.GetInt32(1), UserId = r.GetInt32(2), EmployeeName = r.GetString(3), FieldDayId = r.GetInt32(4),
            CheckInAtUtc = r.GetDateTime(5), Latitude = Dec(r, 6), Longitude = Dec(r, 7), AccuracyM = Dec(r, 8), GpsExceptionReason = Str(r, 9),
            FlagReasons = Str(r, 10), Discussed = r.GetString(11), Notes = Str(r, 12), LeadStatusBefore = r.GetString(13),
            LeadStatusAfter = r.GetString(14), ModifiedByName = Str(r, 15), ModifiedAtUtc = Dt(r, 16),
        };

        private static async Task<List<FieldVisit>> LoadAsync(SqlConnection conn, SqlTransaction? tx, string where, SqlParameter p)
        {
            var visits = new List<FieldVisit>();
            using (var cmd = new SqlCommand(VisitSelect + " WHERE " + where + " ORDER BY v.CheckInAtUtc DESC, v.Id DESC;", conn, tx))
            {
                cmd.Parameters.Add(p);
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    visits.Add(MapVisit(r));
            }
            if (visits.Count == 0)
                return visits;
            var byId = visits.ToDictionary(v => v.Id);
            var ids = string.Join(",", byId.Keys);
            using (var cmd = new SqlCommand(@"SELECT VisitId, LineNumber, ItemText, Quantity FROM dbo.FieldVisitItems
WHERE VisitId IN (SELECT CAST(value AS INT) FROM STRING_SPLIT(@Ids, ',')) ORDER BY VisitId, LineNumber;", conn, tx))
            {
                cmd.Parameters.Add(Text("@Ids", ids, 8000));
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    byId[r.GetInt32(0)].Items.Add(new FieldVisitItem { LineNumber = r.GetInt32(1), ItemText = r.GetString(2), Quantity = Dec(r, 3) });
            }
            using (var cmd = new SqlCommand(@"SELECT VisitId, PhotoId FROM dbo.FieldVisitPhotos
WHERE VisitId IN (SELECT CAST(value AS INT) FROM STRING_SPLIT(@Ids, ',')) ORDER BY VisitId, SortOrder;", conn, tx))
            {
                cmd.Parameters.Add(Text("@Ids", ids, 8000));
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    byId[r.GetInt32(0)].PhotoIds.Add(r.GetInt32(1));
            }
            return visits;
        }

        internal static Task<List<FieldVisit>> ListForCustomerAsync(SqlConnection conn, int customerId)
            => LoadAsync(conn, null, "v.CustomerId = @C", Int("@C", customerId));

        public async Task<FieldVisit?> GetAsync(int visitId)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            return (await LoadAsync(conn, null, "v.Id = @Id", Int("@Id", visitId))).FirstOrDefault();
        }

        // Status of the employee's Field Day for today (null = no Start Day yet).
        public async Task<string?> GetTodayStatusAsync(int userId, DateTime todayIst)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand("SELECT Status FROM dbo.FieldDays WHERE UserId = @U AND WorkDate = @D;", conn);
            cmd.Parameters.Add(Int("@U", userId));
            cmd.Parameters.Add(Date("@D", todayIst));
            return await cmd.ExecuteScalarAsync() as string;
        }

        public sealed record PhotoInput(string StoredFileName, int SizeBytes);

        public sealed record CreateCommand(int CustomerId, int UserId, DateTime TodayIst, FieldDayRules.Position? Position, string? GpsExceptionReason,
            string? FlagReasons, string Discussed, string? Notes, IReadOnlyList<FieldCustomerRules.Item> Items, IReadOnlyList<PhotoInput> Photos,
            int ConsentReminderVersion, string? NewLeadStatus, DateTime? NextFollowUpDate, string? NextFollowUpNote, Guid Token);

        // check(customer, pendingFollowUp) on the LOCKED customer; returns null to proceed.
        public async Task<FieldCustomerRepository.EntryResult> CreateAsync(CreateCommand c, Func<FieldCustomer, FieldFollowUp?, string?> check)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            try
            {
                using (var replay = new SqlCommand("SELECT Id, UserId FROM dbo.FieldVisits WITH (UPDLOCK, HOLDLOCK) WHERE SubmissionToken = @T;", conn, tx))
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
                        return new FieldCustomerRepository.EntryResult(id, true, false, false);
                    }
                }

                // the customer, today's open Field Day and the pending follow-up, in that order (same order everywhere)
                using (var lockCmd = new SqlCommand("SELECT Id FROM dbo.FieldCustomers WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id;", conn, tx))
                {
                    lockCmd.Parameters.Add(Int("@Id", c.CustomerId));
                    if (await lockCmd.ExecuteScalarAsync() == null)
                        throw new InvalidOperationException("Customer not found.");
                }
                FieldCustomer customer;
                using (var cmd = new SqlCommand($@"
SELECT c.Id, c.Name, c.LeadStatus, c.AssignedUserId, c.CreatedByUserId, c.IsActive FROM dbo.FieldCustomers c WHERE c.Id = @Id;", conn, tx))
                {
                    cmd.Parameters.Add(Int("@Id", c.CustomerId));
                    using var r = await cmd.ExecuteReaderAsync();
                    await r.ReadAsync();
                    customer = new FieldCustomer
                    {
                        Id = r.GetInt32(0), Name = r.GetString(1), LeadStatus = r.GetString(2), AssignedUserId = r.GetInt32(3),
                        CreatedByUserId = r.GetInt32(4), IsActive = r.GetBoolean(5),
                    };
                }
                int dayId;
                using (var dayCmd = new SqlCommand(@"SELECT Id FROM dbo.FieldDays WITH (UPDLOCK, HOLDLOCK)
WHERE UserId = @U AND WorkDate = @D AND Status = 'Open';", conn, tx))
                {
                    dayCmd.Parameters.Add(Int("@U", c.UserId));
                    dayCmd.Parameters.Add(Date("@D", c.TodayIst));
                    dayId = (int?)await dayCmd.ExecuteScalarAsync()
                        ?? throw new InvalidOperationException("You have no open Field Day today. Do Start Day first, then log the visit.");
                }
                var pending = await FieldCustomerRepository.GetPendingLockedAsync(conn, tx, c.CustomerId);
                var refusal = check(customer, pending);
                if (refusal != null)
                    throw new InvalidOperationException(refusal);

                var newStatus = c.NewLeadStatus ?? customer.LeadStatus;
                int visitId;
                using (var cmd = new SqlCommand(@"
DECLARE @New TABLE (Id INT);
INSERT INTO dbo.FieldVisits (CustomerId, UserId, FieldDayId, Latitude, Longitude, AccuracyM, GpsExceptionReason, FlagReasons, Discussed, Notes,
                             LeadStatusBefore, LeadStatusAfter, SubmissionToken)
OUTPUT inserted.Id INTO @New (Id)
VALUES (@C, @U, @Day, @Lat, @Lng, @Acc, @Reason, @Flags, @Discussed, @Notes, @Before, @After, @T);
SELECT Id FROM @New;", conn, tx))
                {
                    cmd.Parameters.Add(Int("@C", c.CustomerId));
                    cmd.Parameters.Add(Int("@U", c.UserId));
                    cmd.Parameters.Add(Int("@Day", dayId));
                    cmd.Parameters.Add(Decimal("@Lat", c.Position?.Latitude, 9, 6));
                    cmd.Parameters.Add(Decimal("@Lng", c.Position?.Longitude, 9, 6));
                    cmd.Parameters.Add(Decimal("@Acc", c.Position?.AccuracyM, 9, 1));
                    cmd.Parameters.Add(NText("@Reason", c.GpsExceptionReason, 300));
                    cmd.Parameters.Add(NText("@Flags", c.FlagReasons, 500));
                    cmd.Parameters.Add(NText("@Discussed", c.Discussed, 1000));
                    cmd.Parameters.Add(NText("@Notes", c.Notes, 2000));
                    cmd.Parameters.Add(Text("@Before", customer.LeadStatus, 15));
                    cmd.Parameters.Add(Text("@After", newStatus, 15));
                    cmd.Parameters.Add("@T", SqlDbType.UniqueIdentifier).Value = c.Token;
                    visitId = (int)(await cmd.ExecuteScalarAsync())!;
                }
                await InsertItemsAsync(conn, tx, visitId, c.Items);
                for (var i = 0; i < c.Photos.Count; i++)
                {
                    var photoId = await InsertPhotoAsync(conn, tx, c.UserId, "VisitPhoto", c.Photos[i].StoredFileName, c.Photos[i].SizeBytes,
                        c.Position?.Latitude, c.Position?.Longitude, c.Position?.AccuracyM, c.ConsentReminderVersion);
                    using var cmd = new SqlCommand("INSERT INTO dbo.FieldVisitPhotos (VisitId, PhotoId, SortOrder) VALUES (@V, @P, @S);", conn, tx);
                    cmd.Parameters.Add(Int("@V", visitId));
                    cmd.Parameters.Add(Int("@P", photoId));
                    cmd.Parameters.Add(Int("@S", i + 1));
                    await cmd.ExecuteNonQueryAsync();
                }
                if (newStatus != customer.LeadStatus)
                {
                    await FieldCustomerRepository.UpdateStatusAsync(conn, tx, c.CustomerId, newStatus);
                    await InsertEventAsync(conn, tx, c.CustomerId, "StatusChanged", c.UserId, "At a visit", visitId: visitId,
                        fromStatus: customer.LeadStatus, toStatus: newStatus);
                }
                var completed = pending != null;   // a visit always completes the pending follow-up
                if (completed)
                    await FieldCustomerRepository.CloseFollowUpAsync(conn, tx, pending!, c.UserId, visitId, null, "Visit");
                var created = false;
                if (c.NextFollowUpDate.HasValue)
                {
                    await FieldCustomerRepository.InsertFollowUpAsync(conn, tx, c.CustomerId, c.NextFollowUpDate.Value, c.NextFollowUpNote, c.UserId, visitId, null);
                    created = true;
                }
                await tx.CommitAsync();
                return new FieldCustomerRepository.EntryResult(visitId, false, completed, created);
            }
            catch (SqlException ex) when (ex.Number is 2601 or 2627)
            {
                await SafeRollbackAsync(tx);
                throw new InvalidOperationException("This visit was already saved (the form was submitted twice). Open the customer.");
            }
            catch
            {
                await SafeRollbackAsync(tx);
                throw;
            }
        }

        private static async Task InsertItemsAsync(SqlConnection conn, SqlTransaction tx, int visitId, IReadOnlyList<FieldCustomerRules.Item> items)
        {
            for (var i = 0; i < items.Count; i++)
            {
                using var cmd = new SqlCommand("INSERT INTO dbo.FieldVisitItems (VisitId, LineNumber, ItemText, Quantity) VALUES (@V, @L, @T, @Q);", conn, tx);
                cmd.Parameters.Add(Int("@V", visitId));
                cmd.Parameters.Add(Int("@L", i + 1));
                cmd.Parameters.Add(NText("@T", items[i].Text, 200));
                cmd.Parameters.Add(Decimal("@Q", items[i].Quantity, 10, 2));
                await cmd.ExecuteNonQueryAsync();
            }
        }

        // Manager correction of the text and items. The check-in (time, GPS, customer, day, photos) never changes
        // (TR_FieldVisits_Guard); the old values are kept in dbo.FieldCustomerEvents ('VisitEdited').
        public async Task EditAsync(int visitId, int byUserId, string discussed, string? notes, IReadOnlyList<FieldCustomerRules.Item> items,
            Func<FieldVisit, string?> check, Func<FieldVisit, string> describeChanges)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            try
            {
                using (var lockCmd = new SqlCommand("SELECT Id FROM dbo.FieldVisits WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id;", conn, tx))
                {
                    lockCmd.Parameters.Add(Int("@Id", visitId));
                    if (await lockCmd.ExecuteScalarAsync() == null)
                        throw new InvalidOperationException("Visit not found.");
                }
                var old = (await LoadAsync(conn, tx, "v.Id = @Id", Int("@Id", visitId))).Single();
                var refusal = check(old);
                if (refusal != null)
                    throw new InvalidOperationException(refusal);
                var changes = describeChanges(old);
                if (changes.Length == 0)
                {
                    await tx.CommitAsync();
                    return;
                }
                using (var cmd = new SqlCommand(@"UPDATE dbo.FieldVisits SET Discussed = @D, Notes = @N, ModifiedByUserId = @By, ModifiedAtUtc = SYSUTCDATETIME()
WHERE Id = @Id;", conn, tx))
                {
                    cmd.Parameters.Add(Int("@Id", visitId));
                    cmd.Parameters.Add(NText("@D", discussed, 1000));
                    cmd.Parameters.Add(NText("@N", notes, 2000));
                    cmd.Parameters.Add(Int("@By", byUserId));
                    await cmd.ExecuteNonQueryAsync();
                }
                using (var cmd = new SqlCommand("DELETE FROM dbo.FieldVisitItems WHERE VisitId = @Id;", conn, tx))
                {
                    cmd.Parameters.Add(Int("@Id", visitId));
                    await cmd.ExecuteNonQueryAsync();
                }
                await InsertItemsAsync(conn, tx, visitId, items);
                await InsertEventAsync(conn, tx, old.CustomerId, "VisitEdited", byUserId, changes, visitId: visitId);
                await tx.CommitAsync();
            }
            catch
            {
                await SafeRollbackAsync(tx);
                throw;
            }
        }
    }
}
