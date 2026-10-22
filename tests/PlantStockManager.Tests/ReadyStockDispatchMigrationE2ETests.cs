using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace PlantStockManager.Tests
{
    // 2026-10-19 review: 2026-10-23_ReadyStockDispatchToCustomer.sql and its rollback, run END TO END the way sqlcmd runs them
    // (variables substituted, split on GO, stop at the first error) against a SCRATCH COPY only (PSM_SCRATCH_CONNECTION,
    // database name PlantsIMS2_Scratch_*; SKIPPED otherwise). Each test starts from a copy where the code does not exist yet
    // (skipped if it does) and leaves no grant or code of its own behind; change-log rows it appends stay (append-only history).
    //   - a code CREATED by the migration: the rollback removes the grant and the code, nothing else; both scripts repeatable
    //   - a PRE-EXISTING, unassigned code: the migration uses it as it is; the rollback removes only the grant, keeps the code
    //   - a created code another role also holds: the rollback keeps the code until no role references it
    //   - a wrong server / host / database / copy: refused before anything is read or written
    [Collection("ScratchDb")]
    public class ReadyStockDispatchMigrationE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";
        private const string Code = "ReadyStock.DispatchToCustomer";
        private const string Migration = "2026-10-23_ReadyStockDispatchToCustomer.sql";
        private const string Rollback = "2026-10-23_ReadyStockDispatchToCustomer_Rollback.sql";

        private sealed record Target(string Server, string Machine, string Database, string Created);
        private sealed record Outcome(bool Ok, int ErrorNumber, string Messages);

        private static string ScriptText(string file)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(dir!.FullName, "Database", "Migrations", file));
        }

        private static async Task<string> OpenAsync()
        {
            var cs = Environment.GetEnvironmentVariable(ConnectionVariable);
            Skip.If(string.IsNullOrWhiteSpace(cs), $"Set {ConnectionVariable} to a {RequiredPrefix}* database connection string to run these tests.");
            var name = await ScalarAsync<string>(cs!, "SELECT DB_NAME()");
            if (!name.StartsWith(RequiredPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException($"Refusing to run these tests against '{name}'. Only databases named {RequiredPrefix}* are allowed.");
            Skip.If(await ScalarAsync<int>(cs!, "SELECT COUNT(*) FROM dbo.Permissions WHERE Code = @C", ("@C", Code)) > 0,
                $"{Code} already exists on this copy -- these tests need a copy where the migration has not run.");
            Skip.If(await ScalarAsync<int>(cs!, "SELECT CASE WHEN OBJECT_ID('dbo.RolePermissionChangeLog', 'U') IS NULL THEN 0 ELSE 1 END") == 0,
                "dbo.RolePermissionChangeLog is missing on this copy.");
            return cs!;
        }

        private static async Task<T> ScalarAsync<T>(string cs, string sql, params (string Name, object? Value)[] args)
        {
            using var conn = new SqlConnection(cs);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(sql, conn);
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            var r = await cmd.ExecuteScalarAsync();
            return r == null || r == DBNull.Value ? default! : (T)Convert.ChangeType(r, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
        }

        private static async Task<List<string>> RowsAsync(string cs, string sql, params (string Name, object? Value)[] args)
        {
            var rows = new List<string>();
            using var conn = new SqlConnection(cs);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(sql, conn);
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                rows.Add(string.Join("|", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "<null>" : Convert.ToString(r.GetValue(i)))));
            return rows;
        }

        private static async Task<Target> TargetAsync(string cs)
        {
            var row = (await RowsAsync(cs, @"SELECT @@SERVERNAME, CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)), DB_NAME(), CONVERT(CHAR(19), d.create_date, 126)
FROM sys.databases d WHERE d.database_id = DB_ID()")).Single().Split('|');
            return new Target(row[0], row[1], row[2], row[3]);
        }

        // sqlcmd semantics for these two scripts: $(VAR) substitution, ':on error exit', batches split on GO, the first error stops.
        private static async Task<Outcome> RunAsync(string cs, string file, string mode, Target t)
        {
            var vars = new Dictionary<string, string>
            {
                ["MODE"] = mode, ["TARGET_SERVER"] = t.Server, ["TARGET_MACHINE"] = t.Machine, ["TARGET_DB"] = t.Database, ["TARGET_DB_CREATED"] = t.Created
            };
            var text = Regex.Replace(ScriptText(file), @"\$\((\w+)\)", m => vars.TryGetValue(m.Groups[1].Value, out var v) ? v.Replace("'", "''")
                : throw new InvalidOperationException($"'{m.Groups[1].Value}' scripting variable not defined."));
            text = Regex.Replace(text, @"^:on error exit\s*$", "", RegexOptions.Multiline);
            var batches = Regex.Split(text, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase).Where(b => b.Trim().Length > 0);
            var messages = new System.Text.StringBuilder();
            using var conn = new SqlConnection(cs);
            conn.InfoMessage += (_, e) => messages.AppendLine(e.Message);
            await conn.OpenAsync();
            foreach (var batch in batches)
            {
                try
                {
                    using var cmd = new SqlCommand(batch, conn) { CommandTimeout = 120 };
                    await cmd.ExecuteNonQueryAsync();
                }
                catch (SqlException ex)
                {
                    return new Outcome(false, ex.Number, messages + ex.Message);
                }
            }
            return new Outcome(true, 0, messages.ToString());
        }

        private static Task<int> OcGrantAsync(string cs) => ScalarAsync<int>(cs, @"SELECT COUNT(*) FROM dbo.RolePermissions rp
JOIN dbo.Roles r ON r.Id = rp.RoleId JOIN dbo.Permissions p ON p.Id = rp.PermissionId
WHERE COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = N'Office Coordinator' AND p.Code = @C", ("@C", Code));

        // Everything a rollback must preserve: every grant (as role|permission), every permission row, every log row.
        private static async Task<(List<string> Grants, List<string> Permissions, List<string> Log)> SnapshotAsync(string cs)
            => (await RowsAsync(cs, "SELECT RoleId, PermissionId FROM dbo.RolePermissions ORDER BY RoleId, PermissionId"),
                await RowsAsync(cs, "SELECT Id, Code, ISNULL(Description, N'') FROM dbo.Permissions ORDER BY Id"),
                await RowsAsync(cs, "SELECT Id, ChangedAt, RoleId, RoleName, PermissionCode, Change, Script, Reason FROM dbo.RolePermissionChangeLog ORDER BY Id"));

        private static void AssertOk(Outcome o) => Assert.True(o.Ok, o.Messages);

        // element-wise (a tuple of lists would otherwise compare list references)
        private static void AssertSame((List<string> Grants, List<string> Permissions, List<string> Log) expected,
            (List<string> Grants, List<string> Permissions, List<string> Log) actual)
        {
            Assert.Equal(expected.Grants, actual.Grants);
            Assert.Equal(expected.Permissions, actual.Permissions);
            Assert.Equal(expected.Log, actual.Log);
        }

        // Puts the copy back to "code absent" whatever a failed assertion left behind (scratch only).
        private static async Task CleanUpAsync(string cs, Target t)
        {
            if (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.Permissions WHERE Code = @C", ("@C", Code)) == 0)
                return;
            await RunAsync(cs, Rollback, "COMMIT", t);
            using var conn = new SqlConnection(cs);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"IF DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%' THROW 50000, 'scratch only', 1;
DELETE rp FROM dbo.RolePermissions rp JOIN dbo.Permissions p ON p.Id = rp.PermissionId WHERE p.Code = @C;
DELETE FROM dbo.Permissions WHERE Code = @C;", conn);
            cmd.Parameters.AddWithValue("@C", Code);
            await cmd.ExecuteNonQueryAsync();
        }

        [SkippableFact]
        public async Task CodeCreatedByTheMigration_RollbackRemovesOnlyTheGrantAndTheCode_BothScriptsRepeatable()
        {
            var cs = await OpenAsync();
            var t = await TargetAsync(cs);
            var before = await SnapshotAsync(cs);
            try
            {
                // DRYRUN changes nothing
                var dry = await RunAsync(cs, Migration, "DRYRUN", t);
                AssertOk(dry);
                Assert.Contains("created by this script", dry.Messages);
                Assert.Contains("DRY RUN -- rolled back", dry.Messages);
                AssertSame(before, await SnapshotAsync(cs));

                // COMMIT: the code is created with the owned marker, granted to Office Coordinator, logged as created here
                AssertOk(await RunAsync(cs, Migration, "COMMIT", t));
                var perm = await ScalarAsync<int>(cs, "SELECT Id FROM dbo.Permissions WHERE Code = @C", ("@C", Code));
                Assert.EndsWith("[created by 2026-10-23_ReadyStockDispatchToCustomer.sql]",
                    await ScalarAsync<string>(cs, "SELECT Description FROM dbo.Permissions WHERE Id = @P", ("@P", perm)));
                Assert.Equal(1, await OcGrantAsync(cs));
                Assert.Equal(1, await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.RolePermissions WHERE PermissionId = @P", ("@P", perm)));
                Assert.EndsWith($"[code created by this script; PermissionId={perm}]", await ScalarAsync<string>(cs,
                    "SELECT TOP 1 Reason FROM dbo.RolePermissionChangeLog WHERE PermissionCode = @C AND Change = N'Added' ORDER BY Id DESC", ("@C", Code)));
                var afterMigration = await SnapshotAsync(cs);
                // every pre-existing grant / permission / log row is still there, unchanged
                Assert.Empty(before.Grants.Except(afterMigration.Grants));
                Assert.Empty(before.Permissions.Except(afterMigration.Permissions));
                Assert.Equal(before.Log, afterMigration.Log.Take(before.Log.Count));
                Assert.Equal(before.Log.Count + 1, afterMigration.Log.Count);

                // the migration again: a no-op
                var again = await RunAsync(cs, Migration, "COMMIT", t);
                AssertOk(again);
                Assert.Contains("Already granted -- no-op", again.Messages);
                AssertSame(afterMigration, await SnapshotAsync(cs));

                // rollback DRYRUN: says what it would do, changes nothing
                var rollbackDry = await RunAsync(cs, Rollback, "DRYRUN", t);
                AssertOk(rollbackDry);
                Assert.Contains("(created by the migration) deleted", rollbackDry.Messages);
                AssertSame(afterMigration, await SnapshotAsync(cs));

                // rollback COMMIT: exactly the migration's grant and code are gone; one 'Removed' row appended; the rest intact
                AssertOk(await RunAsync(cs, Rollback, "COMMIT", t));
                var afterRollback = await SnapshotAsync(cs);
                Assert.Equal(before.Grants, afterRollback.Grants);
                Assert.Equal(before.Permissions, afterRollback.Permissions);
                Assert.Equal(afterMigration.Log, afterRollback.Log.Take(afterMigration.Log.Count));
                Assert.Equal(afterMigration.Log.Count + 1, afterRollback.Log.Count);
                Assert.Contains($"|Removed|{Rollback}|", afterRollback.Log.Last());

                // the rollback again: a no-op
                var rollbackAgain = await RunAsync(cs, Rollback, "COMMIT", t);
                AssertOk(rollbackAgain);
                Assert.Contains("nothing of the migration is left; no-op", rollbackAgain.Messages);
                AssertSame(afterRollback, await SnapshotAsync(cs));
            }
            finally
            {
                await CleanUpAsync(cs, t);
            }
        }

        [SkippableFact]
        public async Task PreExistingUnassignedCode_MigrationUsesItAsItIs_RollbackRemovesOnlyTheGrant_KeepsTheCode()
        {
            var cs = await OpenAsync();
            var t = await TargetAsync(cs);
            const string preExisting = "ZZTEST pre-existing code, created before the migration";
            try
            {
                // a code that exists before the migration, held by no role (test setup on the scratch copy)
                var perm = await ScalarAsync<int>(cs, @"IF COLUMNPROPERTY(OBJECT_ID('dbo.Permissions'), 'Id', 'IsIdentity') = 1
    INSERT INTO dbo.Permissions (Code, Description) VALUES (@C, @D);
ELSE
    INSERT INTO dbo.Permissions (Id, Code, Description) SELECT ISNULL(MAX(Id), 0) + 1, @C, @D FROM dbo.Permissions;
SELECT Id FROM dbo.Permissions WHERE Code = @C;", ("@C", Code), ("@D", preExisting));
                var before = await SnapshotAsync(cs);

                var migrated = await RunAsync(cs, Migration, "COMMIT", t);
                AssertOk(migrated);
                Assert.Contains("pre-existed -- used as it is", migrated.Messages);
                Assert.Equal(preExisting, await ScalarAsync<string>(cs, "SELECT Description FROM dbo.Permissions WHERE Id = @P", ("@P", perm)));
                Assert.Equal(1, await OcGrantAsync(cs));
                Assert.EndsWith($"[code pre-existed; PermissionId={perm}]", await ScalarAsync<string>(cs,
                    "SELECT TOP 1 Reason FROM dbo.RolePermissionChangeLog WHERE PermissionCode = @C AND Change = N'Added' ORDER BY Id DESC", ("@C", Code)));

                var rolledBack = await RunAsync(cs, Rollback, "COMMIT", t);
                AssertOk(rolledBack);
                Assert.Contains("KEPT: it was not created by the migration", rolledBack.Messages);
                // the code is still there, same Id and Description, held by no role; every other row as before; history appended only
                var after = await SnapshotAsync(cs);
                Assert.Equal(before.Grants, after.Grants);
                Assert.Equal(before.Permissions, after.Permissions);
                Assert.Equal(before.Log, after.Log.Take(before.Log.Count));
                Assert.Equal(before.Log.Count + 2, after.Log.Count);                    // the migration's 'Added' + the rollback's 'Removed'
                Assert.Equal(0, await OcGrantAsync(cs));

                // the rollback again: nothing more to remove, the code still kept
                var again = await RunAsync(cs, Rollback, "COMMIT", t);
                AssertOk(again);
                AssertSame(after, await SnapshotAsync(cs));
            }
            finally
            {
                // the test's own code (never a real one: OpenAsync skipped if the code existed) and nothing else
                using var conn = new SqlConnection(cs);
                await conn.OpenAsync();
                using var cmd = new SqlCommand(@"IF DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%' THROW 50000, 'scratch only', 1;
DELETE rp FROM dbo.RolePermissions rp JOIN dbo.Permissions p ON p.Id = rp.PermissionId WHERE p.Code = @C AND p.Description = @D;
DELETE FROM dbo.Permissions WHERE Code = @C AND Description = @D;", conn);
                cmd.Parameters.AddWithValue("@C", Code);
                cmd.Parameters.AddWithValue("@D", preExisting);
                await cmd.ExecuteNonQueryAsync();
                await CleanUpAsync(cs, t);
            }
        }

        [SkippableFact]
        public async Task CreatedCodeAlsoHeldByAnotherRole_RollbackRemovesOnlyTheOfficeCoordinatorGrant_KeepsTheCodeUntilUnreferenced()
        {
            var cs = await OpenAsync();
            var t = await TargetAsync(cs);
            var other = await ScalarAsync<int>(cs, @"SELECT TOP 1 Id FROM dbo.Roles
WHERE COALESCE(NULLIF(LTRIM(RTRIM(Name)), N''), RoleName) NOT IN (N'Office Coordinator', N'System Administrator', N'Bed Sowing Transplanter') ORDER BY Id");
            Skip.If(other == 0, "No second role on this copy.");
            try
            {
                AssertOk(await RunAsync(cs, Migration, "COMMIT", t));
                var perm = await ScalarAsync<int>(cs, "SELECT Id FROM dbo.Permissions WHERE Code = @C", ("@C", Code));
                await ScalarAsync<int>(cs, "INSERT INTO dbo.RolePermissions (RoleId, PermissionId) VALUES (@R, @P); SELECT 1", ("@R", other), ("@P", perm));

                var first = await RunAsync(cs, Rollback, "COMMIT", t);
                AssertOk(first);
                Assert.Contains("KEPT: another role still holds", first.Messages);
                Assert.Equal(0, await OcGrantAsync(cs));
                Assert.Equal(1, await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.RolePermissions WHERE RoleId = @R AND PermissionId = @P", ("@R", other), ("@P", perm)));
                Assert.Equal(1, await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.Permissions WHERE Id = @P", ("@P", perm)));

                // once the other role no longer holds it, a repeated rollback deletes the migration-owned code
                await ScalarAsync<int>(cs, "DELETE FROM dbo.RolePermissions WHERE RoleId = @R AND PermissionId = @P; SELECT 1", ("@R", other), ("@P", perm));
                var second = await RunAsync(cs, Rollback, "COMMIT", t);
                AssertOk(second);
                Assert.Contains("(created by the migration) deleted", second.Messages);
                Assert.Equal(0, await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.Permissions WHERE Code = @C", ("@C", Code)));
            }
            finally
            {
                await ScalarAsync<int>(cs, @"DELETE rp FROM dbo.RolePermissions rp JOIN dbo.Permissions p ON p.Id = rp.PermissionId
WHERE p.Code = @C AND rp.RoleId = @R; SELECT 1", ("@C", Code), ("@R", other));
                await CleanUpAsync(cs, t);
            }
        }

        [SkippableFact]
        public async Task WrongServerHostDatabaseOrCopy_BothScriptsRefuseBeforeAnyChange()
        {
            var cs = await OpenAsync();
            var t = await TargetAsync(cs);
            var before = await SnapshotAsync(cs);
            var cases = new (Target Target, string Mode, int Migration, int Rollback)[]
            {
                (t with { Server = t.Server + "-OTHER" }, "COMMIT", 52613, 52623),
                (t with { Machine = t.Machine + "-OTHER" }, "COMMIT", 52618, 52628),
                (t with { Database = "PlantsIMS2_Test" == t.Database ? "PlantsIMS2_Scratch_Other" : "PlantsIMS2_Test" }, "COMMIT", 52619, 52629),
                (t with { Created = "2000-01-01T00:00:00" }, "COMMIT", 52620, 52630),
                (t, "APPLY", 52612, 52622),
            };
            foreach (var (target, mode, migrationError, rollbackError) in cases)
            {
                var m = await RunAsync(cs, Migration, mode, target);
                Assert.False(m.Ok);
                Assert.Equal(migrationError, m.ErrorNumber);
                var r = await RunAsync(cs, Rollback, mode, target);
                Assert.False(r.Ok);
                Assert.Equal(rollbackError, r.ErrorNumber);
            }
            AssertSame(before, await SnapshotAsync(cs));
            Assert.Equal(0, await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.Permissions WHERE Code = @C", ("@C", Code)));
        }
    }
}
