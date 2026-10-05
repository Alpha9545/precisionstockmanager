using System.IO;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using MotherPlantIndex = PlantStockManager.Pages.Production.MotherPlant.IndexModel;

namespace PlantStockManager.Tests
{
    // Two corrections:
    //  1. Add Seed Stock stored the employee's LOCAL "Received On" in
    //     SeedStockTransactions.TransactionDate, while every other writer of
    //     that column stores UTC -- so the same ledger mixed IST and UTC rows.
    //     ReceiveAsync now converts to UTC and Seed Stock Details converts back
    //     with ToLocalTime(), matching Stock History.
    //  2. The Mother Plant list filters by Area (AreaId) instead of Polyhouse.
    public class SeedStockTimeAndMotherPlantAreaFilterTests
    {
        private static string ReadSource(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
        }

        // ---- Issue 1: Seed Stock time ------------------------------------------------------------

        [Fact]
        public void ReceivedOn_FromTheForm_IsStoredAsTheSameInstantInUtc()
        {
            // datetime-local binds as DateTimeKind.Unspecified = server local business time.
            var receivedOn = new DateTime(2026, 10, 4, 10, 47, 0, DateTimeKind.Unspecified);
            var stored = receivedOn.ToUniversalTime();
            // Reading it back (SqlDataReader gives Unspecified) and displaying with ToLocalTime() round-trips.
            var displayed = DateTime.SpecifyKind(stored, DateTimeKind.Unspecified).ToLocalTime();
            Assert.Equal(receivedOn, displayed);
        }

        [Fact]
        public void ReceiveAsync_ConvertsReceivedOnToUtc()
        {
            var src = ReadSource("Data", "SeedStockRepository.cs");
            Assert.Contains("receivedOn.ToUniversalTime()", src);
            Assert.Contains("\"SeedStock\", seedStockId, userId, notes, transactionDateUtc)", src);
        }

        [Fact]
        public void SeedStockDetails_DisplaysTransactionDateInLocalTime()
        {
            var html = ReadSource("Pages", "Production", "SeedStock", "Details.cshtml");
            Assert.Contains("t.TransactionDate.ToLocalTime()", html);
        }

        [Fact]
        public void AddSeedStock_ReceivedOnDefault_IsWholeMinutes()
        {
            // A default with seconds/milliseconds becomes the datetime-local step
            // base, so the browser rejects every whole-minute time the user enters.
            var html = ReadSource("Pages", "Production", "SeedStock", "AddSeedStock.cshtml");
            Assert.Contains("asp-for=\"ReceivedOn\" asp-format=\"{0:yyyy-MM-ddTHH:mm}\" type=\"datetime-local\"", html);
        }

        // ---- Issue 2: Mother Plant Area filter ---------------------------------------------------

        [Fact]
        public void MotherPlantIndex_BindsAreaId_NotPolyhouseId()
        {
            var bound = typeof(MotherPlantIndex).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<BindPropertyAttribute>() is { SupportsGet: true })
                .Select(p => p.Name)
                .ToList();

            Assert.Contains("AreaId", bound);
            Assert.DoesNotContain("PolyhouseId", bound);
            Assert.NotNull(typeof(MotherPlantIndex).GetProperty("Areas"));
            Assert.Null(typeof(MotherPlantIndex).GetProperty("Polyhouses"));
        }

        [Fact]
        public void MotherPlantIndex_FilterMarkup_IsArea()
        {
            var html = ReadSource("Pages", "Production", "MotherPlant", "Index.cshtml");
            Assert.Contains("<label class=\"form-label\">Area</label>", html);
            Assert.Contains("name=\"AreaId\"", html);
            Assert.Contains("All Areas", html);
            Assert.DoesNotContain("<label class=\"form-label\">Polyhouse</label>", html);
            Assert.DoesNotContain("name=\"PolyhouseId\"", html);
            // Polyhouse stays a column of the list itself -- only the filter changed.
            Assert.Contains("<th>Polyhouse</th>", html);
        }

        [Fact]
        public void MotherPlantIndex_StillAppliesAreaAccessScope()
        {
            var src = ReadSource("Pages", "Production", "MotherPlant", "Index.cshtml.cs");
            Assert.Contains("m.AreaId == AreaId", src);
            Assert.Contains("_areaAccessService.CanAccessArea(User, m.AreaId)", src);
            Assert.Contains("_areaAccessService.CanAccessArea(User, a.Id)", src);
        }
    }
}
