using System.Data;
using Microsoft.Data.SqlClient;

namespace PlantStockManager.Data
{
    // Small helpers shared by the Field Work Phase 2 repositories (typed parameters, safe rollback).
    internal static class FieldSql
    {
        public const string UserName = "ISNULL(NULLIF(LTRIM(RTRIM({0}.Name)), N''), {0}.Username)";

        public static string NameOf(string alias) => string.Format(UserName, alias);

        public static SqlParameter Decimal(string name, decimal? value, byte precision, byte scale)
            => new(name, SqlDbType.Decimal) { Precision = precision, Scale = scale, Value = (object?)value ?? DBNull.Value };

        public static SqlParameter NText(string name, string? value, int size)
            => new(name, SqlDbType.NVarChar, size) { Value = (object?)value ?? DBNull.Value };

        public static SqlParameter Text(string name, string? value, int size)
            => new(name, SqlDbType.VarChar, size) { Value = (object?)value ?? DBNull.Value };

        public static SqlParameter Int(string name, int? value)
            => new(name, SqlDbType.Int) { Value = (object?)value ?? DBNull.Value };

        public static SqlParameter Date(string name, DateTime? value)
            => new(name, SqlDbType.Date) { Value = (object?)value?.Date ?? DBNull.Value };

        public static decimal? Dec(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDecimal(i);
        public static int? IntOrNull(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
        public static DateTime? Dt(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDateTime(i);
        public static string? Str(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

        public static async Task SafeRollbackAsync(SqlTransaction tx)
        {
            try
            {
                if (tx.Connection != null)
                    await tx.RollbackAsync();
            }
            catch (InvalidOperationException) { }
            catch (SqlException) { }
        }

        public static async Task<int> InsertPhotoAsync(SqlConnection conn, SqlTransaction tx, int ownerUserId, string purpose, string storedFileName,
            int sizeBytes, decimal? lat, decimal? lng, decimal? acc, int? consentReminderVersion)
        {
            const string sql = @"
INSERT INTO dbo.FieldPhotos (OwnerUserId, Purpose, StoredFileName, ContentType, SizeBytes, Latitude, Longitude, AccuracyM, ConsentReminderVersion)
OUTPUT inserted.Id
VALUES (@U, @Purpose, @File, 'image/jpeg', @Size, @Lat, @Lng, @Acc, @Consent);";
            using var cmd = new SqlCommand(sql, conn, tx);
            cmd.Parameters.Add(Int("@U", ownerUserId));
            cmd.Parameters.Add(Text("@Purpose", purpose, 20));
            cmd.Parameters.Add(Text("@File", storedFileName, 64));
            cmd.Parameters.Add(Int("@Size", sizeBytes));
            cmd.Parameters.Add(Decimal("@Lat", lat, 9, 6));
            cmd.Parameters.Add(Decimal("@Lng", lng, 9, 6));
            cmd.Parameters.Add(Decimal("@Acc", acc, 9, 1));
            cmd.Parameters.Add(Int("@Consent", consentReminderVersion));
            return (int)(await cmd.ExecuteScalarAsync())!;
        }

        public static async Task InsertEventAsync(SqlConnection conn, SqlTransaction tx, int customerId, string type, int byUserId,
            string? details = null, int? visitId = null, int? followUpId = null, string? fromStatus = null, string? toStatus = null,
            int? fromUserId = null, int? toUserId = null)
        {
            const string sql = @"
INSERT INTO dbo.FieldCustomerEvents (CustomerId, VisitId, FollowUpId, EventType, FromStatus, ToStatus, FromUserId, ToUserId, ByUserId, Details)
VALUES (@C, @V, @F, @T, @FromS, @ToS, @FromU, @ToU, @By, @D);";
            using var cmd = new SqlCommand(sql, conn, tx);
            cmd.Parameters.Add(Int("@C", customerId));
            cmd.Parameters.Add(Int("@V", visitId));
            cmd.Parameters.Add(Int("@F", followUpId));
            cmd.Parameters.Add(Text("@T", type, 20));
            cmd.Parameters.Add(Text("@FromS", fromStatus, 15));
            cmd.Parameters.Add(Text("@ToS", toStatus, 15));
            cmd.Parameters.Add(Int("@FromU", fromUserId));
            cmd.Parameters.Add(Int("@ToU", toUserId));
            cmd.Parameters.Add(Int("@By", byUserId));
            cmd.Parameters.Add(NText("@D", details is { Length: > 2000 } ? details[..2000] : details, 2000));
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
