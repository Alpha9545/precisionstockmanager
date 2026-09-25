using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Phase 1: Responsible Person is no longer entered anywhere. Existing
    // values stay in the database (shown read-only as "historical" on the
    // Details pages) and an edit must never clear them.
    public class ResponsiblePersonRemovalTests
    {
        private static readonly Assembly App = typeof(SupervisorRules).Assembly;

        [Fact]
        public void NoPage_OffersOrBindsAResponsiblePerson()
        {
            var offenders = new List<string>();
            foreach (var page in App.GetTypes().Where(t => typeof(PageModel).IsAssignableFrom(t) && !t.IsAbstract))
            {
                foreach (var p in page.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (p.Name is "ResponsiblePersons" or "PersonOptions")
                        offenders.Add($"{page.FullName}.{p.Name}");
                    if (p.Name == "ResponsiblePersonId" && p.GetCustomAttribute<BindPropertyAttribute>() != null)
                        offenders.Add($"{page.FullName}.{p.Name} [BindProperty]");
                }
            }
            // Purchase Order receipt keeps its own "Received By" list (not a
            // Responsible Person), so only the retired names are checked.
            Assert.Empty(offenders.Where(o => !o.Contains(".PurchaseOrder.")));
        }

        [Theory]
        [InlineData("ActualCuttingRepository.cs")]
        [InlineData("CuttingDeliveryRepository.cs")]
        [InlineData("CuttingPlanRepository.cs")]
        [InlineData("DispatchRepository.cs")]
        [InlineData("InternalTransferRepository.cs")]
        [InlineData("MotherPlantRepository.cs")]
        [InlineData("PotProductionRepository.cs")]
        [InlineData("PropagationBatchRepository.cs")]
        [InlineData("SeedSowingRepository.cs")]
        public void EditUpdates_NeverWriteResponsiblePerson(string repositoryFile)
        {
            var source = File.ReadAllText(Path.Combine(RepoRoot(), "Data", repositoryFile));
            var updates = Regex.Matches(source, @"UPDATE\s+dbo\.\w+\s+SET[\s\S]*?WHERE", RegexOptions.IgnoreCase);
            Assert.NotEmpty(updates);
            foreach (Match update in updates)
                Assert.DoesNotContain("ResponsiblePersonId", update.Value);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.csproj")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("PlantStockManager.csproj not found above the test output folder.");
        }
    }
}
