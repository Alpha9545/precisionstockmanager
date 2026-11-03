using System.Data;
using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services.FieldWork;
using static PlantStockManager.Data.FieldSql;

namespace PlantStockManager.Data
{
    // Field Work Phase 3 (2026-11-03): read-only queries for day routes and the team view. No table of its own -- a route
    // is computed from dbo.FieldDays (start / end) and dbo.FieldVisits (check-ins, Phase 2). Callers check
    // FieldCustomerSchema before asking for visits; visibility is decided by FieldRouteService.
    public class FieldRouteRepository
    {
        private readonly DatabaseHelper _db;

        public FieldRouteRepository(DatabaseHelper db) => _db = db;

        public Task<bool> VisitsReadyAsync() => FieldCustomerSchema.ReadyAsync(_db);

        private static FieldRouteRules.PointInput MapVisit(SqlDataReader r, int offset = 0) => new(
            FieldRouteRules.PointKind.Visit, r.GetDateTime(offset + 1), Dec(r, offset + 2), Dec(r, offset + 3), Dec(r, offset + 4), Str(r, offset + 5),
            r.GetInt32(offset + 0), r.GetInt32(offset + 6), r.GetString(offset + 7), r.GetString(offset + 8));

        private const string VisitColumns =
            "v.Id, v.CheckInAtUtc, v.Latitude, v.Longitude, v.AccuracyM, v.GpsExceptionReason, v.CustomerId, c.Name, v.Discussed";

        // The visit check-ins of one Field Day, with customer name and what was discussed (map popups).
        public async Task<List<FieldRouteRules.PointInput>> GetDayVisitsAsync(int dayId)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand($@"SELECT {VisitColumns}
FROM dbo.FieldVisits v JOIN dbo.FieldCustomers c ON c.Id = v.CustomerId
WHERE v.FieldDayId = @D ORDER BY v.CheckInAtUtc, v.Id;", conn);
            cmd.Parameters.Add(Int("@D", dayId));
            var list = new List<FieldRouteRules.PointInput>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(MapVisit(r));
            return list;
        }

        public sealed record TeamMember(int UserId, string Name, bool IsActive, FieldDay? Day);

        // Every ACTIVE field employee (FieldWork.Day through a role), plus anyone else who has a Field Day on that date,
        // with their day (or none).
        public async Task<List<TeamMember>> GetTeamAsync(DateTime workDate)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            var sql = $@"
SELECT u.Id, {NameOf("u")}, CAST(ISNULL(u.IsActive, 0) AS BIT),
       d.Id, d.Status, d.VehicleType, d.StartedAtUtc, d.StartLatitude, d.StartLongitude, d.StartAccuracyM, d.StartKm, d.StartExceptionReason,
       d.EndedAtUtc, d.EndLatitude, d.EndLongitude, d.EndAccuracyM, d.EndKm, d.EndExceptionReason, d.AutoClosedAtUtc, d.ReviewStatus,
       d.StartFlagReasons, d.EndFlagReasons
FROM dbo.IMSUsers u
LEFT JOIN dbo.FieldDays d ON d.UserId = u.Id AND d.WorkDate = @D
WHERE d.Id IS NOT NULL
   OR (u.IsActive = 1 AND EXISTS (SELECT 1 FROM dbo.UserRoles ur JOIN dbo.RolePermissions rp ON rp.RoleId = ur.RoleId
                                  JOIN dbo.Permissions p ON p.Id = rp.PermissionId WHERE ur.UserId = u.Id AND p.Code = N'FieldWork.Day'))
ORDER BY 2;";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.Add(Date("@D", workDate));
            var list = new List<TeamMember>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                FieldDay? day = null;
                if (!r.IsDBNull(3))
                    day = new FieldDay
                    {
                        Id = r.GetInt32(3), UserId = r.GetInt32(0), EmployeeName = r.GetString(1), WorkDate = workDate.Date, Status = r.GetString(4),
                        VehicleType = r.GetString(5), StartedAtUtc = r.GetDateTime(6), StartLatitude = Dec(r, 7), StartLongitude = Dec(r, 8),
                        StartAccuracyM = Dec(r, 9), StartKm = r.GetDecimal(10), StartExceptionReason = Str(r, 11), EndedAtUtc = Dt(r, 12),
                        EndLatitude = Dec(r, 13), EndLongitude = Dec(r, 14), EndAccuracyM = Dec(r, 15), EndKm = Dec(r, 16),
                        EndExceptionReason = Str(r, 17), AutoClosedAtUtc = Dt(r, 18), ReviewStatus = r.GetString(19),
                        StartFlagReasons = Str(r, 20), EndFlagReasons = Str(r, 21),
                    };
                list.Add(new TeamMember(r.GetInt32(0), r.GetString(1), r.GetBoolean(2), day));
            }
            return list;
        }

        // The visit check-ins of every Field Day on one date, keyed by day id.
        public async Task<Dictionary<int, List<FieldRouteRules.PointInput>>> GetVisitsOnDateAsync(DateTime workDate)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand($@"SELECT v.FieldDayId, {VisitColumns}
FROM dbo.FieldVisits v JOIN dbo.FieldDays d ON d.Id = v.FieldDayId JOIN dbo.FieldCustomers c ON c.Id = v.CustomerId
WHERE d.WorkDate = @D ORDER BY v.FieldDayId, v.CheckInAtUtc, v.Id;", conn);
            cmd.Parameters.Add(Date("@D", workDate));
            var map = new Dictionary<int, List<FieldRouteRules.PointInput>>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var dayId = r.GetInt32(0);
                if (!map.TryGetValue(dayId, out var list))
                    map[dayId] = list = new List<FieldRouteRules.PointInput>();
                list.Add(MapVisit(r, 1));
            }
            return map;
        }
    }
}
