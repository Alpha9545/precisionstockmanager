using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;
using PlantStockManager.Authorization;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Empty Pot Stock moved to the Mother Plant Supervisor (Add Empty Pot Stock, own Area only; the Main Office
    // Purchase / Issue workflow retired to System Administrator only; the Purchase Order EmptyPot path disabled), and
    // "own bookings only" seedling-booking visibility for the users in Authorization:OwnBookingsOnlyUserIds.
    // Pure rules + source checks + real HTTP authorization (DeleteAuthorizationTests' test host). The database
    // behaviour is covered by EmptyPotAreaStockAndOwnBookingsE2ETests.
    public class EmptyPotAreaStockAndOwnBookingsTests : IClassFixture<DeleteAuthorizationTests.AppFactory>
    {
        private readonly DeleteAuthorizationTests.AppFactory _factory;

        public EmptyPotAreaStockAndOwnBookingsTests(DeleteAuthorizationTests.AppFactory factory) => _factory = factory;

        private static string ReadSource(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
        }

        // ---- Empty Pot: rules ------------------------------------------------------------------------------------

        private static readonly DateTime Today = new(2026, 10, 5);

        [Theory]
        [InlineData(1)]
        [InlineData(25)]
        [InlineData(100000)]
        public void AddStock_PositiveWholeQuantity_IsValid(int quantity)
            => Assert.True(EmptyPotStockRules.ValidateAddStock("4 inch", quantity, Today, Today).Ok);

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        [InlineData(2.5)]
        [InlineData(0.1)]
        public void AddStock_ZeroNegativeOrFractionalQuantity_IsRejected(double quantity)
        {
            var (ok, error) = EmptyPotStockRules.ValidateAddStock("4 inch", (decimal)quantity, Today, Today);
            Assert.False(ok);
            Assert.Equal("Quantity must be a whole number greater than zero.", error);
        }

        [Fact]
        public void AddStock_FutureDate_IsRejected_PastAndTodayAreAllowed()
        {
            Assert.Equal("Date cannot be in the future.", EmptyPotStockRules.ValidateAddStock("4 inch", 5, Today.AddDays(1), Today).Error);
            Assert.True(EmptyPotStockRules.ValidateAddStock("4 inch", 5, Today, Today).Ok);
            Assert.True(EmptyPotStockRules.ValidateAddStock("4 inch", 5, Today.AddDays(-30), Today).Ok);
            Assert.Equal("Date is required.", EmptyPotStockRules.ValidateAddStock("4 inch", 5, default, Today).Error);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void AddStock_MissingPotSize_IsRejected(string? potSize)
            => Assert.Equal("Pot Size is required.", EmptyPotStockRules.ValidateAddStock(potSize, 5, Today, Today).Error);

        [Fact]
        public void StockArea_MainOfficeInactiveOrMissing_IsNotAllowed()
        {
            Assert.True(EmptyPotStockRules.IsAllowedStockArea(true, "MotherPlant", true));
            Assert.True(EmptyPotStockRules.IsAllowedStockArea(true, "Outlet", true));
            Assert.False(EmptyPotStockRules.IsAllowedStockArea(true, "MainOffice", true));
            Assert.False(EmptyPotStockRules.IsAllowedStockArea(true, "MotherPlant", false));
            Assert.False(EmptyPotStockRules.IsAllowedStockArea(false, null, false));
        }

        private static readonly Area MainOffice = new() { Id = 2, Name = "Main-Office-Areas", AreaType = "MainOffice", IsActive = true };
        private static readonly Area GreenBless = new() { Id = 127, Name = "Green Bless Nursery", AreaType = "MotherPlant", IsActive = true };
        private static readonly Area Tilekarwadi = new() { Id = 128, Name = "TIlekarwadi", AreaType = "MotherPlant", IsActive = true };
        private static readonly Area Closed = new() { Id = 999, Name = "Closed", AreaType = "MotherPlant", IsActive = false };
        private static readonly Area[] AllAreas = { MainOffice, GreenBless, Tilekarwadi, Closed };

        [Fact]
        public void AreaDropdown_Supervisor_SeesOnlyOwnActiveNonOfficeAreas()
        {
            Assert.Equal(new[] { 127 }, EmptyPotStockRules.SelectableAreas(AllAreas, false, new[] { 127 }).Select(a => a.Id));
            // even if a supervisor were assigned to the Main Office or a closed Area, neither is offered
            Assert.Equal(new[] { 127 }, EmptyPotStockRules.SelectableAreas(AllAreas, false, new[] { 2, 127, 999 }).Select(a => a.Id));
            Assert.Empty(EmptyPotStockRules.SelectableAreas(AllAreas, false, Array.Empty<int>()));
        }

        [Fact]
        public void AreaDropdown_FullAreaAccess_SeesEveryActiveNonOfficeArea()
            => Assert.Equal(new[] { 127, 128 }, EmptyPotStockRules.SelectableAreas(AllAreas, true, Array.Empty<int>()).Select(a => a.Id).OrderBy(i => i));

        // ---- Empty Pot: page map / menu / retired paths (source) ---------------------------------------------------

        [Fact]
        public void PageMap_AddStockIsMotherPlantEnter_PurchaseAndIssueAreFullAccessOnly()
        {
            Assert.Equal("MotherPlant.Enter", FeatureAuthorizationConventions.GetRule("/Production/EmptyPotInventory/AddStock").Read);
            Assert.Same(FeatureAuthorizationConventions.FullAccessOnlyRule, FeatureAuthorizationConventions.GetRule("/Production/EmptyPotInventory/Purchase"));
            Assert.Same(FeatureAuthorizationConventions.FullAccessOnlyRule, FeatureAuthorizationConventions.GetRule("/Production/EmptyPotInventory/Issue"));
            // viewing is unchanged
            Assert.Equal("PotProduction.View|Purchase.View|MotherPlant.View", FeatureAuthorizationConventions.GetRule("/Production/EmptyPotInventory/Index").Read);
            Assert.Equal("PotProduction.View|Purchase.View|MotherPlant.View", FeatureAuthorizationConventions.GetRule("/Production/EmptyPotInventory/Details").Read);
            // the permissions the retired pages used still drive their OTHER pages (nothing was removed from any role)
            Assert.Equal("Purchase.Enter", FeatureAuthorizationConventions.GetRule("/Production/PurchaseOrder/Create").Read);
            // 2026-10-19: + ReadyStock.DispatchToCustomer (the Customer destination); the transfer keeps its own policy (MoveModel.TransferPolicy)
            Assert.Equal("InternalTransfer.Enter|MainOffice.Confirm|ReadyStock.DispatchToCustomer", FeatureAuthorizationConventions.GetRule("/Production/ReadyStock/Move").Read);
        }

        // 2026-10-14: Add Empty Pots moved from Pot Production to the Mother Plant & Cutting section (and the
        // Mother Plants / Cutting Production pages); the Pot Production "Empty Pot Stock" page no longer offers it.
        [Fact]
        public void Menu_HasAddEmptyPots_InMotherPlantAndCutting_NotInPotProduction_AndNoLongerPurchaseOrIssue()
        {
            var layout = ReadSource("Pages", "Shared", "_Layout.cshtml");
            Assert.Contains("nav-page=\"/Production/EmptyPotInventory/AddStock\"", layout);
            Assert.Contains("nav-page=\"/Production/EmptyPotInventory/Index\"", layout);
            Assert.DoesNotContain("EmptyPotInventory/Purchase", layout);
            Assert.DoesNotContain("EmptyPotInventory/Issue", layout);
            var addAt = layout.IndexOf("nav-page=\"/Production/EmptyPotInventory/AddStock\"", StringComparison.Ordinal);
            Assert.Equal(addAt, layout.LastIndexOf("nav-page=\"/Production/EmptyPotInventory/AddStock\"", StringComparison.Ordinal));   // once
            Assert.True(addAt > layout.IndexOf(">Mother Plant &amp; Cutting<", StringComparison.Ordinal));
            Assert.True(addAt < layout.IndexOf("POT PRODUCTION", StringComparison.Ordinal));

            var index = ReadSource("Pages", "Production", "EmptyPotInventory", "Index.cshtml");
            Assert.DoesNotContain("/Production/EmptyPotInventory/AddStock", index);
            Assert.DoesNotContain("asp-page=\"/Production/EmptyPotInventory/Purchase\"", index);
        }

        [Fact]
        public void AddStockPage_ChecksTheAreaOnTheServer_AndUsesTheLockedLedgerPath()
        {
            var page = ReadSource("Pages", "Production", "EmptyPotInventory", "AddStock.cshtml.cs");
            Assert.Contains("Areas.FirstOrDefault(a => a.Id == AreaId.Value)", page);        // only an Area the page itself offers
            Assert.Contains("CanAccessRequiredArea(User, AreaId)", page);
            Assert.Contains("_tokens.TryConsume", page);                                       // no double add

            var repo = ReadSource("Data", "EmptyPotInventoryRepository.cs");
            var add = repo[repo.IndexOf("AddAreaStockAsync(", StringComparison.Ordinal)..];
            Assert.Contains("GetOrCreateLockedAsync(conn, tx", add);
            Assert.Contains("RecordTransactionAsync(", add);
            Assert.Contains("EmptyPotStockRules.IsAllowedStockArea", add);
        }

        [Fact]
        public void PurchaseOrder_EmptyPotPath_NoLongerTouchesEmptyPotStock()
        {
            var repo = ReadSource("Data", "PurchaseOrderRepository.cs");
            Assert.DoesNotContain("RecordTransactionAsync", repo);
            Assert.DoesNotContain("EmptyPotInventoryRepository", repo);
            Assert.Contains("EmptyPotPurchaseOrderRetiredMessage", repo);
        }

        // ---- Empty Pot: real HTTP authorization (forged requests) --------------------------------------------------

        private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? permissions)
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var request = new HttpRequestMessage(method, url);
            if (method == HttpMethod.Post)
                request.Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("AreaId", "2") });
            if (permissions != null)
                request.Headers.Add(DeleteAuthorizationTests.HeaderAuthHandler.Header, permissions);
            return await client.SendAsync(request);
        }

        private static void AssertAccessDenied(HttpResponseMessage response)
        {
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/Account/AccessDenied", response.Headers.Location!.ToString());
        }

        // Main Office Store Keeper (Purchase.Enter, InternalTransfer.Enter, ...), Purchase Officer, Mother Plant
        // Supervisor (InternalTransfer.Enter, MotherPlant.Enter) and Pot Production Operator: none may open or post the
        // retired pages any more.
        [Theory]
        [InlineData("GET", "/Production/EmptyPotInventory/Purchase", "Purchase.Enter,Purchase.View,InternalTransfer.Enter,InternalTransfer.View,PotProduction.View")]
        [InlineData("POST", "/Production/EmptyPotInventory/Purchase", "Purchase.Enter,Purchase.View,InternalTransfer.Enter,InternalTransfer.View,PotProduction.View")]
        [InlineData("POST", "/Production/EmptyPotInventory/Purchase", "Purchase.Enter,Purchase.View")]
        [InlineData("GET", "/Production/EmptyPotInventory/Issue", "Purchase.Enter,Purchase.View,InternalTransfer.Enter,InternalTransfer.View,PotProduction.View")]
        [InlineData("POST", "/Production/EmptyPotInventory/Issue", "Purchase.Enter,Purchase.View,InternalTransfer.Enter,InternalTransfer.View,PotProduction.View")]
        [InlineData("POST", "/Production/EmptyPotInventory/Issue", "MotherPlant.Enter,MotherPlant.View,InternalTransfer.Enter,InternalTransfer.View,PotProduction.View")]
        [InlineData("POST", "/Production/EmptyPotInventory/Issue", "PotProduction.Enter,PotProduction.View,InternalTransfer.Enter,InternalTransfer.View")]
        public async Task RetiredPurchaseAndIssue_NormalRoles_AreDenied(string method, string url, string permissions)
            => AssertAccessDenied(await SendAsync(new HttpMethod(method), url, permissions));

        [Theory]
        [InlineData("/Production/EmptyPotInventory/Issue")]
        [InlineData("/Production/EmptyPotInventory/Purchase")]
        public async Task RetiredPurchaseAndIssue_SystemAdministrator_IsStillAuthorized(string url)
            // authorized; the POST then stops only at the missing anti-forgery token (no handler or database ran)
            => Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, url, "FULL")).StatusCode);

        [Theory]
        [InlineData("GET", "Purchase.Enter,Purchase.View,InternalTransfer.Enter,InternalTransfer.View,PotProduction.View")]   // Store Keeper
        [InlineData("POST", "Purchase.Enter,Purchase.View,InternalTransfer.Enter,InternalTransfer.View,PotProduction.View")]
        [InlineData("POST", "PotProduction.Enter,PotProduction.View,InternalTransfer.Enter,InternalTransfer.View")]           // Pot Production Operator
        [InlineData("POST", "Booking.Enter,Booking.View")]
        public async Task AddStock_WithoutMotherPlantEnter_IsDenied(string method, string permissions)
            => AssertAccessDenied(await SendAsync(new HttpMethod(method), "/Production/EmptyPotInventory/AddStock", permissions));

        [Theory]
        [InlineData("MotherPlant.Enter,MotherPlant.View,InternalTransfer.Enter,InternalTransfer.View,PotProduction.View")]
        [InlineData("FULL")]
        public async Task AddStock_MotherPlantSupervisorOrAdmin_IsAuthorized(string permissions)
            => Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/Production/EmptyPotInventory/AddStock", permissions)).StatusCode);

        // ---- Bookings: ownership service --------------------------------------------------------------------------

        private static BookingOwnershipService Ownership(params int[] restricted)
            => new(Options.Create(new SecurityOptions { OwnBookingsOnlyUserIds = restricted }));

        private static ClaimsPrincipal User(int id, bool fullAccess = false)
        {
            var claims = new List<Claim> { new(ClaimTypes.Name, "u" + id), new("UserId", id.ToString()) };
            if (fullAccess) claims.Add(new Claim(ClaimsPrincipalSecurityExtensions.FullAccessClaimType, "true"));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        }

        [Fact]
        public void Ownership_ListedUsers_AreRestrictedToTheirOwnId()
        {
            var svc = Ownership(14, 15);
            Assert.Equal(14, svc.OwnerFilter(User(14)));
            Assert.Equal(15, svc.OwnerFilter(User(15)));
            Assert.True(svc.CanAccessBooking(User(14), 14));
            Assert.False(svc.CanAccessBooking(User(14), 15));
            Assert.False(svc.CanAccessBooking(User(15), 14));
            Assert.False(svc.CanAccessBooking(User(14), 3));
            Assert.False(svc.CanAccessBooking(User(14), null));     // a booking with no BookedById is nobody's own
        }

        [Fact]
        public void Ownership_OtherUsers_AdminsAndAnonymous_AreNotRestricted()
        {
            var svc = Ownership(14, 15);
            Assert.Null(svc.OwnerFilter(User(3)));                          // another Booking Executive: everything
            Assert.True(svc.CanAccessBooking(User(3), 14));
            Assert.True(svc.CanAccessBooking(User(3), 15));
            Assert.Null(svc.OwnerFilter(User(14, fullAccess: true)));       // a full-access user is never restricted, even if listed
            Assert.Null(svc.OwnerFilter(new ClaimsPrincipal(new ClaimsIdentity())));
        }

        [Fact]
        public void Ownership_NoConfiguration_RestrictsNobody()
        {
            Assert.Null(new BookingOwnershipService(Options.Create(new SecurityOptions())).OwnerFilter(User(14)));
            Assert.Null(Ownership(0, -1).OwnerFilter(User(0)));
        }

        // ---- Bookings: every handler that takes a booking Id checks ownership (source) ----------------------------

        [Theory]
        [InlineData("BookingDetails.cshtml.cs", "OnPostReserveAsync", "OnPostReleaseAllAsync", "OnPostCancelAsync")]
        [InlineData("SeedlingDispatch.cshtml.cs", "OnPostDispatchAsync", "OnPostAllocateAsync", "OnPostReleaseAsync")]
        public void BookingIdPostHandlers_RejectBeforeAnyOperation(string file, params string[] handlers)
        {
            var src = ReadSource("Pages", "Bookings", file);
            Assert.Contains("_ownership.CanAccessBookingIdAsync(User, Id, _repo)", src);
            Assert.Contains("_ownership.CanAccessBooking(User, Booking.OrderBookedById)", src);   // the GET (the ORDER's owner)
            foreach (var h in handlers)
            {
                var body = src[src.IndexOf(h, StringComparison.Ordinal)..];
                body = body[..body.IndexOf("\n        }", StringComparison.Ordinal)];
                var check = body.IndexOf("RejectIfNotOwnedAsync()", StringComparison.Ordinal);
                Assert.True(check > 0, $"{file} {h} must check ownership");
                Assert.True(body.IndexOf("_repo.", StringComparison.Ordinal) < 0 || body.IndexOf("_repo.", StringComparison.Ordinal) > check,
                    $"{file} {h} must check ownership before touching the booking");
            }
        }

        [Fact]
        public void ReviseAndEditRecords_CheckOwnership_AndKeepTheOwner()
        {
            var revise = ReadSource("Pages", "Bookings", "Revise.cshtml.cs");
            Assert.Contains("_ownership.CanAccessBooking(User, Booking.OrderBookedById)", revise);
            Assert.True(revise.IndexOf("CanAccessBookingIdAsync", StringComparison.Ordinal) < revise.IndexOf("_repo.ReviseAsync", StringComparison.Ordinal));

            var edit = ReadSource("Pages", "Bookings", "EditBookingRecords.cshtml.cs");
            Assert.True(edit.IndexOf("CanAccessBookingIdAsync(User, Booking.Id", StringComparison.Ordinal) < edit.IndexOf("UpdateBooking(", StringComparison.Ordinal));
            Assert.True(edit.IndexOf("CanAccessBookingIdAsync(User, id", StringComparison.Ordinal) < edit.IndexOf("_fulfilmentRepo.CancelAsync", StringComparison.Ordinal));
            Assert.Contains("Booking.BookedById = ownerId;", edit);

            var book = ReadSource("Pages", "Bookings", "Book.cshtml.cs");
            Assert.Contains("Booking.BookedById = ownerId;", book);
        }

        [Fact]
        public void BookingLists_PassTheOwnerFilterToSql()
        {
            Assert.Contains("ListBookingsAsync(status, Search, From, To, null, owner)", ReadSource("Pages", "Bookings", "Fulfilment.cshtml.cs"));
            Assert.Contains("GetDispatchRegisterAsync(From.Value, To.Value, Search, owner)", ReadSource("Pages", "Bookings", "DispatchRegister.cshtml.cs"));
            var records = ReadSource("Pages", "Data", "BookingsRecord.cshtml.cs");
            Assert.Contains("userId, bookedBy, owner)", records);
            Assert.Contains("_ownership.OwnerFilter(User));", records);                     // the PDF
            Assert.Contains("GetBookedByOptionsAsync(owner)", records);
            Assert.Contains("userId, bookedBy, owner)", ReadSource("Pages", "Bookings", "EditBookingRecords.cshtml.cs"));

            var repo = ReadSource("Data", "SeedlingFulfilmentRepository.cs");
            // the owner of the ORDER (its first line), never a single line
            Assert.Contains("AND (@Owner IS NULL OR ord.BookedById = @Owner)", repo);
            Assert.Contains("oo.BookedById = @Owner", repo);
            Assert.Contains("AND (@Owner IS NULL OR ord.BookedById = @Owner)", ReadSource("Data", "BookingRepository.cs"));
        }

        [Fact]
        public void PottedPlantBookings_AreNotRestricted()
        {
            Assert.DoesNotContain("BookingOwnershipService", ReadSource("Data", "PottedPlantBookingRepository.cs"));
            foreach (var f in Directory.GetFiles(Path.GetDirectoryName(Path.Combine(RepoRoot(), "Pages", "Production", "PottedPlantBooking", "x"))!, "*.cs"))
                Assert.DoesNotContain("BookingOwnershipService", File.ReadAllText(f));
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.sln")))
                dir = dir.Parent;
            return dir!.FullName;
        }
    }
}
