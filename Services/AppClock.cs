namespace PlantStockManager.Services
{
    // Business date/time in India (Asia/Kolkata, UTC+05:30, no daylight saving),
    // independent of the server's own time zone -- a VPS clock set to UTC would
    // otherwise make "today" wrong between 00:00 and 05:30 IST.
    // Used by the 2026-10-06 Labour Attendance module; the older modules still
    // use DateTime.Today and are deliberately left unchanged.
    public static class AppClock
    {
        public static readonly TimeZoneInfo IndiaTimeZone = FindIndiaTimeZone();

        public static DateTime Now => ToIndiaTime(DateTime.UtcNow);

        public static DateTime Today => Now.Date;

        // A UTC timestamp (database DATETIME2 written with SYSUTCDATETIME()) as India time.
        public static DateTime ToIndiaTime(DateTime utc)
            => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), IndiaTimeZone);

        // An India wall-clock time (e.g. the midnight that starts an India date) as UTC --
        // the bound to compare against a SYSUTCDATETIME() column.
        public static DateTime IndiaToUtc(DateTime india)
            => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(india, DateTimeKind.Unspecified), IndiaTimeZone);

        // An India wall-clock time as the server's own local time -- the bound to compare
        // against a GETDATE() column (e.g. dbo.Bookings.BookingDate). Identity on a server
        // that itself runs on India time.
        public static DateTime IndiaToServerLocal(DateTime india)
            => TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(india, DateTimeKind.Unspecified), IndiaTimeZone, TimeZoneInfo.Local);

        // Minutes to add to a UTC timestamp to get India time (+330), for SQL day bucketing.
        public static int IndiaOffsetMinutes => (int)IndiaTimeZone.BaseUtcOffset.TotalMinutes;

        private static TimeZoneInfo FindIndiaTimeZone()
        {
            foreach (var id in new[] { "Asia/Kolkata", "India Standard Time" })
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
                catch (TimeZoneNotFoundException) { }
                catch (InvalidTimeZoneException) { }
            }
            // India has had a fixed +05:30 offset since 1945.
            return TimeZoneInfo.CreateCustomTimeZone("India Standard Time", TimeSpan.FromHours(5.5), "India Standard Time", "India Standard Time");
        }
    }
}
