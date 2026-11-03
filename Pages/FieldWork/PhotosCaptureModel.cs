namespace PlantStockManager.Pages.FieldWork
{
    // Settings of the shared GPS + live photos block (_PhotosCapture.cshtml) on Add Customer and Log Visit.
    public sealed record PhotosCaptureModel(int MaxPhotos, int ConsentReminderVersion, bool GpsRequired, string? Latitude, string? Longitude,
        string? AccuracyM, string? GpsExceptionReason, string PhotoLabel);
}
