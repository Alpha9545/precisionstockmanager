using System.Data;
using Microsoft.Data.SqlClient;
using PlantStockManager.Services;

namespace PlantStockManager.Data
{
    // Correction #6 -- the SQL of the "Booking By" filter, shared by BookingRepository (dbo.Bookings) and
    // PottedPlantBookingRepository (dbo.PottedPlantBookings) so both list pages behave identically.
    // Everything the user chooses reaches the database only as a PARAMETER; the column names come from the callers.
    public static class BookedBySql
    {
        // " AND (...)" -- appended to a WHERE. With no filter (@ByKind IS NULL) it is true for every row, so the list is
        // exactly what it was before the filter existed.
        public static string Clause(string idColumn, string otherColumn) => $@"
  AND (@ByKind IS NULL
       OR (@ByKind = 'U' AND {idColumn} = @ByUserId)
       OR (@ByKind = 'O' AND LTRIM(RTRIM({otherColumn})) = @ByOther)
       OR (@ByKind = 'X' AND ({idColumn} IS NULL OR {idColumn} = 0) AND ({otherColumn} IS NULL OR LTRIM(RTRIM({otherColumn})) = ''))) ";

        public static void AddParameters(SqlCommand cmd, BookedByFilter? filter)
        {
            filter ??= BookedByFilter.All;
            cmd.Parameters.Add("@ByKind", SqlDbType.NVarChar, 1).Value = filter.Kind switch
            {
                BookedByFilter.FilterKind.User => "U",
                BookedByFilter.FilterKind.Other => "O",
                BookedByFilter.FilterKind.NotRecorded => "X",
                _ => DBNull.Value
            };
            cmd.Parameters.Add("@ByUserId", SqlDbType.Int).Value = filter.Kind == BookedByFilter.FilterKind.User ? filter.UserId : DBNull.Value;
            cmd.Parameters.Add("@ByOther", SqlDbType.NVarChar, BookedByFilter.MaxOtherLength).Value = filter.Kind == BookedByFilter.FilterKind.Other ? filter.OtherName! : DBNull.Value;
        }

        // The dropdown choices: the people / names that ACTUALLY occur on bookings of the table, straight from the booking
        // rows (so historical bookings whose user is inactive or gone are still selectable, and no unrelated user is ever
        // listed). `scopeSql` limits the rows to what the caller may see (e.g. the Area rule); it may use @Allowed.
        // Users first (A-Z), then free-text names, then "Not recorded" when such bookings exist.
        public static async Task<List<BookedByFilter.Option>> LoadOptionsAsync(
            SqlConnection conn, string tableAndAlias, string alias, string? scopeSql, string? allowedAreas)
        {
            var where = string.IsNullOrWhiteSpace(scopeSql) ? "1 = 1" : scopeSql;
            SqlCommand Command(string sql)
            {
                var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.Add("@Allowed", SqlDbType.NVarChar, -1).Value = (object?)allowedAreas ?? DBNull.Value;
                return cmd;
            }

            var users = new List<(int Id, string Label)>();
            using (var cmd = Command($@"
SELECT DISTINCT {alias}.BookedById, u.Name, u.IsActive
FROM {tableAndAlias}
LEFT JOIN dbo.IMSUsers u ON u.Id = {alias}.BookedById
WHERE {alias}.BookedById > 0 AND {where}"))
            using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync())
                    users.Add((r.GetInt32(0), BookedByFilter.UserLabel(r.GetInt32(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetBoolean(2))));

            var others = new List<string>();
            using (var cmd = Command($@"
SELECT DISTINCT LTRIM(RTRIM({alias}.BookedByOther))
FROM {tableAndAlias}
WHERE LTRIM(RTRIM(ISNULL({alias}.BookedByOther, ''))) <> '' AND {where}"))
            using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync())
                    others.Add(r.GetString(0));

            bool hasNotRecorded;
            using (var cmd = Command($@"
SELECT CASE WHEN EXISTS (SELECT 1 FROM {tableAndAlias}
                         WHERE ({alias}.BookedById IS NULL OR {alias}.BookedById = 0) AND LTRIM(RTRIM(ISNULL({alias}.BookedByOther, ''))) = '' AND {where})
            THEN 1 ELSE 0 END"))
                hasNotRecorded = Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 1;

            var options = users.OrderBy(u => u.Label, StringComparer.CurrentCultureIgnoreCase).ThenBy(u => u.Id)
                .Select(u => new BookedByFilter.Option(BookedByFilter.UserValue(u.Id), u.Label)).ToList();
            options.AddRange(others.OrderBy(o => o, StringComparer.CurrentCultureIgnoreCase)
                .Select(o => new BookedByFilter.Option(BookedByFilter.OtherValue(o), BookedByFilter.OtherLabel(o))));
            if (hasNotRecorded)
                options.Add(new BookedByFilter.Option(BookedByFilter.NotRecordedValue, BookedByFilter.NotRecordedLabel));
            return options;
        }
    }
}
