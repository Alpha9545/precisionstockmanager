using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using PlantStockManager.Data;

namespace PlantStockManager.Tests
{
    // Focused regression test for the first-time-allocation race in
    // TrayStockRepository.GetOrCreateLockedAsync: two callers creating the
    // SAME brand-new (AreaId, PolyhouseId, TraySize) pool at once must serialize
    // cleanly on the sp_getapplock mutex, never both insert, never throw an
    // unhandled unique-constraint exception.
    //
    // Runs only when PSM_TEST_CONNECTION holds a connection string, and only
    // against PlantsIMS2_Test (any other database is refused) -- same guard
    // as DeleteIntegrationTests. Uses TWO independent connections (a real
    // cross-connection race needs two separate transactions), and BOTH are
    // always rolled back, so nothing this test does is ever kept: it picks
    // a Polyhouse of an active, non-Outlet Area that has no pool of the
    // chosen cavity yet, so it is guaranteed "first time", and never touches
    // any row that already exists.
    public class TrayStockConcurrencyTests
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

        private static TrayStockRepository RepoFor(string cs)
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs })
                .Build();
            return new TrayStockRepository(new DatabaseHelper(config));
        }

        [SkippableFact]
        public async Task GetOrCreateLockedAsync_TwoSimultaneousFirstTimeAllocations_SerializeCleanly_NoDuplicateRow()
        {
            var cs = Environment.GetEnvironmentVariable(ConnectionVariable);
            Skip.If(string.IsNullOrWhiteSpace(cs), $"Set {ConnectionVariable} to a {RequiredDatabase} connection string to run the database tests.");

            // Pick a real Polyhouse of an active, non-Outlet Area with no pool
            // of this cavity yet, so it is a genuine first-time pool for it --
            // never touches a pool that already exists.
            int areaId, polyhouseId;
            const string traySize = "102 Cavity"; // any CHECK-constrained value
            await using (var probe = await OpenAsync(cs!))
            {
                using var findCmd = new SqlCommand(@"
SELECT TOP 1 a.Id, p.Id FROM dbo.Polyhouses p
INNER JOIN dbo.Area a ON a.Id = p.AreaId
WHERE a.IsActive = 1 AND (a.AreaType IS NULL OR a.AreaType <> N'Outlet')
  AND NOT EXISTS (SELECT 1 FROM dbo.TrayStock t WHERE t.AreaId = a.Id AND t.PolyhouseId = p.Id AND t.TraySize = N'102 Cavity')
ORDER BY a.Id, p.Id", probe);
                using var reader = await findCmd.ExecuteReaderAsync();
                var found = await reader.ReadAsync();
                Skip.If(!found, "No Polyhouse without a 102 Cavity pool is available to test a first-time pool against.");
                areaId = reader.GetInt32(0);
                polyhouseId = reader.GetInt32(1);
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
                var taskA = repoA.GetOrCreateLockedAsync(connA, txA, areaId, polyhouseId, traySize, "ConcurrencyTestA");
                var taskB = repoB.GetOrCreateLockedAsync(connB, txB, areaId, polyhouseId, traySize, "ConcurrencyTestB");

                var idA = -1; var idB = -1;
                Exception? exA = null; Exception? exB = null;
                try { idA = await taskA; } catch (Exception ex) { exA = ex; }
                Console.WriteLine($"[{sw.ElapsedMilliseconds}ms] A resolved: id={idA} ex={exA?.GetType().Name}:{exA?.Message}");
                try { idB = await taskB; } catch (Exception ex) { exB = ex; }
                Console.WriteLine($"[{sw.ElapsedMilliseconds}ms] B resolved: id={idB} ex={exB?.GetType().Name}:{exB?.Message}");

                // Both calls must resolve to the SAME row id (no duplicate
                // row was ever created), OR the loser fails cleanly with a
                // friendly message instead of a raw SQL exception.
                if (exA == null && exB == null)
                {
                    Assert.Equal(idA, idB);
                }
                else
                {
                    var failure = exA ?? exB;
                    Assert.NotNull(failure);
                    Assert.DoesNotContain("UX_TrayStock_Area_Polyhouse_Size", failure!.Message);
                    Assert.DoesNotContain("violation of UNIQUE", failure.Message, StringComparison.OrdinalIgnoreCase);
                }

                // Exactly one side must have actually created the row.
                // Whichever did holds the only lock on it (UPDLOCK,HOLDLOCK)
                // for the rest of its own open transaction -- verifying the
                // count from the OTHER (losing/uninvolved) connection would
                // itself block as a reader against that still-open writer,
                // which is a self-inflicted hang in the test, not something
                // the production code needs to tolerate. So the count is
                // read from the winner's own connection/transaction, the
                // same way the real Sowing/Allocate transactions that call
                // GetOrCreateLockedAsync always read it.
                var winnerIsA = exA == null;
                var winnerIsB = exB == null;
                Assert.True(winnerIsA ^ winnerIsB, "Exactly one of the two simultaneous first-time allocations must succeed.");
                var (winnerConn, winnerTx) = winnerIsA ? (connA, txA) : (connB, txB);

                using var countCmd = new SqlCommand(
                    "SELECT COUNT(*) FROM dbo.TrayStock WHERE AreaId = @AreaId AND PolyhouseId = @PolyhouseId AND TraySize = @TraySize",
                    winnerConn, winnerTx);
                countCmd.Parameters.AddWithValue("@AreaId", areaId);
                countCmd.Parameters.AddWithValue("@PolyhouseId", polyhouseId);
                countCmd.Parameters.AddWithValue("@TraySize", traySize);
                var count = (int)(await countCmd.ExecuteScalarAsync())!;
                Assert.Equal(1, count);
            }
            finally
            {
                // ALWAYS roll back both: nothing this test does is ever kept.
                try { txA.Rollback(); } catch { }
                try { txB.Rollback(); } catch { }
            }
        }
    }
}
