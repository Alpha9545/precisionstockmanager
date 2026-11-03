namespace PlantStockManager.Models
{
    // Field Work Phase 1 (2026-11-03): dbo.FieldDays / dbo.FieldDayEvents rows. Times are UTC as stored
    // (SYSUTCDATETIME()); pages show them in India time through AppClock.ToIndiaTime.
    public class FieldDay
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public string EmployeeName { get; set; } = "";
        public DateTime WorkDate { get; set; }
        public string Status { get; set; } = "";
        public string VehicleType { get; set; } = "";

        public DateTime StartedAtUtc { get; set; }
        public decimal? StartLatitude { get; set; }
        public decimal? StartLongitude { get; set; }
        public decimal? StartAccuracyM { get; set; }
        public decimal StartKm { get; set; }
        public int? StartPhotoId { get; set; }
        public string? StartExceptionReason { get; set; }
        public string? StartFlagReasons { get; set; }

        public DateTime? EndedAtUtc { get; set; }
        public decimal? EndLatitude { get; set; }
        public decimal? EndLongitude { get; set; }
        public decimal? EndAccuracyM { get; set; }
        public decimal? EndKm { get; set; }
        public int? EndPhotoId { get; set; }
        public string? EndExceptionReason { get; set; }
        public string? EndFlagReasons { get; set; }
        public DateTime? AutoClosedAtUtc { get; set; }
        public decimal? GpsDistanceKm { get; set; }

        public string ReviewStatus { get; set; } = "";
        public int? ReviewedByUserId { get; set; }
        public string? ReviewedByName { get; set; }
        public DateTime? ReviewedAtUtc { get; set; }
        public string? ReviewComment { get; set; }

        public decimal? OdometerKm => EndKm.HasValue ? EndKm.Value - StartKm : null;

        public string? FlagReasons => string.Join("; ", new[] { StartFlagReasons, EndFlagReasons }.Where(f => !string.IsNullOrWhiteSpace(f))) is { Length: > 0 } s ? s : null;
    }

    public class FieldDayEvent
    {
        public int Id { get; set; }
        public string EventType { get; set; } = "";
        public string? ByName { get; set; }
        public DateTime AtUtc { get; set; }
        public string? Details { get; set; }
    }

    public class FieldPhotoRecord
    {
        public int Id { get; set; }
        public int OwnerUserId { get; set; }
        public string StoredFileName { get; set; } = "";
        public string ContentType { get; set; } = "";
    }

    public sealed record FieldEmployeeOption(int UserId, string Name);
}
