namespace PlantStockManager.Services
{
    // 2026-11-09: the India business date as an injectable service (registered as a singleton in Program.cs), so a
    // page whose rule depends on "today" -- Daily Labour Attendance: non-administrators may enter only today -- can be
    // tested at a fixed instant, including either side of midnight India time. Production always uses the real UTC
    // clock and AppClock's India conversion; the browser's date is never the authority.
    public class BusinessClock
    {
        public virtual DateTime UtcNow => DateTime.UtcNow;

        public DateTime Today => AppClock.IndiaDateOf(UtcNow);
    }
}
