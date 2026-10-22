using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using PlantStockManager.Authorization;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // 2026-10-14: Add Empty Pots / Add Empty Trays in the Mother Plant and Cutting sections (Area only), the purpose
    // of a Ready Stock transfer to a Mother Plant Area (Pot Production / Transplant into a different cavity tray) and
    // no Polyhouse selector on that workflow. Pure rules + source checks + real HTTP authorization
    // (DeleteAuthorizationTests' test host). The database behaviour is in MotherPlantAreaInventoryE2ETests.
    public class MotherPlantAreaInventoryTests : IClassFixture<DeleteAuthorizationTests.AppFactory>
    {
        private readonly DeleteAuthorizationTests.AppFactory _factory;

        public MotherPlantAreaInventoryTests(DeleteAuthorizationTests.AppFactory factory) => _factory = factory;

        private static string ReadSource(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
        }

        private static readonly Area MainOffice = new() { Id = 2, Name = "Main-Office-Areas", AreaType = "MainOffice", IsActive = true };
        private static readonly Area GreenBless = new() { Id = 127, Name = "Green Bless Nursery", AreaType = "MotherPlant", IsActive = true };
        private static readonly Area Ashirawad = new() { Id = 122, Name = "Ashirawad Cutting", AreaType = "MotherPlant", IsActive = true };
        private static readonly Area Outlet = new() { Id = 124, Name = "Outlet", AreaType = "Outlet", IsActive = true };
        private static readonly Area Closed = new() { Id = 999, Name = "Closed", AreaType = "MotherPlant", IsActive = false };
        private static readonly Area[] AllAreas = { MainOffice, GreenBless, Ashirawad, Outlet, Closed };

        // ---- Add Empty Pots: Mother Plant / Cutting Areas only ----------------------------------------------------

        [Fact]
        public void AddEmptyPots_OffersOnlyOwnActiveMotherPlantAndCuttingAreas()
        {
            Assert.Equal(new[] { 127 }, EmptyPotStockRules.SelectableMotherPlantAreas(AllAreas, false, new[] { 127 }).Select(a => a.Id));
            // never the Main Office, an Outlet or a closed Area -- even when assigned
            Assert.Equal(new[] { 122 }, EmptyPotStockRules.SelectableMotherPlantAreas(AllAreas, false, new[] { 2, 122, 124, 999 }).Select(a => a.Id));
            Assert.Empty(EmptyPotStockRules.SelectableMotherPlantAreas(AllAreas, false, Array.Empty<int>()));
            Assert.Equal(new[] { 122, 127 }, EmptyPotStockRules.SelectableMotherPlantAreas(AllAreas, true, Array.Empty<int>()).Select(a => a.Id).OrderBy(i => i));
            // the general rule (Outlets included) is unchanged for its other users
            Assert.Contains(EmptyPotStockRules.SelectableAreas(AllAreas, true, Array.Empty<int>()), a => a.Id == 124);
        }

        [Theory]
        [InlineData(null, "MotherPlant")]
        [InlineData("", "MotherPlant")]
        [InlineData("MotherPlant", "MotherPlant")]
        [InlineData("Cutting", "Cutting")]
        [InlineData("PotProduction", "MotherPlant")]
        public void Section_IsMotherPlantOrCutting(string? posted, string expected) => Assert.Equal(expected, EmptyPotStockRules.NormalizeSection(posted));

        // ---- Transfer purpose / no Polyhouse ---------------------------------------------------------------------

        private static readonly DateTime Today = new(2026, 10, 14);
        private static readonly MotherPlantTransferRules.SourceBatch Source = new("Sowing", "MainOffice", true, 2, "150 Cavity", 1500);

        private static (bool Ok, decimal Trays, string? Error) Create(int? polyhouse, int? polyhouseArea, bool areaOnly, string? purpose)
            => MotherPlantTransferRules.ValidateCreate(Source, new MotherPlantTransferRules.Destination(127, "MotherPlant", true, polyhouse, polyhouseArea),
                450, Today, Today, Today.AddDays(-40), areaOnly, purpose);

        [Theory]
        [InlineData("PotProduction")]
        [InlineData("TrayTransplant")]
        public void AreaOnly_BothPurposes_NoPolyhouseNeeded(string purpose)
        {
            var (ok, trays, error) = Create(null, null, areaOnly: true, purpose);
            Assert.True(ok, error);
            Assert.Equal(3, trays);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Sale")]
        [InlineData("potproduction")]
        public void AreaOnly_PurposeRequired_FromTheClosedList(string? purpose)
        {
            var (ok, _, error) = Create(null, null, areaOnly: true, purpose);
            Assert.False(ok);
            Assert.Contains("purpose", error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void AreaOnly_APolyhouseNamedAnyway_MustBeTheDestinationAreas()
        {
            Assert.False(Create(55, 2, areaOnly: true, "TrayTransplant").Ok);
            Assert.True(Create(55, 127, areaOnly: true, "TrayTransplant").Ok);
        }

        [Fact]
        public void WithoutTheMigration_TheEarlierRuleIsUnchanged()
        {
            var (ok, _, error) = Create(null, null, areaOnly: false, null);
            Assert.False(ok);
            Assert.Equal("Choose the destination Polyhouse.", error);
            Assert.True(Create(55, 127, areaOnly: false, null).Ok);
        }

        [Fact]
        public void Purpose_Labels_AndTheTransplantGate()
        {
            Assert.Equal("Pot Production", MotherPlantTransferRules.PurposeLabel(MotherPlantTransferRules.PurposePotProduction));
            Assert.Equal("Transplant into a different cavity tray", MotherPlantTransferRules.PurposeLabel(MotherPlantTransferRules.PurposeTrayTransplant));
            Assert.Equal("Not recorded", MotherPlantTransferRules.PurposeLabel(null));
            Assert.False(MotherPlantTransferRules.ValidateTransplantPurpose(MotherPlantTransferRules.PurposePotProduction).Ok);
            Assert.True(MotherPlantTransferRules.ValidateTransplantPurpose(MotherPlantTransferRules.PurposeTrayTransplant).Ok);
            Assert.True(MotherPlantTransferRules.ValidateTransplantPurpose(null).Ok);     // older transfers / not a received batch

            Assert.False(new MotherPlantTransfer { TransferPurpose = MotherPlantTransferRules.PurposePotProduction }.ForTrayTransplant);
            Assert.True(new MotherPlantTransfer { TransferPurpose = MotherPlantTransferRules.PurposeTrayTransplant }.ForTrayTransplant);
            Assert.True(new MotherPlantTransfer { TransferPurpose = null }.ForTrayTransplant);
        }

        // ---- page map, menu and source checks -----------------------------------------------------------------------

        [Fact]
        public void PageMap_AddEmptyTrays_UsesTheExistingTrayAddPermissions_NothingWidened()
        {
            Assert.Equal(MaterialStockAccess.TrayAddAny, FeatureAuthorizationConventions.GetRule("/Production/MotherPlant/AddEmptyTrays").Read);
            Assert.Equal("MotherPlant.Enter", FeatureAuthorizationConventions.GetRule("/Production/EmptyPotInventory/AddStock").Read);
            Assert.Equal("ReadyStock.TransferToMotherPlant", FeatureAuthorizationConventions.GetRule("/Production/ReadyStock/TransferToMotherPlant").Read);
            Assert.Equal("Transplant.Enter", FeatureAuthorizationConventions.GetRule("/Production/Transplanting/Create").Read);
            Assert.Equal("MotherPlant.Enter", FeatureAuthorizationConventions.GetRule("/Production/Cutting/Create").Read);   // Cutting to Pots Production
        }

        [Fact]
        public void Menu_AddEmptyPotsAndTrays_InMotherPlantAndCutting_PotProductionUntouchedOtherwise()
        {
            var layout = ReadSource("Pages", "Shared", "_Layout.cshtml");
            var mp = layout.IndexOf(">Mother Plant &amp; Cutting<", StringComparison.Ordinal);
            var pot = layout.IndexOf("POT PRODUCTION", StringComparison.Ordinal);
            var pots = layout.IndexOf("nav-page=\"/Production/MotherPlant/AddEmptyTrays\"", StringComparison.Ordinal);
            Assert.True(mp > 0 && pots > mp && pots < pot);
            // Pot Production keeps its stock view, batches and potted stock
            var potSection = layout[pot..layout.IndexOf("</li>", layout.IndexOf("PottedPlantStock/Index", pot, StringComparison.Ordinal), StringComparison.Ordinal)];
            Assert.Contains("/Production/EmptyPotInventory/Index", potSection);
            Assert.Contains("/Production/PotBatch/Index", potSection);
            Assert.Contains("/Production/PottedPlantStock/Index", potSection);
            Assert.DoesNotContain("AddStock", potSection);

            foreach (var page in new[] { ReadSource("Pages", "Production", "MotherPlant", "Index.cshtml"), ReadSource("Pages", "Production", "Cutting", "Index.cshtml") })
            {
                Assert.Contains("asp-page=\"/Production/EmptyPotInventory/AddStock\"", page);
                Assert.Contains("asp-page=\"/Production/MotherPlant/AddEmptyTrays\"", page);
            }
            // Cutting to Pots Production (Use for Pot Production) is still offered
            Assert.Contains("CuttingDestination.PotProduction", ReadSource("Pages", "Production", "Cutting", "Create.cshtml"));
        }

        [Fact]
        public void AddEmptyPagesAndTransferPages_AreaOnly_ServerChecked()
        {
            var trays = ReadSource("Pages", "Production", "MotherPlant", "AddEmptyTrays.cshtml.cs");
            Assert.Contains("AddStockAsync(\n                area!.Id, null,", trays.Replace("\r\n", "\n"));         // no polyhouse
            Assert.Contains("Areas.FirstOrDefault(a => a.Id == AreaId.Value)", trays);
            Assert.DoesNotContain("PolyhouseId", ReadSource("Pages", "Production", "MotherPlant", "AddEmptyTrays.cshtml"));
            Assert.DoesNotContain("Polyhouse", ReadSource("Pages", "Production", "EmptyPotInventory", "AddStock.cshtml").Replace("(no polyhouse)", ""));

            var transfer = ReadSource("Pages", "Production", "ReadyStock", "TransferToMotherPlant.cshtml.cs");
            Assert.Contains("DestinationPolyhouseId = null;   // the Area, never a posted Polyhouse", transfer);
            var transplant = ReadSource("Pages", "Production", "Transplanting", "Create.cshtml.cs");
            Assert.Contains("TargetPolyhouseId = null;   // the Area's trays, never a posted Polyhouse", transplant);
            Assert.Contains("IsPotProductionAsync", transplant);
            // the repository is the authority: purpose gate under lock
            Assert.Contains("ValidateTransplantPurpose", ReadSource("Data", "TransplantRepository.cs"));
        }

        // ---- real HTTP authorization -------------------------------------------------------------------------------

        private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string permissions)
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var request = new HttpRequestMessage(method, url);
            if (method == HttpMethod.Post)
                request.Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("AreaId", "127") });
            request.Headers.Add(DeleteAuthorizationTests.HeaderAuthHandler.Header, permissions);
            return await client.SendAsync(request);
        }

        // The Mother Plant Supervisor's tray view is read-only (owner decision 2026-10-11); Pot Production and
        // booking roles never add trays.
        [Theory]
        [InlineData("GET", "MotherPlant.Enter,MotherPlant.View,TrayStock.ViewMotherPlant")]
        [InlineData("POST", "MotherPlant.Enter,MotherPlant.View,TrayStock.ViewMotherPlant")]
        [InlineData("POST", "PotProduction.Enter,PotProduction.View")]
        [InlineData("POST", "Booking.Enter,Booking.View")]
        public async Task AddEmptyTrays_WithoutATrayAddPermission_IsDenied(string method, string permissions)
        {
            var response = await SendAsync(new HttpMethod(method), "/Production/MotherPlant/AddEmptyTrays", permissions);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/Account/AccessDenied", response.Headers.Location!.ToString());
        }

        [Theory]
        [InlineData("TrayStock.EnterMotherPlant")]
        [InlineData("TrayStock.Enter,Sowing.Enter")]
        [InlineData("FULL")]
        public async Task AddEmptyTrays_TrayAddPermission_IsAuthorized(string permissions)
            // authorized; the POST then stops only at the missing anti-forgery token (no handler or database ran)
            => Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/Production/MotherPlant/AddEmptyTrays", permissions)).StatusCode);
    }
}
