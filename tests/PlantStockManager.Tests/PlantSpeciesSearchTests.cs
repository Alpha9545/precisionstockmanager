using System.IO;
using System.Linq;

namespace PlantStockManager.Tests
{
    // Phase 9 (Seed Stock Usability): PlantSpeciesRepository.SearchAsync is
    // the shared, capped variety-search method behind both
    // Pages/Production/SeedStock/Create.cshtml.cs's and
    // Pages/Production/SeedSowing/Create.cshtml.cs's search boxes. No live
    // database is reachable from this sandbox, so these tests verify the
    // method's own SQL text directly -- the same "no new history table"
    // style of check used for migration scripts, applied here to a C#
    // source file -- to guard against a regression that silently removes
    // the TOP (@Limit) cap (which is the entire point: "do not load
    // 1,000+ records unnecessarily") or reintroduces string-concatenated
    // SQL (a SQL-injection regression).
    public class PlantSpeciesSearchTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.csproj")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("PlantStockManager.csproj not found above the test output folder.");
        }

        private static string RepositoryText()
            => File.ReadAllText(Path.Combine(RepoRoot(), "Data", "PlantSpeciesRepository.cs"));

        [Fact]
        public void SearchAsync_AlwaysCapsResultsWithTop()
        {
            var sql = RepositoryText();
            var searchMethod = sql.Substring(sql.IndexOf("public async Task<List<PlantSpecies>> SearchAsync"));
            var methodBody = searchMethod.Substring(0, searchMethod.IndexOf("public async Task<List<PlantSpecies>> GetSpeciesByPlantType"));
            Assert.Contains("TOP (@Limit)", methodBody);
            Assert.Contains("cmd.Parameters.AddWithValue(\"@Limit\"", methodBody);
        }

        [Fact]
        public void SearchAsync_FiltersByNameOrScientificName_ParameterizedNotConcatenated()
        {
            var sql = RepositoryText();
            var searchMethod = sql.Substring(sql.IndexOf("public async Task<List<PlantSpecies>> SearchAsync"));
            var methodBody = searchMethod.Substring(0, searchMethod.IndexOf("public async Task<List<PlantSpecies>> GetSpeciesByPlantType"));

            Assert.Contains("Name LIKE @Query", methodBody);
            Assert.Contains("ScientificName LIKE @Query", methodBody);
            Assert.Contains("cmd.Parameters.AddWithValue(\"@Query\"", methodBody);
            // Never string-concatenated into the SQL text itself.
            Assert.DoesNotContain("+ query", methodBody);
            Assert.DoesNotContain("$\"SELECT", methodBody);
        }

        [Fact]
        public void SearchAsync_SupportsOptionalPlantTypeFilter()
        {
            var sql = RepositoryText();
            var searchMethod = sql.Substring(sql.IndexOf("public async Task<List<PlantSpecies>> SearchAsync"));
            var methodBody = searchMethod.Substring(0, searchMethod.IndexOf("public async Task<List<PlantSpecies>> GetSpeciesByPlantType"));
            Assert.Contains("@PlantTypeId IS NULL OR PlantTypeId = @PlantTypeId", methodBody);
        }

        [Fact]
        public void SearchAsync_OrdersByName()
        {
            var sql = RepositoryText();
            var searchMethod = sql.Substring(sql.IndexOf("public async Task<List<PlantSpecies>> SearchAsync"));
            var methodBody = searchMethod.Substring(0, searchMethod.IndexOf("public async Task<List<PlantSpecies>> GetSpeciesByPlantType"));
            Assert.Contains("ORDER BY Name", methodBody);
        }

        [Fact]
        public void GetAllAsync_AndGetSpeciesByPlantType_AreUnchanged()
        {
            // Phase 9 is additive only -- the two existing, still-used
            // bulk methods (Management Dashboard's filter dropdown; every
            // other Plant-Type cascade in the app) must still exist
            // unchanged, never removed or narrowed by this new method.
            var sql = RepositoryText();
            Assert.Contains("public async Task<List<PlantSpecies>> GetAllAsync()", sql);
            Assert.Contains("public async Task<List<PlantSpecies>> GetSpeciesByPlantType(int plantTypeId)", sql);
        }
    }
}
