using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PlantStockManager.Data;
using PlantStockManager.Services;
using Xunit.Abstractions;

namespace PlantStockManager.Tests
{
    // Step 8A: the ONE sanctioned way to manually send a REAL WhatsApp
    // report end to end, for manual verification only. This NEVER runs as
    // part of a normal `dotnet test` -- it is gated behind an explicit
    // opt-in environment variable that a plain test run will never have
    // set, exactly the same "must be deliberately turned on" shape as this
    // project's existing PSM_SCRATCH_CONNECTION-gated E2E tests.
    //
    // To actually send one test message:
    //   1. Fill in Sendvise:ApiKey and Sendvise:TestRecipient in the
    //      (gitignored) appsettings.json.
    //   2. Set the environment variable SENDVISE_MANUAL_SEND_TEST=1.
    //   3. Run: dotnet test --filter FullyQualifiedName~DailyReportWhatsAppSenderManualTests
    // Any other invocation of `dotnet test` skips this class entirely and
    // sends nothing.
    public class DailyReportWhatsAppSenderManualTests
    {
        private readonly ITestOutputHelper _output;
        public DailyReportWhatsAppSenderManualTests(ITestOutputHelper output) => _output = output;

        [SkippableFact]
        public async Task SendTestReportAsync_ManualOnly_SendsOneRealMessage_ToTheOneConfiguredTestRecipient()
        {
            Skip.IfNot(Environment.GetEnvironmentVariable("SENDVISE_MANUAL_SEND_TEST") == "1",
                "Set SENDVISE_MANUAL_SEND_TEST=1 to run this manual, real-sending test explicitly. Never runs otherwise.");

            // Loads the SAME configuration sources Program.cs uses
            // (appsettings.json, appsettings.{Environment}.json, env vars)
            // so this exercises the real, locally-configured Sendvise
            // settings and the real (non-scratch) database -- reading it is
            // safe, since DailyReportService is read-only.
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile(FindRepoRootFile("appsettings.json"), optional: true)
                .AddJsonFile(FindRepoRootFile("appsettings.Development.json"), optional: true)
                .AddEnvironmentVariables()
                .Build();

            var sendviseOptions = configuration.GetSection(SendviseOptions.SectionName).Get<SendviseOptions>() ?? new SendviseOptions();
            Skip.If(string.IsNullOrWhiteSpace(sendviseOptions.ApiKey), "Sendvise:ApiKey is not configured in appsettings.json -- nothing to send with.");
            Skip.If(string.IsNullOrWhiteSpace(sendviseOptions.TestRecipient), "Sendvise:TestRecipient is not configured in appsettings.json -- no recipient to send to.");

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton<DatabaseHelper>();
            foreach (var t in typeof(DatabaseHelper).Assembly.GetTypes().Where(t => t.Namespace == "PlantStockManager.Data" && t.IsClass && t.Name.EndsWith("Repository")))
                services.AddScoped(t);
            services.AddScoped<DailyReportService>();
            services.Configure<SendviseOptions>(configuration.GetSection(SendviseOptions.SectionName));
            services.AddHttpClient<SendviseClient>();
            services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
            services.AddScoped<DailyReportWhatsAppSender>();

            var provider = services.BuildServiceProvider();
            var sender = provider.GetRequiredService<DailyReportWhatsAppSender>();

            var result = await sender.SendTestReportAsync();

            // Log/report the outcome -- NEVER the API key (this result type
            // structurally cannot contain it; see SendviseClientTests for
            // the regression guard on that).
            _output.WriteLine($"Sendvise manual test send: Success={result.Success}, HttpStatusCode={result.HttpStatusCode}, MessageId={result.MessageId}, StatusOrError={result.StatusOrError}");

            Assert.True(result.Success, $"Manual Sendvise send failed: {result.StatusOrError}");
        }

        private static string FindRepoRootFile(string fileName)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, fileName)))
                dir = dir.Parent;
            return dir != null ? Path.Combine(dir.FullName, fileName) : fileName;
        }
    }
}
