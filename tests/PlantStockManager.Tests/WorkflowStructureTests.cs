using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using PlantStockManager.Authorization;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // The restructured workflow's pages, permissions and retired features.
    public class WorkflowStructureTests
    {
        [Theory]
        [InlineData("/Production/Cutting/Create", "MotherPlant.Enter")]
        [InlineData("/Production/CuttingStock/ConfirmReceipt", "MainOffice.Confirm")]
        [InlineData("/Production/SeedSowing/CreateFromCutting", "Sowing.Enter")]
        [InlineData("/Production/EmptyPotInventory/Purchase", "Purchase.Enter")]
        [InlineData("/Production/ReadyConfirmation/Confirm", "ReadyStock.Confirm")]
        [InlineData("/Admin/PotSizes", "Admin.ManageMasters")]
        [InlineData("/Production/EmptyPotInventory/Issue", "InternalTransfer.Enter")]
        [InlineData("/Production/PotBatch/Create", "PotProduction.Enter")]
        public void NewPages_HaveTheirPermission(string page, string permission)
        {
            Assert.True(FeatureAuthorizationConventions.IsMapped(page));
            Assert.Contains(permission, FeatureAuthorizationConventions.GetRule(page).Read);
        }

        [Theory]
        [InlineData("/Production/CuttingPlan/Index")]
        [InlineData("/Production/ActualCutting/Index")]
        [InlineData("/Production/CuttingDelivery/Index")]
        [InlineData("/Production/PropagationBatch/Index")]
        [InlineData("/Production/PotProduction/Index")]
        [InlineData("/Production/CuttingStock/Transplant")]
        [InlineData("/Production/CuttingStock/EnterCutting")]
        [InlineData("/Production/EmptyPotInventory/AddStock")]
        [InlineData("/Bookings/CancleBooking")]
        [InlineData("/Bookings/TotalBookings")]
        [InlineData("/Data/test201225")]
        public void RetiredPages_AreGone(string page)
        {
            Assert.False(FeatureAuthorizationConventions.IsMapped(page));
            var pageType = typeof(FeatureAuthorizationConventions).Assembly.GetTypes()
                .Any(t => t.Namespace != null && ("/" + t.Namespace.Replace("PlantStockManager.Pages.", "").Replace('.', '/') + "/" + t.Name.Replace("Model", ""))
                          .Equals(page, StringComparison.OrdinalIgnoreCase));
            Assert.False(pageType);
        }

        [Theory]
        [InlineData("/Production/Cutting/Index")]
        [InlineData("/Production/PotBatch/Details")]
        [InlineData("/Production/EmptyPotInventory/Issue")]
        public void PageLookup_FindsExistingPages(string page)
        {
            // control for RetiredPages_AreGone: the same lookup finds live pages
            var found = typeof(FeatureAuthorizationConventions).Assembly.GetTypes()
                .Any(t => t.Namespace != null && ("/" + t.Namespace.Replace("PlantStockManager.Pages.", "").Replace('.', '/') + "/" + t.Name.Replace("Model", ""))
                          .Equals(page, StringComparison.OrdinalIgnoreCase));
            Assert.True(found);
            Assert.True(FeatureAuthorizationConventions.IsMapped(page));
        }

        [Fact]
        public void PotBatchReady_OnlyTakesQuantityReasonAndAction_NotStock()
        {
            // READY is confirmed from the batch page; the confirmation has no
            // variety / Area / pot size / stock inputs (all from the batch).
            var bound = typeof(PlantStockManager.Pages.Production.PotBatch.DetailsModel)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<BindPropertyAttribute>() != null)
                .Select(p => p.Name).ToList();
            Assert.DoesNotContain(bound, n => n.Contains("Area") || n.Contains("Species") || n.Contains("PotSize") || n.Contains("Stock") || n.Contains("Supervisor"));
        }

        [Fact]
        public void CuttingProduction_TakesNoVarietyOrArea_FromTheForm()
        {
            // The variety and the SOURCE Area always come from the Mother Plant. The only additions
            // are the destination choice (Correction #1) and the one-time form token: the
            // destination Area is a Main Office Area picked among the DB's Main Office Areas,
            // never the source Area, and is re-validated by the repository.
            var bound = typeof(PlantStockManager.Pages.Production.Cutting.CreateModel)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<BindPropertyAttribute>() != null)
                .Select(p => p.Name).OrderBy(n => n).ToArray();
            Assert.Equal(new[] { "CuttingDate", "Destination", "MainOfficeAreaId", "MotherPlantId", "Quantity", "Remarks", "SubmissionToken", "SupervisorId" }, bound);
            Assert.DoesNotContain("AreaId", bound);
            Assert.DoesNotContain("SpeciesId", bound);
        }

        [Fact]
        public void ApprovalCancel_IsRestrictedToTheAssignedSupervisor()
        {
            // ReadyConfirmationRepository.CancelAsync takes the acting user;
            // the check itself runs under the row lock (see the dry-run tests).
            var p = typeof(PlantStockManager.Data.ReadyConfirmationRepository).GetMethod("CancelAsync")!.GetParameters();
            Assert.Contains(p, x => x.Name == "userId");
        }

        [Fact]
        public void EmptyPotIssue_IsNotAPurchasePermission()
            => Assert.DoesNotContain("Purchase.Enter", FeatureAuthorizationConventions.GetRule("/Production/EmptyPotInventory/Issue").Read);

        [Fact]
        public void PotBatchCreate_AreaAndPotSize_ComeFromTheIssuedPots()
        {
            // the employee picks the cuttings and the empty pots issued to the
            // Area; there is no free Area or pot-size input
            var bound = typeof(PlantStockManager.Pages.Production.PotBatch.CreateModel)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<BindPropertyAttribute>() != null)
                .Select(p => p.Name).ToList();
            Assert.Contains("EmptyPotInventoryId", bound);
            Assert.DoesNotContain("AreaId", bound);
            Assert.DoesNotContain("PotSize", bound);
        }

        [Fact]
        public void PottedMove_SaleAndTransfer_NeedDifferentPermissions()
        {
            Assert.Equal("Outlet.Sell", PlantStockManager.Pages.Production.PottedPlantStock.MoveModel.SellPolicy);
            Assert.DoesNotContain("Outlet.Sell", PlantStockManager.Pages.Production.PottedPlantStock.MoveModel.TransferPolicy);
        }

        [Fact]
        public void DirectCustomerSale_IsNotRestrictedToOutletAreaType()
        {
            // Gated by permission + Area access only -- READY stock held at
            // any active Area (Main Office, a production Area, or an Outlet)
            // can be sold directly, never by the Area's AreaType. The rule
            // itself lives in DispatchRules.ValidateDirectSale (see
            // DispatchRulesTests) and takes no AreaType at all.
            var moveType = typeof(PlantStockManager.Pages.Production.PottedPlantStock.MoveModel);
            Assert.DoesNotContain(moveType.GetProperties(BindingFlags.Public | BindingFlags.Instance), p => p.Name == "IsOutletStock");
            var ruleParams = typeof(DispatchRules).GetMethod("ValidateDirectSale")!.GetParameters().Select(p => p.Name).ToArray();
            Assert.DoesNotContain(ruleParams, n => n != null && n.Contains("AreaType", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void ReadyStockMove_ToMainOfficeOrOutlet_TakesOnlyDestinationAndQuantity()
        {
            // Same permission-gated, no-confirmation shape as PottedPlantStock/Move
            var bound = typeof(PlantStockManager.Pages.Production.ReadyStock.MoveModel)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<BindPropertyAttribute>() != null)
                .Select(p => p.Name).OrderBy(n => n).ToArray();
            Assert.Equal(new[] { "Destination", "DestinationAreaId", "Quantity", "Remarks" }, bound);
            Assert.Equal("InternalTransfer.Enter|MainOffice.Confirm", PlantStockManager.Pages.Production.ReadyStock.MoveModel.TransferPolicy);
        }

        [Fact]
        public void Area_HasNoPolyhouseLink_PolyhouseHasTheArea()
        {
            // one Area <-> Polyhouse relationship: Polyhouse.AreaId
            Assert.Null(typeof(PlantStockManager.Models.Area).GetProperty("PolyhouseId"));
            Assert.NotNull(typeof(PlantStockManager.Models.Polyhouse).GetProperty("AreaId"));
        }

        [Theory]
        [InlineData("/Data/BookingHistory")]
        public void OldSystemReports_AreReadOnly(string page)
        {
            // only GET handlers (and an Excel export) -- they never change data
            Assert.Null(FeatureAuthorizationConventions.GetRule(page).Write);
            var type = typeof(FeatureAuthorizationConventions).Assembly.GetTypes()
                .Single(t => t.FullName == "PlantStockManager.Pages" + page.Replace('/', '.') + "Model");
            var posts = type.GetMethods().Where(m => m.Name.StartsWith("OnPost")).Select(m => m.Name).ToList();
            Assert.All(posts, n => Assert.Contains("Export", n));
        }

        [Fact]
        public void SeedSowingMonthlyReport_IsReadOnly_WithSameCodesAsRetiredRoute()
        {
            // Phase H (2026-09-30): the "Monthly Wise Sowing" replacement --
            // same permission codes as the retired /Data/MonthWiseSowing
            // route, read-only (no OnPost handler at all: unlike the retired
            // route this one has no Excel/PDF export handler either), and
            // Area-scoped in SQL, not the old page's zero-security shape.
            var rule = FeatureAuthorizationConventions.GetRule("/Data/SeedSowingMonthlyReport");
            Assert.Equal("Sowing.View|Reports.View", rule.Read);
            Assert.Null(rule.Write);
            var type = typeof(PlantStockManager.Pages.Data.SeedSowingMonthlyReportModel);
            Assert.Empty(type.GetMethods().Where(m => m.Name.StartsWith("OnPost")));
        }

        [Fact]
        public void SeedSowingMonthlyReport_UsesGetMonthlyPivotAsync_WithParameterizedAreaScope()
        {
            // Structural guard against ever regressing to string-concatenated
            // SQL for the Area IN-list (the CuttingProductionRepository
            // pattern this reuses always parameterizes @Allowed).
            var method = typeof(PlantStockManager.Data.SeedSowingRepository).GetMethod("GetMonthlyPivotAsync");
            Assert.NotNull(method);
            var paramNames = method!.GetParameters().Select(p => p.Name).ToArray();
            Assert.Contains("allowedAreaIds", paramNames);
        }

        [Fact]
        public void SeedSowingModel_HasSurvivorshipCheckpointFields()
        {
            var props = typeof(PlantStockManager.Models.SeedSowing).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name).ToArray();
            Assert.Contains("TraysAliveQuantity", props);
            Assert.Contains("TraysAliveDate", props);
            Assert.Contains("HardeningAliveQuantity", props);
            Assert.Contains("HardeningAliveDate", props);
            Assert.Contains("Location", props);
            Assert.Contains("LocationDescription", props);
        }
    }
}
