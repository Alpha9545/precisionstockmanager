using PlantStockManager.Services;
using Xunit;

namespace PlantStockManager.Tests
{
    // Actually exercises the ClosedXML/iTextSharp file-building code the new
    // Stock section Print/Excel buttons call -- not just a compile check.
    public class ExportHelperTests
    {
        private static readonly string[] Headers = { "Species", "Area", "Qty" };

        [Fact]
        public void BuildExcel_WithRows_ProducesAValidNonEmptyWorkbook()
        {
            var rows = new List<IReadOnlyList<object?>>
            {
                new object?[] { "Vinca", "Outlet A", 12.00m },
                new object?[] { "Petunia", "Outlet A", 40 },
            };
            var bytes = ExportHelper.BuildExcel("Test Sheet", Headers, rows);
            Assert.True(bytes.Length > 0);
            // XLSX is a zip container -- "PK" magic bytes confirm a real file, not garbage.
            Assert.Equal((byte)'P', bytes[0]);
            Assert.Equal((byte)'K', bytes[1]);
        }

        [Fact]
        public void BuildExcel_NoRows_StillProducesAValidWorkbook()
        {
            var bytes = ExportHelper.BuildExcel("Empty", Headers, Array.Empty<IReadOnlyList<object?>>());
            Assert.True(bytes.Length > 0);
            Assert.Equal((byte)'P', bytes[0]);
            Assert.Equal((byte)'K', bytes[1]);
        }

        [Fact]
        public void BuildExcel_LargeResult_ProducesAValidWorkbook()
        {
            var rows = Enumerable.Range(1, 5000).Select(i => (IReadOnlyList<object?>)new object?[] { $"Species {i}", "Area X", (decimal)i });
            var bytes = ExportHelper.BuildExcel("Large", Headers, rows);
            Assert.True(bytes.Length > 0);
        }

        [Fact]
        public void BuildPdf_WithRows_ProducesAValidPdf()
        {
            var rows = new List<IReadOnlyList<string>>
            {
                new[] { "Vinca", "Outlet A", "12" },
            };
            var bytes = ExportHelper.BuildPdf("Test Report", Headers, rows);
            Assert.True(bytes.Length > 0);
            Assert.Equal((byte)'%', bytes[0]);
            Assert.Equal((byte)'P', bytes[1]);
            Assert.Equal((byte)'D', bytes[2]);
            Assert.Equal((byte)'F', bytes[3]);
        }

        [Fact]
        public void BuildPdf_NoRows_StillProducesAValidPdf()
        {
            var bytes = ExportHelper.BuildPdf("Empty Report", Headers, Array.Empty<IReadOnlyList<string>>());
            Assert.True(bytes.Length > 0);
            Assert.Equal((byte)'%', bytes[0]);
        }
    }
}
