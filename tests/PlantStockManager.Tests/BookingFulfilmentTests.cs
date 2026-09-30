using System.Text.RegularExpressions;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // CORRECTION #4 -- BOOKING FULFIL (Potted Plant Booking -> Dispatch).
    //
    // ROOT CAUSE: DispatchRepository.InsertAsync deducted PHYSICAL stock first and only then released the
    // booking's RESERVATION. The stock rules (PottedPlantStockRepository.RecordTransactionAsync and the CHECK
    // constraint CK_PottedPlantStock_ReservedWithinPhysical) require Physical >= Reserved after every step,
    // so the first step was refused whenever Physical - quantity < Reserved -- i.e. unless the pool held spare,
    // unreserved plants. A booking that reserved (all of) its stock could not be dispatched at all:
    //   "This would take Physical Quantity (10) below what is already Reserved (12)".
    // FIX: release the reservation first, then deduct physical (both by the dispatched quantity), inside the same
    // transaction; plus server-side quantity / status / Area / stale-form (double submit) rules.
    //
    // Example used below (the one from the requirement): Booked 100, already fulfilled 30, remaining 70.
    public class BookingFulfilmentTests
    {
        // ---- quantity rules ------------------------------------------------------------------------------

        [Fact]
        public void FullFulfilment_OfAFreshBooking_IsAllowed_AndCompletesIt()
        {
            var (ok, remaining, error) = DispatchRules.ValidateBookingDispatch("Pending", 100, 0, 100);
            Assert.True(ok, error);
            Assert.Equal(100m, remaining);
            Assert.Equal("Dispatched", DispatchRules.StatusAfterDispatch(100, 100));
        }

        [Fact]
        public void PartialFulfilment_Booked100_Fulfilled30_Remaining70_Fulfil40_LeavesRemaining30()
        {
            var (ok, remaining, error) = DispatchRules.ValidateBookingDispatch("PartiallyDispatched", 100, 30, 40);
            Assert.True(ok, error);
            Assert.Equal(70m, remaining);                                              // remaining BEFORE this dispatch
            Assert.Equal(70m, 30m + 40m);                                              // fulfilled after
            Assert.Equal(30m, remaining - 40m);                                        // remaining after
            Assert.Equal("PartiallyDispatched", DispatchRules.StatusAfterDispatch(100, 70));
        }

        [Fact]
        public void Fulfilling31_WhenOnly30Remain_IsRejected()
        {
            var (ok, remaining, error) = DispatchRules.ValidateBookingDispatch("PartiallyDispatched", 100, 70, 31);
            Assert.False(ok);
            Assert.Equal(30m, remaining);
            Assert.Contains("31", error);
            Assert.Contains("30", error);
            Assert.True(DispatchRules.ValidateBookingDispatch("PartiallyDispatched", 100, 70, 30).Ok);   // exactly the remainder is fine
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(-100)]
        [InlineData(0.5)]
        [InlineData(2.5)]
        public void ZeroNegativeAndFractionalQuantities_AreRejected(double quantity)
        {
            var (ok, _, error) = DispatchRules.ValidateBookingDispatch("Pending", 100, 0, (decimal)quantity);
            Assert.False(ok);
            Assert.Contains("whole number greater than zero", error);
        }

        [Theory]
        [InlineData("Cancelled")]
        [InlineData("Dispatched")]
        [InlineData("Completed")]
        [InlineData("")]
        [InlineData(null)]
        public void CancelledClosedOrUnknownBookings_CannotBeFulfilled(string? status)
        {
            var (ok, _, error) = DispatchRules.ValidateBookingDispatch(status, 100, 0, 10);
            Assert.False(ok);
            Assert.Contains("cannot be dispatched", error);
        }

        [Theory]
        [InlineData("Pending", true)]
        [InlineData("PartiallyDispatched", true)]
        [InlineData("Dispatched", false)]
        [InlineData("Cancelled", false)]
        public void OnlyOpenBookingsCanBeDispatched(string status, bool expected)
            => Assert.Equal(expected, DispatchRules.CanDispatchStatus(status));

        [Theory]
        [InlineData(100, 0, "PartiallyDispatched")]
        [InlineData(100, 30, "PartiallyDispatched")]
        [InlineData(100, 99, "PartiallyDispatched")]
        [InlineData(100, 100, "Dispatched")]
        [InlineData(1, 1, "Dispatched")]
        public void TheStatusIsCalculatedFromTheQuantities(double booked, double fulfilled, string expected)
            => Assert.Equal(expected, DispatchRules.StatusAfterDispatch((decimal)booked, (decimal)fulfilled));

        // ---- double submit: the form remembers how much was dispatched when it was opened -------------------

        [Fact]
        public void AFormOpenedAtTheCurrentQuantity_IsCurrent()
            => Assert.True(DispatchRules.CheckFormIsCurrent(30, 30).Ok);

        [Fact]
        public void ARepeatedOrStaleForm_IsRefused_NothingIsDispatchedTwice()
        {
            var (ok, error) = DispatchRules.CheckFormIsCurrent(30, 70);      // opened at 30 dispatched; 70 now
            Assert.False(ok);
            Assert.Contains("70", error);
            Assert.Contains("Nothing was dispatched", error);
        }

        [Fact]
        public void WithoutAToken_NoStaleCheckIsMade_ForCallersThatDoNotHaveOne()
            => Assert.True(DispatchRules.CheckFormIsCurrent(null, 70).Ok);

        // ---- wiring (source scans) --------------------------------------------------------------------------

        private static string Repo(params string[] parts)
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.GetFiles(dir, "*.sln").Any()) dir = Path.GetDirectoryName(dir);
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir! }.Concat(parts).ToArray()));
        }

        private static string Method(string source, string signatureStart, string nextSignatureStart)
        {
            var start = source.IndexOf(signatureStart, StringComparison.Ordinal);
            Assert.True(start >= 0, signatureStart);
            var end = source.IndexOf(nextSignatureStart, start + signatureStart.Length, StringComparison.Ordinal);
            Assert.True(end > start, nextSignatureStart);
            return source.Substring(start, end - start);
        }

        [Fact]
        public void Dispatch_ReleasesTheReservationBeforeDeductingPhysicalStock()
        {
            var repo = Repo("Data", "DispatchRepository.cs");
            var insert = Method(repo, "public async Task<(bool Success, string? Message, int Id)> InsertAsync(", "// Phase D: Direct customer sale");
            var release = insert.IndexOf("\"ReservationRelease\", \"Dispatch\"", StringComparison.Ordinal);
            var deduct = insert.IndexOf("\"Dispatch\", \"Dispatch\", newId", StringComparison.Ordinal);
            Assert.True(release > 0 && deduct > 0, "both stock movements must be present");
            Assert.True(release < deduct, "the reservation must be released BEFORE physical stock is deducted");
            // exactly one of each -- the dispatched quantity is taken once
            Assert.Single(Regex.Matches(insert, @"RecordReservationAsync"));
            Assert.Single(Regex.Matches(insert, @"RecordTransactionAsync"));
        }

        [Fact]
        public void Dispatch_IsOneTransaction_ThatCommitsOnce_AndRollsBackOnEveryRefusal()
        {
            var repo = Repo("Data", "DispatchRepository.cs");
            var insert = Method(repo, "public async Task<(bool Success, string? Message, int Id)> InsertAsync(", "// Phase D: Direct customer sale");
            Assert.Single(Regex.Matches(insert, @"tx\.Commit\(\)"));
            Assert.Contains("conn.BeginTransaction()", insert);
            Assert.True(Regex.Matches(insert, @"tx\.Rollback\(\)").Count >= 6);
            Assert.Contains("WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", insert);            // the booking row is locked first
        }

        [Fact]
        public void Dispatch_EnforcesEveryRuleOnTheServer()
        {
            var repo = Repo("Data", "DispatchRepository.cs");
            var insert = Method(repo, "public async Task<(bool Success, string? Message, int Id)> InsertAsync(", "// Phase D: Direct customer sale");
            Assert.Contains("!DirectSowingRules.IsWholeNumber(entry.Quantity)", insert);                   // whole, > 0
            Assert.Contains("DispatchRules.CanDispatchStatus(status)", insert);                             // not Cancelled / Dispatched
            Assert.Contains("canAccessArea != null && !canAccessArea(entry.AreaId)", insert);               // Area, from the booking's own row
            Assert.Contains("DispatchRules.CheckFormIsCurrent(expectedDispatched, dispatchedSoFar)", insert);  // double submit
            Assert.Contains("DispatchRules.ValidateBookingDispatch(status, bookingQuantity, dispatchedSoFar, entry.Quantity)", insert);  // <= remaining
            Assert.Contains("DispatchRules.StatusAfterDispatch(bookingQuantity, newDispatchedQuantity)", insert);
            // the stock row comes from the locked booking, never from the caller
            Assert.Contains("entry.PottedPlantStockId = bookingReader.GetInt32", insert);
            // order of the checks: status, Area, stale form, quantity -- all before any row is written
            var status = insert.IndexOf("CanDispatchStatus(status)", StringComparison.Ordinal);
            var area = insert.IndexOf("canAccessArea(entry.AreaId)", StringComparison.Ordinal);
            var stale = insert.IndexOf("CheckFormIsCurrent(", StringComparison.Ordinal);
            var quantity = insert.IndexOf("ValidateBookingDispatch(status, bookingQuantity, dispatchedSoFar, entry.Quantity);", StringComparison.Ordinal);
            var firstWrite = insert.IndexOf("INSERT INTO dbo.Dispatches", StringComparison.Ordinal);
            Assert.True(status < area && area < stale && stale < quantity && quantity < firstWrite);
        }

        [Fact]
        public void TheDispatchPage_PassesTheAreaRuleAndTheOpenedAtValue_AndNeverTrustsThem_ForTheRules()
        {
            var page = Repo("Pages", "Production", "Dispatch", "Create.cshtml.cs");
            Assert.Contains("public decimal? ExpectedDispatched { get; set; }", page);
            Assert.Contains("_dispatchRepo.InsertAsync(Dispatch, userId,", page);
            Assert.Contains("areaId => _areaAccessService.CanAccessArea(User, areaId), ExpectedDispatched", page);
            Assert.Contains("whole number greater than zero", page);
            Assert.Contains("(\"Dispatch.PottedPlantStockId\")", page);   // the stock row is still server-derived
        }

        [Fact]
        public void TheDispatchForm_ShowsBookedFulfilledRemainingAndStock_AndTakesWholePlants()
        {
            var view = Repo("Pages", "Production", "Dispatch", "Create.cshtml");
            foreach (var label in new[] { "Booked", "Fulfilled (dispatched)", "Remaining", "Stock physical", "Stock reserved (all bookings)", "Stock available (unreserved)", "Quantity to Dispatch Now" })
                Assert.Contains(label, view);
            Assert.Contains("type=\"number\" step=\"1\" min=\"1\" asp-for=\"Dispatch.Quantity\"", view);
            Assert.DoesNotContain("step=\"0.01\"", view);
            Assert.Contains("asp-for=\"ExpectedDispatched\"", view);
            Assert.Contains("After this dispatch:", view);
            Assert.Contains("Only ' + fmt(remaining) + ' plants remain on this Booking.", view);   // clear message when over the remainder
            Assert.Contains("button[type=submit]').prop('disabled', true)", view);                  // a double click cannot submit twice
        }

        // ---- regressions: what must NOT change --------------------------------------------------------------

        [Fact]
        public void CancellingADispatch_StillRestoresPhysicalFirst_ThenReservesAgain()
        {
            var repo = Repo("Data", "DispatchRepository.cs");
            var cancel = repo.Substring(repo.IndexOf("public async Task<(bool Success, string? Message)> CancelAsync", StringComparison.Ordinal));
            var restore = cancel.IndexOf("\"Dispatch\", \"Dispatch\", id", StringComparison.Ordinal);
            var reserve = cancel.IndexOf("\"Reservation\", \"Dispatch\", id", StringComparison.Ordinal);
            Assert.True(restore > 0 && reserve > restore);        // the opposite order is the correct one when reversing
        }

        [Fact]
        public void DirectSale_IsUnchanged_ItNeverTouchesReservations()
        {
            var repo = Repo("Data", "DispatchRepository.cs");
            var direct = Method(repo, "public async Task<(bool Success, string? Message, string? DispatchCode)> DirectSaleAsync", "public async Task<(bool Success, string? Message)> UpdateDetailsAsync");
            Assert.DoesNotContain("ReservationRelease", direct);
            Assert.DoesNotContain("RecordReservationAsync", direct);
            Assert.DoesNotContain("DispatchRules.ValidateBookingDispatch", direct);
        }

        [Fact]
        public void OutletBookingCollect_AndSeedlingDispatch_AreNotTouched()
        {
            var outlet = Repo("Data", "OutletBookingRepository.cs");
            var collect = Method(outlet, "public async Task<(bool Success, string? Message)> CollectItemAsync", "public async Task<(bool Success, string? Message)> CancelAsync");
            Assert.True(collect.IndexOf("\"ReservationRelease\"", StringComparison.Ordinal) < collect.IndexOf("\"Dispatch\", \"OutletBookingItem\"", StringComparison.Ordinal));
            var seedling = Repo("Data", "SeedlingFulfilmentRepository.cs");
            Assert.DoesNotContain("DispatchRules.", seedling);
            Assert.DoesNotContain("CheckFormIsCurrent", seedling);
        }

        [Fact]
        public void ExistingDispatchRuleTests_StillSeeTheDirectSaleRuleUnchanged()
        {
            Assert.True(DispatchRules.ValidateDirectSale(100, "Customer", true, true, 100).Ok);
            Assert.False(DispatchRules.ValidateDirectSale(101, "Customer", true, true, 100).Ok);
        }

        [Fact]
        public void PermissionsAreUnchanged_DispatchNeedsDispatchEnterOrOutletSell()
        {
            Assert.Equal("Dispatch.Enter|Outlet.Sell", PlantStockManager.Authorization.FeatureAuthorizationConventions.GetRule("/Production/Dispatch/Create").Read);
            Assert.Equal("Booking.Enter|Outlet.Sell", PlantStockManager.Authorization.FeatureAuthorizationConventions.GetRule("/Production/PottedPlantBooking/Create").Read);
            Assert.Equal("Booking.View|Dispatch.View", PlantStockManager.Authorization.FeatureAuthorizationConventions.GetRule("/Bookings/BookingDetails").Read);
        }
    }
}
