using System.IO;
using PlantStockManager.Services;
using CreatePage = PlantStockManager.Pages.Production.SeedSowing.CreateModel;

namespace PlantStockManager.Tests
{
    // Direct Seed Sowing -- Main Areas only, Area derived from the Polyhouse (2026-10-08):
    // the pure rule and the page wiring. Database behaviour: DirectSowingMainAreaE2ETests.
    public class DirectSowingMainAreaTests
    {
        private const string MainOffice = DirectSowingRules.MainOfficeAreaType;

        private static string ReadSource(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
        }

        // ---- the rule --------------------------------------------------------------------------------

        [Theory]
        [InlineData(null)]
        [InlineData(0)]
        public void MainAreaPolyhouse_UsesThePolyhousesArea_WhenNoAreaIsPosted(int? posted)
        {
            var (ok, areaId, error) = DirectSowingRules.ResolveDirectSowingArea(posted, 3, true, 2, MainOffice, true);
            Assert.True(ok);
            Assert.Equal(2, areaId);
            Assert.Null(error);
        }

        [Fact]
        public void PostedAreaEqualToThePolyhousesArea_IsAccepted()
            => Assert.Equal((true, 2, (string?)null), DirectSowingRules.ResolveDirectSowingArea(2, 3, true, 2, MainOffice, true));

        [Fact]
        public void ForgedAreaId_DifferentFromThePolyhousesArea_IsRefused()
        {
            var (ok, areaId, error) = DirectSowingRules.ResolveDirectSowingArea(127, 3, true, 2, MainOffice, true);
            Assert.False(ok);
            Assert.Equal(0, areaId);
            Assert.Equal(DirectSowingRules.DirectSowingAreaMismatchMessage, error);
        }

        [Theory]
        [InlineData("MotherPlant", true)]
        [InlineData("Outlet", true)]
        [InlineData(null, true)]
        [InlineData("mainoffice", true)]    // exact AreaType only
        [InlineData("MainOffice", false)]   // an inactive Main Area
        public void NonMainOrInactiveArea_IsRefused(string? areaType, bool active)
            => Assert.Equal((false, 0, DirectSowingRules.DirectSowingNotMainAreaMessage),
                DirectSowingRules.ResolveDirectSowingArea(null, 14, true, 127, areaType, active));

        [Fact]
        public void PolyhouseWithoutArea_IsRefused()
            => Assert.Equal((false, 0, DirectSowingRules.DirectSowingNotMainAreaMessage),
                DirectSowingRules.ResolveDirectSowingArea(2, 5, true, null, null, false));

        [Theory]
        [InlineData(null)]
        [InlineData(0)]
        [InlineData(-1)]
        public void MissingPolyhouse_IsRefused(int? polyhouseId)
            => Assert.Equal((false, 0, DirectSowingRules.DirectSowingPolyhouseRequiredMessage),
                DirectSowingRules.ResolveDirectSowingArea(2, polyhouseId, false, null, null, false));

        [Fact]
        public void UnknownPolyhouse_IsRefused()
            => Assert.Equal((false, 0, DirectSowingRules.DirectSowingPolyhouseNotFoundMessage),
                DirectSowingRules.ResolveDirectSowingArea(2, 999999, false, null, null, false));

        [Fact]
        public void ForgedMainAreaId_OnANonMainPolyhouse_IsStillRefused()
            => Assert.Equal(DirectSowingRules.DirectSowingNotMainAreaMessage,
                DirectSowingRules.ResolveDirectSowingArea(2, 14, true, 127, "MotherPlant", true).Error);

        // ---- the page: no Area selector, the Area is derived on the server -------------------------------

        [Fact]
        public void Form_HasNoAreaSelector_OnlyAReadOnlyMainArea_AndPolyhousesFromTheServer()
        {
            var html = ReadSource("Pages", "Production", "SeedSowing", "Create.cshtml");
            Assert.DoesNotContain("asp-for=\"SeedSowing.AreaId\"", html);
            Assert.DoesNotContain("name=\"SeedSowing.AreaId\"", html);
            Assert.DoesNotContain("areaSelect", html);
            Assert.DoesNotContain("Growing Area", html);
            Assert.Contains("id=\"mainAreaDisplay\" class=\"form-control\" readonly", html);
            Assert.Contains(">Main Area</label>", html);
            Assert.Contains("@foreach (var g in Model.Polyhouses.GroupBy(p => p.AreaName))", html);
            // Field order: Main Area, then Polyhouse.
            Assert.True(html.IndexOf(">Main Area</label>", StringComparison.Ordinal) < html.IndexOf(">Polyhouse</label>", StringComparison.Ordinal));
        }

        [Fact]
        public void Page_DerivesTheAreaFromThePolyhouse_AndChecksTheAreaScope()
        {
            var page = ReadSource("Pages", "Production", "SeedSowing", "Create.cshtml.cs");
            Assert.Contains("await _polyhouseRepo.GetByIdAsync(SeedSowing.PolyhouseId.Value)", page);
            Assert.Contains("DirectSowingRules.ResolveDirectSowingArea(", page);
            Assert.Contains("_areaAccessService.CanAccessArea(User, derivedAreaId)", page);
            Assert.Contains("SeedSowing.AreaId = derivedAreaId;", page);
            Assert.Contains("_polyhouseRepo.GetDirectSowingPolyhousesAsync()", page);
            Assert.Null(typeof(CreatePage).GetProperty("Areas"));
            Assert.Null(typeof(CreatePage).GetMethod("OnGetPolyhousesAsync"));
            // The material preview takes the Polyhouse only; its Area is looked up, never posted.
            var preview = typeof(CreatePage).GetMethod("OnGetMaterialPreviewAsync")!;
            Assert.DoesNotContain(preview.GetParameters(), p => p.Name == "areaId");
        }

        [Fact]
        public void Repository_ReadsTheAreaFromThePolyhouse_InsideTheSaveTransaction()
        {
            var repo = ReadSource("Data", "SeedSowingRepository.cs");
            Assert.Contains("DirectSowingRules.ResolveDirectSowingArea(", repo);
            Assert.DoesNotContain("Please select the Growing Area for this sowing.", repo);
            var polyhouses = ReadSource("Data", "PolyhouseRepository.cs");
            Assert.Contains("a.AreaType = @MainOffice", polyhouses);
            Assert.Contains("Services.DirectSowingRules.MainOfficeAreaType", polyhouses);
        }
    }
}
