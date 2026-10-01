namespace PlantStockManager.Services
{
    // Step 8A: Sendvise WhatsApp options, bound from the "Sendvise"
    // configuration section -- same IOptions pattern already used by
    // SecurityOptions/SeedlingWorkflowOptions. ApiKey and TestRecipient are
    // deliberately left blank here and in appsettings.Development.json (the
    // TRACKED file) -- real values belong only in appsettings.json, which
    // this project's own .gitignore already excludes from source control
    // (see SecurityOptions.cs's own comment: "appsettings.json is not
    // committed to the repository"). This class itself never logs or
    // exposes ApiKey; nothing outside SendviseClient ever reads it.
    public class SendviseOptions
    {
        public const string SectionName = "Sendvise";

        public string BaseUrl { get; set; } = "https://app.sendvise.com/api/v1/external";
        public string TemplateName { get; set; } = "daily_report_plantmanager";
        public string Language { get; set; } = "en";

        // Step 8A: exactly ONE configurable recipient. The 3 real managing
        // persons' numbers are explicitly NOT wired in anywhere in this
        // codebase yet -- a later, separately-approved step.
        public string? TestRecipient { get; set; }

        public string? ApiKey { get; set; }
    }
}
