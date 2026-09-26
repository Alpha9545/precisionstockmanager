using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Pot Size master: one standard spelling per size.
    public class PotSizeRulesTests
    {
        [Theory]
        [InlineData("5 inch", "5 inch")]
        [InlineData("5 Inch", "5 inch")]
        [InlineData("5\"", "5 inch")]
        [InlineData("5-inch", "5 inch")]
        [InlineData("5in", "5 inch")]
        [InlineData("  5   INCHES ", "5 inch")]
        [InlineData("4.5\"", "4.5 inch")]
        [InlineData("4 inch", "4 inch")]
        public void InchSpellings_AreOneStandardValue(string typed, string expected)
            => Assert.Equal(expected, PotSizeRules.Normalize(typed));

        [Fact]
        public void NonInchSizes_AreKeptAsTyped_Trimmed()
            => Assert.Equal("Hanging basket", PotSizeRules.Normalize("  Hanging   basket "));

        [Theory]
        [InlineData("5\"")]
        [InlineData("5-inch")]
        [InlineData("5 INCH")]
        public void Duplicate_IsRefused(string typed)
        {
            var (ok, _, error) = PotSizeRules.Validate(typed, new[] { "4 inch", "5 inch" });
            Assert.False(ok);
            Assert.Contains("already exists", error);
        }

        [Fact]
        public void NewSize_IsAccepted_AndStandardised()
        {
            var (ok, name, _) = PotSizeRules.Validate("6\"", new[] { "4 inch", "5 inch" });
            Assert.True(ok);
            Assert.Equal("6 inch", name);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void Empty_IsRefused(string? typed) => Assert.False(PotSizeRules.Validate(typed, Array.Empty<string>()).Ok);
    }
}
