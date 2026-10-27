using Microsoft.AspNetCore.Http;
using PlantStockManager.Models;
using PlantStockManager.Services;
using ReadyStockIndex = PlantStockManager.Pages.Production.ReadyStock.IndexModel;

namespace PlantStockManager.Tests
{
    // 2026-10-27: the Ready Stock list's option-list filters (Services/ReadyStockListFilter.cs) and the Sowings list's
    // Category / Source filters (Services/SeedSowingListFilter.cs) -- pure rules, no database.
    public class ReadyStockListFilterTests
    {
        private static ReadyStock Batch(int id, int species, string speciesName, string plantType, int area, string areaName,
            int? polyhouse = null, string? polyhouseName = null, string origin = ReadyStock.OriginSowing, decimal qty = 240, decimal reserved = 0)
            => new()
            {
                Id = id, SpeciesId = species, SpeciesName = speciesName, PlantTypeName = plantType, AreaId = area, AreaName = areaName,
                PolyhouseId = polyhouse, PolyhouseName = polyhouseName, OriginType = origin, Quantity = qty, ReservedQuantity = reserved, CavityType = "24 Cavity"
            };

        // The user's Area-scoped rows: Areas 2 and 7 only.
        private static readonly List<ReadyStock> Visible = new()
        {
            Batch(1, 10, "Marigold Orange", "MERI GOLD", 2, "Main Office", 4, "PH-1"),
            Batch(2, 10, "Marigold Orange", "MERI GOLD", 2, "Main Office", 5, "PH-2", reserved: 100),
            Batch(3, 11, "Zinnia Red", "ZINNIA", 7, "MP Area", null, null, ReadyStock.OriginTransfer),
            Batch(4, 11, "Zinnia Red", "ZINNIA", 7, "MP Area", null, null, ReadyStock.OriginTransplant, reserved: 240),
        };

        private static ReadyStockListFilter.ListOptions Options => ReadyStockListFilter.OptionsFor(Visible);

        private static int[] Apply(ReadyStockListFilter f) => Visible.Where(f.Matches).Select(r => r.Id).ToArray();

        [Fact]
        public void Options_AreBuiltOnlyFromTheVisibleRows()
        {
            var o = Options;
            Assert.Equal(new[] { "MERI GOLD", "ZINNIA" }, o.Species);
            Assert.Equal(new[] { 10, 11 }, o.Varieties.Select(v => v.Id));
            Assert.Equal("Marigold Orange (MERI GOLD)", o.Varieties[0].Label);
            Assert.Equal(new[] { 2, 7 }, o.Areas.Select(a => a.Id));
            Assert.Equal(new[] { 4, 5 }, o.Polyhouses.Select(p => p.Id));
            Assert.Equal(ReadyStockListFilter.OriginValues, o.Origins);
            Assert.Equal(new[] { "Available", "Partly reserved/dispatched", "Fully reserved" }, o.Statuses);
        }

        [Fact]
        public void Options_RepeatedPolyhouseName_GetsItsAreaInTheLabel()
        {
            var rows = new[] { Batch(1, 1, "A", "T", 2, "Main Office", 4, "PH-1"), Batch(2, 1, "A", "T", 3, "Second", 9, "PH-1") };
            Assert.Equal(new[] { "PH-1 (Main Office)", "PH-1 (Second)" }, ReadyStockListFilter.OptionsFor(rows).Polyhouses.Select(p => p.Label));
        }

        [Fact]
        public void StatusValues_CoverEveryStatusTheModelCanReport()
        {
            var samples = new[]
            {
                new ReadyStock { Quantity = 0 },
                new ReadyStock { Quantity = 10, DispatchedQuantity = 10 },
                new ReadyStock { Quantity = 10, InTransitQuantity = 10 },
                new ReadyStock { Quantity = 10, ReservedQuantity = 10 },
                new ReadyStock { Quantity = 10, ReservedQuantity = 5 },
                new ReadyStock { Quantity = 10 },
            };
            Assert.All(samples, s => Assert.Contains(s.Status, ReadyStockListFilter.StatusValues));
            Assert.Equal(ReadyStockListFilter.StatusValues.OrderBy(x => x), samples.Select(s => s.Status).Distinct().OrderBy(x => x));
        }

        [Fact]
        public void Normalize_KeepsValidValues_AndTheyCombine()
        {
            var (f, note) = ReadyStockListFilter.Normalize("MERI GOLD", 10, 2, 5, ReadyStock.OriginSowing, "Partly reserved/dispatched", Options);
            Assert.Null(note);
            Assert.True(f.IsActive);
            Assert.Equal(new[] { 2 }, Apply(f));
            Assert.Equal(new[] { 1, 2 }, Apply(ReadyStockListFilter.Normalize("MERI GOLD", null, null, null, null, null, Options).Filter));
            Assert.Equal(new[] { 4 }, Apply(ReadyStockListFilter.Normalize(null, 11, null, null, ReadyStock.OriginTransplant, null, Options).Filter));
            // valid individually, contradictory together: nothing
            Assert.Empty(Apply(ReadyStockListFilter.Normalize("ZINNIA", 10, null, null, null, null, Options).Filter));
            Assert.Empty(Apply(ReadyStockListFilter.Normalize(null, null, 7, 4, null, null, Options).Filter));
        }

        [Fact]
        public void Normalize_Unset_IsNoRestriction()
        {
            var (f, note) = ReadyStockListFilter.Normalize(null, null, null, null, "", "", Options);
            Assert.Null(note);
            Assert.False(f.IsActive);
            Assert.Equal(new[] { 1, 2, 3, 4 }, Apply(f));
        }

        [Theory]
        [InlineData(null, 99, null, null, null, null, "Variety")]          // a variety not in the user's list
        [InlineData(null, -1, null, null, null, null, "Variety")]
        [InlineData(null, null, 5, null, null, null, "Area")]              // an Area the user may not see
        [InlineData(null, null, 0, null, null, null, "Area")]
        [InlineData(null, null, null, 77, null, null, "Polyhouse")]        // another Area's Polyhouse
        [InlineData("meri gold", null, null, null, null, null, "Species")] // exact match only
        [InlineData("<script>", null, null, null, null, null, "Species")]
        [InlineData(null, null, null, null, "sowing", null, "Origin")]
        [InlineData(null, null, null, null, null, "Empty", "Status")]      // not among the listed rows
        [InlineData(null, null, null, null, null, "'; DROP TABLE x --", "Status")]
        public void Normalize_DropsAnInvalidOrUnauthorisedValue_AndSaysSo(string? species, int? variety, int? area, int? polyhouse,
            string? origin, string? status, string named)
        {
            var (f, note) = ReadyStockListFilter.Normalize(species, variety, area, polyhouse, origin, status, Options);
            Assert.False(f.IsActive);
            Assert.NotNull(note);
            Assert.StartsWith(ReadyStockListFilter.ClearedMessage, note);
            Assert.Contains(named, note);
            Assert.Equal(new[] { 1, 2, 3, 4 }, Apply(f));
        }

        [Fact]
        public void Normalize_OneBadValue_KeepsTheOthers()
        {
            var (f, note) = ReadyStockListFilter.Normalize("ZINNIA", null, 5, null, null, null, Options);
            Assert.Equal("ZINNIA", f.Species);
            Assert.Null(f.AreaId);
            Assert.Contains("Area", note);
            Assert.Equal(new[] { 3, 4 }, Apply(f));
        }

        [Fact]
        public void Options_FromNoRows_AcceptNothing()
        {
            var empty = ReadyStockListFilter.OptionsFor(Array.Empty<ReadyStock>());
            var (f, note) = ReadyStockListFilter.Normalize("MERI GOLD", 10, 2, 4, ReadyStock.OriginSowing, "Available", empty);
            Assert.False(f.IsActive);
            Assert.NotNull(note);
        }

        [Fact]
        public void ListRoute_WithoutOptionFilters_IsUnchanged_AndAddsOnlySetFilters()
        {
            Assert.Equal(new Dictionary<string, string?> { ["Stock"] = "og", ["Search"] = "x", ["IncludeEmpty"] = "true" },
                ReadyStockIndex.ListRouteFor("og", "x", true));
            Assert.Equal(new Dictionary<string, string?> { ["Stock"] = "og", ["Search"] = "x", ["IncludeEmpty"] = "true" },
                ReadyStockIndex.ListRouteFor("og", "x", true, ReadyStockListFilter.None));
            var f = new ReadyStockListFilter("ZINNIA", 11, null, 4, ReadyStock.OriginTransfer, null);
            Assert.Equal(new Dictionary<string, string?>
            {
                ["Stock"] = null, ["Search"] = null, ["IncludeEmpty"] = null,
                ["Species"] = "ZINNIA", ["Variety"] = "11", ["Polyhouse"] = "4", ["Origin"] = ReadyStock.OriginTransfer,
            }, ReadyStockIndex.ListRouteFor(null, null, false, f));
        }

        [Fact]
        public void FromQuery_ShapeChecksOnly()
        {
            var q = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
            {
                ["Species"] = "ZINNIA", ["Variety"] = "11", ["Area"] = "-3", ["Polyhouse"] = "4x", ["Origin"] = new string('o', 101), ["Status"] = "Available",
            });
            Assert.Equal(new ReadyStockListFilter("ZINNIA", 11, null, null, null, "Available"), ReadyStockListFilter.FromQuery(q));
            Assert.Equal(ReadyStockListFilter.None, ReadyStockListFilter.FromQuery(QueryCollection.Empty));
        }

        // ---- Sowings list: Category (Plant Type) / Source ------------------------------------------------------------

        [Fact]
        public void SowingFilter_KeepsAKnownCategoryAndAnExactSource()
        {
            var (f, note) = SeedSowingListFilter.Normalize(2, SeedSowing.SourceCutting, new[] { 1, 2, 3, 4 });
            Assert.Null(note);
            Assert.Equal(new SeedSowingListFilter(2, SeedSowing.SourceCutting), f);
            Assert.True(f.IsActive);
            Assert.False(SeedSowingListFilter.Normalize(null, null, new[] { 1 }).Filter.IsActive);
            Assert.False(SeedSowingListFilter.Normalize(null, "", new[] { 1 }).Filter.IsActive);
        }

        [Theory]
        [InlineData(9, null, SeedSowingListFilter.CategoryClearedMessage)]
        [InlineData(0, null, SeedSowingListFilter.CategoryClearedMessage)]
        [InlineData(-1, null, SeedSowingListFilter.CategoryClearedMessage)]
        [InlineData(null, "seed", SeedSowingListFilter.SourceClearedMessage)]
        [InlineData(null, "Seed' OR 1=1 --", SeedSowingListFilter.SourceClearedMessage)]
        public void SowingFilter_DropsAnUnknownValue_AndSaysSo(int? category, string? source, string message)
        {
            var (f, note) = SeedSowingListFilter.Normalize(category, source, new[] { 1, 2, 3, 4 });
            Assert.False(f.IsActive);
            Assert.Equal(message, note);
        }

        [Fact]
        public void SowingFilter_SourceOptions_AreTheFourSourceTypes_WithTheModelsLabels()
        {
            Assert.Equal(new[] { SeedSowing.SourceSeed, SeedSowing.SourceCutting, SeedSowing.SourceBedSowing, SeedSowing.SourceTransplant },
                SeedSowingListFilter.SourceOptions.Select(o => o.Value));
            Assert.Equal(new[] { "Seed lot", "Cutting Stock", "Bed Sowing", "Transplanted Ready Stock" }, SeedSowingListFilter.SourceOptions.Select(o => o.Label));
        }
    }
}
