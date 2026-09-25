using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using PlantStockManager.Pages.Production.SeedSowing;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Direct Sowing tray calculation: NoOfTrays = FLOOR(SeedQuantity / TraySize)
    // (Services/DirectSowingRules.CalculateTrays). Always floor -- never
    // rounded to nearest, never rounded up.
    public class TrayCalculationTests
    {
        [Theory]
        [InlineData("9 Cavity", 9)]
        [InlineData("24 Cavity", 24)]
        [InlineData("42 Cavity", 42)]
        [InlineData("102 Cavity", 102)]
        [InlineData("150 Cavity", 150)]
        public void CavityCount_AllowedSizes(string cavityType, int expected)
            => Assert.Equal(expected, DirectSowingRules.CavityCount(cavityType));

        [Theory]
        [InlineData("10 Cavity")]
        [InlineData("102")]
        [InlineData("102 cavity")]
        [InlineData("")]
        [InlineData(null)]
        public void CavityCount_InvalidSizes_AreNull(string? cavityType)
            => Assert.Null(DirectSowingRules.CavityCount(cavityType));

        // Exact division: no seeds left over.
        [Theory]
        [InlineData(90, "9 Cavity", 10)]
        [InlineData(120, "24 Cavity", 5)]
        [InlineData(126, "42 Cavity", 3)]
        [InlineData(14484, "102 Cavity", 142)]
        [InlineData(600, "150 Cavity", 4)]
        public void ExactDivision(decimal quantity, string cavityType, int trays)
        {
            var r = DirectSowingRules.CalculateTrays(quantity, cavityType);
            Assert.True(r.Ok);
            Assert.Equal(trays, r.Trays);
            Assert.Equal(quantity, r.SeedsUsed);
            Assert.Equal(0, r.RemainingSeeds);
        }

        // Division with a remainder: complete trays only.
        [Theory]
        [InlineData(100, "9 Cavity", 11, 99, 1)]
        [InlineData(50, "24 Cavity", 2, 48, 2)]
        [InlineData(50, "42 Cavity", 1, 42, 8)]
        [InlineData(14492, "102 Cavity", 142, 14484, 8)]   // 142.078... -> 142
        [InlineData(299, "150 Cavity", 1, 150, 149)]       // 1.993... -> 1 (never rounded up)
        [InlineData(4000, "24 Cavity", 166, 3984, 16)]     // 166.67 -> 166 (the sowing already in PlantsIMS2_Test)
        public void DivisionWithRemainder(decimal quantity, string cavityType, int trays, decimal used, decimal remaining)
        {
            var r = DirectSowingRules.CalculateTrays(quantity, cavityType);
            Assert.True(r.Ok);
            Assert.Equal(trays, r.Trays);
            Assert.Equal(used, r.SeedsUsed);
            Assert.Equal(remaining, r.RemainingSeeds);
            Assert.Equal(quantity, r.SeedsUsed + r.RemainingSeeds);
        }

        // Floor behaviour, including results just below the next whole tray
        // and fractional seed quantities (the calculation itself floors; the
        // sowing entry separately requires whole seeds).
        [Theory]
        [InlineData(21738, "150 Cavity", 144)]   // 144.92 -> 144
        [InlineData(1449.2, "9 Cavity", 161)]     // 161.02 -> 161
        [InlineData(1457.99, "9 Cavity", 161)]    // 161.998... -> 161
        [InlineData(203.99, "102 Cavity", 1)]     // 1.9999 -> 1
        public void AlwaysFloors(decimal quantity, string cavityType, int trays)
            => Assert.Equal(trays, DirectSowingRules.CalculateTrays(quantity, cavityType).Trays);

        [Theory]
        [InlineData("9 Cavity", 9)]
        [InlineData("24 Cavity", 24)]
        [InlineData("42 Cavity", 42)]
        [InlineData("102 Cavity", 102)]
        [InlineData("150 Cavity", 150)]
        public void ExactlyOneTray(string cavityType, decimal cavity)
        {
            var r = DirectSowingRules.CalculateTrays(cavity, cavityType);
            Assert.True(r.Ok);
            Assert.Equal(1, r.Trays);
        }

        [Theory]
        [InlineData(8, "9 Cavity")]
        [InlineData(23, "24 Cavity")]
        [InlineData(41, "42 Cavity")]
        [InlineData(101, "102 Cavity")]
        [InlineData(149, "150 Cavity")]
        public void LessThanOneTray_IsRefused(decimal quantity, string cavityType)
        {
            var r = DirectSowingRules.CalculateTrays(quantity, cavityType);
            Assert.False(r.Ok);
            Assert.Equal(0, r.Trays);
            Assert.Contains("less than one complete", r.Error);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(-14492)]
        public void ZeroOrNegativeQuantity_IsRefused(decimal quantity)
        {
            var r = DirectSowingRules.CalculateTrays(quantity, "102 Cavity");
            Assert.False(r.Ok);
            Assert.Contains("greater than zero", r.Error);
        }

        [Theory]
        [InlineData("10 Cavity")]
        [InlineData("1449")]
        [InlineData(null)]
        public void InvalidTraySize_IsRefused(string? cavityType)
        {
            var r = DirectSowingRules.CalculateTrays(1449.2m, cavityType);
            Assert.False(r.Ok);
            Assert.Contains("Tray size must be one of", r.Error);
        }

        [Fact]
        public void TrayCountIsAlwaysAWholeNumber()
        {
            foreach (var cavityType in DirectSowingRules.CavityTypes)
                for (decimal q = 1; q <= 1000; q += 7.3m)
                {
                    var r = DirectSowingRules.CalculateTrays(q, cavityType);
                    if (!r.Ok) continue;
                    var cavity = DirectSowingRules.CavityCount(cavityType)!.Value;
                    Assert.Equal(decimal.Floor(q / cavity), r.Trays);
                    Assert.True(r.RemainingSeeds >= 0 && r.RemainingSeeds < cavity);
                }
        }

        // Client/server consistency: the live preview on the Direct Sowing
        // page is served by the page's TrayCalculation handler, which must
        // return exactly what the save uses (DirectSowingRules.CalculateTrays).
        [Theory]
        [InlineData(14492, "102 Cavity")]
        [InlineData(1449.2, "9 Cavity")]
        [InlineData(8, "9 Cavity")]
        [InlineData(0, "24 Cavity")]
        [InlineData(500, "10 Cavity")]
        public void PagePreview_MatchesServerCalculation(decimal quantity, string cavityType)
        {
            var page = new CreateModel(null!, null!, null!, null!, null!, null!, null!, null!, null!);
            var json = Assert.IsType<JsonResult>(page.OnGetTrayCalculation(quantity, cavityType));
            object? Prop(string name) => json.Value!.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!.GetValue(json.Value);

            var server = DirectSowingRules.CalculateTrays(quantity, cavityType);
            Assert.Equal(server.Ok, Prop("ok"));
            Assert.Equal(server.Trays, Prop("trays"));
            Assert.Equal(server.SeedsUsed, Prop("seedsUsed"));
            Assert.Equal(server.RemainingSeeds, Prop("remainingSeeds"));
            Assert.Equal(server.Error, Prop("error"));
            Assert.Equal(DirectSowingRules.IsWholeNumber(quantity), Prop("wholeNumber"));
        }
    }
}
