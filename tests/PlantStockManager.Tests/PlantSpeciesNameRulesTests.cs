using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // When two variety names count as the same (PlantSpeciesNameRules): case and whitespace ignored, punctuation and
    // spelling kept. No database needed.
    public class PlantSpeciesNameRulesTests
    {
        [Theory]
        [InlineData("Petunia Bello Mix", "Petunia Bello Mix")]
        [InlineData("Petunia Bello Mix", "petunia bello mix")]
        [InlineData("Petunia Bello Mix", "  PETUNIA BELLO MIX  ")]
        [InlineData("Petunia Bello Mix", "Petunia   Bello\tMix")]
        [InlineData("Petunia Bello Mix", "Petunia\r\nBello Mix\r\n")]
        [InlineData("Petunia Bello Mix", "Petunia Bello Mix")]
        public void SameName_IgnoresCaseAndWhitespace(string a, string b)
            => Assert.True(PlantSpeciesNameRules.SameName(a, b));

        [Theory]
        [InlineData("Petunia S.C. White", "Petunia SC White")]                         // punctuation kept
        [InlineData("Rudbeckia Pavnee Spirit", "Rudbeckia Pawnee Spirit")]             // spelling kept
        [InlineData("Petunia Suc. 360 Rose Star", "Petunia Success 360 Rose Star")]    // abbreviations are an owner decision
        [InlineData("Petunia Success 360 Purple", "Petunia Success 360 Violet")]
        [InlineData("Petunia Success 360 Purple", "Petunia Success 360 Blue")]
        [InlineData("Verbena Quartz Mix", "Verbena Quartz XP Mix")]
        [InlineData("SH-WR", "SHWR")]
        public void DifferentNames_StayDifferent(string a, string b)
            => Assert.False(PlantSpeciesNameRules.SameName(a, b));

        [Fact]
        public void Key_IsTrimmedUpperCaseWithSingleSpaces()
        {
            Assert.Equal("PETUNIA BELLO MIX", PlantSpeciesNameRules.Key("  petunia \t bello\r\nmix "));
            Assert.Equal("", PlantSpeciesNameRules.Key(null));
            Assert.Equal("", PlantSpeciesNameRules.Key("   "));
        }
    }
}
