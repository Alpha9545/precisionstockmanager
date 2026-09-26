using System.IO;
using System.Text.RegularExpressions;

namespace PlantStockManager.Tests
{
    // Regression test for the reported bug: on Mother Plant Create/Edit, selecting
    // an Area never populated the Polyhouse dropdown. Root cause was NOT the
    // database/repository/handler (Data/PolyhouseRepository.GetByAreaIdAsync and
    // OnGetAreaOptionsAsync were always correct) -- it was a duplicated stray
    // "});" at the end of the inline @section Scripts block in both
    // Create.cshtml and Edit.cshtml, which made the whole <script> block a JS
    // syntax error. A syntax error anywhere in a <script> tag prevents the
    // ENTIRE tag from executing, so the $('#areaSelect').on('change', ...)
    // handler was never even attached -- the AJAX call to OnGetAreaOptionsAsync
    // was never made.
    public class MotherPlantPolyhouseCascadeTests
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

        private static string ExtractScriptsSectionBody(string cshtmlPath)
        {
            var text = File.ReadAllText(cshtmlPath);
            var match = Regex.Match(text, @"<script>(.*)</script>", RegexOptions.Singleline);
            Assert.True(match.Success, $"No <script> block found in {cshtmlPath}");
            return match.Groups[1].Value;
        }

        // A plain brace/paren balance check is enough here: none of these
        // scripts' string literals contain '{', '}', '(' or ')'.
        private static void AssertBalanced(string js, string label)
        {
            int braces = 0, parens = 0;
            foreach (var c in js)
            {
                if (c == '{') braces++;
                else if (c == '}') braces--;
                else if (c == '(') parens++;
                else if (c == ')') parens--;
                Assert.True(braces >= 0, $"{label}: unmatched closing brace encountered mid-script.");
                Assert.True(parens >= 0, $"{label}: unmatched closing paren encountered mid-script.");
            }
            Assert.Equal(0, braces);
            Assert.Equal(0, parens);
        }

        [Theory]
        [InlineData("Pages/Production/MotherPlant/Create.cshtml")]
        [InlineData("Pages/Production/MotherPlant/Edit.cshtml")]
        public void InlineScript_HasBalancedBracesAndParens(string relativePath)
        {
            var path = Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
            var js = ExtractScriptsSectionBody(path);
            AssertBalanced(js, relativePath);
        }

        // Guards the handlers the cascade depends on: Area -> Polyhouses/Supervisors.
        [Theory]
        [InlineData(typeof(PlantStockManager.Pages.Production.MotherPlant.CreateModel))]
        [InlineData(typeof(PlantStockManager.Pages.Production.MotherPlant.EditModel))]
        public void PageModel_HasAreaOptionsHandler(Type pageModelType)
        {
            var method = pageModelType.GetMethod("OnGetAreaOptionsAsync");
            Assert.NotNull(method);
            Assert.Contains("areaId", method!.GetParameters().Select(p => p.Name));
        }
    }
}
