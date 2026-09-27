using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using PlantStockManager.Data;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Dependency-aware Delete / Deactivate against a REAL SQL Server.
    //
    // Runs only when PSM_TEST_CONNECTION holds a connection string, and only
    // against the PlantsIMS2_Test database (any other database is refused).
    // Otherwise every test here is SKIPPED, never silently passed.
    //
    // Safety: each test runs inside ONE transaction that is ALWAYS rolled
    // back, so nothing a test creates, changes or deletes is ever kept. Test
    // rows are named ZZTEST-...; the only real rows touched are (a) one real
    // Species / active user / role looked up as foreign-key values, and
    // (b) for the Seed Sowing / Ready Stock cases, one real Area that already
    // has such records -- its delete is expected to be REFUSED, and the test
    // proves nothing changed before the rollback.
    //
    //   PowerShell:  $env:PSM_TEST_CONNECTION = "Server=...;Database=PlantsIMS2_Test;..."
    //                dotnet test --filter FullyQualifiedName~DeleteIntegrationTests
    public class DeleteIntegrationTests
    {
        private const string ConnectionVariable = "PSM_TEST_CONNECTION";
        private const string RequiredDatabase = "PlantsIMS2_Test";

        private sealed class TestDb : IAsyncDisposable
        {
            public SqlConnection Conn { get; }
            public SqlTransaction Tx { get; }
            public AreaRepository Areas { get; }
            public MotherPlantRepository MotherPlants { get; }
            private readonly string _suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

            private TestDb(SqlConnection conn, SqlTransaction tx, DatabaseHelper helper)
            {
                Conn = conn;
                Tx = tx;
                Areas = new AreaRepository(helper);
                MotherPlants = new MotherPlantRepository(helper, new BatchNumberRepository(helper));
            }

            public static async Task<TestDb> OpenAsync()
            {
                var cs = Environment.GetEnvironmentVariable(ConnectionVariable);
                Skip.If(string.IsNullOrWhiteSpace(cs), $"Set {ConnectionVariable} to a {RequiredDatabase} connection string to run the database tests.");

                var conn = new SqlConnection(cs);
                await conn.OpenAsync();
                using (var dbName = new SqlCommand("SELECT DB_NAME()", conn))
                {
                    var name = (string)(await dbName.ExecuteScalarAsync())!;
                    if (!string.Equals(name, RequiredDatabase, StringComparison.OrdinalIgnoreCase))
                    {
                        await conn.DisposeAsync();
                        throw new InvalidOperationException($"Refusing to run database tests against '{name}'. Only {RequiredDatabase} is allowed.");
                    }
                }
                var config = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs })
                    .Build();
                return new TestDb(conn, conn.BeginTransaction(), new DatabaseHelper(config));
            }

            public async ValueTask DisposeAsync()
            {
                try { Tx.Rollback(); } catch { }   // ALWAYS roll back: nothing is kept
                await Conn.DisposeAsync();
            }

            public string Name(string kind) => $"ZZTEST-{kind}-{_suffix}";

            public async Task<object?> ScalarAsync(string sql, params (string Name, object? Value)[] args)
            {
                using var cmd = new SqlCommand(sql, Conn, Tx);
                foreach (var (n, v) in args)
                    cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
                var result = await cmd.ExecuteScalarAsync();
                return result == DBNull.Value ? null : result;
            }

            public async Task<int> IntAsync(string sql, params (string Name, object? Value)[] args)
                => Convert.ToInt32(await ScalarAsync(sql, args));

            public async Task<int> NewAreaAsync()
                => await IntAsync(@"
INSERT INTO dbo.Area (Name, IsActive, CreatedAt, AreaType, Remarks)
VALUES (@Name, 1, SYSUTCDATETIME(), N'Kunjir', N'ZZTEST delete test');
SELECT CAST(SCOPE_IDENTITY() AS INT);", ("@Name", Name("AREA")));

            public async Task<int> RealSpeciesIdAsync()
            {
                var id = await ScalarAsync("SELECT TOP 1 Id FROM dbo.PlantSpecies ORDER BY Id");
                Skip.If(id == null, "No PlantSpecies row exists.");
                return Convert.ToInt32(id);
            }

            // A Mother Plant needs (enforced by TR_MotherPlants_AreaAndSupervisor)
            // a Polyhouse of its Area and an active "Mother Plant Supervisor" of
            // its Area: both are created here as ZZTEST rows in the same
            // rolled-back transaction.
            public async Task<(int MotherPlantId, int SpeciesId, int SupervisorId)> NewMotherPlantAsync(int areaId)
            {
                var speciesId = await RealSpeciesIdAsync();
                var userId = await ScalarAsync("SELECT TOP 1 Id FROM dbo.IMSUsers WHERE IsActive = 1 ORDER BY Id");
                Skip.If(userId == null, "No active IMSUsers row exists.");
                var roleId = await ScalarAsync(
                    "SELECT TOP 1 Id FROM dbo.Roles WHERE COALESCE(NULLIF(LTRIM(RTRIM(Name)), N''), RoleName) = N'Mother Plant Supervisor'");
                Skip.If(roleId == null, "The 'Mother Plant Supervisor' role does not exist.");

                await ScalarAsync("INSERT INTO dbo.UserRoles (UserId, RoleId, AreaId) VALUES (@U, @R, @A)",
                    ("@U", userId), ("@R", roleId), ("@A", areaId));
                var polyhouseId = await IntAsync(
                    "INSERT INTO dbo.Polyhouses (Name, AreaId) VALUES (@N, @A); SELECT CAST(SCOPE_IDENTITY() AS INT);",
                    ("@N", Name("PH")), ("@A", areaId));

                var mpId = await IntAsync(@"
INSERT INTO dbo.MotherPlants
(MotherPlantCode, PolyhouseId, SpeciesId, AreaId, SupervisorId, PlantingDate, MotherPlantQuantity, CuttingPeriodDays, CuttingRate,
 ExpectedCuttingQuantity, ExpectedMonthlyCuttingQuantity, Status, Remarks, CreatedDate, CreatedBy)
VALUES
(@Code, @Polyhouse, @Species, @Area, @Supervisor, CAST(GETDATE() AS DATE), 10, 30, 1, 10, 10, N'Active', N'ZZTEST delete test', SYSUTCDATETIME(), N'ZZTEST');
SELECT CAST(SCOPE_IDENTITY() AS INT);",
                    ("@Code", Name("MP")), ("@Polyhouse", polyhouseId), ("@Species", speciesId), ("@Area", areaId), ("@Supervisor", userId));
                return (mpId, speciesId, Convert.ToInt32(userId));
            }

            public async Task<int> NewCuttingPlanAsync(int motherPlantId, int speciesId)
                => await IntAsync(@"
INSERT INTO dbo.CuttingPlans (PlanNumber, MotherPlantId, SpeciesId, PlannedCuttingDate, PlannedQuantity, CuttingRate, Status, Remarks, CreatedBy)
VALUES (@Code, @Mp, @Species, CAST(GETDATE() AS DATE), 10, 1, N'Planned', N'ZZTEST', N'ZZTEST');
SELECT CAST(SCOPE_IDENTITY() AS INT);", ("@Code", Name("CP")), ("@Mp", motherPlantId), ("@Species", speciesId));

            // Row counts + checksums of the parent row and every table that
            // can reference it -- used to prove a refused delete changed nothing.
            public async Task<string> SnapshotAsync(string parentTable, int id, IEnumerable<DependencyDefinition> deps)
            {
                var parts = new List<string>
                {
                    $"parent={await ScalarAsync($"SELECT CHECKSUM_AGG(BINARY_CHECKSUM(*)) FROM {parentTable} WHERE Id = @Id", ("@Id", id))}"
                };
                foreach (var table in deps.Select(d => d.Table).Distinct())
                {
                    var exists = await IntAsync("SELECT CASE WHEN OBJECT_ID(@T, N'U') IS NULL THEN 0 ELSE 1 END", ("@T", $"dbo.{table}"));
                    if (exists == 0) continue;
                    parts.Add($"{table}={await ScalarAsync($"SELECT CAST(COUNT(*) AS NVARCHAR(20)) + N':' + ISNULL(CAST(CHECKSUM_AGG(BINARY_CHECKSUM(*)) AS NVARCHAR(20)), N'-') FROM [dbo].[{table}]")}");
                }
                return string.Join("|", parts);
            }
        }

        private static void AssertBlockedBy(DeleteResult result, string label)
        {
            Assert.Equal(DeleteOutcome.Blocked, result.Outcome);
            Assert.Contains(result.Dependencies, d => d.Label == label && d.Count > 0);
            Assert.Contains("cannot be deleted because it is being used by existing records", result.Message);
        }

        // 1
        [SkippableFact]
        public async Task Area_WithNoDependencies_IsDeleted()
        {
            await using var db = await TestDb.OpenAsync();
            var areaId = await db.NewAreaAsync();

            var result = await db.Areas.DeleteAsync(db.Conn, db.Tx, areaId);

            Assert.Equal(DeleteOutcome.Deleted, result.Outcome);
            Assert.All(result.Dependencies, d => Assert.Equal(0, d.Count));
            Assert.Equal(0, await db.IntAsync("SELECT COUNT(*) FROM dbo.Area WHERE Id = @Id", ("@Id", areaId)));
        }

        // 2 -- uses an existing Area that already has Seed Sowings (a sowing
        //      needs a real seed lot, tray rules and a Sowing Supervisor, so it
        //      is not fabricated); the delete must be refused.
        [SkippableFact]
        public async Task Area_WithSeedSowings_CannotBeDeleted()
        {
            await using var db = await TestDb.OpenAsync();
            var areaId = await db.ScalarAsync("SELECT TOP 1 AreaId FROM dbo.SeedSowings ORDER BY Id");
            Skip.If(areaId == null, "No Area with Seed Sowings exists in this database.");
            var id = Convert.ToInt32(areaId);
            var before = await db.SnapshotAsync("dbo.Area", id, DeletionRules.AreaDependencies);

            var result = await db.Areas.DeleteAsync(db.Conn, db.Tx, id);

            AssertBlockedBy(result, "Seed Sowings");
            Assert.Equal(before, await db.SnapshotAsync("dbo.Area", id, DeletionRules.AreaDependencies));
        }

        // 3
        [SkippableFact]
        public async Task Area_WithMotherPlants_CannotBeDeleted()
        {
            await using var db = await TestDb.OpenAsync();
            var areaId = await db.NewAreaAsync();
            await db.NewMotherPlantAsync(areaId);

            var result = await db.Areas.DeleteAsync(db.Conn, db.Tx, areaId);

            AssertBlockedBy(result, "Mother Plants");
            Assert.Equal(1, await db.IntAsync("SELECT COUNT(*) FROM dbo.Area WHERE Id = @Id", ("@Id", areaId)));
        }

        // 4 -- existing Area with Ready Stock (see test 2 for why it is not fabricated).
        [SkippableFact]
        public async Task Area_WithReadyStock_CannotBeDeleted()
        {
            await using var db = await TestDb.OpenAsync();
            var areaId = await db.ScalarAsync("SELECT TOP 1 AreaId FROM dbo.ReadyStock ORDER BY Id");
            Skip.If(areaId == null, "No Area with Ready Stock exists in this database.");
            var id = Convert.ToInt32(areaId);
            var before = await db.SnapshotAsync("dbo.Area", id, DeletionRules.AreaDependencies);

            var result = await db.Areas.DeleteAsync(db.Conn, db.Tx, id);

            AssertBlockedBy(result, "Ready Stock");
            Assert.Equal(before, await db.SnapshotAsync("dbo.Area", id, DeletionRules.AreaDependencies));
        }

        // 4b -- another kind of dependent record, fully ZZTEST: a Cutting Stock row.
        [SkippableFact]
        public async Task Area_WithStockRecords_CannotBeDeleted()
        {
            await using var db = await TestDb.OpenAsync();
            var areaId = await db.NewAreaAsync();
            var speciesId = await db.RealSpeciesIdAsync();
            await db.ScalarAsync(
                "INSERT INTO dbo.CuttingStock (SpeciesId, AreaId, PhysicalQuantity, CreatedDate, CreatedBy) VALUES (@S, @A, 0, SYSUTCDATETIME(), N'ZZTEST')",
                ("@S", speciesId), ("@A", areaId));

            var result = await db.Areas.DeleteAsync(db.Conn, db.Tx, areaId);

            AssertBlockedBy(result, "Cutting Stock");
            Assert.Equal(1, await db.IntAsync("SELECT COUNT(*) FROM dbo.Area WHERE Id = @Id", ("@Id", areaId)));
        }

        // 5
        [SkippableFact]
        public async Task MotherPlant_WithNoDependencies_IsDeleted()
        {
            await using var db = await TestDb.OpenAsync();
            var (mpId, _, _) = await db.NewMotherPlantAsync(await db.NewAreaAsync());

            var result = await db.MotherPlants.DeleteAsync(db.Conn, db.Tx, mpId);

            Assert.Equal(DeleteOutcome.Deleted, result.Outcome);
            Assert.Equal(0, await db.IntAsync("SELECT COUNT(*) FROM dbo.MotherPlants WHERE Id = @Id", ("@Id", mpId)));
        }

        // 6
        [SkippableFact]
        public async Task MotherPlant_WithCuttingProductions_CannotBeDeleted()
        {
            await using var db = await TestDb.OpenAsync();
            var areaId = await db.NewAreaAsync();
            var (mpId, speciesId, supervisorId) = await db.NewMotherPlantAsync(areaId);
            var stockId = await db.IntAsync(@"
INSERT INTO dbo.CuttingStock (SpeciesId, AreaId, PhysicalQuantity, CreatedDate, CreatedBy) VALUES (@S, @A, 0, SYSUTCDATETIME(), N'ZZTEST');
SELECT CAST(SCOPE_IDENTITY() AS INT);", ("@S", speciesId), ("@A", areaId));
            await db.ScalarAsync(@"
INSERT INTO dbo.CuttingProductions (ProductionCode, MotherPlantId, SpeciesId, AreaId, CuttingStockId, CuttingDate, Quantity, SupervisorId, Remarks, CreatedBy)
VALUES (@Code, @Mp, @S, @A, @Stock, CAST(GETDATE() AS DATE), 5, @Sup, N'ZZTEST', N'ZZTEST')",
                ("@Code", db.Name("CPROD")), ("@Mp", mpId), ("@S", speciesId), ("@A", areaId), ("@Stock", stockId), ("@Sup", supervisorId));

            var result = await db.MotherPlants.DeleteAsync(db.Conn, db.Tx, mpId);

            AssertBlockedBy(result, "Cutting Productions");
            Assert.Equal(1, await db.IntAsync("SELECT COUNT(*) FROM dbo.MotherPlants WHERE Id = @Id", ("@Id", mpId)));
        }

        // 7
        [SkippableFact]
        public async Task MotherPlant_WithCuttingPlans_CannotBeDeleted()
        {
            await using var db = await TestDb.OpenAsync();
            var (mpId, speciesId, _) = await db.NewMotherPlantAsync(await db.NewAreaAsync());
            await db.NewCuttingPlanAsync(mpId, speciesId);

            var result = await db.MotherPlants.DeleteAsync(db.Conn, db.Tx, mpId);

            AssertBlockedBy(result, "Cutting Plans");
        }

        // 8
        [SkippableFact]
        public async Task MotherPlant_WithActualCuttings_CannotBeDeleted()
        {
            await using var db = await TestDb.OpenAsync();
            var (mpId, speciesId, _) = await db.NewMotherPlantAsync(await db.NewAreaAsync());
            var planId = await db.NewCuttingPlanAsync(mpId, speciesId);
            await db.ScalarAsync(@"
INSERT INTO dbo.ActualCuttings (ActualCuttingCode, CuttingPlanId, MotherPlantId, SpeciesId, CuttingDate, PlannedQuantity, ActualQuantity, GoodQuantity, DamagedQuantity, RejectedQuantity, Remarks, CreatedBy)
VALUES (@Code, @Plan, @Mp, @S, CAST(GETDATE() AS DATE), 10, 5, 5, 0, 0, N'ZZTEST', N'ZZTEST')",
                ("@Code", db.Name("AC")), ("@Plan", planId), ("@Mp", mpId), ("@S", speciesId));

            var result = await db.MotherPlants.DeleteAsync(db.Conn, db.Tx, mpId);

            AssertBlockedBy(result, "Actual Cuttings");
        }

        // 9
        [SkippableFact]
        public async Task FailedDeletion_DoesNotModifyAnyRecord()
        {
            await using var db = await TestDb.OpenAsync();
            var areaId = await db.NewAreaAsync();
            var (mpId, speciesId, _) = await db.NewMotherPlantAsync(areaId);
            await db.NewCuttingPlanAsync(mpId, speciesId);

            var areaBefore = await db.SnapshotAsync("dbo.Area", areaId, DeletionRules.AreaDependencies);
            var mpBefore = await db.SnapshotAsync("dbo.MotherPlants", mpId, DeletionRules.MotherPlantDependencies);

            var areaResult = await db.Areas.DeleteAsync(db.Conn, db.Tx, areaId);
            var mpResult = await db.MotherPlants.DeleteAsync(db.Conn, db.Tx, mpId);

            Assert.Equal(DeleteOutcome.Blocked, areaResult.Outcome);
            Assert.Equal(DeleteOutcome.Blocked, mpResult.Outcome);
            Assert.Equal(areaBefore, await db.SnapshotAsync("dbo.Area", areaId, DeletionRules.AreaDependencies));
            Assert.Equal(mpBefore, await db.SnapshotAsync("dbo.MotherPlants", mpId, DeletionRules.MotherPlantDependencies));
        }

        // 10
        [SkippableFact]
        public async Task Deactivation_WorksWhenDeleteIsRefused_AndKeepsAllRecords()
        {
            await using var db = await TestDb.OpenAsync();
            var areaId = await db.NewAreaAsync();
            var (mpId, speciesId, _) = await db.NewMotherPlantAsync(areaId);
            await db.NewCuttingPlanAsync(mpId, speciesId);
            Assert.Equal(DeleteOutcome.Blocked, (await db.MotherPlants.DeleteAsync(db.Conn, db.Tx, mpId)).Outcome);
            Assert.Equal(DeleteOutcome.Blocked, (await db.Areas.DeleteAsync(db.Conn, db.Tx, areaId)).Outcome);

            var (ok, _) = await db.MotherPlants.DeactivateAsync(db.Conn, db.Tx, mpId, "ZZTEST");
            Assert.True(ok);
            Assert.Equal(DeletionRules.MotherPlantDeactivatedStatus,
                (string?)await db.ScalarAsync("SELECT Status FROM dbo.MotherPlants WHERE Id = @Id", ("@Id", mpId)));
            Assert.Equal(1, await db.IntAsync("SELECT COUNT(*) FROM dbo.CuttingPlans WHERE MotherPlantId = @Id", ("@Id", mpId)));
            var (again, _) = await db.MotherPlants.DeactivateAsync(db.Conn, db.Tx, mpId, "ZZTEST");
            Assert.False(again);   // already deactivated

            // The Area has history (a Mother Plant, a Cutting Plan) but no
            // current stock, so it CAN be deactivated.
            var deactivated = await db.Areas.DeactivateAsync(db.Conn, db.Tx, areaId);
            Assert.True(deactivated.Succeeded);
            Assert.Equal(0, await db.IntAsync("SELECT CAST(IsActive AS INT) FROM dbo.Area WHERE Id = @Id", ("@Id", areaId)));
            Assert.Equal(1, await db.IntAsync("SELECT COUNT(*) FROM dbo.MotherPlants WHERE Id = @Id", ("@Id", mpId)));
            Assert.True(await db.Areas.ActivateAsync(db.Conn, db.Tx, areaId));
            Assert.Equal(1, await db.IntAsync("SELECT CAST(IsActive AS INT) FROM dbo.Area WHERE Id = @Id", ("@Id", areaId)));
        }

        // 11 -- an Area holding current stock cannot be deactivated, and the
        //       refused attempt changes nothing.
        [SkippableFact]
        public async Task Area_WithCurrentStock_CannotBeDeactivated()
        {
            await using var db = await TestDb.OpenAsync();
            var areaId = await db.NewAreaAsync();
            var speciesId = await db.RealSpeciesIdAsync();
            await db.ScalarAsync(
                "INSERT INTO dbo.CuttingStock (SpeciesId, AreaId, PhysicalQuantity, CreatedDate, CreatedBy) VALUES (@S, @A, 5, SYSUTCDATETIME(), N'ZZTEST')",
                ("@S", speciesId), ("@A", areaId));
            var before = await db.SnapshotAsync("dbo.Area", areaId, DeletionRules.AreaDependencies);

            var result = await db.Areas.DeactivateAsync(db.Conn, db.Tx, areaId);

            Assert.False(result.Succeeded);
            Assert.False(result.NotFound);
            Assert.Contains(result.Stock, s => s.Label == "Cutting Stock" && s.Count == 1);
            Assert.StartsWith("This Area cannot be deactivated because it currently contains stock", result.Message);
            Assert.EndsWith("Move or clear the stock before deactivating this Area.", result.Message);
            Assert.Equal(1, await db.IntAsync("SELECT CAST(IsActive AS INT) FROM dbo.Area WHERE Id = @Id", ("@Id", areaId)));
            Assert.Equal(before, await db.SnapshotAsync("dbo.Area", areaId, DeletionRules.AreaDependencies));
        }

        // 12 -- stock rows that are empty (history only) do not block deactivation.
        [SkippableFact]
        public async Task Area_WithOnlyEmptyStockRows_CanBeDeactivated()
        {
            await using var db = await TestDb.OpenAsync();
            var areaId = await db.NewAreaAsync();
            var speciesId = await db.RealSpeciesIdAsync();
            await db.ScalarAsync(
                "INSERT INTO dbo.CuttingStock (SpeciesId, AreaId, PhysicalQuantity, CreatedDate, CreatedBy) VALUES (@S, @A, 0, SYSUTCDATETIME(), N'ZZTEST')",
                ("@S", speciesId), ("@A", areaId));

            Assert.Equal(DeleteOutcome.Blocked, (await db.Areas.DeleteAsync(db.Conn, db.Tx, areaId)).Outcome);   // still in use
            var result = await db.Areas.DeactivateAsync(db.Conn, db.Tx, areaId);

            Assert.True(result.Succeeded);
            Assert.All(result.Stock, s => Assert.Equal(0, s.Count));
            Assert.Equal(0, await db.IntAsync("SELECT CAST(IsActive AS INT) FROM dbo.Area WHERE Id = @Id", ("@Id", areaId)));
        }

        // The run-time check covers every foreign key the live database
        // declares against dbo.Area / dbo.MotherPlants (read-only).
        [SkippableFact]
        public async Task DependencyCheck_CoversEveryForeignKeyInTheDatabase()
        {
            await using var db = await TestDb.OpenAsync();
            var areaId = await db.NewAreaAsync();
            var counts = await DependencyChecker.CountAsync(db.Conn, db.Tx, DeletionRules.AreaTable, DeletionRules.AreaDependencies, areaId);

            using var cmd = new SqlCommand(@"
SELECT OBJECT_NAME(fkc.parent_object_id) + N'.' + COL_NAME(fkc.parent_object_id, fkc.parent_column_id)
FROM sys.foreign_key_columns fkc
WHERE fkc.referenced_object_id = OBJECT_ID(N'dbo.Area') AND COL_NAME(fkc.referenced_object_id, fkc.referenced_column_id) = N'Id'", db.Conn, db.Tx);
            var fks = new List<string>();
            using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync())
                    fks.Add(r.GetString(0));

            Assert.All(fks, fk => Assert.Contains(counts, c => $"{c.Table}.{c.Column}".Equals(fk, StringComparison.OrdinalIgnoreCase)));
        }
    }
}
