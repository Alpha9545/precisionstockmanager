using System.Text.RegularExpressions;
using PlantStockManager.Authorization;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // CORRECTION #2 -- Cutting Tray Sowing: the supervisor may approve MORE ready trays than were sown.
    //   * TOTAL ready cuttings (trays x cavity) may not be more than the pool's AVAILABLE Cutting Stock
    //     (Physical - In-Transit);
    //   * only the EXTRA cuttings (ready - still expected) are taken from the sowing's own pool at
    //     approval; the sowing's own cuttings were already taken when it was recorded;
    //   * cancelling the approval returns only the extra;
    //   * Direct Seed Sowing's OWN overage rule is untouched by this correction -- it already allowed
    //     (and still allows) more trays than sown, from the earlier, source-type-agnostic Approved
    //     Change 3; it simply has no stock pool to draw extra cuttings from, so none of this
    //     correction's pool-locking/overage-stock logic applies to it.
    // These are the pure-rule and wiring tests; CuttingSowingOverageE2ETests runs the real code against
    // a scratch database copy.
    public class CuttingSowingOverageTests
    {
        private const string Cutting = SeedSowing.SourceCutting;

        // ---- the extra cuttings ------------------------------------------------------------------------

        [Fact]
        public void Extra_IsOnlyTheTraysBeyondTheSownOnes_TimesTheCavity()
        {
            // 500 trays sown of a 24 cavity = 12,000 cuttings; 600 trays ready -> 100 extra trays = 2,400 cuttings
            Assert.Equal(2400m, DirectSowingRules.ExtraCuttingsNeeded(Cutting, 600, "24 Cavity", 500 * 24));
        }

        [Theory]
        [InlineData(500)]   // exactly as many as sown
        [InlineData(499)]
        [InlineData(1)]
        public void NoExtra_WhenReadyTraysAreNotMoreThanSown(decimal readyTrays)
            => Assert.Equal(0m, DirectSowingRules.ExtraCuttingsNeeded(Cutting, readyTrays, "24 Cavity", 500 * 24));

        [Fact]
        public void Extra_UsesWhatIsStillExpected_NotTheOriginalSownQuantity()
        {
            // equal to (ready trays - sown trays) x cavity for a first approval; 6 trays already approved leave 14 expected
            Assert.Equal((17 - 14) * 24m, DirectSowingRules.ExtraCuttingsNeeded(Cutting, 17, "24 Cavity", 14 * 24));
        }

        [Theory]
        [InlineData("Seed")]
        [InlineData(null)]
        [InlineData("")]
        public void SeedSowings_NeverTakeAnythingExtra(string? sourceType)
            => Assert.Equal(0m, DirectSowingRules.ExtraCuttingsNeeded(sourceType, 600, "24 Cavity", 500 * 24));

        [Theory]
        [InlineData(0)]
        [InlineData(-3)]
        public void NoExtra_ForANonPositiveTrayCount(decimal trays)
            => Assert.Equal(0m, DirectSowingRules.ExtraCuttingsNeeded(Cutting, trays, "24 Cavity", 480));

        [Fact]
        public void NoExtra_ForAnUnknownCavity() => Assert.Equal(0m, DirectSowingRules.ExtraCuttingsNeeded(Cutting, 30, "10 Cavity", 240));

        // ---- the stock rule: total ready CUTTINGS against AVAILABLE Cutting Stock (cuttings) ----------
        // UNITS: CheckCuttingOverage compares CUTTINGS with CUTTINGS. The 500 / 600 / 1,000 / 1,001 figures in
        // the next tests are numbers of CUTTINGS against 1,000 cuttings available; the TRAY-level examples
        // (trays x cavity -> cuttings, always with a real cavity) follow further down.

        [Theory]
        [InlineData(500, 0)]      // as sown: nothing extra, nothing to check
        [InlineData(600, 100)]
        [InlineData(1000, 500)]   // exactly the available stock
        public void ReadyTotalNotMoreThanAvailable_IsAllowed(decimal readyCuttings, decimal extra)
        {
            var (ok, available, error) = DirectSowingRules.CheckCuttingOverage(readyCuttings, extra, 1000, 0);
            Assert.True(ok, error);
            Assert.Equal(1000m, available);
        }

        [Fact]
        public void ReadyTotalMoreThanAvailable_IsRejected()
        {
            var (ok, available, error) = DirectSowingRules.CheckCuttingOverage(1001, 501, 1000, 0);
            Assert.False(ok);
            Assert.Equal(1000m, available);
            Assert.Contains("1,001", error);
            Assert.Contains("1,000", error);
            Assert.Contains("available", error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TheCapIsOnTheTotal_NotOnRecordedPlusExtra()
        {
            // recorded 500 + extra 1,000 would be 1,500: NOT allowed just because the extra alone (1,000) fits
            var (ok, _, _) = DirectSowingRules.CheckCuttingOverage(1500, 1000, 1000, 0);
            Assert.False(ok);
        }

        [Fact]
        public void InTransitStock_IsNeverAvailable()
        {
            // 1,000 physical but 300 in transit -> 700 available
            Assert.True(DirectSowingRules.CheckCuttingOverage(700, 100, 1000, 300).Ok);
            var (ok, available, _) = DirectSowingRules.CheckCuttingOverage(701, 101, 1000, 300);
            Assert.False(ok);
            Assert.Equal(700m, available);
        }

        [Fact]
        public void TheExtraItselfMustAlsoBeCovered_SoStockCanNeverGoNegative()
        {
            // defensive: even if a caller passed an extra above the available stock it is refused
            Assert.False(DirectSowingRules.CheckCuttingOverage(400, 900, 1000, 500).Ok);
        }

        [Fact]
        public void NothingIsChecked_WhenThereIsNoExtra()
        {
            // an ordinary approval (ready <= sown) does not depend on the pool at all, even an empty one
            var (ok, available, error) = DirectSowingRules.CheckCuttingOverage(12000, 0, 0, 0);
            Assert.True(ok, error);
            Assert.Equal(0m, available);
        }

        [Fact]
        public void WorkedExample_500Trays_Cavity24_600Ready()
        {
            // the pool held the rest of the stock after the sowing was recorded (12,000 already taken)
            const decimal sownCuttings = 500 * 24, availableAfterSowing = 20000;
            var extra = DirectSowingRules.ExtraCuttingsNeeded(Cutting, 600, "24 Cavity", sownCuttings);
            Assert.Equal(2400m, extra);                                      // NOT 12,000 + 2,400
            var (ok, _, error) = DirectSowingRules.CheckCuttingOverage(600 * 24, extra, availableAfterSowing, 0);
            Assert.True(ok, error);
            Assert.Equal(availableAfterSowing - 2400, availableAfterSowing - extra);   // the pool after approval
        }

        // ---- the same rule, entered as TRAYS (what the supervisor actually types) ----------------------
        // Cavity 24; the sowing is 500 TRAYS = 12,000 cuttings; 20,000 CUTTINGS are still available after it was recorded.
        //   ready trays  -> ready cuttings (trays x 24) -> extra trays -> extra cuttings -> allowed?
        //   500 trays    -> 12,000                       -> 0           -> 0             -> yes (no stock check)
        //   600 trays    -> 14,400                       -> 100         -> 2,400         -> yes (14,400 <= 20,000)
        //   833 trays    -> 19,992                       -> 333         -> 7,992         -> yes (19,992 <= 20,000)
        //   834 trays    -> 20,016                       -> 334         -> 8,016         -> NO  (20,016 > 20,000)
        private const int SownTrays24 = 500, Cavity24 = 24;
        private const decimal SownCuttings24 = SownTrays24 * Cavity24, Available20k = 20000;

        private static (decimal ExtraTrays, decimal ExtraCuttings, bool Allowed) Approve(decimal readyTrays, decimal availableCuttings)
        {
            var extra = DirectSowingRules.ExtraCuttingsNeeded(Cutting, readyTrays, "24 Cavity", SownCuttings24);
            var check = DirectSowingRules.CheckCuttingOverage(readyTrays * Cavity24, extra, availableCuttings, 0, readyTrays, Cavity24);
            return (DirectSowingRules.ExtraTrays(extra, "24 Cavity"), extra, check.Ok);
        }

        [Theory]
        [InlineData(500, 0, 0, true)]
        [InlineData(600, 100, 2400, true)]
        [InlineData(833, 333, 7992, true)]     // 833 x 24 = 19,992 cuttings: the most that 20,000 available cuttings allow
        [InlineData(834, 334, 8016, false)]    // 834 x 24 = 20,016 cuttings: one tray too many
        public void TrayExamples_500SownTrays_Cavity24_20000CuttingsAvailable(int readyTrays, int extraTrays, int extraCuttings, bool allowed)
        {
            var r = Approve(readyTrays, Available20k);
            Assert.Equal(extraTrays, r.ExtraTrays);
            Assert.Equal(extraCuttings, r.ExtraCuttings);
            Assert.Equal(allowed, r.Allowed);
            Assert.Equal(extraCuttings, extraTrays * (decimal)Cavity24);                 // extra cuttings = extra trays x cavity
        }

        [Fact]
        public void MaxReadyTrays_IsTheLargestTrayCountTheRuleAccepts_ForEveryCavityAndStockLevel()
        {
            foreach (var cavityType in DirectSowingRules.CavityTypes)
            {
                var cavity = DirectSowingRules.CavityCount(cavityType)!.Value;
                foreach (decimal available in new decimal[] { 0, 5, cavity - 1, cavity, 1000, 1008, 20000 })
                {
                    const int sownTrays = 20;
                    var sownCuttings = sownTrays * (decimal)cavity;
                    var max = DirectSowingRules.MaxReadyTrays(sownCuttings, cavityType, available);
                    Assert.True(max >= sownTrays);                                        // ready <= sown never needs stock
                    bool Allowed(decimal trays)
                    {
                        var extra = DirectSowingRules.ExtraCuttingsNeeded(Cutting, trays, cavityType, sownCuttings);
                        return DirectSowingRules.CheckCuttingOverage(trays * cavity, extra, available, 0).Ok;
                    }
                    Assert.True(Allowed(max), $"{cavityType}, available {available}: {max} trays must be allowed");
                    Assert.False(Allowed(max + 1), $"{cavityType}, available {available}: {max + 1} trays must be refused");
                }
            }
        }

        [Fact]
        public void MaxReadyTrays_Examples()
        {
            Assert.Equal(833m, DirectSowingRules.MaxReadyTrays(SownCuttings24, "24 Cavity", 20000));   // floor(20,000 / 24)
            Assert.Equal(500m, DirectSowingRules.MaxReadyTrays(SownCuttings24, "24 Cavity", 1000));    // stock is short: the sown 500 trays always remain allowed
            Assert.Equal(41m, DirectSowingRules.MaxReadyTrays(0, "24 Cavity", 1000));                  // floor(1,000 / 24)
            Assert.Equal(0m, DirectSowingRules.MaxReadyTrays(480, "10 Cavity", 1000));                 // unknown cavity
        }

        [Fact]
        public void ExtraTrays_IsExtraCuttingsDividedByTheCavity()
        {
            Assert.Equal(100m, DirectSowingRules.ExtraTrays(2400, "24 Cavity"));
            Assert.Equal(0m, DirectSowingRules.ExtraTrays(0, "24 Cavity"));
            Assert.Equal(0m, DirectSowingRules.ExtraTrays(2400, "10 Cavity"));
        }

        [Theory]
        [InlineData("9 Cavity")]
        [InlineData("24 Cavity")]
        [InlineData("42 Cavity")]
        [InlineData("102 Cavity")]
        [InlineData("150 Cavity")]
        public void TrayCountsAreNeverComparedWithCuttingsDirectly_600Or1001TraysAreNotAllowedByAThousandCuttings(string cavityType)
        {
            // "600 trays" or "1,001 trays" against 1,000 AVAILABLE CUTTINGS is 5,400+ cuttings for even the smallest tray (9),
            // so it is refused for every cavity: a tray count is always converted to cuttings before it is compared.
            var cavity = DirectSowingRules.CavityCount(cavityType)!.Value;
            foreach (var trays in new decimal[] { 600, 1000, 1001 })
            {
                var extra = DirectSowingRules.ExtraCuttingsNeeded(Cutting, trays, cavityType, 500m * cavity);
                Assert.False(DirectSowingRules.CheckCuttingOverage(trays * cavity, extra, 1000, 0).Ok, $"{trays} trays x {cavity}");
            }
        }

        [Fact]
        public void TheErrorMessage_SpellsOutTraysCavityAndCuttings()
        {
            var (ok, _, error) = DirectSowingRules.CheckCuttingOverage(20016, 8016, 20000, 0, 834, 24);
            Assert.False(ok);
            Assert.Contains("834 Actual Ready Trays x 24-cavity = 20,016 cuttings", error);
            Assert.Contains("available Cutting Stock (20,000 cuttings, in-transit excluded)", error);
            Assert.Contains("8,016 extra cuttings (334 extra trays)", error);
        }

        // ---- the approval arithmetic that existed before still holds ----------------------------------

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(2.5)]
        public void ZeroNegativeAndFractionalTraysAreStillRejected(decimal trays)
            => Assert.False(DirectSowingRules.ComputeTrayApproval(480, 20, "24 Cavity", 0, 0, trays, "Disease").Ok);

        [Fact]
        public void MoreTraysThanSown_StillClosesTheBatchWithZeroWastage()
        {
            var r = DirectSowingRules.ComputeTrayApproval(480, 20, "24 Cavity", 0, 0, 25, null);
            Assert.True(r.Ok, r.Error);
            Assert.Equal(600m, r.ActualSeedlings);
            Assert.Equal(0m, r.Wastage);
        }

        [Fact]
        public void AFullyApprovedSowing_CannotBeApprovedAgain()
            => Assert.False(DirectSowingRules.ComputeTrayApproval(480, 20, "24 Cavity", 480, 0, 1, null).Ok);

        // ---- approval authority is unchanged ------------------------------------------------------------

        [Fact]
        public void OnlyTheAssignedSupervisorApproves_AndTheCuttingRecorderMayBeThem()
        {
            Assert.True(DirectSowingRules.CanApprove(5, 5, "rec", 5, "rec", Cutting).Ok);          // recorder = assigned supervisor (cutting only)
            Assert.False(DirectSowingRules.CanApprove(5, 5, "rec", 5, "rec", "Seed").Ok);          // seed recorder never approves
            Assert.False(DirectSowingRules.CanApprove(5, 9, "rec", 6, "other", Cutting).Ok);       // not the assigned supervisor
            Assert.False(DirectSowingRules.CanApprove(null, 9, "rec", 6, "other", Cutting).Ok);    // nobody assigned
            Assert.False(DirectSowingRules.CanApprove(5, 9, "rec", null, null, Cutting).Ok);       // unknown user
        }

        [Fact]
        public void TheApprovalPage_StillNeedsReadyStockConfirm()
            => Assert.Equal("ReadyStock.Confirm", FeatureAuthorizationConventions.GetRule("/Production/ReadyConfirmation/Confirm").Read);

        // ---- wiring (source scans) ------------------------------------------------------------------------

        private static string Repo(params string[] parts)
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.GetFiles(dir, "*.sln").Any()) dir = Path.GetDirectoryName(dir);
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir! }.Concat(parts).ToArray()));
        }

        [Fact]
        public void ApprovalView_HasNoBrowserMaximumForCuttingSowings_AndDoesNotSayOneToN()
        {
            var view = Repo("Pages", "Production", "ReadyConfirmation", "Confirm.cshtml");
            var cuttingBranchStart = view.IndexOf("@if (s.IsCuttingSource)", StringComparison.Ordinal);
            var elseStart = view.IndexOf("else", cuttingBranchStart, StringComparison.Ordinal);
            Assert.True(cuttingBranchStart > 0 && elseStart > cuttingBranchStart);
            var cuttingBranch = view.Substring(cuttingBranchStart, elseStart - cuttingBranchStart);
            Assert.DoesNotContain("max=", cuttingBranch);
            Assert.DoesNotContain("1 to", cuttingBranch);
            Assert.Contains("available Cutting Stock", cuttingBranch);
            Assert.Contains("automatically", cuttingBranch);
            Assert.Contains("min=\"1\"", cuttingBranch);
            Assert.Contains("step=\"1\"", cuttingBranch);
        }

        [Fact]
        public void ApprovalView_UsesUnambiguousUnits_TraysAreEntered_StockIsShownInCuttings()
        {
            var view = Repo("Pages", "Production", "ReadyConfirmation", "Confirm.cshtml");
            var cuttingBranchStart = view.IndexOf("@if (s.IsCuttingSource)", StringComparison.Ordinal);
            var cuttingBranch = view.Substring(cuttingBranchStart, view.IndexOf("else", cuttingBranchStart, StringComparison.Ordinal) - cuttingBranchStart);
            // the input is TRAYS, and the help says so with the conversion to cuttings
            Assert.Contains("Enter <strong>TRAYS</strong>, not cuttings", cuttingBranch);
            Assert.Contains("One tray = @(cavity?.ToString() ?? \"?\") cuttings", cuttingBranch);
            Assert.Contains("@Qty(s.QuantitySown) cuttings", cuttingBranch);
            Assert.Contains("(you do not enter the extra)", cuttingBranch);
            Assert.Contains("total ready cuttings (trays &times;", cuttingBranch);
            Assert.Contains("cuttings now", cuttingBranch);
            Assert.Contains("at most", cuttingBranch);
            // the stock figure is labelled and suffixed as CUTTINGS; the maximum as TRAYS
            Assert.Contains("(cuttings; excludes in-transit)", view);
            Assert.Contains("@Qty(Model.AvailableCuttingStock.Value) cuttings</td>", view);
            Assert.Contains("Most Actual Ready Trays you can enter now", view);
            Assert.Contains("@Qty(Model.MaxReadyTrays.Value) trays</td>", view);
            // extra is shown as BOTH trays and cuttings, and is calculated (never typed)
            Assert.Contains("Extra Trays", view);
            Assert.Contains("id=\"outExtraTrays\"", view);
            Assert.Contains("Extra Cuttings taken from Cutting Stock", view);
            Assert.Contains("id=\"outExtra\"", view);
            Assert.Contains("+ ' trays'", view);
            Assert.Contains("+ ' cuttings'", view);
            // no bare, unit-less "quantity" wording is used for the stock cap on the cutting branch
            Assert.DoesNotContain("ready quantity", cuttingBranch);
        }

        [Fact]
        public void ApprovalPage_SuccessMessageAndPreview_ReportExtraTraysAndExtraCuttings()
        {
            var page = Repo("Pages", "Production", "ReadyConfirmation", "Confirm.cshtml.cs");
            Assert.Contains("extra trays = {QuantityFormat.Qty(extra)} extra cuttings were taken from Cutting Stock", page);
            Assert.Contains("extraTrays, extraCuttings = extra, availableCuttings = available, maxReadyTrays = maxTrays", page);
            Assert.Contains("MaxReadyTrays(", page);
        }

        [Fact]
        public void ApprovalView_DirectSeedSowing_HasNoBrowserMaximumEither()
        {
            // Approved Change 3 (Database/PhaseG_ReadyConfirmationTrayOverage.sql) removed the
            // "ActualTrayQuantity > sw.NumberOfTrays" cap at the database level for every source
            // type, not only Cutting Tray Sowing -- confirmed by DirectSowingRules.ComputeTrayApproval
            // taking no sourceType parameter at all (TrayApprovalTests.MoreTraysThanSown_IsAcceptedWithZeroWastage)
            // and by the ZZTEST fixture that exercised exactly this on a SourceType='Seed' sowing
            // (Database/ZZTEST_SeedData.sql's ZZTEST-SOW-001, "demonstration of the new 'overage
            // allowed' rule (Approved Change 3)"). A browser max="291" here only blocks a value the
            // server and database both already accept -- it must not render one.
            var view = Repo("Pages", "Production", "ReadyConfirmation", "Confirm.cshtml");
            var elseStart = view.IndexOf("else", view.IndexOf("@if (s.IsCuttingSource)", StringComparison.Ordinal), StringComparison.Ordinal);
            var seedBranch = view.Substring(elseStart, 700);
            Assert.DoesNotContain("max=", seedBranch);
            Assert.Contains("min=\"1\"", seedBranch);
            Assert.Contains("step=\"1\"", seedBranch);
            Assert.Contains("Whole trays, at least 1", seedBranch);
        }

        [Fact]
        public void ApprovalView_ExtraCuttingsIsCalculated_NeverAnInputField()
        {
            var view = Repo("Pages", "Production", "ReadyConfirmation", "Confirm.cshtml");
            Assert.Contains("id=\"outExtra\"", view);
            Assert.DoesNotContain("asp-for=\"Extra", view);
            var page = Repo("Pages", "Production", "ReadyConfirmation", "Confirm.cshtml.cs");
            var bound = Regex.Matches(page, @"\[BindProperty\][^;{]*?public\s+\S+\s+(\w+)").Select(m => m.Groups[1].Value).OrderBy(n => n).ToArray();
            Assert.Equal(new[] { "ActualReadyTrays", "Remarks", "SeedSowingId", "WastageReason" }, bound);
        }

        [Fact]
        public void ConfirmAsync_LocksThePool_ChecksAvailable_AndWritesOneLedgerRow_OnlyForCuttingSowings()
        {
            var repo = Repo("Data", "ReadyConfirmationRepository.cs");
            var confirm = repo.Substring(repo.IndexOf("public async Task<(bool Success, string? Message, int Id)> ConfirmAsync", StringComparison.Ordinal));
            confirm = confirm.Substring(0, confirm.IndexOf("public async Task<(bool Success, string? Message)> CancelAsync", StringComparison.Ordinal));
            Assert.Contains("ExtraCuttingsNeeded(", confirm);
            Assert.Contains("sowingSourceType, actualTrays, cavityType", confirm);
            Assert.Contains("FROM dbo.CuttingStock WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", confirm);
            Assert.Contains("CheckCuttingOverage(", confirm);
            // total ready CUTTINGS (trays x cavity), the extra CUTTINGS, and the pool's physical / in-transit CUTTINGS
            Assert.Contains("readyQuantity, extraCuttings, poolPhysical, poolInTransit, actualTrays, DirectSowingRules.CavityCount(cavityType)", confirm);
            Assert.Contains("canUseSourceArea(poolAreaId)", confirm);
            Assert.Contains("-extraCuttings, \"Sown\", \"ReadyConfirmation\", newId", confirm);
            // exactly one cutting-stock write in the approval; the sowing's own quantity is never taken again
            Assert.Single(Regex.Matches(confirm, @"_cuttingStockRepo\.RecordTransactionAsync"));
            Assert.DoesNotContain("-quantitySown", confirm);
            Assert.DoesNotContain("-actualTrays", confirm);
            // the pool comes from the sowing's own SourceCuttingStockId, never from the caller
            Assert.Contains("sourceCuttingStockId.Value", confirm);
        }

        [Fact]
        public void CancelAsync_ReturnsOnlyWhatTheLedgerShowsThisApprovalTook_OnceOnly()
        {
            var repo = Repo("Data", "ReadyConfirmationRepository.cs");
            var cancel = repo.Substring(repo.IndexOf("public async Task<(bool Success, string? Message)> CancelAsync", StringComparison.Ordinal));
            Assert.Contains("ReferenceType = N'ReadyConfirmation' AND ReferenceId = @Id", cancel);
            Assert.Contains("TransactionType IN (N'Sown', N'ReversalReturn')", cancel);
            Assert.Contains("\"ReversalReturn\", \"ReadyConfirmation\", id", cancel);
            Assert.Contains("extraToReturn > 0", cancel);
            Assert.Contains("if (status == \"Cancelled\")", cancel);
            // the return is inside the cutting-source branch, and the sowing's own quantity is not returned here
            Assert.Contains("sowingSourceType == SeedSowing.SourceCutting", cancel);
            Assert.DoesNotContain("quantitySown", cancel);
            Assert.Single(Regex.Matches(cancel, @"_cuttingStockRepo\.RecordTransactionAsync"));
        }

        [Fact]
        public void DirectSeedSowing_CodePathsAreNotTouched()
        {
            var sowingRepo = Repo("Data", "SeedSowingRepository.cs");
            Assert.DoesNotContain("ExtraCuttingsNeeded", sowingRepo);
            Assert.DoesNotContain("CheckCuttingOverage", sowingRepo);
            var direct = Repo("Pages", "Production", "SeedSowing", "Create.cshtml.cs");
            Assert.DoesNotContain("ExtraCuttings", direct);
            // the rules only ever act on the Cutting source type
            var rules = Repo("Services", "DirectSowingRules.cs");
            Assert.Contains("sourceType != Models.SeedSowing.SourceCutting", rules);
        }

        [Fact]
        public void RecordingACuttingSowing_IsUnchanged_QuantityCheckedAgainstAvailableAtEntry()
        {
            var repo = Repo("Data", "SeedSowingRepository.cs");
            var insert = repo.Substring(repo.IndexOf("InsertFromCuttingAsync", StringComparison.Ordinal));
            insert = insert.Substring(0, insert.IndexOf("private static string TruncateBatch", StringComparison.Ordinal));
            Assert.Contains("PlanSowing(", insert);
            Assert.Contains("physical, inTransit", insert);
            Assert.Contains("-entry.QuantitySown, \"Sown\", \"SeedSowing\", newId", insert);
        }

        [Fact]
        public void NoDatabaseMigration_WasNeededOrAdded_ForThisCorrection()
        {
            // the existing ledger (CK_CuttingStockTx_Type allows 'Sown' and 'ReversalReturn'; ReferenceType is free text)
            // records both the extra deduction and its return, so no schema object changes
            var dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.GetFiles(dir, "*.sln").Any()) dir = Path.GetDirectoryName(dir);
            var migrations = Directory.GetFiles(Path.Combine(dir!, "Database", "Migrations"), "*.sql");
            Assert.DoesNotContain(migrations, f => Path.GetFileName(f).Contains("Overage", StringComparison.OrdinalIgnoreCase)
                                                   || Path.GetFileName(f).Contains("ExtraCutting", StringComparison.OrdinalIgnoreCase));
        }
    }
}
