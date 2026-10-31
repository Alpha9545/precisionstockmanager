using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // 2026-10-19 workflow corrections -- the pure rules (no database):
    //   R4  70-cavity tray option in the closed cavity list and every tray / plant calculation; 9/24/42/102/150 unchanged.
    //   R1/R3 Sowing Approvals: who SEES a pending batch (Area scope only) vs who may APPROVE it (SowingApprovalRules).
    //   R2  Mother Plant Area Ready Stock actions: Send to Outlet / Sell to customer, or why not (AreaReadyStockActionRules).
    public class WorkflowCorrections1019RulesTests
    {
        private static readonly DateTime Today = new(2026, 10, 19);

        // =========================================================================================
        // R4: 70 Cavity
        // =========================================================================================

        [Fact]
        public void CavityList_Has70_InSizeOrder_AndTheOtherFiveUnchanged()
        {
            Assert.Equal(new[] { "9 Cavity", "24 Cavity", "42 Cavity", "70 Cavity", "102 Cavity", "150 Cavity" }, DirectSowingRules.CavityTypes);
            Assert.Equal(DirectSowingRules.Cavity70, "70 Cavity");
            Assert.True(DirectSowingRules.IsValidCavityType("70 Cavity"));
            Assert.Equal(70, DirectSowingRules.CavityCount("70 Cavity"));
            foreach (var (c, n) in new[] { ("9 Cavity", 9), ("24 Cavity", 24), ("42 Cavity", 42), ("102 Cavity", 102), ("150 Cavity", 150) })
                Assert.Equal(n, DirectSowingRules.CavityCount(c));
            // still a CLOSED list: near-miss spellings are refused, never folded into 70
            foreach (var bad in new[] { "70 cavity", "70-Cavity", "70", " 70 Cavity", "71 Cavity" })
                Assert.Null(DirectSowingRules.CavityCount(bad));
            Assert.Same(DirectSowingRules.CavityTypes, TransplantRules.TargetCavityTypes);
        }

        [Theory]
        [InlineData(70, 1, 70, 0)]      // one full tray = 70 plants
        [InlineData(140, 2, 140, 0)]    // two trays = 140
        [InlineData(150, 2, 140, 10)]   // 150 -> 2 complete trays, 10 left over
        [InlineData(209, 2, 140, 69)]
        [InlineData(210, 3, 210, 0)]
        public void Sowing_70Cavity_CompleteTraysOnly(int quantity, int trays, int used, int remaining)
        {
            var r = DirectSowingRules.CalculateTrays(quantity, "70 Cavity");
            Assert.True(r.Ok, r.Error);
            Assert.Equal((trays, (decimal)used, (decimal)remaining), (r.Trays, r.SeedsUsed, r.RemainingSeeds));
        }

        [Fact]
        public void Sowing_70Cavity_LessThanOneTray_Refused_AndPlanChecksStock()
        {
            Assert.False(DirectSowingRules.CalculateTrays(69, "70 Cavity").Ok);
            var plan = DirectSowingRules.PlanSowing(150, "70 Cavity", physical: 1000, inTransit: 0);
            Assert.True(plan.Ok, plan.Error);
            Assert.Equal((2, 140m, 10m, 860m), (plan.Trays, plan.SeedsUsed, plan.RemainingSeeds, plan.AvailableAfter));   // only used seeds leave the lot
            Assert.False(DirectSowingRules.PlanSowing(150, "70 Cavity", physical: 100, inTransit: 0).Ok);              // insufficient stock
            // cutting tray sowing: same complete-tray rule (the existing cutting label)
            var cut = DirectSowingRules.PlanSowing(150, "70 Cavity", 1000, 0, DirectSowingRules.CuttingQuantityLabel, "Cutting Stock");
            Assert.Equal((2, 140m), (cut.Trays, cut.SeedsUsed));
        }

        [Fact]
        public void PhysicalTrays_70Cavity_PartlyFilledTrayStillOccupiesATray()
        {
            // existing rule for physical tray consumption = CEILING (the sub-tray remainder still occupies a tray)
            Assert.Equal(1m, DirectSowingRules.PhysicalTraysRequired(70, "70 Cavity"));
            Assert.Equal(2m, DirectSowingRules.PhysicalTraysRequired(140, "70 Cavity"));
            Assert.Equal(3m, DirectSowingRules.PhysicalTraysRequired(150, "70 Cavity"));
            // and the existing cavities keep theirs
            Assert.Equal(40m, DirectSowingRules.PhysicalTraysRequired(950, "24 Cavity"));
            Assert.Equal(1m, DirectSowingRules.PhysicalTraysRequired(150, "150 Cavity"));
        }

        [Fact]
        public void Approval_70Cavity_SeedlingsAreTraysTimes70()
        {
            var r = DirectSowingRules.ComputeTrayApproval(140, 2, "70 Cavity", 0, 0, 2, null);
            Assert.True(r.Ok, r.Error);
            Assert.Equal(140m, r.ActualSeedlings);
            var partial = DirectSowingRules.ComputeTrayApproval(140, 2, "70 Cavity", 0, 0, 1, "Disease");
            Assert.Equal((70m, 70m), (partial.ActualSeedlings, partial.Wastage));
        }

        [Fact]
        public void MotherPlantTransplant_150Seedlings_Into70_TwoFullTrays_TenWaste_ReasonRequired()
        {
            var noReason = TransplantRules.PlanProcessing(150, 150, "70 Cavity", false, null);
            Assert.False(noReason.Ok);
            Assert.Contains("10 seedlings do not fill a complete tray", noReason.Error);
            var r = TransplantRules.PlanProcessing(150, 150, "70 Cavity", false, "Remainder");
            Assert.True(r.Ok, r.Error);
            Assert.Equal((2, 140m, 10m, 0m, 70), (r.Plan!.Trays, r.Plan.Transplanted, r.Plan.Waste, r.Plan.LeftInBatch, r.Plan.Cavity));
            // processing only part: the unprocessed seedlings stay in the batch (not waste)
            var part = TransplantRules.PlanProcessing(150, 140, "70 Cavity", false, null);
            Assert.Equal((2, 140m, 0m, 10m), (part.Plan!.Trays, part.Plan.Transplanted, part.Plan.Waste, part.Plan.LeftInBatch));
            Assert.False(TransplantRules.PlanProcessing(60, 60, "70 Cavity", false, "x").Ok);   // less than one tray -> no tray batch
            // exact plant-count transplant variant: CEILING output trays (unchanged rule)
            var exact = TransplantRules.PlanTransplant(150, 150, 0, 150, "70 Cavity");
            Assert.Equal((3, 2, 10m), (exact.Plan!.OutputTrays, exact.Plan.FullTrays, exact.Plan.PlantsInLastTray));
        }

        [Fact]
        public void BedTransplant_Into70_WholeTraysOnly()
        {
            var plan = BedSowingRules.PlanTransplant(150, 140, "70 Cavity", null, Today, Today.AddDays(-20), Today);
            Assert.True(plan.Ok, plan.Error);
            Assert.Equal((2, 140m), (plan.Plan!.Trays, plan.Plan.Transplanted));
            Assert.False(BedSowingRules.PlanTransplant(150, 150, "70 Cavity", null, Today, Today.AddDays(-20), Today).Ok);   // 150 is not whole 70-trays
            Assert.False(BedSowingRules.PlanTransplant(150, 140, "70 Cavity", 3, Today, Today.AddDays(-20), Today).Ok);      // posted trays disagree
        }

        [Theory]
        [InlineData("9 Cavity", 100, 11, 99, 1)]
        [InlineData("24 Cavity", 1000, 41, 984, 16)]
        [InlineData("42 Cavity", 1000, 23, 966, 34)]
        [InlineData("102 Cavity", 1000, 9, 918, 82)]
        [InlineData("150 Cavity", 1000, 6, 900, 100)]
        public void ExistingCavities_CalculationsUnchanged(string cavity, int q, int trays, int used, int remaining)
        {
            var r = DirectSowingRules.CalculateTrays(q, cavity);
            Assert.Equal((true, trays, (decimal)used, (decimal)remaining), (r.Ok, r.Trays, r.SeedsUsed, r.RemainingSeeds));
        }

        // =========================================================================================
        // R1 / R3: Sowing Approvals -- visible vs approvable
        // =========================================================================================

        private const int Maya = 9, OtherSupervisor = 13, AreaSup1 = 113, AreaSup2 = 115, Admin = 21, Recorder = 21;
        private static readonly DirectSowingRules.BedApprover BedRule = new(true, Maya, "Maya Shinde");
        private static readonly SowingApprovalRules.Settings Settings = new(BedRule, BedAnyDate: true, TransplantAreaScope: true);

        private static SeedSowing Transplant(DateTime? ready = null) => new()
        {
            Id = 1, SourceType = SeedSowing.SourceTransplant, AreaId = 1, AreaName = "Shree Swami Samarth Agro", CreatedById = Recorder, CreatedBy = "rec",
            ExpectedReadyDate = ready ?? Today.AddDays(-1), CavityType = "70 Cavity", NumberOfTrays = 2, QuantitySown = 140
        };

        private static SeedSowing Bed(int createdBy = Maya) => new()
        {
            Id = 2, SourceType = SeedSowing.SourceBedSowing, AreaId = 2, AreaName = "Main-Office-Areas", CreatedById = createdBy, CreatedBy = "x",
            ExpectedReadyDate = Today.AddDays(10), CavityType = "24 Cavity", NumberOfTrays = 10, QuantitySown = 240
        };

        private static SeedSowing Seed(int? supervisor) => new()
        {
            Id = 3, SourceType = SeedSowing.SourceSeed, AreaId = 2, AreaName = "Main-Office-Areas", CreatedById = Recorder, CreatedBy = "rec",
            SupervisorId = supervisor, SupervisorName = supervisor.HasValue ? "Somnath" : null, ExpectedReadyDate = Today.AddDays(5),
            CavityType = "42 Cavity", NumberOfTrays = 10, QuantitySown = 420
        };

        [Fact]
        public void Categories_AreSeparate()
        {
            Assert.Equal(SowingApprovalRules.CategoryMotherPlantTransplant, SowingApprovalRules.CategoryOf(SeedSowing.SourceTransplant));
            Assert.Equal(SowingApprovalRules.CategoryBedSowing, SowingApprovalRules.CategoryOf(SeedSowing.SourceBedSowing));
            Assert.Equal(SowingApprovalRules.CategorySowing, SowingApprovalRules.CategoryOf(SeedSowing.SourceSeed));
            Assert.Equal(SowingApprovalRules.CategorySowing, SowingApprovalRules.CategoryOf(SeedSowing.SourceCutting));
            Assert.Equal(SowingApprovalRules.CategorySowing, SowingApprovalRules.CategoryOf(null));
        }

        [Fact]
        public void Visibility_IsAreaScopeOnly_MotherPlantBatchesUseTheStrictAreaRule()
        {
            Func<int, bool> seedlingAll = _ => true, strictOnlyArea1 = a => a == 1, none = _ => false;
            Assert.True(SowingApprovalRules.IsVisible(SowingApprovalRules.CategoryMotherPlantTransplant, 1, none, strictOnlyArea1));
            Assert.False(SowingApprovalRules.IsVisible(SowingApprovalRules.CategoryMotherPlantTransplant, 127, seedlingAll, strictOnlyArea1));   // the seedling switch never widens it
            Assert.True(SowingApprovalRules.IsVisible(SowingApprovalRules.CategorySowing, 2, seedlingAll, none));
            Assert.False(SowingApprovalRules.IsVisible(SowingApprovalRules.CategoryBedSowing, 2, none, seedlingAll));
        }

        [Fact]
        public void TwoAreaSupervisors_BothMayApprove_TheSameTransplantBatch()
        {
            var s = Transplant();
            Assert.True(SowingApprovalRules.Evaluate(s, AreaSup1, "a", true, isAreaApprover: true, Settings, Today).CanApprove);
            Assert.True(SowingApprovalRules.Evaluate(s, AreaSup2, "b", true, isAreaApprover: true, Settings, Today).CanApprove);
        }

        [Fact]
        public void SeeingIsNotApproving_NoPermission_OrNotThisAreasSupervisor()
        {
            var s = Transplant();
            var viewOnly = SowingApprovalRules.Evaluate(s, 3, "booking", hasConfirmPermission: false, isAreaApprover: false, Settings, Today);
            Assert.Equal((false, SowingApprovalRules.NoApprovePermissionMessage), (viewOnly.CanApprove, viewOnly.Reason));
            var otherArea = SowingApprovalRules.Evaluate(s, 110, "other", true, isAreaApprover: false, Settings, Today);
            Assert.False(otherArea.CanApprove);
            Assert.Equal(string.Format(SowingApprovalRules.NotAreaSupervisorMessageFormat, s.AreaName), otherArea.Reason);
            Assert.Equal("Area Supervisor of Shree Swami Samarth Agro", otherArea.Approver);
            // without the TransplantAreaScope migration any approver may (the earlier rule, unchanged)
            Assert.True(SowingApprovalRules.Evaluate(s, 110, "other", true, false, Settings with { TransplantAreaScope = false }, Today).CanApprove);
        }

        [Fact]
        public void TransplantBatch_BeforeItsReadyDate_WaitsForTheDate()
        {
            var e = SowingApprovalRules.Evaluate(Transplant(Today.AddDays(3)), AreaSup1, "a", true, true, Settings, Today);
            Assert.False(e.CanApprove);
            Assert.True(e.WaitingForReadyDate);
            Assert.Equal(Today.AddDays(3), e.ApproveFrom);
            Assert.True(SowingApprovalRules.Evaluate(Transplant(Today), AreaSup1, "a", true, true, Settings, Today).CanApprove);   // on the date
        }

        [Fact]
        public void BedSowingBatch_OnlyMaya_AnyDate_EvenOwnTransplant_AdministratorsToo()
        {
            var bed = Bed(createdBy: Maya);
            var maya = SowingApprovalRules.Evaluate(bed, Maya, "maya", true, false, Settings, Today);
            Assert.True(maya.CanApprove, maya.Reason);                                                           // before Ready Date, own transplant
            foreach (var other in new[] { OtherSupervisor, Admin, AreaSup1 })
            {
                var e = SowingApprovalRules.Evaluate(bed, other, "x", true, true, Settings, Today);
                Assert.False(e.CanApprove);
                Assert.Equal(DirectSowingRules.NotBedApproverMessage("Maya Shinde"), e.Reason);
            }
            Assert.Equal("Maya Shinde (Bed Sowing approver)", SowingApprovalRules.ApproverLabel(bed, Settings));
            // no approver designated: nobody
            var none = Settings with { BedApprover = new DirectSowingRules.BedApprover(true, null, null) };
            Assert.Equal(DirectSowingRules.NoBedApproverMessage, SowingApprovalRules.Evaluate(bed, Maya, "m", true, false, none, Today).Reason);
            // before the BedAnyDate migration: from its Ready Date only
            Assert.True(SowingApprovalRules.Evaluate(bed, Maya, "m", true, false, Settings with { BedAnyDate = false }, Today).WaitingForReadyDate);
        }

        [Fact]
        public void SeedSowing_OnlyItsAssignedSupervisor_NeverTheRecorder()
        {
            Assert.True(SowingApprovalRules.Evaluate(Seed(OtherSupervisor), OtherSupervisor, "s", true, false, Settings, Today).CanApprove);
            Assert.Equal(DirectSowingRules.NotAssignedMessage, SowingApprovalRules.Evaluate(Seed(OtherSupervisor), Maya, "m", true, false, Settings, Today).Reason);
            Assert.Equal(DirectSowingRules.OwnSowingMessage, SowingApprovalRules.Evaluate(Seed(Recorder), Recorder, "rec", true, false, Settings, Today).Reason);
            Assert.Equal(DirectSowingRules.NoSupervisorMessage, SowingApprovalRules.Evaluate(Seed(null), Maya, "m", true, false, Settings, Today).Reason);
            Assert.Equal("Somnath (assigned supervisor)", SowingApprovalRules.ApproverLabel(Seed(OtherSupervisor), Settings));
            Assert.False(SowingApprovalRules.Evaluate(Seed(OtherSupervisor), null, null, true, false, Settings, Today).CanApprove);   // unidentified user
        }

        // =========================================================================================
        // R2: Ready Stock actions
        // =========================================================================================

        private static ReadyStock Batch(decimal qty = 240, decimal reserved = 0, decimal dispatched = 0, decimal inTransit = 0,
            string origin = ReadyStock.OriginSowing, string cavity = "24 Cavity") => new()
        {
            Id = 7, AreaId = 127, AreaType = "MotherPlant", Quantity = qty, ReservedQuantity = reserved, DispatchedQuantity = dispatched,
            InTransitQuantity = inTransit, OriginType = origin, CavityType = cavity
        };

        [Fact]
        public void SendToOutlet_EligibleWholeUntouchedSowingBatch()
            => Assert.True(AreaReadyStockActionRules.SendToOutlet(Batch(), canTransfer: true, forPotProduction: false).Available);

        [Theory]
        [InlineData(false, false, ReadyStock.OriginSowing, 0, 0, 0, "not part of your role")]
        [InlineData(true, true, ReadyStock.OriginSowing, 0, 0, 0, "Pot Production")]
        [InlineData(true, false, ReadyStock.OriginTransfer, 0, 0, 0, "received or transplanted batch stays")]
        [InlineData(true, false, ReadyStock.OriginTransplant, 0, 0, 0, "received or transplanted batch stays")]
        [InlineData(true, false, ReadyStock.OriginSowing, 0, 0, 24, "on its way")]
        [InlineData(true, false, ReadyStock.OriginSowing, 24, 0, 0, "only as a whole")]
        [InlineData(true, false, ReadyStock.OriginSowing, 0, 72, 0, "only as a whole")]
        public void SendToOutlet_Ineligible_SaysWhy(bool canTransfer, bool pots, string origin, int reserved, int dispatched, int inTransit, string why)
        {
            var a = AreaReadyStockActionRules.SendToOutlet(Batch(reserved: reserved, dispatched: dispatched, inTransit: inTransit, origin: origin), canTransfer, pots);
            Assert.False(a.Available);
            Assert.Contains(why, a.Reason);
        }

        [Fact]
        public void SellToCustomer_CompleteTraysOnly_AndWhyNot()
        {
            Assert.True(AreaReadyStockActionRules.SellToCustomer(Batch(), true, false, true).Available);
            Assert.True(AreaReadyStockActionRules.SellToCustomer(Batch(dispatched: 216), true, false, true).Available);         // exactly one tray left
            Assert.True(AreaReadyStockActionRules.SellToCustomer(Batch(origin: ReadyStock.OriginTransfer), true, false, true).Available);   // received batches can be sold
            Assert.Contains("one complete 24 Cavity tray", AreaReadyStockActionRules.SellToCustomer(Batch(dispatched: 217), true, false, true).Reason);
            Assert.Contains("not part of your role", AreaReadyStockActionRules.SellToCustomer(Batch(), false, false, true).Reason);
            Assert.Contains("Pot Production", AreaReadyStockActionRules.SellToCustomer(Batch(), true, true, true).Reason);
            Assert.Contains("2026-10-18_SeedlingDirectSaleSubmissionToken", AreaReadyStockActionRules.SellToCustomer(Batch(), true, false, false).Reason);
            Assert.Equal(10, AreaReadyStockActionRules.AvailableCompleteTrays(Batch()));
            Assert.Equal(2, AreaReadyStockActionRules.AvailableCompleteTrays(Batch(qty: 150, cavity: "70 Cavity")));               // 150 plants at 70 -> 2 trays
            // 2026-10-31: a received batch under one tray has no complete tray; a SOWING batch under one tray can only be a
            // partial-tray cutting sowing (TR_ReadyStock_WholeTrays), which is one tray of its whole quantity (PartialTrayRules).
            Assert.Equal(0, AreaReadyStockActionRules.AvailableCompleteTrays(Batch(qty: 69, cavity: "70 Cavity", origin: ReadyStock.OriginTransfer)));
            Assert.Equal(1, AreaReadyStockActionRules.AvailableCompleteTrays(Batch(qty: 69, cavity: "70 Cavity")));
        }
    }
}
