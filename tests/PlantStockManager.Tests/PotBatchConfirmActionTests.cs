using System.IO;

namespace PlantStockManager.Tests
{
    // The one genuine UI gap found while re-reviewing the Pot Production ->
    // Ready confirmation workflow: the batch list page had no direct
    // "Confirm" action, only a generic "Open" link into Details. This adds
    // a visible "Confirm Ready Stock" button (shown only to the assigned
    // supervisor, while the batch is still InProduction and has something
    // to confirm) that deep-links to the existing Confirm READY section on
    // Details.cshtml -- no new page, no new logic, purely additive.
    public class PotBatchConfirmActionTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.sln")))
                dir = dir.Parent;
            if (dir == null)
                throw new InvalidOperationException("Could not locate repo root (PlantStockManager.sln) from " + AppContext.BaseDirectory);
            return dir.FullName;
        }

        [Fact]
        public void IndexPage_HasAConfirmReadyStockAction()
        {
            var path = Path.Combine(RepoRoot(), "Pages", "Production", "PotBatch", "Index.cshtml");
            var html = File.ReadAllText(path);
            Assert.Contains("Confirm Ready Stock", html);
            Assert.Contains("asp-fragment=\"confirm-ready\"", html);
        }

        [Fact]
        public void DetailsPage_HasTheMatchingAnchor()
        {
            var path = Path.Combine(RepoRoot(), "Pages", "Production", "PotBatch", "Details.cshtml");
            var html = File.ReadAllText(path);
            Assert.Contains("id=\"confirm-ready\"", html);
        }
    }
}
