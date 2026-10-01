using Microsoft.Extensions.Configuration;
using PlantStockManager.Data;

namespace PlantStockManager.Tests
{
    // Step 3B: the "never blocks a real login" contract, testable without
    // any database at all -- an unreachable/invalid connection string is
    // enough to exercise RecordLoginAsync's internal try/catch. This is the
    // one behavior LoginModel's own correctness actually depends on (it
    // trusts RecordLoginAsync to never throw), so it gets its own always-
    // runnable test rather than only being covered by the scratch-DB E2E
    // suite (DailyReportServiceE2ETests-style [SkippableFact]s), which may
    // not run in every environment.
    public class UserLoginHistoryRepositoryTests
    {
        [Fact]
        public async Task RecordLoginAsync_UnreachableDatabase_NeverThrows_ReturnsFailure()
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Deliberately invalid: a non-routable/nonexistent server,
                    // with a short timeout so the test doesn't hang.
                    ["ConnectionStrings:DefaultConnection"] =
                        "Server=tcp:127.0.0.1,1;Database=DoesNotExist;Connect Timeout=1;TrustServerCertificate=True;"
                })
                .Build();
            var dbHelper = new DatabaseHelper(config);
            var repo = new UserLoginHistoryRepository(dbHelper);

            var (success, message) = await repo.RecordLoginAsync(999999);

            Assert.False(success);
            Assert.NotNull(message);
        }
    }
}
