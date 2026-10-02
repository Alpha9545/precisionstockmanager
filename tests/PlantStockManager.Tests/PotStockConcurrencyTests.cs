using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using PlantStockManager.Data;

namespace PlantStockManager.Tests
{
    // R4 (2026-10-02): mechanical port of TrayStockConcurrencyTests to
    // EmptyPotInventoryRepository.GetOrCreateLockedAsync, which had the
    // identical first-time-allocation race TrayStockRepository did before
    // its own sp_getapplock fix. Same structure, same safety rules: runs
    // only when PSM_TEST_CONNECTION holds a connection string, only against
    // PlantsIMS2_Test (any other database is refused), uses TWO independent
    // connections/transactions (a real cross-connection race needs two
    // separate transactions), and BOTH are always rolled back -- nothing
    // this test does is ever kept. Picks an active (AreaId, PotSize) pair
    // with zero existing EmptyPotInventory rows, so the allocation is a
    // genuine first-time race, never touching a pool that already exists.
    public class PotStockConcurrencyTests
    {
        private const string ConnectionVariable = "PSM_TEST_CONNECTION";
        private const string RequiredDatabase = "PlantsIMS2_Test";

        private static async Task<SqlConnection> OpenAsync(string cs)
        {
            var conn = new SqlConnection(cs);
            await conn.OpenAsync();
            using var dbName = new SqlCommand("SELECT DB_NAME()", conn);
            var name = (string)(await dbName.ExecuteScalarAsync())!;
            if (!string.Equals(name, RequiredDatabase, StringComparison.OrdinalIgnoreCase))
            {
                await conn.DisposeAsync();
                throw new InvalidOperationException($"Refusing to run database tests against '{name}'. Only {RequiredDatabase} is allowed.");
            }
            return conn;
        }

        private static EmptyPotInventoryRepository RepoFor(string cs)
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs })
                .Build();
            return new EmptyPotInventoryRepository(new DatabaseHelper(config));
        }

        [SkippableFact]
        public async Task GetOrCreateLockedAsync_TwoSimultaneousFirstTimeAllocations_SerializeCleanly_NoDuplicateRow()
        {
            var cs = Environment.GetEnvironmentVariable(ConnectionVariable);
            Skip.If(string.IsNullOrWhiteSpace(cs), $"Set {ConnectionVariable} to a {RequiredDatabase} connection string to run the database tests.");

            int areaId;
            string potSize;
            await using (var probe = await OpenAsync(cs!))
            {
                using var findCmd = new SqlCommand(@"
SELECT TOP 1 a.Id, ps.PotSize
FROM dbo.Area a
CROSS JOIN (SELECT '4 inch' AS PotSize UNION SELECT '6 inch' UNION SELECT '8 inch') ps
WHERE a.IsActive = 1
  AND NOT EXISTS (SELECT 1 FROM dbo.EmptyPotInventory e WHERE e.AreaId = a.Id AND e.PotSize = ps.PotSize)
ORDER BY a.Id", probe);
                using var reader = await findCmd.ExecuteReaderAsync();
                Skip.If(!await reader.ReadAsync(), "No (AreaId, PotSize) pair with zero existing EmptyPotInventory rows is available to test a first-time allocation against.");
                areaId = reader.GetInt32(0);
                potSize = reader.GetString(1);
            }

            var connA = await OpenAsync(cs!);
            var connB = await OpenAsync(cs!);
            await using var _a = connA;
            await using var _b = connB;

            var txA = connA.BeginTransaction();
            var txB = connB.BeginTransaction();
            await using var _ta = txA;
            await using var _tb = txB;

            try
            {
                var repoA = RepoFor(cs!);
                var repoB = RepoFor(cs!);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var taskA = repoA.GetOrCreateLockedAsync(connA, txA, potSize, areaId, "ConcurrencyTestA");
                var taskB = repoB.GetOrCreateLockedAsync(connB, txB, potSize, areaId, "ConcurrencyTestB");

                var idA = -1; var idB = -1;
                Exception? exA = null; Exception? exB = null;
                try { idA = await taskA; } catch (Exception ex) { exA = ex; }
                Console.WriteLine($"[{sw.ElapsedMilliseconds}ms] A resolved: id={idA} ex={exA?.GetType().Name}:{exA?.Message}");
                try { idB = await taskB; } catch (Exception ex) { exB = ex; }
                Console.WriteLine($"[{sw.ElapsedMilliseconds}ms] B resolved: id={idB} ex={exB?.GetType().Name}:{exB?.Message}");

                if (exA == null && exB == null)
                {
                    Assert.Equal(idA, idB);
                }
                else
                {
                    var failure = exA ?? exB;
                    Assert.NotNull(failure);
                    Assert.DoesNotContain("UQ_EmptyPotInventory_PotSize_AreaId", failure!.Message);
                    Assert.DoesNotContain("violation of UNIQUE", failure.Message, StringComparison.OrdinalIgnoreCase);
                }

                var winnerIsA = exA == null;
                var winnerIsB = exB == null;
                Assert.True(winnerIsA ^ winnerIsB, "Exactly one of the two simultaneous first-time allocations must succeed.");
                var (winnerConn, winnerTx) = winnerIsA ? (connA, txA) : (connB, txB);

                using var countCmd = new SqlCommand(
                    "SELECT COUNT(*) FROM dbo.EmptyPotInventory WHERE AreaId = @AreaId AND PotSize = @PotSize",
                    winnerConn, winnerTx);
                countCmd.Parameters.AddWithValue("@AreaId", areaId);
                countCmd.Parameters.AddWithValue("@PotSize", potSize);
                var count = (int)(await countCmd.ExecuteScalarAsync())!;
                Assert.Equal(1, count);
            }
            finally
            {
                try { txA.Rollback(); } catch { }
                try { txB.Rollback(); } catch { }
            }
        }

        // Confirms the lock key correctly includes PotSize/AreaId: a third,
        // unrelated (PotSize, AreaId) pair must proceed independently and
        // is never blocked by the two concurrent callers above contending
        // on a different key.
        [SkippableFact]
        public async Task GetOrCreateLockedAsync_UnrelatedPotSizeAreaCombination_IsNeverBlocked()
        {
            var cs = Environment.GetEnvironmentVariable(ConnectionVariable);
            Skip.If(string.IsNullOrWhiteSpace(cs), $"Set {ConnectionVariable} to a {RequiredDatabase} connection string to run the database tests.");

            using var conn = await OpenAsync(cs!);
            using var tx = conn.BeginTransaction();
            try
            {
                var repo = RepoFor(cs!);
                // An existing, already-allocated pool: resolving it must
                // return the same existing row instantly, never attempt an
                // insert, and never contend with any first-time-allocation
                // lock for a different key.
                using var existingCmd = new SqlCommand("SELECT TOP 1 AreaId, PotSize FROM dbo.EmptyPotInventory WHERE AreaId IS NOT NULL", conn, tx);
                using var reader = await existingCmd.ExecuteReaderAsync();
                Skip.If(!await reader.ReadAsync(), "No existing EmptyPotInventory row is available for the isolation check.");
                var existingAreaId = reader.GetInt32(0);
                var existingPotSize = reader.GetString(1);
                reader.Close();

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var id = await repo.GetOrCreateLockedAsync(conn, tx, existingPotSize, existingAreaId, "IsolationTest");
                Assert.True(id > 0);
                Assert.True(sw.ElapsedMilliseconds < 5000, "Resolving an already-existing, unrelated pool should never wait on another key's lock.");
            }
            finally
            {
                try { tx.Rollback(); } catch { }
            }
        }
    }
}
