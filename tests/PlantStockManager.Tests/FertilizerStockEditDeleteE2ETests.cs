using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // CORRECTION I3 -- end-to-end: the REAL FertilizerStockRepository (Insert/Update/Delete) against a SCRATCH COPY
    // of the test database (same safety rules as the other E2E classes: PSM_SCRATCH_CONNECTION, database name must
    // start with PlantsIMS2_Scratch_). Batch 37 and every other pre-existing row is never written to by this class;
    // it only ever creates its OWN stock batches (unique BatchNumber) and reads/checks batch 37 to prove it stayed
    // byte-for-byte unchanged.
    [Collection("ScratchDb")]
    public class FertilizerStockEditDeleteE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";
        private const int Mahadev = 11;

        private sealed class StaticMonitor<T> : IOptionsMonitor<T>
        {
            public StaticMonitor(T value) => CurrentValue = value;
            public T CurrentValue { get; }
            public T Get(string? name) => CurrentValue;
            public IDisposable? OnChange(Action<T, string?> listener) => null;
        }

        private sealed class Env
        {
            public string ConnectionString { get; init; } = "";
            public ServiceProvider Provider { get; init; } = null!;
            public T Get<T>() where T : notnull => Provider.GetRequiredService<T>();

            public async Task<T> ScalarAsync<T>(string sql, params (string Name, object? Value)[] args)
            {
                using var conn = new SqlConnection(ConnectionString);
                await conn.OpenAsync();
                using var cmd = new SqlCommand(sql, conn);
                foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
                var r = await cmd.ExecuteScalarAsync();
                return r == null || r == DBNull.Value ? default! : (T)Convert.ChangeType(r, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
            }
        }

        private static async Task<Env> OpenAsync()
        {
            var cs = Environment.GetEnvironmentVariable(ConnectionVariable);
            Skip.If(string.IsNullOrWhiteSpace(cs), $"Set {ConnectionVariable} to a {RequiredPrefix}* database connection string to run the end-to-end tests.");
            string name;
            using (var conn = new SqlConnection(cs))
            {
                await conn.OpenAsync();
                using var cmd = new SqlCommand("SELECT DB_NAME()", conn);
                name = (string)(await cmd.ExecuteScalarAsync())!;
            }
            if (!name.StartsWith(RequiredPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException($"Refusing to run end-to-end tests against '{name}'. Only databases named {RequiredPrefix}* are allowed.");

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs }).Build());
            services.AddSingleton<DatabaseHelper>();
            services.AddSingleton<AreaAccessService>();
            services.AddSingleton<IOptions<SecurityOptions>>(Options.Create(new SecurityOptions { FullAccessRoleNames = new[] { "System Administrator" } }));
            services.AddSingleton<IOptionsMonitor<SeedlingWorkflowOptions>>(new StaticMonitor<SeedlingWorkflowOptions>(new SeedlingWorkflowOptions()));
            services.AddSingleton<SeedlingAreaScope>();
            services.AddScoped<UserClaimsFactory>();
            foreach (var t in typeof(DatabaseHelper).Assembly.GetTypes().Where(t => t.Namespace == "PlantStockManager.Data" && t.IsClass && t.Name.EndsWith("Repository")))
                services.AddScoped(t);
            return new Env { ConnectionString = cs!, Provider = services.BuildServiceProvider() };
        }

        private static async Task<int> ExistingIdAsync(Env env, string sql)
            => await env.ScalarAsync<int>(sql);

        private static async Task<FertilizerStock> NewBatchAsync(Env env, decimal quantity = 10)
        {
            var repo = env.Get<FertilizerStockRepository>();
            var fertilizerId = await ExistingIdAsync(env, "SELECT MIN(FertilizerId) FROM dbo.FertilizerMaster");
            var unitId = await ExistingIdAsync(env, "SELECT MIN(UnitId) FROM dbo.UnitMaster");
            var sourceId = await ExistingIdAsync(env, "SELECT MIN(SourceId) FROM dbo.FertilizerSource");
            var stock = new FertilizerStock
            {
                FertilizerId = fertilizerId, UnitId = unitId, SourceId = sourceId, Quantity = quantity,
                PurchaseDate = new DateTime(2020, 1, 1), BatchNumber = "E2E-I3-" + Guid.NewGuid().ToString("N")[..8]
            };
            var result = await repo.InsertAsync(stock);
            Assert.True(result.Success, result.Message);
            stock.StockId = result.StockId;
            return stock;
        }

        private static async Task<string> Batch37FingerprintAsync(Env env)
            => await env.ScalarAsync<string>(
                "SELECT CONCAT(StockId, '/', Quantity, '/', LatestAvailableQuantity, '/', IsUtilized, '/', ISNULL(BatchNumber, N''), '/', PurchaseDate, '/', ISNULL(CONVERT(NVARCHAR(20), ExpiryDate, 120), '')) FROM dbo.FertilizerStock WHERE StockId = 37");

        // ---- unused edit ----------------------------------------------------------------------------------------

        [SkippableFact]
        public async Task EditingAnUnusedBatch_Succeeds_AndKeepsAvailableEqualToQuantity()
        {
            var env = await OpenAsync();
            var stock = await NewBatchAsync(env, 20);
            var repo = env.Get<FertilizerStockRepository>();

            stock.Quantity = 35;
            stock.BatchNumber += "-edited";
            var result = await repo.UpdateAsync(stock);
            Assert.True(result.Success, result.Message);

            var reloaded = await repo.GetByIdAsync(stock.StockId);
            Assert.NotNull(reloaded);
            Assert.Equal((35m, 35m), (reloaded!.Quantity, reloaded.LatestAvailableQuantity));
            Assert.EndsWith("-edited", reloaded.BatchNumber);
        }

        // ---- used edit is refused ---------------------------------------------------------------------------------

        [SkippableFact]
        public async Task EditingABatchThatHasAlreadyBeenIssued_IsRefused_NothingChanges()
        {
            var env = await OpenAsync();
            var stock = await NewBatchAsync(env, 20);
            var transactions = env.Get<FertilizerTransactionRepository>();
            var issueResult = await transactions.IssueAsync(stock.StockId, 5, new DateTime(2020, 1, 2), Mahadev, "e2e I3", Mahadev);
            Assert.True(issueResult.Success, issueResult.Message);

            var repo = env.Get<FertilizerStockRepository>();
            var before = await repo.GetByIdAsync(stock.StockId);

            stock.Quantity = 999;
            var result = await repo.UpdateAsync(stock);
            Assert.False(result.Success);
            Assert.Equal(FertilizerStockRules.ChangedSinceOpenedMessage, result.Message);

            var after = await repo.GetByIdAsync(stock.StockId);
            Assert.Equal((before!.Quantity, before.LatestAvailableQuantity), (after!.Quantity, after.LatestAvailableQuantity));
            Assert.Equal(15m, after.LatestAvailableQuantity);   // 20 - 5, unaffected by the refused edit
        }

        // ---- stale / race edit: usage happens strictly BETWEEN loading the form and saving it -----------------------

        [SkippableFact]
        public async Task AStaleEditForm_IsRefused_IfTheBatchWasIssuedFromMeanwhile()
        {
            var env = await OpenAsync();
            var stock = await NewBatchAsync(env, 10);
            var repo = env.Get<FertilizerStockRepository>();

            // the form is "opened": load it while still unused (as OnPostEditAsync would)
            var loaded = await repo.GetByIdAsync(stock.StockId);
            Assert.True(FertilizerStockRules.EditAllowed(loaded!.Quantity, loaded.LatestAvailableQuantity));

            // ... meanwhile, someone else issues from the same batch ...
            var transactions = env.Get<FertilizerTransactionRepository>();
            var issueResult = await transactions.IssueAsync(stock.StockId, 3, new DateTime(2020, 1, 3), Mahadev, "e2e race", Mahadev);
            Assert.True(issueResult.Success, issueResult.Message);

            // ... now the stale form is submitted
            loaded.Quantity = 50;
            var saveResult = await repo.UpdateAsync(loaded);
            Assert.False(saveResult.Success);
            Assert.Equal(FertilizerStockRules.ChangedSinceOpenedMessage, saveResult.Message);

            var after = await repo.GetByIdAsync(stock.StockId);
            Assert.Equal(10m, after!.Quantity);       // the stale 50 never landed
            Assert.Equal(7m, after.LatestAvailableQuantity);
        }

        // ---- unused delete ------------------------------------------------------------------------------------

        [SkippableFact]
        public async Task DeletingAnUnusedBatch_Succeeds()
        {
            var env = await OpenAsync();
            var stock = await NewBatchAsync(env, 5);
            var repo = env.Get<FertilizerStockRepository>();

            var result = await repo.DeleteAsync(stock.StockId);
            Assert.True(result.Succeeded, result.Message);
            Assert.Null(await repo.GetByIdAsync(stock.StockId));
        }

        // ---- used delete is refused ---------------------------------------------------------------------------

        [SkippableFact]
        public async Task DeletingABatchThatHasBeenIssued_IsRefused_NothingChanges()
        {
            var env = await OpenAsync();
            var stock = await NewBatchAsync(env, 10);
            var transactions = env.Get<FertilizerTransactionRepository>();
            var issueResult = await transactions.IssueAsync(stock.StockId, 2, new DateTime(2020, 1, 4), Mahadev, "e2e I3 delete", Mahadev);
            Assert.True(issueResult.Success, issueResult.Message);

            var repo = env.Get<FertilizerStockRepository>();
            var result = await repo.DeleteAsync(stock.StockId);
            Assert.False(result.Succeeded);
            Assert.Equal(PlantStockManager.Services.DeleteOutcome.Blocked, result.Outcome);
            Assert.Contains("Fertilizer Usage", result.Message);
            var dep = Assert.Single(result.Dependencies);
            Assert.Equal(1, dep.Count);

            Assert.NotNull(await repo.GetByIdAsync(stock.StockId));   // still there
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.FertilizerUsage WHERE StockId = @S", ("@S", stock.StockId)));
        }

        // ---- invalid input --------------------------------------------------------------------------------------

        [SkippableFact]
        public async Task InvalidInputs_AreRefusedWithAFriendlyMessage_NeverARawSqlError()
        {
            var env = await OpenAsync();
            var repo = env.Get<FertilizerStockRepository>();
            var fertilizerId = await ExistingIdAsync(env, "SELECT MIN(FertilizerId) FROM dbo.FertilizerMaster");
            var unitId = await ExistingIdAsync(env, "SELECT MIN(UnitId) FROM dbo.UnitMaster");
            var sourceId = await ExistingIdAsync(env, "SELECT MIN(SourceId) FROM dbo.FertilizerSource");

            async Task Refused(FertilizerStock s, string why)
            {
                var result = await repo.InsertAsync(s);
                Assert.False(result.Success, why);
                Assert.False(string.IsNullOrWhiteSpace(result.Message), why);
            }

            await Refused(new FertilizerStock { FertilizerId = fertilizerId, UnitId = unitId, SourceId = sourceId, Quantity = 0, PurchaseDate = DateTime.Today }, "zero quantity");
            await Refused(new FertilizerStock { FertilizerId = fertilizerId, UnitId = unitId, SourceId = sourceId, Quantity = -5, PurchaseDate = DateTime.Today }, "negative quantity");
            await Refused(new FertilizerStock { FertilizerId = 987654, UnitId = unitId, SourceId = sourceId, Quantity = 1, PurchaseDate = DateTime.Today }, "unknown fertilizer");
            await Refused(new FertilizerStock { FertilizerId = fertilizerId, UnitId = 987654, SourceId = sourceId, Quantity = 1, PurchaseDate = DateTime.Today }, "unknown unit");
            await Refused(new FertilizerStock { FertilizerId = fertilizerId, UnitId = unitId, SourceId = 987654, Quantity = 1, PurchaseDate = DateTime.Today }, "unknown source");
            await Refused(new FertilizerStock { FertilizerId = fertilizerId, UnitId = unitId, SourceId = sourceId, Quantity = 1, PurchaseDate = default }, "no purchase date");

            // and nothing was inserted by any of the refused attempts
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.FertilizerStock WHERE FertilizerId = 987654 OR UnitId = 987654 OR SourceId = 987654"));

            // the same invalid values are refused on Update too
            var stock = await NewBatchAsync(env, 4);
            stock.Quantity = -1;
            var updateResult = await repo.UpdateAsync(stock);
            Assert.False(updateResult.Success);
        }

        // ---- concurrency: Issue and Delete racing the same batch -------------------------------------------------

        [SkippableFact]
        public async Task ConcurrentIssueAndDelete_NeverCorruptData_ExactlyOneOutcomeWins()
        {
            var env = await OpenAsync();
            var stock = await NewBatchAsync(env, 8);
            var stockRepo = env.Get<FertilizerStockRepository>();
            var transactions = env.Get<FertilizerTransactionRepository>();

            var deleteTask = Task.Run(() => stockRepo.DeleteAsync(stock.StockId));
            var issueTask = Task.Run(() => transactions.IssueAsync(stock.StockId, 3, new DateTime(2020, 1, 5), Mahadev, "e2e race delete", Mahadev));
            await Task.WhenAll(deleteTask, issueTask);

            var deleteResult = await deleteTask;
            var issueResult = await issueTask;

            var stillExists = await stockRepo.GetByIdAsync(stock.StockId);
            var usageRows = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.FertilizerUsage WHERE StockId = @S", ("@S", stock.StockId));

            if (deleteResult.Succeeded)
            {
                // the delete won the lock first: the batch is gone and the issue must have failed (nothing to issue from)
                Assert.Null(stillExists);
                Assert.False(issueResult.Success);
                Assert.Equal(0, usageRows);
            }
            else
            {
                // the issue won the lock first: the delete was refused because a dependency now exists, and nothing is lost
                Assert.True(issueResult.Success, issueResult.Message);
                Assert.NotNull(stillExists);
                Assert.Equal(1, usageRows);
                Assert.Equal(PlantStockManager.Services.DeleteOutcome.Blocked, deleteResult.Outcome);
            }
        }

        // ---- historical data: batch 37 is never touched --------------------------------------------------------

        [SkippableFact]
        public async Task Batch37_StaysByteForByteUnchanged_ThroughoutThisClassesActivity()
        {
            var env = await OpenAsync();
            var before = await Batch37FingerprintAsync(env);

            // exercise everything this class does, on OTHER batches only
            await EditingAnUnusedBatch_Succeeds_AndKeepsAvailableEqualToQuantity();
            await DeletingAnUnusedBatch_Succeeds();
            var attempt = await env.Get<FertilizerStockRepository>().UpdateAsync(new FertilizerStock { StockId = 37, FertilizerId = 1, UnitId = 1, SourceId = 1, Quantity = 1, PurchaseDate = DateTime.Today });
            Assert.False(attempt.Success);   // batch 37 already has usage (Quantity 2, used 3) -- the edit lock refuses it, exactly as it should

            Assert.Equal(before, await Batch37FingerprintAsync(env));
        }
    }
}
