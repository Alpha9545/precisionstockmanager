using Microsoft.Extensions.Options;

namespace PlantStockManager.Services
{
    // Step 8A: the ONLY orchestration point --
    //   DailyReportService -> DailyReportWhatsAppFormatter -> SendviseClient
    // -- and the ONLY way this codebase can currently send a WhatsApp
    // report. There is no scheduler, no BackgroundService, no loop, and no
    // caller anywhere in this codebase that invokes SendTestReportAsync
    // automatically -- it must be triggered manually (e.g. from the gated
    // manual test in tests/PlantStockManager.Tests/
    // DailyReportWhatsAppSenderManualTests.cs, or a future admin action in
    // a later, separately-approved step). Sends to exactly ONE recipient
    // (Sendvise:TestRecipient) -- the 3 real managing persons are not wired
    // in anywhere in this codebase.
    public class DailyReportWhatsAppSender
    {
        private readonly DailyReportService _reportService;
        private readonly SendviseClient _sendviseClient;
        private readonly SendviseOptions _options;

        public DailyReportWhatsAppSender(DailyReportService reportService, SendviseClient sendviseClient, IOptions<SendviseOptions> options)
        {
            _reportService = reportService;
            _sendviseClient = sendviseClient;
            _options = options.Value;
        }

        public async Task<SendviseSendResult> SendTestReportAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_options.TestRecipient))
                return new SendviseSendResult(false, null, null, "Sendvise:TestRecipient is not configured.");

            var report = await _reportService.BuildTodayReportAsync();
            var variables = DailyReportWhatsAppFormatter.FormatVariables(report, DateTime.Now);
            return await _sendviseClient.SendTemplateAsync(_options.TestRecipient, variables, cancellationToken);
        }
    }
}
