using System.IO;
using System.Text.RegularExpressions;

namespace PlantStockManager.Tests
{
    // History: Cutting Tray Sowing (Pages/Production/SeedSowing/CreateFromCutting)
    // first gained an OPTIONAL Polyhouse field cascading from an OPTIONAL Growing
    // Area dropdown (the gap this file originally documented). Business rule
    // change (Sowing Destination): Cutting Stock may be held/managed at Main
    // Office, but the cutting must always be SOWN at a real growing Main Area /
    // Polyhouse -- never silently defaulted to Main Office because the operator
    // left an optional field blank. The separate Area dropdown is gone; the
    // destination Polyhouse (PolyhouseRepository.GetGrowingDestinationsAsync,
    // which already excludes Main Office/Outlet/Area-less Polyhouses) is the
    // single REQUIRED destination choice, server-rendered on this same page
    // (never deferred to a later confirmation step), and the growing Area is
    // always derived FROM it -- both on the page and, independently, inside
    // SeedSowingRepository.InsertFromCuttingAsync itself.
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
        public void PageModel_HasRequiredPolyhouseId_NoSeparateAreaProperty_NoPolyhousesHandler()
        {
            var pageModelType = typeof(PlantStockManager.Pages.Production.SeedSowing.CreateFromCuttingModel);
            var property = pageModelType.GetProperty("PolyhouseId");
            Assert.NotNull(property);
            Assert.Equal(typeof(int?), property!.PropertyType);
            Assert.Null(pageModelType.GetProperty("AreaId"));
            Assert.NotNull(pageModelType.GetProperty("Destinations"));

            // The destination list is small and server-rendered (Model.Destinations);
            // there is no AJAX "Polyhouses" handler to cascade from an Area choice.
            Assert.Null(pageModelType.GetMethod("OnGetPolyhousesAsync"));
        }

        [Fact]
        public void CshtmlHasRequiredPolyhouseDropdown_ServerRendered_NoAreaDropdown()
        {
            var path = Path.Combine(RepoRoot(), "Pages", "Production", "SeedSowing", "CreateFromCutting.cshtml");
            var html = File.ReadAllText(path);
            Assert.Matches(new Regex("asp-for=\"PolyhouseId\""), html);
            Assert.Contains("id=\"polyhouseSelect\"", html);
            Assert.Contains("Model.Destinations", html);     // server-rendered from the filtered list, not an AJAX call
            Assert.DoesNotContain("asp-for=\"AreaId\"", html);
            Assert.DoesNotContain("handler: 'Polyhouses'", html);
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
