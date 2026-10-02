using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Step 8G: the production multi-recipient path (DailyReportWhatsAppSender.
    // SendDailyReportAsync / NormalizeRecipients / SendToRecipientsAsync).
    // Entirely stub-HTTP based, exactly like SendviseClientTests.cs -- no
    // real network call, no database (SendToRecipientsAsync/NormalizeRecipients
    // are static and take no DailyReportService dependency at all, by design,
    // specifically so this class never needs one). SendTestReportAsync (the
    // gated real-send manual test) is untouched and covered separately by
    // DailyReportWhatsAppSenderManualTests.cs -- nothing here ever calls it.
    public class DailyReportWhatsAppSenderTests
    {
        private sealed class StubHandler : HttpMessageHandler
        {
            public List<string> CapturedBodies { get; } = new();
            public List<string?> CapturedRecipients { get; } = new();
            // Per-call status codes, in order; falls back to OK once exhausted.
            public Queue<HttpStatusCode> ResponseQueue { get; } = new();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
                CapturedBodies.Add(body);
                using (var doc = System.Text.Json.JsonDocument.Parse(body))
                    CapturedRecipients.Add(doc.RootElement.TryGetProperty("to", out var toEl) ? toEl.GetString() : null);

                var status = ResponseQueue.Count > 0 ? ResponseQueue.Dequeue() : HttpStatusCode.OK;
                var responseBody = status == HttpStatusCode.OK
                    ? """{"message_id":"msg_ok","status":"queued"}"""
                    : """{"error":"simulated failure"}""";
                return new HttpResponseMessage(status) { Content = new StringContent(responseBody, Encoding.UTF8, "application/json") };
            }
        }

        // Records every formatted log message (not just whether something was
        // logged) so tests can assert on content -- e.g. that a phone number
        // or API key never appears anywhere that was logged.
        private sealed class CapturingLogger : ILogger
        {
            public List<string> Messages { get; } = new();
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => Messages.Add(formatter(state, exception));

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();
                public void Dispose() { }
            }
        }

        private static (SendviseClient Client, StubHandler Handler) BuildClient(string apiKey = "sv_test_fake_key_never_real")
        {
            var handler = new StubHandler();
            var httpClient = new HttpClient(handler);
            var options = Options.Create(new SendviseOptions
            {
                BaseUrl = "https://app.sendvise.com/api/v1/external",
                TemplateName = "daily_report_plantmanager",
                Language = "en",
                ApiKey = apiKey,
            });
            var client = new SendviseClient(httpClient, options, NullLogger<SendviseClient>.Instance);
            return (client, handler);
        }

        private static List<string> Sample16Variables() =>
            Enumerable.Range(1, DailyReportWhatsAppFormatter.ExpectedVariableCount).Select(i => $"value{i}").ToList();

        // ---- NormalizeRecipients: null / empty / whitespace / duplicates ----

        [Fact]
        public void NormalizeRecipients_ThreeDistinctRecipients_AllKept_InOrder()
        {
            var result = DailyReportWhatsAppSender.NormalizeRecipients(new[] { "911111111111", "922222222222", "933333333333" });
            Assert.Equal(new[] { "911111111111", "922222222222", "933333333333" }, result);
        }

        [Fact]
        public void NormalizeRecipients_OneRecipient_Kept()
        {
            var result = DailyReportWhatsAppSender.NormalizeRecipients(new[] { "911111111111" });
            Assert.Equal(new[] { "911111111111" }, result);
        }

        [Fact]
        public void NormalizeRecipients_Null_ReturnsEmpty_NeverThrows()
        {
            var result = DailyReportWhatsAppSender.NormalizeRecipients(null);
            Assert.Empty(result);
        }

        [Fact]
        public void NormalizeRecipients_EmptyList_ReturnsEmpty()
        {
            var result = DailyReportWhatsAppSender.NormalizeRecipients(new List<string>());
            Assert.Empty(result);
        }

        [Fact]
        public void NormalizeRecipients_EmptyOrWhitespaceEntries_AreDropped()
        {
            var result = DailyReportWhatsAppSender.NormalizeRecipients(new[] { "911111111111", "", "   ", "922222222222", null! });
            Assert.Equal(new[] { "911111111111", "922222222222" }, result);
        }

        [Fact]
        public void NormalizeRecipients_DuplicateEntries_CollapseToOne()
        {
            var result = DailyReportWhatsAppSender.NormalizeRecipients(new[] { "911111111111", "911111111111", "922222222222", " 911111111111 " });
            Assert.Equal(new[] { "911111111111", "922222222222" }, result);
        }

        // ---- ValidateRecipients: the STRICT gate SendDailyReportAsync actually uses ----

        [Fact]
        public void ValidateRecipients_ThreeConfigured_AllValid_Ok_ReturnsAllThree()
        {
            var (ok, recipients, error) = DailyReportWhatsAppSender.ValidateRecipients(new[] { "911111111111", "922222222222", "933333333333" });
            Assert.True(ok);
            Assert.Null(error);
            Assert.Equal(new[] { "911111111111", "922222222222", "933333333333" }, recipients);
        }

        [Fact]
        public void ValidateRecipients_EmptyList_NotOk_ClearError()
        {
            var (ok, recipients, error) = DailyReportWhatsAppSender.ValidateRecipients(new List<string>());
            Assert.False(ok);
            Assert.Empty(recipients);
            Assert.False(string.IsNullOrWhiteSpace(error));
        }

        [Fact]
        public void ValidateRecipients_Null_NotOk_ClearError_NeverThrows()
        {
            var (ok, recipients, error) = DailyReportWhatsAppSender.ValidateRecipients(null);
            Assert.False(ok);
            Assert.Empty(recipients);
            Assert.False(string.IsNullOrWhiteSpace(error));
        }

        [Fact]
        public void ValidateRecipients_MissingOneOfThree_NotOk_DoesNotSilentlyReturnTheOtherTwo()
        {
            // Exactly the scenario the business rule forbids: 3 configured,
            // one blank -- must never silently fall back to sending the 2
            // that are present.
            var (ok, recipients, error) = DailyReportWhatsAppSender.ValidateRecipients(new[] { "911111111111", "", "933333333333" });
            Assert.False(ok);
            Assert.Empty(recipients);
            Assert.Contains("blank", error);
            Assert.Contains("3", error);
        }

        [Fact]
        public void ValidateRecipients_WhitespaceOnlyEntry_IsTreatedAsBlank_NotOk()
        {
            var (ok, _, error) = DailyReportWhatsAppSender.ValidateRecipients(new[] { "911111111111", "   ", "933333333333" });
            Assert.False(ok);
            Assert.NotNull(error);
        }

        [Fact]
        public void ValidateRecipients_ErrorMessage_NeverContainsAPhoneNumber()
        {
            var (_, _, error) = DailyReportWhatsAppSender.ValidateRecipients(new[] { "919545797879", "", "918766811087" });
            Assert.DoesNotContain("919545797879", error);
            Assert.DoesNotContain("918766811087", error);
        }

        [Fact]
        public void ValidateRecipients_DuplicateButAllNonBlank_Ok_CollapsesToDistinct()
        {
            // All 3 entries are non-blank (satisfies "all 3 non-empty"); two
            // happen to be the same number -- that's redundant configuration,
            // not a validation failure, and is still sent only once.
            var (ok, recipients, error) = DailyReportWhatsAppSender.ValidateRecipients(new[] { "911111111111", "911111111111", "933333333333" });
            Assert.True(ok);
            Assert.Null(error);
            Assert.Equal(new[] { "911111111111", "933333333333" }, recipients);
        }

        [Fact]
        public void ValidateRecipients_OneConfigured_Valid_Ok()
        {
            var (ok, recipients, error) = DailyReportWhatsAppSender.ValidateRecipients(new[] { "911111111111" });
            Assert.True(ok);
            Assert.Null(error);
            Assert.Equal(new[] { "911111111111" }, recipients);
        }

        // ---- SendToRecipientsAsync: independent sends, partial failure ----

        [Fact]
        public async Task SendToRecipientsAsync_AllThreeSucceed_ReportsThreeSuccesses()
        {
            var (client, handler) = BuildClient();
            var logger = new CapturingLogger();
            var recipients = new[] { "911111111111", "922222222222", "933333333333" };

            var result = await DailyReportWhatsAppSender.SendToRecipientsAsync(client, logger, Sample16Variables(), recipients);

            Assert.Equal(3, result.SuccessCount);
            Assert.Equal(0, result.FailureCount);
            Assert.Equal(3, result.Outcomes.Count);
            Assert.All(result.Outcomes, o => Assert.True(o.Result.Success));
            Assert.Equal(3, handler.CapturedBodies.Count);
        }

        [Fact]
        public async Task SendToRecipientsAsync_MiddleRecipientFails_OthersStillSent_ReportsTwoSuccessOneFailure()
        {
            var (client, handler) = BuildClient();
            handler.ResponseQueue.Enqueue(HttpStatusCode.OK);           // recipient 1 -> success
            handler.ResponseQueue.Enqueue(HttpStatusCode.BadRequest);   // recipient 2 -> failure
            handler.ResponseQueue.Enqueue(HttpStatusCode.OK);           // recipient 3 -> success
            var logger = new CapturingLogger();
            var recipients = new[] { "911111111111", "922222222222", "933333333333" };

            var result = await DailyReportWhatsAppSender.SendToRecipientsAsync(client, logger, Sample16Variables(), recipients);

            Assert.Equal(2, result.SuccessCount);
            Assert.Equal(1, result.FailureCount);
            Assert.Equal(3, result.Outcomes.Count);
            Assert.True(result.Outcomes[0].Result.Success);
            Assert.False(result.Outcomes[1].Result.Success);
            Assert.True(result.Outcomes[2].Result.Success);
            // The failure of recipient 2 did not stop recipient 3 from being attempted.
            Assert.Equal(3, handler.CapturedBodies.Count);
        }

        [Fact]
        public async Task SendToRecipientsAsync_FirstRecipientFails_SecondAndThirdStillReceiveIt()
        {
            var (client, handler) = BuildClient();
            handler.ResponseQueue.Enqueue(HttpStatusCode.Unauthorized); // recipient 1 -> failure
            var logger = new CapturingLogger();
            var recipients = new[] { "911111111111", "922222222222", "933333333333" };

            var result = await DailyReportWhatsAppSender.SendToRecipientsAsync(client, logger, Sample16Variables(), recipients);

            Assert.False(result.Outcomes[0].Result.Success);
            Assert.True(result.Outcomes[1].Result.Success);
            Assert.True(result.Outcomes[2].Result.Success);
            Assert.Equal(3, handler.CapturedRecipients.Count);
            Assert.Equal("922222222222", handler.CapturedRecipients[1]);
            Assert.Equal("933333333333", handler.CapturedRecipients[2]);
        }

        [Fact]
        public async Task SendToRecipientsAsync_EachRecipientGetsItsOwnIndependentRequest()
        {
            var (client, handler) = BuildClient();
            var logger = new CapturingLogger();
            var recipients = new[] { "911111111111", "922222222222", "933333333333" };

            await DailyReportWhatsAppSender.SendToRecipientsAsync(client, logger, Sample16Variables(), recipients);

            Assert.Equal(recipients, handler.CapturedRecipients);
        }

        [Fact]
        public async Task SendToRecipientsAsync_SameFormattedVariables_SentToEveryRecipient_NeverRegeneratedPerRecipient()
        {
            var (client, handler) = BuildClient();
            var logger = new CapturingLogger();
            var variables = Sample16Variables();
            var recipients = new[] { "911111111111", "922222222222", "933333333333" };

            await DailyReportWhatsAppSender.SendToRecipientsAsync(client, logger, variables, recipients);

            // All 3 request bodies carry an identical "variables" array -- the
            // one formatted list was reused verbatim, never rebuilt per recipient.
            Assert.Equal(3, handler.CapturedBodies.Count);
            var variablesJson = handler.CapturedBodies
                .Select(b => System.Text.Json.JsonDocument.Parse(b).RootElement.GetProperty("variables").ToString())
                .ToList();
            Assert.Equal(variablesJson[0], variablesJson[1]);
            Assert.Equal(variablesJson[0], variablesJson[2]);
        }

        [Fact]
        public async Task SendToRecipientsAsync_VariablesCount_RemainsExactly16()
        {
            var (client, handler) = BuildClient();
            var logger = new CapturingLogger();
            var variables = Sample16Variables();
            Assert.Equal(16, variables.Count);

            await DailyReportWhatsAppSender.SendToRecipientsAsync(client, logger, variables, new[] { "911111111111" });

            using var doc = System.Text.Json.JsonDocument.Parse(handler.CapturedBodies[0]);
            Assert.Equal(16, doc.RootElement.GetProperty("variables").GetArrayLength());
        }

        [Fact]
        public async Task SendToRecipientsAsync_ZeroRecipients_SendsNothing_ReportsZeroZero()
        {
            var (client, handler) = BuildClient();
            var logger = new CapturingLogger();

            var result = await DailyReportWhatsAppSender.SendToRecipientsAsync(client, logger, Sample16Variables(), Array.Empty<string>());

            Assert.Equal(0, result.SuccessCount);
            Assert.Equal(0, result.FailureCount);
            Assert.Empty(result.Outcomes);
            Assert.Empty(handler.CapturedBodies);
        }

        // ---- Nothing sensitive ever appears in logs or outcomes -----------

        [Fact]
        public async Task SendToRecipientsAsync_LogsUseOnlyRecipientPosition_NeverThePhoneNumber()
        {
            var (client, handler) = BuildClient();
            handler.ResponseQueue.Enqueue(HttpStatusCode.BadRequest); // give at least one failure log too
            var logger = new CapturingLogger();
            var recipients = new[] { "919545797879", "918698065757", "918766811087" };

            await DailyReportWhatsAppSender.SendToRecipientsAsync(client, logger, Sample16Variables(), recipients);

            Assert.NotEmpty(logger.Messages);
            foreach (var number in recipients)
                Assert.All(logger.Messages, m => Assert.DoesNotContain(number, m));
            Assert.Contains(logger.Messages, m => m.Contains("recipient 1"));
            Assert.Contains(logger.Messages, m => m.Contains("recipient 2"));
            Assert.Contains(logger.Messages, m => m.Contains("recipient 3"));
        }

        [Fact]
        public async Task SendToRecipientsAsync_LogsNeverContainTheApiKey()
        {
            var (client, handler) = BuildClient(apiKey: "sv_live_should_never_leak_IN_LOGS_999");
            handler.ResponseQueue.Enqueue(HttpStatusCode.BadRequest);
            var logger = new CapturingLogger();

            await DailyReportWhatsAppSender.SendToRecipientsAsync(client, logger, Sample16Variables(), new[] { "911111111111" });

            Assert.All(logger.Messages, m => Assert.DoesNotContain("sv_live_should_never_leak_IN_LOGS_999", m));
            Assert.All(logger.Messages, m => Assert.DoesNotContain("sv_", m, StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task SendToRecipientsAsync_Outcomes_NeverExposeThePhoneNumber()
        {
            // DailyReportSendOutcome only carries a 1-based index + the
            // SendviseSendResult (which structurally has no recipient field),
            // so the phone number cannot leak through the return value either.
            var (client, _) = BuildClient();
            var logger = new CapturingLogger();
            var recipients = new[] { "919545797879" };

            var result = await DailyReportWhatsAppSender.SendToRecipientsAsync(client, logger, Sample16Variables(), recipients);

            var outcome = Assert.Single(result.Outcomes);
            Assert.Equal(1, outcome.RecipientIndex);
            var outcomeProperties = typeof(SendviseSendResult).GetProperties().Select(p => p.Name);
            Assert.DoesNotContain(outcomeProperties, n => n.Contains("Recipient", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Phone", StringComparison.OrdinalIgnoreCase) || n == "To");
        }

        // ---- Wiring: SendTestReportAsync is untouched / still gated elsewhere ----

        [Fact]
        public void SendDailyReportAsync_IsADistinctMethodFromSendTestReportAsync()
        {
            // The manual test (DailyReportWhatsAppSenderManualTests.cs) calls
            // SendTestReportAsync ONLY -- this just documents that the new
            // production method is a separate entry point nothing wires it
            // into automatically.
            var methods = typeof(DailyReportWhatsAppSender).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
                .Select(m => m.Name).ToArray();
            Assert.Contains("SendTestReportAsync", methods);
            Assert.Contains("SendDailyReportAsync", methods);
            Assert.NotEqual("SendTestReportAsync", "SendDailyReportAsync");
        }

        // ---- Source-level evidence: the report is built once, formatted once ----

        private static string ReadSource()
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.GetFiles(dir, "*.sln").Any())
                dir = Path.GetDirectoryName(dir);
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(dir!, "Services", "DailyReportWhatsAppSender.cs"));
        }

        [Fact]
        public void SendDailyReportAsync_BuildsTheReportExactlyOnce_NotInsideTheRecipientLoop()
        {
            var source = ReadSource();
            var methodStart = source.IndexOf("public async Task<DailyReportBroadcastResult> SendDailyReportAsync", StringComparison.Ordinal);
            var methodEnd = source.IndexOf("\n        }", methodStart, StringComparison.Ordinal);
            Assert.True(methodStart > 0 && methodEnd > methodStart);
            var body = source.Substring(methodStart, methodEnd - methodStart);

            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(body, "BuildTodayReportAsync").Count);
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(body, "FormatVariables").Count);
            Assert.DoesNotContain("for (", body);
            Assert.DoesNotContain("foreach (", body);
        }

        [Fact]
        public void SendToRecipientsAsync_NeverCallsBuildReportOrFormatVariables()
        {
            var source = ReadSource();
            var methodStart = source.IndexOf("public static async Task<DailyReportBroadcastResult> SendToRecipientsAsync", StringComparison.Ordinal);
            Assert.True(methodStart > 0);
            var body = source.Substring(methodStart);

            Assert.DoesNotContain("BuildTodayReportAsync", body);
            Assert.DoesNotContain("FormatVariables", body);
        }
    }
}
