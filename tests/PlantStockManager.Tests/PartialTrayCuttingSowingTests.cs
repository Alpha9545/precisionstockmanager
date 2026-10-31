using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // 2026-10-31: a cutting tray sowing under one complete tray (e.g. 62 cuttings at 150 Cavity) is ONE partial tray --
    // a yellow warning, never an error; 62 recorded as sown, 1 physical tray, approved as 1 tray of its own 62 cuttings,
    // and its Ready Stock batch moves only as a whole. Pure rules (no database); PartialTrayCuttingSowingE2ETests drives
    // the real repositories on a scratch copy.
    public class PartialTrayCuttingSowingTests
    {
        private const string Cutting = DirectSowingRules.CuttingQuantityLabel;
        private const string C150 = "150 Cavity";

        // ---- sowing --------------------------------------------------------

        [Fact]
        public void Cuttings62_At150_IsOnePartialTray_62Sown_NothingWasted_OnePhysicalTray()
        {
            var r = DirectSowingRules.PlanSowing(62, C150, physical: 500, inTransit: 0, Cutting, "Cutting Stock", allowPartialTray: true);
            Assert.True(r.Ok, r.Error);
            Assert.Equal(1, r.Trays);
            Assert.Equal(62m, r.SeedsUsed);              // never rounded up to 150
            Assert.Equal(0m, r.RemainingSeeds);          // so no automatic sub-tray wastage
            Assert.Equal(438m, r.AvailableAfter);        // only the 62 leave the pool
            Assert.Equal(1m, DirectSowingRules.PhysicalTraysRequired(62, C150));   // the existing CEILING rule: one tray deducted
            Assert.True(PartialTrayRules.IsUnderOneTray(62, C150));
        }

        [Fact]
        public void Warning_IsTheRequestedText()
            => Assert.Equal(
                "Warning: Cutting Quantity (62 seedlings) is less than one complete 150-cavity tray. One tray will be deducted, "
                + "but only 62 seedlings will be recorded as sown. You can still approve this sowing.",
                PartialTrayRules.SowingWarning(62, C150));

        [Fact]
        public void WithoutTheMigration_TheEarlierRefusalIsUnchanged()
        {
            var r = DirectSowingRules.PlanSowing(62, C150, 500, 0, Cutting, "Cutting Stock");   // allowPartialTray defaults to false
            Assert.False(r.Ok);
            Assert.Equal("Cutting Quantity (62) is less than one complete 150-cavity tray.", r.Error);
        }

        [Fact]
        public void SeedSowing_NeverUsesThePartialTrayRule()
        {
            // the seed paths never pass allowPartialTray: the default is the earlier refusal
            Assert.False(DirectSowingRules.PlanSowing(62, C150, 500, 0).Ok);
            Assert.False(DirectSowingRules.CalculateTrays(62, C150).Ok);
            Assert.False(PartialTrayRules.IsPartialTraySowing(SeedSowing.SourceSeed, 62, C150));
            Assert.True(PartialTrayRules.IsPartialTraySowing(SeedSowing.SourceCutting, 62, C150));
        }

        [Theory]
        [InlineData(150, 1, 150, 1)]
        [InlineData(300, 2, 300, 2)]
        [InlineData(212, 1, 150, 2)]     // one tray or more: unchanged (FLOOR sown, remainder wasted, CEILING physical trays)
        public void FullTrayQuantities_AreUnchanged_WithOrWithoutTheOption(decimal qty, int trays, decimal sown, decimal physicalTrays)
        {
            foreach (var allow in new[] { false, true })
            {
                var r = DirectSowingRules.PlanSowing(qty, C150, 1000, 0, Cutting, "Cutting Stock", allow);
                Assert.True(r.Ok, r.Error);
                Assert.Equal(trays, r.Trays);
                Assert.Equal(sown, r.SeedsUsed);
                Assert.Equal(qty - sown, r.RemainingSeeds);
            }
            Assert.Equal(physicalTrays, DirectSowingRules.PhysicalTraysRequired(qty, C150));
            Assert.False(PartialTrayRules.IsUnderOneTray(qty, C150));    // no warning
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public void ZeroOrNegative_IsStillRefused(decimal qty)
        {
            var r = DirectSowingRules.PlanSowing(qty, C150, 500, 0, Cutting, "Cutting Stock", allowPartialTray: true);
            Assert.False(r.Ok);
            Assert.Equal("Cutting Quantity must be greater than zero.", r.Error);
            Assert.False(PartialTrayRules.IsUnderOneTray(qty, C150));
        }

        [Fact]
        public void FractionalQuantity_IsStillRefused()
        {
            Assert.False(DirectSowingRules.PlanSowing(62.5m, C150, 500, 0, Cutting, "Cutting Stock", allowPartialTray: true).Ok);
            Assert.False(DirectSowingRules.CalculateTrays(62.5m, C150, Cutting, allowPartialTray: true).Ok);
        }

        [Fact]
        public void InsufficientCuttingStock_IsStillRefused()
        {
            var r = DirectSowingRules.PlanSowing(62, C150, physical: 70, inTransit: 20, Cutting, "Cutting Stock", allowPartialTray: true);
            Assert.False(r.Ok);
            Assert.Equal("Insufficient Cutting Stock (available 50, requested 62).", r.Error);
        }

        [Fact]
        public void InvalidCavity_IsStillRefused()
            => Assert.False(DirectSowingRules.PlanSowing(62, "10 Cavity", 500, 0, Cutting, "Cutting Stock", allowPartialTray: true).Ok);

        // ---- the form ------------------------------------------------------

        private static string RepoFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.csproj")))
                dir = dir.Parent;
            return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
        }

        [Fact]
        public void CuttingSowingView_ShowsTheWarningInYellow_SeparateFromTheRedError_AndNeverDisablesConfirm()
        {
            var view = RepoFile("Pages", "Production", "SeedSowing", "CreateFromCutting.cshtml");
            Assert.Contains("class=\"alert alert-warning", view);
            Assert.Contains("id=\"trayWarning\"", view);
            Assert.Contains("&#9888;", view);                                             // the warning icon used elsewhere (Overgrowth)
            Assert.Contains("if (!msg && r.warning)", view);                              // shown only when there is no real error
            Assert.Contains("<div class=\"text-danger small d-none\" id=\"trayError\">", view);   // genuine errors stay red
            // the Confirm button is disabled only while the form is being submitted -- never by the tray preview
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(view, @"confirmSowingButton'\)\.prop\('disabled', true\)"));
            var model = RepoFile("Pages", "Production", "SeedSowing", "CreateFromCutting.cshtml.cs");
            Assert.Contains("CuttingPartialTrayAvailableAsync()", model);                 // the POST re-checks with the same rule as the save
            var repo = RepoFile("Data", "SeedSowingRepository.cs");
            Assert.Contains("allowPartialTray: await WorkflowCorrectionsSchema.CuttingPartialTrayAsync(conn, tx)", repo);   // under the stock lock
        }

        // ---- approval ------------------------------------------------------

        [Fact]
        public void Approval_OneTray_Is62Seedlings_NoWastage_NoExtraCuttings()
        {
            var r = DirectSowingRules.ComputeTrayApproval(62, 1, C150, 0, 0, actualTrays: 1, null, SeedSowing.SourceCutting);
            Assert.True(r.Ok, r.Error);
            Assert.Equal(1, r.ActualTrays);
            Assert.Equal(62m, r.ActualSeedlings);        // never 1 x 150
            Assert.Equal(0m, r.Wastage);
            Assert.Equal(0m, DirectSowingRules.ExtraCuttingsNeeded(SeedSowing.SourceCutting, 1, C150, 62, partialTraySowing: true));
        }

        [Theory]
        [InlineData(2)]
        [InlineData(0)]
        [InlineData(1.5)]
        public void Approval_AnythingButOneTray_IsRefused(decimal trays)
        {
            var r = DirectSowingRules.ComputeTrayApproval(62, 1, C150, 0, 0, trays, "Other", SeedSowing.SourceCutting);
            Assert.False(r.Ok);
            Assert.Equal(PartialTrayRules.ApprovalTrayMessage(62, C150), r.Error);
        }

        [Fact]
        public void Approval_OfWholeTraySowings_IsUnchanged()
        {
            var cutting = DirectSowingRules.ComputeTrayApproval(300, 2, C150, 0, 0, 2, null, SeedSowing.SourceCutting);
            Assert.True(cutting.Ok, cutting.Error);
            Assert.Equal(300m, cutting.ActualSeedlings);
            // more trays than sown still takes the extra cuttings (existing overage rule)
            Assert.Equal(150m, DirectSowingRules.ExtraCuttingsNeeded(SeedSowing.SourceCutting, 3, C150, 300));
            var seed = DirectSowingRules.ComputeTrayApproval(150, 1, C150, 0, 0, 1, null, SeedSowing.SourceSeed);
            Assert.Equal(150m, seed.ActualSeedlings);
            // without a source type (the earlier callers) nothing changes either
            Assert.Equal(150m, DirectSowingRules.ComputeTrayApproval(150, 1, C150, 0, 0, 1, null).ActualSeedlings);
        }

        // ---- the Ready Stock batch moves only as a whole ------------------

        private static ReadyStock Batch(decimal quantity, decimal reserved = 0, string origin = ReadyStock.OriginSowing)
            => new() { Quantity = quantity, ReservedQuantity = reserved, CavityType = C150, OriginType = origin };

        [Fact]
        public void PartialBatch_OneTrayIsItsWholeQuantity()
        {
            var b = Batch(62);
            Assert.True(b.IsPartialTrayBatch);
            Assert.Equal(62, b.TraySize);
            Assert.Equal(1, AreaReadyStockActionRules.AvailableCompleteTrays(b));
            Assert.True(AreaReadyStockActionRules.SellToCustomer(b, true, false, true).Available);
            // whole-tray and received batches: unchanged
            Assert.Equal(150, Batch(300).TraySize);
            Assert.False(Batch(300).IsPartialTrayBatch);
            Assert.False(Batch(62, origin: ReadyStock.OriginTransfer).IsPartialTrayBatch);
            Assert.Equal(150, Batch(62, origin: ReadyStock.OriginTransfer).TraySize);
        }

        [Fact]
        public void PartialBatch_PartlyReserved_CannotBeSoldByTrays()
        {
            var b = Batch(62, reserved: 10);
            Assert.Equal(0, AreaReadyStockActionRules.AvailableCompleteTrays(b));
            var a = AreaReadyStockActionRules.SellToCustomer(b, true, false, true);
            Assert.False(a.Available);
            Assert.Equal(PartialTrayRules.WholeBatchMessage(62, C150), a.Reason);
        }

        private static MotherPlantTransferRules.SourceBatch Source(decimal available, decimal quantity)
            => new(ReadyStock.OriginSowing, DirectSowingRules.MainOfficeAreaType, true, 2, C150, available, quantity);

        private static readonly MotherPlantTransferRules.Destination Dest = new(1, MotherPlantTransferRules.MotherPlantAreaType, true, null, null);

        [Fact]
        public void MotherPlantTransfer_PartialBatch_OnlyAsAWhole()
        {
            var today = new DateTime(2026, 10, 31);
            var whole = MotherPlantTransferRules.ValidateCreate(Source(62, 62), Dest, 62, today, today, today.AddDays(-30), true, MotherPlantTransferRules.PurposePotProduction);
            Assert.True(whole.Ok, whole.Error);
            Assert.Equal(1m, whole.Trays);
            var part = MotherPlantTransferRules.ValidateCreate(Source(62, 62), Dest, 31, today, today, today.AddDays(-30), true, MotherPlantTransferRules.PurposePotProduction);
            Assert.False(part.Ok);
            Assert.Equal(PartialTrayRules.WholeBatchMessage(62, C150), part.Error);
            // whole-tray batches: the earlier rule
            var normal = MotherPlantTransferRules.ValidateCreate(Source(300, 300), Dest, 62, today, today, today.AddDays(-30), true, MotherPlantTransferRules.PurposePotProduction);
            Assert.Equal("Main Area stock moves in whole trays: the quantity must be a multiple of 150 (150 Cavity).", normal.Error);
        }

        [Fact]
        public void Transplant_PartialBatch_OnlyAsAWhole()
        {
            var ok = TransplantRules.PlanProcessing(62, 62, "24 Cavity", false, "Poor growth", sourceWholeTrayCavity: 62, sourcePartialTray: true);
            Assert.True(ok.Ok, ok.Error);
            Assert.Equal(2, ok.Plan!.Trays);              // 48 transplanted into two 24-cavity trays, 14 waste
            Assert.Equal(14m, ok.Plan.Waste);
            var part = TransplantRules.PlanProcessing(62, 31, "24 Cavity", false, "Poor growth", 62, true);
            Assert.False(part.Ok);
            Assert.Contains("one partial tray of 62 seedlings", part.Error);
        }
    }
}
