namespace PlantStockManager.Services.FieldWork
{
    // Configuration section "FieldWork" (appsettings.json). Every value has a safe default.
    public class FieldWorkOptions
    {
        public const string SectionName = "FieldWork";

        // A GPS fix less precise than this is refused unless the employee gives a reason (then the day is flagged).
        public decimal MaxGpsAccuracyMeters { get; set; } = 100m;

        // A day is flagged when the straight-line GPS distance from start to end is MORE than the odometer distance plus
        // this slack -- physically impossible. (A round trip has ~0 km straight-line distance, so "less" proves nothing.)
        public decimal DistanceMismatchSlackKm { get; set; } = 2m;

        // Folder for the meter photos. Empty = <LocalApplicationData>/PlantStockManager/FieldWorkPhotos -- outside wwwroot
        // and outside the publish folder. Production should set an explicit persistent folder and back it up.
        public string? PhotoRoot { get; set; }

        public long MaxPhotoBytes { get; set; } = 2L * 1024 * 1024;

        // The midnight auto-close job (FieldDayAutoCloseService).
        public bool AutoCloseEnabled { get; set; } = true;
        public int AutoCloseIntervalMinutes { get; set; } = 5;

        // Raise when the consent text changes: every field employee is asked again.
        public int ConsentVersion { get; set; } = 1;

        // ---- Phase 2 (customers / visits) ----------------------------------------------------------------------------
        // Live photos per visit (each at most MaxPhotoBytes). Capped at MaxPhotosPerVisitLimit: the visit form's request
        // size limit is sized for it.
        public const int MaxPhotosPerVisitLimit = 10;
        public int MaxPhotosPerVisit { get; set; } = 5;

        // Version of the "Take the customer's permission before a photo" reminder shown on the customer / visit capture
        // screens; stored on each customer / visit photo (dbo.FieldPhotos.ConsentReminderVersion). Raise when the text changes.
        public int CustomerPhotoConsentVersion { get; set; } = 1;

        public int CustomerPageSize { get; set; } = 25;

        public int EffectiveMaxPhotosPerVisit => Math.Clamp(MaxPhotosPerVisit, 0, MaxPhotosPerVisitLimit);

        // ---- Phase 3 (routes / map) ----------------------------------------------------------------------------------
        // A leg between two GPS points is shown as suspicious when, after subtracting both points' GPS accuracy, it is at
        // least SuspiciousLegMinKm long AND its implied speed is above SuspiciousLegSpeedKmh (a warning, never stored).
        public decimal SuspiciousLegSpeedKmh { get; set; } = 80m;
        public decimal SuspiciousLegMinKm { get; set; } = 2m;

        // Odometer much longer than the straight-line route is shown as INFO only (roads wind). Both must hold:
        // odometer >= route x OdometerRouteInfoRatio AND odometer - route >= OdometerRouteInfoExtraKm.
        // (Kept in config so it can become a flag later.)
        public decimal OdometerRouteInfoRatio { get; set; } = 3m;
        public decimal OdometerRouteInfoExtraKm { get; set; } = 20m;

        // Map tiles (Leaflet). The public OpenStreetMap tiles need the attribution; switch provider here if needed.
        public string MapTileUrl { get; set; } = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";
        public string MapAttribution { get; set; } = "&copy; <a href=\"https://www.openstreetmap.org/copyright\">OpenStreetMap</a> contributors";
        public int MapMaxZoom { get; set; } = 19;

        public string ResolvePhotoRoot()
            => Path.GetFullPath(!string.IsNullOrWhiteSpace(PhotoRoot)
                ? PhotoRoot
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlantStockManager", "FieldWorkPhotos"));
    }
}
