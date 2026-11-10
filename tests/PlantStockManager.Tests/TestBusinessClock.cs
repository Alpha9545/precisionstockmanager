using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // 2026-11-09: a BusinessClock fixed at a chosen instant (null = the real clock), so "today" rules can be tested on a
    // given India date and either side of midnight India time.
    public sealed class TestBusinessClock : BusinessClock
    {
        public DateTime? FixedUtc { get; set; }

        public override DateTime UtcNow => FixedUtc ?? DateTime.UtcNow;

        // An India wall-clock instant, e.g. new DateTime(2001, 3, 2, 23, 59, 59).
        public void AtIndia(DateTime india) => FixedUtc = AppClock.IndiaToUtc(india);

        public void Real() => FixedUtc = null;
    }
}
