using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PlantStockManager.Services
{
    // Step 8A/8G: the ONLY orchestration point --
    //   DailyReportService -> DailyReportWhatsAppFormatter -> SendviseClient
    // -- and the ONLY way this codebase can currently send a WhatsApp
    // report. There is still no scheduler, no BackgroundService, no loop,
    // and no caller anywhere in this codebase that invokes either send
    // method automatically -- both must be triggered manually today (e.g.
    // the gated manual test in tests/PlantStockManager.Tests/
    // DailyReportWhatsAppSenderManualTests.cs, or a future admin action /
    // scheduler in a later, separately-approved step).
    //
    // Step 8G (this change): added SendDailyReportAsync, the PRODUCTION path
    // that sends the same report to every configured DailyReportWhatsApp:
    // Recipients entry (one independent Sendvise request per recipient).
    // SendTestReportAsync (the manual-test path, Sendvise:TestRecipient) is
    // UNCHANGED -- it remains the one dedicated, single-recipient safety
    // valve for real-send testing; SENDVISE_MANUAL_SEND_TEST=1 only ever
    // exercises this path, never SendDailyReportAsync. No phone number is
    // ever logged from this class -- only each recipient's 1-based position
    // ("recipient 1", "recipient 2", ...).
    public class DailyReportWhatsAppSender
    {
        private readonly DailyReportService _reportService;
        private readonly SendviseClient _sendviseClient;
        private readonly SendviseOptions _sendviseOptions;
        private readonly DailyReportWhatsAppOptions _recipientOptions;
        private readonly ILogger<DailyReportWhatsAppSender> _logger;

        public DailyReportWhatsAppSender(
            DailyReportService reportService,
            SendviseClient sendviseClient,
            IOptions<SendviseOptions> sendviseOptions,
            IOptions<DailyReportWhatsAppOptions> recipientOptions,
            ILogger<DailyReportWhatsAppSender> logger)
        {
            _reportService = reportService;
            _sendviseClient = sendviseClient;
            _sendviseOptions = sendviseOptions.Value;
            _recipientOptions = recipientOptions.Value;
            _logger = logger;
        }

        public async Task<SendviseSendResult> SendTestReportAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_sendviseOptions.TestRecipient))
                return new SendviseSendResult(false, null, null, "Sendvise:TestRecipient is not configured.");

            var report = await _reportService.BuildTodayReportAsync();
            var variables = DailyReportWhatsAppFormatter.FormatVariables(report, DateTime.Now);
            return await _sendviseClient.SendTemplateAsync(_sendviseOptions.TestRecipient, variables, cancellationToken);
        }

        // The production path. Validates DailyReportWhatsApp:Recipients FIRST
        // (pure, no DB) so a misconfiguration (disabled, empty, or containing
        // a blank entry) never costs a DailyReport build or a Sendvise call.
        // STRICT validation: an empty list is a config error; a list that
        // contains even one blank/whitespace entry is ALSO a config error --
        // this never silently sends to fewer recipients than were configured
        // just because one entry was blank. The only normalization applied
        // is trimming + collapsing EXACT duplicate numbers (configuring the
        // same number twice is not an error, just redundant -- it is still
        // sent exactly once, never twice). When validation passes, the
        // report is built and the 16 variables are formatted EXACTLY ONCE
        // here, then handed to SendToRecipientsAsync to broadcast unchanged.
        public async Task<DailyReportBroadcastResult> SendDailyReportAsync(CancellationToken cancellationToken = default)
        {
            if (!_recipientOptions.Enabled)
            {
                _logger.LogInformation("Daily Report WhatsApp send skipped: DailyReportWhatsApp:Enabled is false.");
                return DailyReportBroadcastResult.Empty;
            }

            var (ok, recipients, error) = ValidateRecipients(_recipientOptions.Recipients);
            if (!ok)
            {
                _logger.LogError("Daily Report WhatsApp send aborted: {Error}", error);
                return DailyReportBroadcastResult.Empty;
            }

            var report = await _reportService.BuildTodayReportAsync();
            var variables = DailyReportWhatsAppFormatter.FormatVariables(report, DateTime.Now);
            return await SendToRecipientsAsync(_sendviseClient, _logger, variables, recipients, cancellationToken);
        }

        // Pure, static, no DB -- directly unit-testable. STRICT: an empty
        // list is rejected; a list containing even one blank/whitespace
        // entry is ALSO rejected (never silently send to fewer recipients
        // than were configured just because one entry was blank). The only
        // normalization on an otherwise-valid list is trimming + collapsing
        // EXACT duplicate numbers -- configuring the same number twice is
        // redundant, not an error, and is still sent exactly once.
        public static (bool Ok, List<string> Recipients, string? Error) ValidateRecipients(IReadOnlyList<string>? configured)
        {
            var list = configured ?? Array.Empty<string>();
            if (list.Count == 0)
                return (false, new List<string>(), "DailyReportWhatsApp:Recipients is empty.");
            if (list.Any(string.IsNullOrWhiteSpace))
                return (false, new List<string>(),
                    $"{list.Count} recipient(s) configured in DailyReportWhatsApp:Recipients but at least one entry is blank.");
            return (true, NormalizeRecipients(list), null);
        }

        // Trims and collapses EXACT duplicate numbers in an ALREADY-validated
        // (non-empty, no-blank-entries) list -- see ValidateRecipients, which
        // is the gate SendDailyReportAsync actually uses. Still null-safe and
        // still drops blanks defensively for any other direct caller (e.g.
        // tests exercising this helper in isolation).
        public static List<string> NormalizeRecipients(IEnumerable<string>? recipients)
            => (recipients ?? Enumerable.Empty<string>())
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(r => r.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();

        // Pure orchestration over an ALREADY-built variables list and an
        // ALREADY-normalized, non-empty recipient list -- no DB, no
        // formatting, no instance state (static, takes the client/logger as
        // parameters) so it is directly unit-testable with a stub
        // SendviseClient and a hand-built DailyReportWhatsAppSender is never
        // required. Every recipient gets its own independent try/send: one
        // recipient's failure (or an unexpected exception) never stops the
        // remaining recipients from being attempted. Only the 1-based
        // position is ever logged, never the recipient string itself.
        public static async Task<DailyReportBroadcastResult> SendToRecipientsAsync(
            SendviseClient sendviseClient, ILogger logger,
            List<string> variables, IReadOnlyList<string> recipients, CancellationToken cancellationToken = default)
        {
            var outcomes = new List<DailyReportSendOutcome>();
            for (var i = 0; i < recipients.Count; i++)
            {
                var label = $"recipient {i + 1}";
                SendviseSendResult result;
                try
                {
                    result = await sendviseClient.SendTemplateAsync(recipients[i], variables, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Daily Report WhatsApp send to {Recipient} threw unexpectedly.", label);
                    result = new SendviseSendResult(false, null, null, ex.Message);
                }

                if (result.Success)
                    logger.LogInformation("Daily Report WhatsApp send to {Recipient} succeeded.", label);
                else
                    logger.LogWarning("Daily Report WhatsApp send to {Recipient} failed: {Error}", label, result.StatusOrError);

                outcomes.Add(new DailyReportSendOutcome(i + 1, result));
            }

            var successCount = outcomes.Count(o => o.Result.Success);
            return new DailyReportBroadcastResult(successCount, outcomes.Count - successCount, outcomes);
        }
    }

    // One recipient's outcome, identified ONLY by its 1-based position in
    // the configured list -- never the phone number itself.
    public sealed record DailyReportSendOutcome(int RecipientIndex, SendviseSendResult Result);

    public sealed record DailyReportBroadcastResult(int SuccessCount, int FailureCount, IReadOnlyList<DailyReportSendOutcome> Outcomes)
    {
        public static DailyReportBroadcastResult Empty { get; } = new(0, 0, Array.Empty<DailyReportSendOutcome>());
    }
}
