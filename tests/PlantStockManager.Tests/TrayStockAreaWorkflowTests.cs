using System.IO;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using PlantStockManager.Authorization;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Tray Stock Area + Polyhouse + Cavity workflow (2026-10-04) -- pure rules and wiring.
    // The database behaviour (add, consume, block, reverse, overage,
    // concurrency) is TrayStockReversalTests, against a scratch copy.
    public class TrayStockAreaWorkflowTests
    {
        private static string ReadSource(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
        }

        // ---- physical trays: CEILING, unchanged -------------------------------------------------------

        [Theory]
        [InlineData(950, "24 Cavity", 40)]   // 39.58 -> 40
        [InlineData(960, "24 Cavity", 40)]   // exact
        [InlineData(480, "24 Cavity", 20)]
        [InlineData(1, "150 Cavity", 1)]
        [InlineData(2000, "150 Cavity", 14)]
        [InlineData(7000, "150 Cavity", 47)]
        public void PhysicalTraysRequired_IsCeiling(decimal quantity, string cavity, decimal expected)
            => Assert.Equal(expected, DirectSowingRules.PhysicalTraysRequired(quantity, cavity));

        [Fact]
        public void PhysicalTrays_AndCompleteTrays_StaySeparateConcepts()
        {
            var (ok, trays, _, _, _) = DirectSowingRules.CalculateTrays(950, "24 Cavity");
            Assert.True(ok);
            Assert.Equal(39, trays);                                                   // NumberOfTrays (FLOOR)
            Assert.Equal(40m, DirectSowingRules.PhysicalTraysRequired(950, "24 Cavity"));   // physical trays (CEILING)
        }

        [Theory]
        [InlineData(0, "24 Cavity")]
        [InlineData(100, "50 Cavity")]
        [InlineData(100, null)]
        public void PhysicalTraysRequired_InvalidInput_IsZero(decimal quantity, string? cavity)
            => Assert.Equal(0m, DirectSowingRules.PhysicalTraysRequired(quantity, cavity));

        // ---- permissions / retired workflow -----------------------------------------------------------

        [Fact]
        public void AddTrayStock_RequiresTrayStockEnter_Only()
        {
            Assert.True(FeatureAuthorizationConventions.IsMapped("/Production/TrayStock/Add"));
            var rule = FeatureAuthorizationConventions.GetRule("/Production/TrayStock/Add");
            Assert.Equal("TrayStock.Enter", rule.Read);
            Assert.DoesNotContain("MainOffice", rule.Read);
            Assert.DoesNotContain("Sowing.Enter", rule.Read);
        }

        [Fact]
        public void MainOfficeAllocateTrays_IsRetired()
        {
            Assert.False(FeatureAuthorizationConventions.IsMapped("/Production/TrayStock/Allocate"));
            Assert.Null(typeof(PlantStockManager.Pages.Production.TrayStock.IndexModel).Assembly
                .GetType("PlantStockManager.Pages.Production.TrayStock.AllocateModel"));
            Assert.DoesNotContain("TrayStock/Allocate", ReadSource("Pages", "Shared", "_Layout.cshtml"));
            Assert.Contains("/Production/TrayStock/Add", ReadSource("Pages", "Shared", "_Layout.cshtml"));
        }

        [Fact]
        public void AddTrayStock_Form_HasAreaPolyhouseCavityQuantityDateRemark_AndAOneTimeToken()
        {
            var bound = typeof(PlantStockManager.Pages.Production.TrayStock.AddModel)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<BindPropertyAttribute>() != null)
                .Select(p => p.Name).ToList();
            Assert.Equal(new[] { "AreaId", "PolyhouseId", "TraySize", "Quantity", "TransactionDate", "Remarks", "SubmissionToken" }.OrderBy(x => x), bound.OrderBy(x => x));

            var html = ReadSource("Pages", "Production", "TrayStock", "Add.cshtml");
            var order = new[] { ">Area<", ">Polyhouse<", ">Cavity<", ">Tray Quantity<", ">Date / Time<", ">Remark " }
                .Select(label => html.IndexOf(label, StringComparison.Ordinal)).ToList();
            Assert.DoesNotContain(-1, order);
            Assert.Equal(order.OrderBy(i => i), order);                              // the approved field order
            Assert.Contains("asp-format=\"{0:0}\"", html);                          // 100, never 100.00
            Assert.Contains("handler=Polyhouses&areaId=", html);                     // Polyhouse depends on Area
        }

        [Fact]
        public void AddTrayStock_ValidatesTheAreaPolyhousePairOnTheServer()
        {
            var page = ReadSource("Pages", "Production", "TrayStock", "Add.cshtml.cs");
            Assert.Contains("Selected Polyhouse does not belong to the selected Area.", page);
            Assert.Contains("_polyhouseRepo.GetByAreaIdAsync(areaId)", page);
            var repo = ReadSource("Data", "TrayStockRepository.cs");
            Assert.Contains("ValidateLocationAsync(conn, tx, areaId, polyhouseId)", repo);   // re-checked inside the save transaction
        }

        [Fact]
        public void TrayStockPages_AreScopedToTheUsersAreas()
        {
            Assert.Contains("_areaAccess.FilterByArea(User", ReadSource("Pages", "Production", "TrayStock", "Index.cshtml.cs"));
            var tx = ReadSource("Pages", "Production", "TrayStock", "Transactions.cshtml.cs");
            Assert.Contains("GetAccessibleAreaIds(User)", tx);
            Assert.Contains("GetTransactionsAsync(areaId, polyhouseId, TraySize, allowed)", tx);
            Assert.Contains("_areaAccess.CanAccessRequiredArea(User, AreaId)", ReadSource("Pages", "Production", "TrayStock", "Add.cshtml.cs"));
        }

        // ---- sowing integration ----------------------------------------------------------------------

        [Fact]
        public void Sowing_ConsumesFromTheAreaPolyhouseCavityPool()
        {
            var src = ReadSource("Data", "SeedSowingRepository.cs");
            // Both Seed (InsertAsync) and Cutting (InsertFromCuttingAsync) pass the resolved growing Area AND Polyhouse.
            Assert.Equal(2, src.Split("conn, tx, entry.AreaId, entry.PolyhouseId, entry.CavityType, entry.SeedQuantity, newId").Length - 1);
            Assert.Contains("conn, tx, areaId, polyhouseId, cavityType, requiredExtraTrays", ReadSource("Data", "ReadyConfirmationRepository.cs"));
            Assert.DoesNotContain("IsMainOfficePolyhouseAsync", src);
            Assert.DoesNotContain("IsMainOfficePolyhouseAsync", ReadSource("Data", "ReadyConfirmationRepository.cs"));
            Assert.DoesNotContain("IsMainOfficePolyhouseAsync", ReadSource("Data", "TrayStockRepository.cs"));
        }

        [Fact]
        public void SeedSowing_RequiresAreaAndPolyhouse_OnThePageAndInTheRepository()
        {
            Assert.Equal("Please select a Polyhouse for this sowing.", PlantStockManager.Data.TrayStockRepository.PolyhouseRequiredMessage);
            var page = ReadSource("Pages", "Production", "SeedSowing", "Create.cshtml.cs");
            Assert.Contains("if (SeedSowing.PolyhouseId is not > 0)", page);
            Assert.Contains("if (SeedSowing.AreaId <= 0)", page);
            var repo = ReadSource("Data", "SeedSowingRepository.cs");
            Assert.Contains("if (entry.PolyhouseId is not > 0)", repo);
            var html = ReadSource("Pages", "Production", "SeedSowing", "Create.cshtml");
            Assert.DoesNotContain("-- No Polyhouse --", html);
            Assert.Contains("id=\"polyhouseSelect\" class=\"form-select\" required", html);
        }

        [Fact]
        public void AdminPolyhouse_AreaChange_IsGuardedByTrayStock()
        {
            Assert.Equal("This Polyhouse cannot be moved to another Area while tray stock is available. Transfer/use the tray stock first.",
                PlantStockManager.Data.PolyhouseRepository.MoveBlockedByTrayStockMessage);
            var repo = ReadSource("Data", "PolyhouseRepository.cs");
            Assert.Contains("SELECT ISNULL(SUM(PhysicalQuantity), 0) FROM dbo.TrayStock WITH (UPDLOCK, HOLDLOCK) WHERE PolyhouseId = @Id", repo);
            Assert.Contains("TempData[\"Error\"] = message;", ReadSource("Pages", "Admin", "Polyhouse.cshtml.cs"));
        }

        [Fact]
        public void SeedSowingSuccessMessage_SaysTheRemainderIsRecordedAsWaste()
        {
            var src = ReadSource("Pages", "Production", "SeedSowing", "Create.cshtml.cs");
            Assert.DoesNotContain("stay in the lot", src);
            Assert.Contains("remaining seeds recorded as waste", src);
        }

        // ---- history labels --------------------------------------------------------------------------

        [Theory]
        [InlineData("Allocation", "TrayStockAdd", 100, "Tray Stock Added")]
        [InlineData("Allocation", null, 100, "Tray Stock Added")]
        [InlineData("Sowing", "SeedSowing", -40, "Used for Sowing")]
        [InlineData("ReversalReturn", "SeedSowing", 40, "Returned (cancellation)")]
        [InlineData("Adjustment", "TrayStockMigration", -1914, "Moved to Area stock (2026-10-04)")]
        [InlineData("Adjustment", "TrayStockMigration", 1914, "Carried over from Polyhouse stock (2026-10-04)")]
        [InlineData("Adjustment", "TrayStockPolyhouseSplit", -1914, "Moved back to Polyhouse stock (2026-10-04)")]
        [InlineData("Adjustment", "TrayStockPolyhouseSplit", 1914, "Restored to Polyhouse stock (2026-10-04)")]
        [InlineData("Adjustment", "TrayStockReversal", -400, "Reversed entry (no Polyhouse recorded)")]
        public void TransactionTypeLabels_ArePlainLanguage(string type, string? referenceType, decimal quantity, string expected)
            => Assert.Equal(expected, new TrayStockTransaction { TransactionType = type, ReferenceType = referenceType, Quantity = quantity }.TypeLabel);
    }
}
