namespace PlantStockManager.Services
{
    // Step 8G: WHEN and WHO the daily production WhatsApp send targets --
    // deliberately separate from SendviseOptions, which is HOW to talk to
    // Sendvise (endpoint/auth/template/language) and still owns the single
    // TestRecipient used only by the manual real-send test
    // (DailyReportWhatsAppSenderManualTests.cs). Enabled/SendTime/TimeZone
    // are bound and validated here so the configuration shape matches the
    // approved design now, but nothing in this codebase reads SendTime/
    // TimeZone yet -- there is still no scheduler/BackgroundService anywhere
    // (see DailyReportWhatsAppSender's own long-standing comment); wiring an
    // actual time-based trigger is a separately-approved future step, not
    // guessed at here.
    public class DailyReportWhatsAppOptions
    {
        public const string SectionName = "DailyReportWhatsApp";

        public bool Enabled { get; set; }
        public string? SendTime { get; set; }
        public string? TimeZone { get; set; }

        // The production recipient list for the daily broadcast send (one
        // Sendvise request per recipient -- see DailyReportWhatsAppSender.
        // SendDailyReportAsync). Real numbers belong only in appsettings.json
        // (gitignored), same pattern as Sendvise.ApiKey/TestRecipient -- left
        // empty here and in appsettings.Development.json.
        public List<string> Recipients { get; set; } = new();
    }
}
