using System.IO;
using System.Text.RegularExpressions;

namespace PlantStockManager.Tests
{
    // Gap found while reviewing the full Mother Plant -> Cutting Production ->
    // Sowing -> Ready Stock workflow: Cutting Tray Sowing
    // (Pages/Production/SeedSowing/CreateFromCutting) let the Sowing Supervisor
    // pick a growing Area but never offered a Polyhouse -- even though
    // Data/SeedSowingRepository.InsertFromCuttingAsync already accepted and
    // cross-validated a PolyhouseId (DirectSowingRules.ResolveGrowingLocation,
    // covered by DirectSowingRulesTests) exactly like Direct Seed Sowing
    // (Pages/Production/SeedSowing/Create) already does. Only the page was
    // missing the field; no repository/schema change was needed or made.
    public class CuttingTraySowingPolyhouseCascadeTests
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
        public void PageModel_HasPolyhouseIdAndPolyhousesHandler()
        {
            var pageModelType = typeof(PlantStockManager.Pages.Production.SeedSowing.CreateFromCuttingModel);
            var property = pageModelType.GetProperty("PolyhouseId");
            Assert.NotNull(property);
            Assert.Equal(typeof(int?), property!.PropertyType);

            var handler = pageModelType.GetMethod("OnGetPolyhousesAsync");
            Assert.NotNull(handler);
            Assert.Contains("areaId", handler!.GetParameters().Select(p => p.Name));
        }

        [Fact]
        public void CshtmlHasPolyhouseDropdownBoundToPolyhouseId()
        {
            var path = Path.Combine(RepoRoot(), "Pages", "Production", "SeedSowing", "CreateFromCutting.cshtml");
            var html = File.ReadAllText(path);
            Assert.Matches(new Regex("asp-for=\"PolyhouseId\""), html);
            Assert.Contains("id=\"polyhouseSelect\"", html);
            Assert.Contains("handler: 'Polyhouses'", html);
        }

        [Fact]
        public void InlineScript_IsValidJavaScript()
        {
            var path = Path.Combine(RepoRoot(), "Pages", "Production", "SeedSowing", "CreateFromCutting.cshtml");
            var html = File.ReadAllText(path);
            var match = Regex.Match(html, @"<script>(.*)</script>", RegexOptions.Singleline);
            Assert.True(match.Success, "No <script> block found in CreateFromCutting.cshtml");
            var js = match.Groups[1].Value;

            int braces = 0, parens = 0;
            foreach (var c in js)
            {
                if (c == '{') braces++;
                else if (c == '}') braces--;
                else if (c == '(') parens++;
                else if (c == ')') parens--;
                Assert.True(braces >= 0, "Unmatched closing brace encountered mid-script.");
                Assert.True(parens >= 0, "Unmatched closing paren encountered mid-script.");
            }
            Assert.Equal(0, braces);
            Assert.Equal(0, parens);
        }
    }
}
