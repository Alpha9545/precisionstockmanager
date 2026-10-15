using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using PlantStockManager.Data;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // 2026-10-15 duplicate-variety cleanup: PlantSpecies.IsActive + UX_PlantSpecies_Name_Active.
    // Name rule (no database) + end-to-end against a SCRATCH COPY (PlantsIMS2_Scratch_* only; SKIPPED without
    // PSM_SCRATCH_CONNECTION or the two 2026-10-15 migrations). Every test makes its OWN "ZZTEST Var ..." varieties.
    public class VarietyNameRuleTests
    {
        [Theory]
        [InlineData("Lantana Beach", "lantana beach")]
        [InlineData("Lantana Beach", "  Lantana Beach \r\n")]
        [InlineData("Lantana Beach", "Lantana\tBeach")]
        [InlineData("Lantana  Beach", "LANTANA BEACH")]
        public void SameName_IgnoresCapitalsAndSpaces(string a, string b) => Assert.True(PlantSpeciesNameRules.SameName(a, b));

        [Theory]
        [InlineData("Lantana Beach", "Latania Beach")]
        [InlineData("Petunia S.C. White", "Petunia SC White")]
        [InlineData("Jayashree Bronze Red", "Jayashree Bronze Red TC")]
        public void DifferentSpelling_IsADifferentName(string a, string b) => Assert.False(PlantSpeciesNameRules.SameName(a, b));

        [Theory]
        [InlineData("DUP-3926-Lantana Beach", "Lantana Beach")]
        [InlineData("dup-3134-M.G Hot pack mix", "M.G Hot pack mix")]
        [InlineData("  DUP-12-SH-WR", "SH-WR")]
        [InlineData("DUP-Old name", "Old name")]
        public void MergedDuplicateName_IsRecognised_AndGivesTheOldName(string name, string from)
        {
            Assert.True(PlantSpeciesNameRules.IsMergedDuplicate(name));
            Assert.Equal(from, PlantSpeciesNameRules.MergedFromName(name));
        }

        [Theory]
        [InlineData("Lantana Beach")]
        [InlineData("Duplex Mix")]
        [InlineData("SH-WR")]
        public void OrdinaryName_IsNotAMergedDuplicate(string name)
        {
            Assert.False(PlantSpeciesNameRules.IsMergedDuplicate(name));
            Assert.Null(PlantSpeciesNameRules.MergedFromName(name));
        }
    }

    [Collection("ScratchDb")]
    public class VarietyActiveAndDuplicateNameE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";

        private sealed class Env
        {
            public string Cs = "";
            public PlantSpeciesRepository Repo = null!;
            public int TypeId;

            public async Task<T> ScalarAsync<T>(string sql, params (string Name, object? Value)[] args)
            {
                using var conn = new SqlConnection(Cs);
                await conn.OpenAsync();
                using var cmd = new SqlCommand(sql, conn);
                foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
                var r = await cmd.ExecuteScalarAsync();
                return r == null || r == DBNull.Value ? default! : (T)Convert.ChangeType(r, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
            }

            public Task<int> InsertAsync(string name, bool active)
                => ScalarAsync<int>("INSERT INTO dbo.PlantSpecies (PlantTypeId, Name, ReadyStockDays, IsActive) VALUES (@T, @N, 30, @A); SELECT CAST(SCOPE_IDENTITY() AS INT)",
                    ("@T", TypeId), ("@N", name), ("@A", active));

            public Task<int> CountAsync(string name)
                => ScalarAsync<int>("SELECT COUNT(*) FROM dbo.PlantSpecies WHERE Name = @N", ("@N", name));
        }

        private static async Task<Env> OpenAsync()
        {
            var cs = Environment.GetEnvironmentVariable(ConnectionVariable);
            Skip.If(string.IsNullOrWhiteSpace(cs), $"Set {ConnectionVariable} to a {RequiredPrefix}* database connection string to run these tests.");
            var env = new Env { Cs = cs! };
            var db = await env.ScalarAsync<string>("SELECT DB_NAME()");
            if (!db.StartsWith(RequiredPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException($"Refusing to run these tests against '{db}'. Only databases named {RequiredPrefix}* are allowed.");
            Skip.If(await env.ScalarAsync<int>("SELECT CASE WHEN COL_LENGTH('dbo.PlantSpecies', 'IsActive') IS NULL THEN 0 ELSE 1 END") != 1,
                "2026-10-15_PlantSpeciesIsActive.sql is not applied to this scratch database.");
            Skip.If(await env.ScalarAsync<int>("SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.PlantSpecies') AND name = 'UX_PlantSpecies_Name_Active'") != 1,
                "2026-10-15_PlantSpeciesUniqueActiveName.sql is not applied to this scratch database.");
            env.TypeId = await env.ScalarAsync<int>("SELECT MIN(Id) FROM dbo.PlantTypes");
            env.Repo = new PlantSpeciesRepository(new DatabaseHelper(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs }).Build()));
            return env;
        }

        private static string NewName() => "ZZTEST Var " + Guid.NewGuid().ToString("N")[..10];

        [SkippableFact]
        public async Task Add_RefusesAnActiveName_IgnoringCapitalsAndSpaces_AndSavesTrimmed()
        {
            var env = await OpenAsync();
            var name = NewName();
            Assert.Null(await env.Repo.AddPlantSpecies(env.TypeId, "  " + name + " \r\n", ""));
            Assert.Equal(1, await env.CountAsync(name));
            Assert.Equal(name, await env.ScalarAsync<string>("SELECT Name FROM dbo.PlantSpecies WHERE Name = @N", ("@N", name)));

            var clash = await env.Repo.AddPlantSpecies(env.TypeId, " " + name.ToUpperInvariant().Replace(" ", "   ") + " ", "");
            Assert.NotNull(clash);
            Assert.Equal(name, clash!.Value.Name);
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.PlantSpecies WHERE UPPER(Name) = UPPER(@N)", ("@N", name)));
        }

        [SkippableFact]
        public async Task Add_IsAllowed_WhenOnlyAnInactiveVarietyHasTheName()
        {
            var env = await OpenAsync();
            var name = NewName();
            await env.InsertAsync(name, active: false);
            Assert.Null(await env.Repo.AddPlantSpecies(env.TypeId, name, ""));
            Assert.Equal(2, await env.CountAsync(name));
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.PlantSpecies WHERE Name = @N AND IsActive = 1", ("@N", name)));
        }

        [SkippableFact]
        public async Task Edit_RefusesAnotherActiveVarietysName_ButNotAnInactiveOnes_AndSavesTrimmed()
        {
            var env = await OpenAsync();
            var taken = NewName();
            var retired = NewName();
            var mine = await env.InsertAsync(NewName(), active: true);
            await env.InsertAsync(taken, active: true);
            await env.InsertAsync(retired, active: false);

            var clash = await env.Repo.UpdatePlantSpecies(mine, taken.ToLowerInvariant() + "  ", "", 30);
            Assert.NotNull(clash);
            Assert.Equal(taken, clash!.Value.Name);

            Assert.Null(await env.Repo.UpdatePlantSpecies(mine, " " + retired + "\r\n", "", 30));
            Assert.Equal(retired, await env.ScalarAsync<string>("SELECT Name FROM dbo.PlantSpecies WHERE Id = @I", ("@I", mine)));
        }

        [SkippableFact]
        public async Task Dropdowns_HideInactiveVarieties_UnlessAskedFor_AndHistoryLookupStillFindsThem()
        {
            var env = await OpenAsync();
            var active = await env.InsertAsync(NewName(), active: true);
            var inactive = await env.InsertAsync("DUP-" + NewName(), active: false);

            var byType = await env.Repo.GetSpeciesByPlantType(env.TypeId);
            Assert.Contains(byType, s => s.Id == active);
            Assert.DoesNotContain(byType, s => s.Id == inactive);
            Assert.All(byType, s => Assert.True(s.IsActive));
            Assert.DoesNotContain(await env.Repo.GetAllAsync(), s => s.Id == inactive);

            Assert.Contains(await env.Repo.GetSpeciesByPlantType(env.TypeId, includeInactive: true), s => s.Id == inactive && !s.IsActive);
            Assert.Contains(await env.Repo.GetAllAsync(includeInactive: true), s => s.Id == inactive);
            var byId = await env.Repo.GetByIdAsync(inactive);
            Assert.NotNull(byId);
            Assert.False(byId!.IsActive);
        }

        [SkippableFact]
        public async Task Reactivate_BringsBackADeactivatedVariety_ButIsRefusedWhileAnActiveVarietyHasTheName()
        {
            var env = await OpenAsync();
            var name = NewName();
            var retired = await env.InsertAsync(name, active: false);
            var holder = await env.InsertAsync(name.ToUpperInvariant(), active: true);

            var (ok, message) = await env.Repo.ReactivatePlantSpecies(retired);
            Assert.False(ok);
            Assert.Contains($"(Id {holder}", message);
            Assert.False(await env.ScalarAsync<bool>("SELECT IsActive FROM dbo.PlantSpecies WHERE Id = @I", ("@I", retired)));

            await env.ScalarAsync<int>("UPDATE dbo.PlantSpecies SET Name = @N WHERE Id = @I; SELECT 1", ("@N", NewName()), ("@I", holder));
            (ok, message) = await env.Repo.ReactivatePlantSpecies(retired);
            Assert.True(ok, message);
            Assert.True(await env.ScalarAsync<bool>("SELECT IsActive FROM dbo.PlantSpecies WHERE Id = @I", ("@I", retired)));
            Assert.Contains(await env.Repo.GetSpeciesByPlantType(env.TypeId), s => s.Id == retired);
        }

        [SkippableFact]
        public async Task Reactivate_IsRefusedForAMergedDuplicate_NamingTheKeptVariety()
        {
            var env = await OpenAsync();
            var keptName = NewName();
            var kept = await env.InsertAsync(keptName, active: true);
            var dup = await env.InsertAsync("placeholder", active: false);
            await env.ScalarAsync<int>("UPDATE dbo.PlantSpecies SET Name = @N WHERE Id = @I; SELECT 1", ("@N", $"DUP-{dup}-{keptName}"), ("@I", dup));

            var (ok, message) = await env.Repo.ReactivatePlantSpecies(dup);
            Assert.False(ok);
            Assert.Contains($"This variety was merged into '{keptName}' (Id {kept})", message);
            Assert.False(await env.ScalarAsync<bool>("SELECT IsActive FROM dbo.PlantSpecies WHERE Id = @I", ("@I", dup)));
            Assert.Equal((kept, keptName), await env.Repo.FindMergedIntoAsync(dup));
        }

        [SkippableFact]
        public async Task Deactivate_HidesAnUnusedVariety_KeepsTheRow_AndCanBeReactivated()
        {
            var env = await OpenAsync();
            var id = await env.InsertAsync(NewName(), active: true);
            var (ok, message) = await env.Repo.DeactivatePlantSpecies(id);
            Assert.True(ok, message);
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.PlantSpecies WHERE Id = @I AND IsActive = 0", ("@I", id)));
            Assert.DoesNotContain(await env.Repo.GetSpeciesByPlantType(env.TypeId), s => s.Id == id);
            Assert.True((await env.Repo.ReactivatePlantSpecies(id)).Success);
        }

        [SkippableFact]
        public async Task Deactivate_IsRefusedWhileStockOrAnActiveMotherPlantUsesIt_AndAllowedOnceEmpty()
        {
            var env = await OpenAsync();
            var id = await env.InsertAsync(NewName(), active: true);
            var area = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.Area WHERE AreaType = N'MainOffice' AND IsActive = 1 ORDER BY Id");
            Skip.If(area == 0, "No active Main Office Area on this scratch copy.");
            var lot = await env.ScalarAsync<int>(@"INSERT INTO dbo.SeedStock (SpeciesId, AreaId, BatchNo, Unit, PhysicalQuantity, CreatedBy)
VALUES (@S, @A, @B, N'Seeds', 500, N'var-test'); SELECT CAST(SCOPE_IDENTITY() AS INT)", ("@S", id), ("@A", area), ("@B", "LOT-" + Guid.NewGuid().ToString("N")[..8]));

            var (ok, message) = await env.Repo.DeactivatePlantSpecies(id);
            Assert.False(ok);
            Assert.Contains("1 Seed Stock lot(s) with seeds", message);
            Assert.True(await env.ScalarAsync<bool>("SELECT IsActive FROM dbo.PlantSpecies WHERE Id = @I", ("@I", id)));

            await env.ScalarAsync<int>("UPDATE dbo.SeedStock SET PhysicalQuantity = 0 WHERE Id = @L; SELECT 1", ("@L", lot));
            (ok, message) = await env.Repo.DeactivatePlantSpecies(id);
            Assert.True(ok, message);
        }

        [SkippableFact]
        public async Task Deactivate_IsRefusedForAnAlreadyInactiveVariety()
        {
            var env = await OpenAsync();
            var id = await env.InsertAsync("DUP-" + NewName(), active: false);
            var (ok, message) = await env.Repo.DeactivatePlantSpecies(id);
            Assert.False(ok);
            Assert.Contains("already inactive", message);
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.PlantSpecies WHERE Id = @I", ("@I", id)));
        }

        [SkippableFact]
        public async Task UniqueIndex_RefusesASecondActiveName_EvenFromPlainSql()
        {
            var env = await OpenAsync();
            var name = NewName();
            await env.InsertAsync(name, active: true);
            var ex = await Assert.ThrowsAsync<SqlException>(() => env.InsertAsync(name.ToLowerInvariant() + " ", active: true));
            Assert.Contains("UX_PlantSpecies_Name_Active", ex.Message);
            await env.InsertAsync(name, active: false);
            Assert.Equal(2, await env.CountAsync(name));
        }
    }
}
