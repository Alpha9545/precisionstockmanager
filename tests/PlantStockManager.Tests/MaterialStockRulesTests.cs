using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using PlantStockManager.Authorization;
using PlantStockManager.Pages.Production.SeedSowing;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Material Stock -- the pure rules, page permissions, the sowing form's "exactly one
    // material" check and the navigation. The database behaviour is MaterialStockE2ETests.
    public class MaterialStockRulesTests
    {
        // ---- conversion ---------------------------------------------------------------------

        [Theory]
        [InlineData(5, 5.2, 26)]       // 5 kg dry Cocopeat -> 26 kg usable
        [InlineData(10, 5.2, 52)]
        [InlineData(1, 5.2, 5.2)]
        [InlineData(0.125, 5.2, 0.65)]
        [InlineData(5, 1.0, 5)]        // Pitmos does not expand
        [InlineData(10, 1.0, 10)]
        [InlineData(0.333, 5.2, 1.732)] // 1.7316 -> 3 decimals
        public void StockQuantity_IsInputTimesFactor_RoundedTo3Decimals(decimal input, decimal factor, decimal expected)
            => Assert.Equal(expected, MaterialStockRules.StockQuantityFor(input, factor));

        [Fact]
        public void StockQuantity_UsesWhateverFactorTheDataHolds()
            => Assert.Equal(50m, MaterialStockRules.StockQuantityFor(10, 5.0m));

        // ---- consumption ----------------------------------------------------------------------

        [Theory]
        [InlineData(100, 1.5, 150)]
        [InlineData(50, 1.5, 75)]
        [InlineData(1, 1.5, 1.5)]
        [InlineData(100, 1.25, 125)]   // a later, changed value
        public void Required_IsTraysTimesKgPerTray(decimal trays, decimal perTray, decimal expected)
            => Assert.Equal(expected, MaterialStockRules.RequiredFor(trays, perTray));

        [Theory]
        [InlineData("150 Cavity", 15000, 100)]
        [InlineData("102 Cavity", 10200, 100)]
        [InlineData("42 Cavity", 4200, 100)]
        [InlineData("24 Cavity", 2400, 100)]
        [InlineData("9 Cavity", 900, 100)]
        [InlineData("24 Cavity", 2401, 101)]   // a partly-filled tray is still a physical tray (as Tray Stock counts it)
        public void EveryTrayType_100Trays_Need150Kg_AtTheCurrent1Point5(string cavity, decimal quantity, decimal trays)
        {
            Assert.Equal(trays, DirectSowingRules.PhysicalTraysRequired(quantity, cavity));
            Assert.Equal(trays * 1.5m, MaterialStockRules.RequiredFor(DirectSowingRules.PhysicalTraysRequired(quantity, cavity), 1.5m));
        }

        // ---- validation, formatting, messages ---------------------------------------------------

        [Theory]
        [InlineData(1.5, true)]
        [InlineData(2.25, true)]
        [InlineData(0.750, true)]
        [InlineData(0.001, true)]
        [InlineData(0, false)]
        [InlineData(-5, false)]
        [InlineData(1.2345, false)]
        [InlineData(2000000, false)]
        public void Quantities_Positive_AtMost3Decimals(decimal quantity, bool valid)
            => Assert.Equal(valid, MaterialStockRules.ValidateQuantity(quantity) == null);

        [Fact]
        public void MissingQuantity_IsRejected() => Assert.NotNull(MaterialStockRules.ValidateQuantity(null));

        [Fact]
        public void Kilograms_AreShownWithTwoToThreeDecimals()
        {
            Assert.Equal("126.00 kg", MaterialStockRules.Kg(126));
            Assert.Equal("2.25 kg", MaterialStockRules.Kg(2.25m));
            Assert.Equal("0.75 kg", MaterialStockRules.Kg(0.750m));
            Assert.Equal("0.125 kg", MaterialStockRules.Kg(0.125m));
        }

        [Fact]
        public void InsufficientMessage_NamesTheMaterialPolyhouseAvailableAndRequired()
            => Assert.Equal("Insufficient Cocopeat stock in PH-03. Available: 100.00 kg. Required: 150.00 kg.",
                MaterialStockRules.InsufficientMessage("Cocopeat", "PH-03", 100, 150));

        [Theory]
        [InlineData("MainOffice", true, true)]
        [InlineData("MainOffice", false, false)]
        [InlineData("MotherPlant", true, false)]
        [InlineData("Outlet", true, false)]
        [InlineData(null, true, false)]
        public void OnlyAnActiveMainOfficeAreaHoldsMaterial(string? areaType, bool active, bool expected)
            => Assert.Equal(expected, MaterialStockRules.IsMainOfficeLocation(areaType, active));

        // ---- the sowing form: exactly one material ------------------------------------------------

        private static HttpRequest Form(params string[] values)
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.ContentType = "application/x-www-form-urlencoded";
            ctx.Request.Form = new FormCollection(new Dictionary<string, StringValues> { [GrowingMaterialForm.FieldName] = new StringValues(values) });
            return ctx.Request;
        }

        [Fact]
        public void SowingForm_OneMaterial_IsAccepted_TwoAreRejected()
        {
            Assert.Null(GrowingMaterialForm.ValidatePosted(Form("1")));
            Assert.Null(GrowingMaterialForm.ValidatePosted(Form()));   // none: required or not is decided by the Area, server-side
            Assert.Equal(MaterialStockRules.OneMaterialOnlyMessage, GrowingMaterialForm.ValidatePosted(Form("1", "2")));
        }

        [Fact]
        public void SowingForm_UsesRadioButtons_SoOnlyOneCanBeChosen()
        {
            var partial = File.ReadAllText(Path.Combine(RepoRoot(), "Pages", "Production", "SeedSowing", "_GrowingMaterial.cshtml"));
            Assert.Contains("type=\"radio\"", partial);
            Assert.DoesNotContain("type=\"checkbox\"", partial);
            foreach (var page in new[] { "Create.cshtml", "CreateFromCutting.cshtml" })
                Assert.Contains("<partial name=\"_GrowingMaterial\"", File.ReadAllText(Path.Combine(RepoRoot(), "Pages", "Production", "SeedSowing", page)));
        }

        // ---- permissions ---------------------------------------------------------------------------

        private static ClaimsPrincipal User(params string[] permissions)
            => new(new ClaimsIdentity(permissions.Select(p => new Claim(MinimumAuthorizationLevelHandler.PermissionClaimType, p)), "test"));

        private static ClaimsPrincipal Admin()
            => new(new ClaimsIdentity(new[] { new Claim(ClaimsPrincipalSecurityExtensions.FullAccessClaimType, "true") }, "test"));

        // 2026-10-08 consolidation: Index / Add / History also show Tray Stock, so each opens
        // for EITHER domain's existing permission (MaterialStockAccess) and then shows -- and
        // accepts -- only the domain the user holds. Settings stays material-only.
        [Theory]
        [InlineData("/Production/MaterialStock/Index", "MaterialStock.View|MainOffice.View|Sowing.View|Sowing.Enter|TrayStock.Enter|TrayStock.EnterMotherPlant|TrayStock.ViewMotherPlant")]   // + EnterMotherPlant (2026-10-09_ReadyStockTransplanting.sql, owner decision); + ViewMotherPlant (read-only, 2026-10-11 owner decision)
        [InlineData("/Production/MaterialStock/Add", "MaterialStock.Add|TrayStock.Enter|TrayStock.EnterMotherPlant")]
        [InlineData("/Production/MaterialStock/History", "MaterialStock.History|MainOffice.View|Sowing.View|Sowing.Enter|TrayStock.Enter|TrayStock.EnterMotherPlant|TrayStock.ViewMotherPlant")]
        [InlineData("/Production/MaterialStock/Settings", "MaterialStock.Manage")]
        public void EveryPage_HasItsOwnServerSideRule(string page, string permission)
        {
            Assert.True(FeatureAuthorizationConventions.IsMapped(page));
            Assert.Equal(permission, FeatureAuthorizationConventions.GetRule(page).Read);
        }

        [Fact]
        public void SowingSupervisor_ViewsAddsAndSeesHistory_ButCannotManage_AdministratorCanDoEverything()
        {
            var supervisor = User("Sowing.View", "TrayStock.Enter", "MaterialStock.View", "MaterialStock.Add", "MaterialStock.History");
            foreach (var page in new[] { "Index", "Add", "History" })
                Assert.True(supervisor.HasPermission(FeatureAuthorizationConventions.GetRule($"/Production/MaterialStock/{page}").Read));
            Assert.False(supervisor.HasPermission(FeatureAuthorizationConventions.GetRule("/Production/MaterialStock/Settings").Read));
            foreach (var page in new[] { "Index", "Add", "History", "Settings" })
                Assert.True(Admin().HasPermission(FeatureAuthorizationConventions.GetRule($"/Production/MaterialStock/{page}").Read));
        }

        [Fact]
        public void OtherRoles_AreBlocked()
        {
            var others = new[]
            {
                User("Store.Manage", "Store.View"),                            // Inventory Store
                User("Dashboard.View", "Reports.View"),
                new ClaimsPrincipal(new ClaimsIdentity()),                     // anonymous
            };
            foreach (var u in others)
                foreach (var page in new[] { "Index", "Add", "History", "Settings" })
                    Assert.False(u.HasPermission(FeatureAuthorizationConventions.GetRule($"/Production/MaterialStock/{page}").Read));
        }

        // The Sowing Operator / Store Keeper could already VIEW Tray Stock and its history
        // (old Tray Stock rules), so they open Material Stock / History -- but see trays only:
        // no Cocopeat / Pitmos, no Add Material, no Settings. Nothing was widened.
        [Fact]
        public void SowingOperator_SeesTraysOnly_NeverMaterials()
        {
            var operatorUser = User("Sowing.Enter", "Sowing.View", "MainOffice.View");
            Assert.True(operatorUser.HasPermission(FeatureAuthorizationConventions.GetRule("/Production/MaterialStock/Index").Read));
            Assert.True(operatorUser.HasPermission(FeatureAuthorizationConventions.GetRule("/Production/MaterialStock/History").Read));
            Assert.False(operatorUser.HasPermission(FeatureAuthorizationConventions.GetRule("/Production/MaterialStock/Add").Read));
            Assert.False(operatorUser.HasPermission(FeatureAuthorizationConventions.GetRule("/Production/MaterialStock/Settings").Read));
            Assert.True(operatorUser.CanViewTrays());
            Assert.False(operatorUser.CanViewMaterials());
            Assert.False(operatorUser.CanSeeMaterialHistory());
            Assert.False(operatorUser.CanAddMaterials());
            Assert.False(operatorUser.CanAddTrays());
        }

        // The old Tray Stock URLs keep their ORIGINAL rules, so a redirect never lets anyone
        // reach a page they could not open before.
        [Theory]
        [InlineData("/Production/TrayStock/Index", "MainOffice.View|Sowing.View|Sowing.Enter|TrayStock.Enter|TrayStock.EnterMotherPlant")]
        [InlineData("/Production/TrayStock/Add", "TrayStock.Enter")]
        [InlineData("/Production/TrayStock/Transactions", "MainOffice.View|Sowing.View|Sowing.Enter|TrayStock.Enter|TrayStock.EnterMotherPlant")]
        public void OldTrayStockUrls_KeepTheirOriginalRules(string page, string permission)
            => Assert.Equal(permission, FeatureAuthorizationConventions.GetRule(page).Read);

        [Fact]
        public void SowingEntryPages_KeepTheirExistingRules()
        {
            Assert.Equal("Sowing.Enter", FeatureAuthorizationConventions.GetRule("/Production/SeedSowing/Create").Read);
            Assert.Equal("Sowing.Enter", FeatureAuthorizationConventions.GetRule("/Production/SeedSowing/CreateFromCutting").Read);
        }

        // ---- navigation ------------------------------------------------------------------------------

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.sln")))
                dir = dir.Parent;
            return dir!.FullName;
        }

        // The section of the menu (between its <li nav-section> and the next one) whose title is `title`.
        private static string MenuSection(string layout, string title)
        {
            var start = layout.IndexOf($@"<span class=""link_name"">{title}</span>", StringComparison.Ordinal);
            Assert.True(start >= 0, $"menu section '{title}' not found");
            var end = layout.IndexOf("<li nav-section>", start, StringComparison.Ordinal);
            return end < 0 ? layout[start..] : layout[start..end];
        }

        // 2026-10-08: USER SEED STOCK -> Seed Stock, Material Stock, Add Material;
        // REPORTS -> Material History; MASTER DATA -> Material Settings. Each page once,
        // no standalone Tray Stock / Material Stock section, no Tray Stock links.
        [Fact]
        public void Menu_PlacesEachMaterialPageOnce_InItsSection()
        {
            var layout = File.ReadAllText(Path.Combine(RepoRoot(), "Pages", "Shared", "_Layout.cshtml"));
            foreach (var page in new[] { "Index", "Add", "History", "Settings" })
                Assert.Single(Regex.Matches(layout, $@"<li nav-page=""/Production/MaterialStock/{page}"">"));

            Assert.Single(Regex.Matches(layout, @"<span class=""link_name"">User Seed Stock</span>"));
            Assert.DoesNotContain(@"<span class=""link_name"">Seed Production</span>", layout);
            Assert.DoesNotContain(@"<span class=""link_name"">Material Stock</span>", layout);
            Assert.DoesNotContain(@"<span class=""link_name"">Tray Stock</span>", layout);
            Assert.DoesNotContain("/Production/TrayStock/", layout);

            var seed = MenuSection(layout, "User Seed Stock");
            var order = new[] { ">Seed Stock</a>", ">Material Stock</a>", ">Add Material</a>" }.Select(l => seed.IndexOf(l, StringComparison.Ordinal)).ToList();
            Assert.DoesNotContain(-1, order);
            Assert.Equal(order.OrderBy(i => i), order);
            Assert.DoesNotContain("MaterialStock/History", seed);
            Assert.DoesNotContain("MaterialStock/Settings", seed);

            Assert.Contains(@"<li nav-page=""/Production/MaterialStock/History""><a asp-page=""/Production/MaterialStock/History"">Material History</a>", MenuSection(layout, "Reports"));
            Assert.Contains(@"<li nav-page=""/Production/MaterialStock/Settings""><a asp-page=""/Production/MaterialStock/Settings"">Material Settings</a>", MenuSection(layout, "Master Data &amp; Admin"));
        }

        [Fact]
        public void HomeQuickAccess_HasAMaterialStockTile()
            => Assert.Contains(HomeQuickAccess.Modules, m => m.Key == "material" && m.Links.Any(l => l.Page == "/Production/MaterialStock/Add"));
    }
}
