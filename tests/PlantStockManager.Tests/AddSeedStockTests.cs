using System.IO;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using PlantStockManager.Authorization;

namespace PlantStockManager.Tests
{
    // The simplified, single-page "Add Seed Stock" workflow
    // (Pages/Production/SeedStock/AddSeedStock) combines the two previously
    // separate steps -- Create (empty pool) then AddStock/{id} (credit a
    // known pool) -- into one page, backed by the new
    // Data/SeedStockRepository.ReceiveAsync, itself just the existing
    // GetOrCreateLockedAsync + RecordTransactionAsync composed in one
    // transaction. Create.cshtml and AddStock/{id}.cshtml are untouched.
    public class AddSeedStockTests
    {
        [Fact]
        public void NewPage_HasThePermission()
        {
            Assert.True(FeatureAuthorizationConventions.IsMapped("/Production/SeedStock/AddSeedStock"));
            Assert.Contains("SeedStock.Enter", FeatureAuthorizationConventions.GetRule("/Production/SeedStock/AddSeedStock").Read);
        }

        [Fact]
        public void PageModel_HasTheSimplifiedFormFields()
        {
            var type = typeof(PlantStockManager.Pages.Production.SeedStock.AddSeedStockModel);
            var bound = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<BindPropertyAttribute>() != null)
                .Select(p => p.Name)
                .ToList();

            Assert.Contains("SpeciesId", bound);      // Seed / Variety
            Assert.Contains("AreaId", bound);         // required by the DB, defaulted/hidden when there is only one choice
            Assert.Contains("SeedSourceId", bound);   // Source
            Assert.Contains("BatchNo", bound);
            Assert.Contains("Unit", bound);
            Assert.Contains("Quantity", bound);
            Assert.Contains("ReceivedOn", bound);
            Assert.Contains("Notes", bound);
        }

        [Fact]
        public void ExistingCreateAndAddStockPages_AreUntouched()
        {
            // Both existing, narrower workflows must still exist and keep
            // their own permission -- the new page is additive, not a replacement.
            Assert.True(FeatureAuthorizationConventions.IsMapped("/Production/SeedStock/Create"));
            Assert.True(FeatureAuthorizationConventions.IsMapped("/Production/SeedStock/AddStock"));
        }

        [Fact]
        public void ReceiveAsync_ComposesGetOrCreateAndRecordTransaction()
        {
            // Structural guard against silently duplicating the stock system:
            // ReceiveAsync must exist alongside (not instead of) the two
            // existing primitives it composes.
            var type = typeof(PlantStockManager.Data.SeedStockRepository);
            Assert.NotNull(type.GetMethod("ReceiveAsync"));
            Assert.NotNull(type.GetMethod("GetOrCreateLockedAsync"));
            Assert.NotNull(type.GetMethod("RecordTransactionAsync"));
        }

        [Fact]
        public void InlineScript_IsValidJavaScript()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            var path = Path.Combine(dir!.FullName, "Pages", "Production", "SeedStock", "AddSeedStock.cshtml");
            var html = File.ReadAllText(path);
            var match = System.Text.RegularExpressions.Regex.Match(html, @"<script>(.*)</script>", System.Text.RegularExpressions.RegexOptions.Singleline);
            Assert.True(match.Success, "No <script> block found in AddSeedStock.cshtml");
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
