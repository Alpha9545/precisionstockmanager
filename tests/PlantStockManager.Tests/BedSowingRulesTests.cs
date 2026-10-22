using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // 2026-10-12 Bed Sowing: the pure rules (Services/BedSowingRules.cs), the page rules and the
    // sowing-activity source shared by the reports. No database.
    public class BedSowingRulesTests
    {
        private static readonly DateTime Today = new(2026, 10, 12);
        private static readonly DateTime BedDate = new(2026, 10, 1);

        [Theory]
        [InlineData(1000, 0, 0, BedSowing.StatusPending)]
        [InlineData(1000, 240, 0, BedSowing.StatusPartial)]
        [InlineData(1000, 0, 10, BedSowing.StatusPartial)]
        [InlineData(1000, 960, 40, BedSowing.StatusCompleted)]
        [InlineData(1000, 0, 1000, BedSowing.StatusCompleted)]
        public void StatusFollowsTheTotals(decimal qty, decimal transplanted, decimal lost, string expected)
            => Assert.Equal(expected, BedSowingRules.StatusFor(qty, transplanted, lost));

        [Fact]
        public void ConservationOriginalEqualsRemainingPlusTransplantedPlusLost()
        {
            Assert.True(BedSowingRules.IsConserved(1000, 480, 20, 500));
            Assert.False(BedSowingRules.IsConserved(1000, 480, 20, 501));
            Assert.False(BedSowingRules.IsConserved(1000, 900, 200, -100));   // never negative
            Assert.Equal(500, BedSowingRules.Remaining(1000, 480, 20));
        }

        [Fact]
        public void ReadyDateIsBedSowingDatePlusReadyDays_NothingSubstitutedWhenMissing()
        {
            Assert.Equal(new DateTime(2026, 10, 31), BedSowingRules.ReadyDate(BedDate, 30));
            Assert.Null(BedSowingRules.ReadyDate(BedDate, null));
            Assert.Null(BedSowingRules.ReadyDate(BedDate, 0));
            Assert.Null(BedSowingRules.ReadyDate(BedDate, -5));
        }

        [Fact]
        public void BusinessDate_NoFuture_NotBeforeTheBed()
        {
            Assert.False(BedSowingRules.ValidateBusinessDate(Today.AddDays(1), Today, "Sowing Date").Ok);
            Assert.False(BedSowingRules.ValidateBusinessDate(default, Today, "Sowing Date").Ok);
            Assert.True(BedSowingRules.ValidateBusinessDate(Today, Today, "Sowing Date").Ok);
            var (ok, error) = BedSowingRules.ValidateBusinessDate(BedDate.AddDays(-1), Today, "Transplant Date", BedDate, "bed sowing date");
            Assert.False(ok);
            Assert.Contains("before the bed sowing date", error);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-3)]
        [InlineData(10.5)]
        public void BedQuantity_PositiveWholeNumberOnly(decimal qty) => Assert.False(BedSowingRules.ValidateBedQuantity(qty).Ok);

        [Fact]
        public void Transplant_CompleteTraysOnly_WithinRemaining()
        {
            var (ok, trays, error) = BedSowingRules.ValidateTransplant(500, 240, "24 Cavity", null, Today, BedDate, Today);
            Assert.True(ok, error);
            Assert.Equal(10, trays);

            (ok, _, error) = BedSowingRules.ValidateTransplant(500, 250, "24 Cavity", null, Today, BedDate, Today);
            Assert.False(ok);
            Assert.Contains("multiple of 24", error);
            Assert.Contains("240 (10 trays)", error);

            (ok, _, error) = BedSowingRules.ValidateTransplant(200, 240, "24 Cavity", null, Today, BedDate, Today);
            Assert.False(ok);
            Assert.Contains("Only 200 seedlings remain", error);

            Assert.False(BedSowingRules.ValidateTransplant(500, 240, "24 Cavity", 9, Today, BedDate, Today).Ok);    // posted trays disagree
            Assert.True(BedSowingRules.ValidateTransplant(500, 240, "24 Cavity", 10, Today, BedDate, Today).Ok);
            Assert.False(BedSowingRules.ValidateTransplant(500, 240, "25 Cavity", null, Today, BedDate, Today).Ok);  // not a listed cavity
            Assert.False(BedSowingRules.ValidateTransplant(500, 240, "24 Cavity", null, Today.AddDays(1), BedDate, Today).Ok);
            Assert.False(BedSowingRules.ValidateTransplant(500, 240, "24 Cavity", null, BedDate.AddDays(-1), BedDate, Today).Ok);
            Assert.False(BedSowingRules.ValidateTransplant(500, 0, "24 Cavity", null, Today, BedDate, Today).Ok);
        }

        [Fact]
        public void Loss_NeedsReason_WithinRemaining()
        {
            Assert.False(BedSowingRules.ValidateLoss(100, 10, " ", Today, BedDate, Today).Ok);
            Assert.False(BedSowingRules.ValidateLoss(100, 101, "damping off", Today, BedDate, Today).Ok);
            Assert.False(BedSowingRules.ValidateLoss(100, 1.5m, "damping off", Today, BedDate, Today).Ok);
            Assert.True(BedSowingRules.ValidateLoss(100, 100, "damping off", Today, BedDate, Today).Ok);
        }

        [Fact]
        public void TransplantReversal_OnlyWhileTheTrayBatchHasNoApproval()
        {
            Assert.True(BedSowingRules.CanReverseTransplant("Sown", 0, 0).Ok);
            Assert.False(BedSowingRules.CanReverseTransplant("Sown", 24, 0).Ok);
            Assert.False(BedSowingRules.CanReverseTransplant("Sown", 0, 24).Ok);
            Assert.False(BedSowingRules.CanReverseTransplant("Completed", 240, 0).Ok);
            Assert.False(BedSowingRules.CanReverseTransplant("Cancelled", 0, 0).Ok);
        }

        [Fact]
        public void Approval_OfABedTrayBatch_NeverAboveWhatWasTransplanted_OtherSourcesUnchanged()
        {
            Assert.True(BedSowingRules.ValidateApprovalWithinTransplanted(SeedSowing.SourceBedSowing, 240, 240, "24 Cavity").Ok);
            Assert.True(BedSowingRules.ValidateApprovalWithinTransplanted(SeedSowing.SourceBedSowing, 120, 240, "24 Cavity").Ok);
            Assert.False(BedSowingRules.ValidateApprovalWithinTransplanted(SeedSowing.SourceBedSowing, 264, 240, "24 Cavity").Ok);
            Assert.True(BedSowingRules.ValidateApprovalWithinTransplanted(SeedSowing.SourceSeed, 264, 240, "24 Cavity").Ok);
            Assert.False(BedSowingRules.ValidateApprovalWithinTransplanted(SeedSowing.SourceTransplant, 264, 240, "24 Cavity").Ok);
            Assert.True(BedSowingRules.ValidateApprovalWithinTransplanted(SeedSowing.SourceCutting, 264, 240, "24 Cavity").Ok);
        }

        [Fact]
        public void Approval_OfABedTrayBatch_OnlyFromItsReadyDate_OtherSourcesUnchanged()
        {
            var ready = new DateTime(2026, 10, 20);
            Assert.False(BedSowingRules.ValidateApprovalReadyDate(SeedSowing.SourceBedSowing, ready, ready.AddDays(-1)).Ok);
            Assert.True(BedSowingRules.ValidateApprovalReadyDate(SeedSowing.SourceBedSowing, ready, ready).Ok);
            Assert.True(BedSowingRules.ValidateApprovalReadyDate(SeedSowing.SourceBedSowing, ready, ready.AddDays(3)).Ok);
            Assert.False(BedSowingRules.ValidateApprovalReadyDate(SeedSowing.SourceBedSowing, null, ready).Ok);
            Assert.True(BedSowingRules.ValidateApprovalReadyDate(SeedSowing.SourceSeed, ready, ready.AddDays(-10)).Ok);
            Assert.True(BedSowingRules.ValidateApprovalReadyDate(SeedSowing.SourceCutting, null, ready).Ok);
            Assert.Contains(ready.ToString("dd/MM/yyyy"), BedSowingRules.ValidateApprovalReadyDate(SeedSowing.SourceBedSowing, ready, ready.AddDays(-1)).Error);
        }

        // 2026-10-13: a tray batch transplanted from received Ready Stock is gated exactly like a bed tray
        // batch (transplant date + Ready Days); Direct Seed Sowing and Cutting Sowing stay ungated.
        [Fact]
        public void Approval_OfATransplantedReadyStockTrayBatch_OnlyFromItsReadyDate()
        {
            var ready = new DateTime(2026, 11, 2);
            var before = BedSowingRules.ValidateApprovalReadyDate(SeedSowing.SourceTransplant, ready, ready.AddDays(-1));
            Assert.False(before.Ok);
            Assert.Equal(string.Format(BedSowingRules.TransplantNotReadyYetMessageFormat, ready), before.Error);
            Assert.Contains(ready.ToString("dd/MM/yyyy"), before.Error);
            Assert.True(BedSowingRules.ValidateApprovalReadyDate(SeedSowing.SourceTransplant, ready, ready).Ok);
            Assert.True(BedSowingRules.ValidateApprovalReadyDate(SeedSowing.SourceTransplant, ready, ready.AddDays(5)).Ok);
            Assert.False(BedSowingRules.ValidateApprovalReadyDate(SeedSowing.SourceTransplant, null, ready).Ok);
            Assert.True(BedSowingRules.ValidateApprovalReadyDate(SeedSowing.SourceSeed, ready, ready.AddDays(-30)).Ok);
            Assert.True(BedSowingRules.ValidateApprovalReadyDate(SeedSowing.SourceCutting, ready, ready.AddDays(-30)).Ok);
        }

        // 2026-10-14: the Transplanter enters T; TrayCount = T / C; WasteQuantity = N - T; nothing stays in the bed.
        [Theory]
        [InlineData(500, "150 Cavity", 450, 3, 50)]     // owner example 1
        [InlineData(500, "150 Cavity", 300, 2, 200)]    // owner example 2
        [InlineData(450, "150 Cavity", 450, 3, 0)]      // owner example 3
        [InlineData(500, "102 Cavity", 408, 4, 92)]
        [InlineData(480, "24 Cavity", 240, 10, 240)]
        [InlineData(480, "24 Cavity", 480, 20, 0)]
        public void Transplant_EnteredQuantity_CompleteTrays_RestRecordedAsWaste(int remaining, string cavity, int quantity, int trays, int waste)
        {
            var (ok, plan, error) = BedSowingRules.PlanTransplant(remaining, quantity, cavity, null, Today, BedDate, Today);
            Assert.True(ok, error);
            Assert.Equal((trays, (decimal)quantity, (decimal)waste), (plan!.Trays, plan.Transplanted, plan.Waste));
            Assert.Equal(remaining, plan.Transplanted + plan.Waste);                        // nothing left open, nothing invented
            Assert.True(plan.MakesTrayBatch);
            Assert.Equal(BedSowing.StatusCompleted, BedSowingRules.StatusFor(remaining, plan.Transplanted, plan.Waste));
        }

        [Theory]
        [InlineData(500, 449)]    // not a multiple of 150
        [InlineData(500, 451)]
        [InlineData(500, 600)]    // more than remaining
        [InlineData(500, 0)]      // a transplant must be positive
        [InlineData(500, -150)]
        [InlineData(500, 150.5)]
        public void Transplant_EnteredQuantity_Refused(decimal remaining, decimal quantity)
        {
            var (ok, plan, error) = BedSowingRules.PlanTransplant(remaining, quantity, "150 Cavity", null, Today, BedDate, Today);
            Assert.False(ok);
            Assert.Null(plan);
            Assert.False(string.IsNullOrWhiteSpace(error));
        }

        [Fact]
        public void Transplant_NonMultiple_ExplainsTheCavity()
        {
            var (_, _, error) = BedSowingRules.PlanTransplant(500, 449, "150 Cavity", null, Today, BedDate, Today);
            Assert.Contains("multiple of 150", error);
            Assert.Contains("300 (2 trays)", error);
            (_, _, error) = BedSowingRules.PlanTransplant(500, 600, "150 Cavity", null, Today, BedDate, Today);
            Assert.Contains("Only 500 seedlings remain", error);
        }

        // Owner decision 2 (2026-10-14): a waste-only closure -- ALL remaining seedlings dead / unusable -- at ANY remaining
        // quantity, below or above one tray, only with the explicit confirmation AND a reason. No cavity, no tray batch.
        [Theory]
        [InlineData(100, "150 Cavity")]   // below one tray
        [InlineData(500, "150 Cavity")]   // above one tray (3 complete trays were possible)
        [InlineData(500, null)]           // no cavity needed
        [InlineData(24, "24 Cavity")]     // exactly one tray
        public void WasteOnlyClosure_AnyQuantity_ConfirmedWithAReason(int remaining, string? cavity)
        {
            var (ok, plan, error) = BedSowingRules.PlanTransplant(remaining, 0, cavity, null, Today, BedDate, Today, closeAsWaste: true, wasteReason: "damping off");
            Assert.True(ok, error);
            Assert.Equal((0, 0m, (decimal)remaining), (plan!.Trays, plan.Transplanted, plan.Waste));
            Assert.False(plan.MakesTrayBatch);
            Assert.Equal(BedSowing.StatusCompleted, BedSowingRules.StatusFor(remaining, plan.Transplanted, plan.Waste));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void WasteOnlyClosure_WithoutAReason_IsRefused(string? reason)
        {
            foreach (var remaining in new decimal[] { 100, 500 })
            {
                var (ok, plan, error) = BedSowingRules.PlanTransplant(remaining, 0, "150 Cavity", null, Today, BedDate, Today, true, reason);
                Assert.False(ok);
                Assert.Null(plan);
                Assert.Contains("reason", error);
            }
        }

        [Fact]
        public void WasteOnlyClosure_RefusedWithoutConfirmation_WithAQuantity_TooLongReason_OrBadDate()
        {
            // no confirmation: a zero quantity never closes a bed -- below one tray the message points to the confirmation
            var (ok, _, error) = BedSowingRules.PlanTransplant(100, 0, "150 Cavity", null, Today, BedDate, Today);
            Assert.False(ok);
            Assert.Contains("fewer than one 150 Cavity tray", error);
            Assert.Contains("confirm", error);
            Assert.False(BedSowingRules.PlanTransplant(500, 0, "150 Cavity", null, Today, BedDate, Today, false, "dead").Ok);   // reason alone is not confirmation
            // with a quantity, a too-long reason, a bad date, or an empty bed: refused
            Assert.False(BedSowingRules.PlanTransplant(500, 150, "150 Cavity", null, Today, BedDate, Today, true, "dead").Ok);
            Assert.False(BedSowingRules.PlanTransplant(100, 0, "150 Cavity", null, Today, BedDate, Today, true, new string('x', BedSowingRules.MaxWasteReasonLength + 1)).Ok);
            Assert.True(BedSowingRules.PlanTransplant(100, 0, "150 Cavity", null, Today, BedDate, Today, true, new string('x', BedSowingRules.MaxWasteReasonLength)).Ok);
            Assert.False(BedSowingRules.PlanTransplant(100, 0, "150 Cavity", null, Today.AddDays(1), BedDate, Today, true, "dead").Ok);
            Assert.False(BedSowingRules.PlanTransplant(100, 0, "150 Cavity", null, BedDate.AddDays(-1), BedDate, Today, true, "dead").Ok);
            Assert.False(BedSowingRules.PlanTransplant(0, 0, "150 Cavity", null, Today, BedDate, Today, true, "dead").Ok);
            // the normal path is unchanged: a positive multiple of the cavity, the rest recorded as waste
            Assert.True(BedSowingRules.PlanTransplant(100, 99, "9 Cavity", null, Today, BedDate, Today).Ok);
            Assert.False(BedSowingRules.PlanTransplant(100, 98, "9 Cavity", null, Today, BedDate, Today).Ok);
            Assert.Contains("Dead", BedSowingRules.WasteOnlyConfirmation, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Transplant_Summary_And_PostedTraysAndDates()
        {
            Assert.Equal("450 seedlings will be transplanted into 3 150 Cavity trays, and the remaining 50 seedlings will be recorded as waste.",
                new BedSowingRules.TransplantPlan(3, 450, 50).Summary("150 Cavity"));
            Assert.Equal("450 seedlings will be transplanted into 3 150 Cavity trays.",
                new BedSowingRules.TransplantPlan(3, 450, 0).Summary("150 Cavity"));
            Assert.Equal("No tray batch will be created: all 100 remaining seedlings will be recorded as waste and the bed will be closed.",
                new BedSowingRules.TransplantPlan(0, 0, 100).Summary("150 Cavity"));
            Assert.Equal("450 seedlings were transplanted into 3 150 Cavity trays, and the remaining 50 seedlings were recorded as waste.",
                new BedSowingRules.TransplantPlan(3, 450, 50).Summary("150 Cavity", done: true));
            Assert.Equal("No tray batch was created: all 100 remaining seedlings were recorded as waste and the bed was closed.",
                new BedSowingRules.TransplantPlan(0, 0, 100).Summary("150 Cavity", done: true));
            // the posted tray count must agree; dates and the cavity are still checked
            Assert.False(BedSowingRules.PlanTransplant(500, 450, "150 Cavity", 4, Today, BedDate, Today).Ok);
            Assert.True(BedSowingRules.PlanTransplant(500, 450, "150 Cavity", 3, Today, BedDate, Today).Ok);
            Assert.False(BedSowingRules.PlanTransplant(500, 450, "150 Cavity", null, Today.AddDays(1), BedDate, Today).Ok);
            Assert.False(BedSowingRules.PlanTransplant(500, 450, "150 Cavity", null, BedDate.AddDays(-1), BedDate, Today).Ok);
            Assert.False(BedSowingRules.PlanTransplant(500, 450, "25 Cavity", null, Today, BedDate, Today).Ok);
        }

        [Fact]
        public void MaterialLedger_ReadersUseTheBusinessDate_OnlyWhenTheColumnExists()
        {
            Assert.Equal("COALESCE(t.TransactionDate, t.CreatedAt)", MaterialStockRepository.BusinessDateSql(true));
            Assert.Equal("t.CreatedAt", MaterialStockRepository.BusinessDateSql(false));
        }

        [Fact]
        public void TransplantedBatches_HaveNoAssignedSupervisor_AnyApprover_BedNeverItsRecorder()
        {
            Assert.True(DirectSowingRules.CanApprove(null, 9, "maya", 13, "somnath", SeedSowing.SourceBedSowing).Ok);
            Assert.False(DirectSowingRules.CanApprove(null, 9, "maya", 9, "maya", SeedSowing.SourceBedSowing).Ok);
            Assert.True(DirectSowingRules.CanApprove(null, 110, "vasant", 110, "vasant", SeedSowing.SourceTransplant).Ok);
            // Seed sowings keep their rule: an assigned supervisor is required.
            Assert.False(DirectSowingRules.CanApprove(null, 9, "maya", 13, "somnath", SeedSowing.SourceSeed).Ok);
            Assert.True(TrayBatchRules.IsTransplantedBatch(SeedSowing.SourceTransplant));
            Assert.False(TrayBatchRules.IsTransplantedBatch(SeedSowing.SourceCutting));
        }

        [Theory]
        [InlineData(480, "24 Cavity", true, 20)]
        [InlineData(250, "24 Cavity", false, 0)]
        [InlineData(150, "150 Cavity", true, 1)]
        [InlineData(84, "42 Cavity", true, 2)]
        [InlineData(9, "9 Cavity", true, 1)]
        [InlineData(204, "102 Cavity", true, 2)]
        public void TrayBatch_CompleteTraysOnly_NoSeedlingInvented(decimal qty, string cavity, bool ok, int trays)
        {
            var r = TrayBatchRules.CompleteTrays(qty, cavity, "in the batch");
            Assert.Equal(ok, r.Ok);
            Assert.Equal(trays, r.Trays);
        }

        [Fact]
        public void TrayBatch_ReadyDate_IsTheTransplantDatePlusReadyDays()
        {
            var transplant = new DateTime(2026, 10, 9);
            Assert.Equal(new DateTime(2026, 11, 10), TrayBatchRules.ReadyDate(transplant, 32));
            Assert.Null(TrayBatchRules.ReadyDate(transplant, null));
            Assert.Null(TrayBatchRules.ReadyDate(transplant, 0));
        }

        [Fact]
        public void PageRules_TransplantingIsTheDedicatedPermissionOnly()
        {
            Assert.Equal("BedSowing.Transplant", BedSowingRules.TransplantPermission);
            Assert.Equal("BedSowing.Transplant", FeatureAuthorizationConventions.Rules["/Production/BedSowing/Transplant"].Read);
            Assert.Equal("Sowing.Enter", FeatureAuthorizationConventions.Rules["/Production/BedSowing/Create"].Read);
            foreach (var page in new[] { "/Production/BedSowing/Index", "/Production/BedSowing/Details", "/Production/BedSowing/History" })
                Assert.Equal("Sowing.View|BedSowing.Transplant", FeatureAuthorizationConventions.Rules[page].Read);
        }

        [Fact]
        public void SowingActivity_CountsTheBedOnce_NeverItsTrayBatches()
        {
            var bed = BedSowingSchema.SowingActivitySql(bedReady: true);
            Assert.Contains("sw.SourceType NOT IN (N'BedSowing', N'Transplant')", bed);   // tray batches made by a transplant are not sowings again
            Assert.Contains("FROM dbo.BedSowings b WHERE b.Status <> N'Cancelled'", bed);
            var plain = BedSowingSchema.SowingActivitySql(bedReady: false);
            Assert.DoesNotContain("BedSowings", plain);
            Assert.Contains("WHERE sw.Status <> 'Cancelled'", plain);
        }

        [Fact]
        public void SeedSowingModel_LabelsTheBedOrigin()
        {
            var s = new SeedSowing { SourceType = SeedSowing.SourceBedSowing };
            Assert.True(s.IsBedSowingSource);
            Assert.False(s.IsCuttingSource);
            Assert.Equal("Bed Sowing", s.OriginLabel);
            Assert.Equal("Seedlings", s.UnitName);
            Assert.Equal("Direct Seed Sowing", new SeedSowing().OriginLabel);
        }

        [Fact]
        public void WastageSources_IncludeBedLossesOnlyWithTheMigration()
        {
            Assert.DoesNotContain("BedSowingOperations", WastageRepository.SourcesSql(indiaDays: false));
            var sql = WastageRepository.SourcesSql(indiaDays: true, bedReady: true);
            Assert.Contains("o.OperationType = N'Loss' AND o.Status = N'Active'", sql);
            Assert.Contains("@IndiaOffsetMinutes", sql);
        }
    }
}
