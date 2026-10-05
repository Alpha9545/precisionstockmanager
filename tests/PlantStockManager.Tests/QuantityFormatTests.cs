using System.Globalization;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Global quantity display rule: no trailing ".00" on whole quantities,
    // real fractions kept, thousands separator kept. Pure, no DB.
    public class QuantityFormatTests : IDisposable
    {
        private readonly CultureInfo _previous = CultureInfo.CurrentCulture;

        public QuantityFormatTests() => CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");

        public void Dispose() => CultureInfo.CurrentCulture = _previous;

        [Theory]
        [InlineData("100.00", "100")]
        [InlineData("250.00", "250")]
        [InlineData("25.00", "25")]
        [InlineData("1500.00", "1,500")]
        [InlineData("4762.00", "4,762")]
        [InlineData("25000.00", "25,000")]
        [InlineData("100.50", "100.5")]
        [InlineData("100.25", "100.25")]
        [InlineData("12.25", "12.25")]
        [InlineData("0.00", "0")]
        [InlineData("0.50", "0.5")]
        [InlineData("-50.00", "-50")]
        [InlineData("-12.50", "-12.5")]
        public void Qty_DropsTrailingZeroesAndKeepsFractions(string input, string expected)
        {
            // decimal.Parse keeps the scale, exactly like a DECIMAL(18,2) read from SQL
            var value = decimal.Parse(input, CultureInfo.InvariantCulture);
            Assert.Equal(expected, QuantityFormat.Qty(value));
        }

        [Fact]
        public void Qty_DoesNotRoundAFractionToAWholeNumber()
        {
            // the old N0 format showed 100.50 as "101"
            Assert.Equal("100.5", QuantityFormat.Qty(100.50m));
            Assert.NotEqual("101", QuantityFormat.Qty(100.50m));
        }

        [Fact]
        public void Qty_Nullable_UsesNullTextOrValue()
        {
            Assert.Equal("-", QuantityFormat.Qty((decimal?)null));
            Assert.Equal("N/A", QuantityFormat.Qty((decimal?)null, "N/A"));
            Assert.Equal("100", QuantityFormat.Qty((decimal?)100.00m));
        }

        [Fact]
        public void Qty_Int_FormatsTheSameWay()
        {
            Assert.Equal("1,000", QuantityFormat.Qty(1000));
            Assert.Equal("-", QuantityFormat.Qty((int?)null));
        }

        [Theory]
        [InlineData("12.125", "12.125")]
        [InlineData("100.000", "100")]
        [InlineData("45.500", "45.5")]
        [InlineData("1234.250", "1,234.25")]
        public void Qty3_KeepsThreeDecimalFertilizerQuantities(string input, string expected)
        {
            Assert.Equal(expected, QuantityFormat.Qty3(decimal.Parse(input, CultureInfo.InvariantCulture)));
        }

        [Theory]
        [InlineData("100.00", "100")]
        [InlineData("4762.00", "4762")]
        [InlineData("100.50", "100.5")]
        public void QtyInput_HasNoGroupingSoTheValueParsesBack(string input, string expected)
        {
            var value = decimal.Parse(input, CultureInfo.InvariantCulture);
            var text = QuantityFormat.QtyInput(value);
            Assert.Equal(expected, text);
            Assert.Equal(value, decimal.Parse(text, CultureInfo.InvariantCulture));
        }

        [Fact]
        public void Formatting_DoesNotChangeTheValue()
        {
            var value = 4762.00m;
            _ = QuantityFormat.Qty(value);
            Assert.Equal(4762.00m, value);
        }

        [Fact]
        public void MoneyFormat_IsUntouched()
        {
            // money keeps its own two-decimal format -- Qty is never used for it
            Assert.Equal("100.00", 100.00m.ToString("N2", CultureInfo.GetCultureInfo("en-US")));
        }
    }
}
