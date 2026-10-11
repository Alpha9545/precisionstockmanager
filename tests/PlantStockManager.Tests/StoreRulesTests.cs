using System.IO;
using System.Security.Claims;
using PlantStockManager.Authorization;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Inventory Store (2026-10-06) -- pure rules, authorization and wiring: quantities, settlement
    // arithmetic, line outcomes, holdings, who may do what (the Store Keeper's central reach stays
    // inside the Store module). The database behaviour (ledger, locking, concurrency) is StoreE2ETests.
    public class StoreRulesTests
    {
        private static string ReadSource(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
        }

        // ---- quantities -------------------------------------------------------------------------

        [Theory]
        [InlineData(20.0, "Consumable", false, null)]
        [InlineData(2.5, "Consumable", false, null)]
        [InlineData(0.125, "Consumable", false, null)]
        [InlineData(0.0, "Consumable", true, null)]
        [InlineData(0.0, "Consumable", false, "X: quantity must be greater than 0.")]
        [InlineData(-1.0, "Consumable", true, "X: quantity must be 0 or more.")]
        [InlineData(1.2345, "Consumable", false, "X: at most 3 decimal places.")]
        [InlineData(1.5, "Returnable", false, "X: a returnable item is counted in whole units.")]
        [InlineData(2.0, "Returnable", false, null)]
        [InlineData(2000000.0, "Consumable", false, "X: quantity is too large.")]
        public void ValidateQuantity(double qty, string type, bool allowZero, string? error)
            => Assert.Equal(error, StoreRules.ValidateQuantity((decimal)qty, "X", type, allowZero));

        [Fact]
        public void Quantity_IsRequired() => Assert.Equal("X: quantity is required.", StoreRules.ValidateQuantity(null, "X", "Consumable"));

        // ---- settlement -------------------------------------------------------------------------

        [Fact]
        public void Settlement_ApprovedCableExample_60Consumed40Returned()
            => Assert.Empty(StoreRules.ValidateSettlement("Cable", "Consumable", 100, 60, 0, 40));

        [Fact]
        public void Settlement_MustAccountForEveryIssuedUnitExactlyOnce()
        {
            Assert.Equal(new[] { "Cable: consumed + kept + returned must equal the 100 issued (now 90)." }, StoreRules.ValidateSettlement("Cable", "Consumable", 100, 60, 0, 30));
            Assert.Equal(new[] { "Cable: consumed + kept + returned must equal the 100 issued (now 110)." }, StoreRules.ValidateSettlement("Cable", "Consumable", 100, 60, 10, 40));
            Assert.Contains("Cable consumed: quantity must be 0 or more.", StoreRules.ValidateSettlement("Cable", "Consumable", 100, -1, 61, 40));
        }

        [Fact]
        public void Settlement_ReturnableItem_IsReturnedOrKept_NeverConsumed()
        {
            Assert.Empty(StoreRules.ValidateSettlement("Drill", "Returnable", 1, 0, 0, 1));
            Assert.Empty(StoreRules.ValidateSettlement("Pump", "Returnable", 1, 0, 1, 0));
            Assert.Contains("Drill: a returnable item cannot be consumed -- it is either returned or kept.", StoreRules.ValidateSettlement("Drill", "Returnable", 1, 1, 0, 0));
            Assert.Contains("Drill returned: a returnable item is counted in whole units.", StoreRules.ValidateSettlement("Drill", "Returnable", 2, 0, 1.5m, 0.5m));
        }

        // ---- line outcomes and holdings --------------------------------------------------------

        [Fact]
        public void LineOutcome_FollowsTheWorkflow()
        {
            var l = new StoreRequestLine { RequestedQty = 100 };
            Assert.Equal("Awaiting review", l.Outcome);
            l.ApprovedQty = 0;
            Assert.Equal("Rejected", l.Outcome);
            l.ApprovedQty = 60;
            Assert.Equal("Approved", l.Outcome);
            l.IssuedQty = 60;
            Assert.True(l.NeedsSettlement);
            Assert.Equal("Issued -- to settle", l.Outcome);
            l.SettledAt = DateTime.UtcNow; l.ConsumedQty = 36; l.ReturnDeclaredQty = 24;
            Assert.Equal("Return to confirm", l.Outcome);
            l.ReturnDeclaredQty = 0; l.ReturnedQty = 24;
            Assert.Equal("Settled", l.Outcome);
        }

        [Fact]
        public void Holding_AtLocation_IsOutOnIssue_PendingReturn_AndAssigned_NotConsumed()
        {
            var h = new StoreHolding { OutOnIssue = 5, PendingReturn = 24, Kept = 1, Transferred = 2, Consumed = 36 };
            Assert.Equal(3, h.Assigned);
            Assert.Equal(32, h.AtLocation);
        }

        [Theory]
        [InlineData("Pending", true)]
        [InlineData("Approved", true)]
        [InlineData("Issued", false)]
        [InlineData("Closed", false)]
        [InlineData("Rejected", false)]
        [InlineData("Cancelled", false)]
        public void Cancel_OnlyBeforeIssue(string status, bool allowed) => Assert.Equal(allowed, StoreRules.CanCancel(status));

        // ---- authorization -----------------------------------------------------------------------

        private static ClaimsPrincipal User(int id, string[] permissions, int[] areas, bool fullAccess = false)
        {
            var claims = new List<Claim> { new("UserId", id.ToString()) };
            claims.AddRange(permissions.Select(p => new Claim(MinimumAuthorizationLevelHandler.PermissionClaimType, p)));
            claims.AddRange(areas.Select(a => new Claim(AreaAccessService.AreaAccessClaimType, a.ToString())));
            if (fullAccess) claims.Add(new Claim(ClaimsPrincipalSecurityExtensions.FullAccessClaimType, "true"));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        }

        private static readonly ClaimsPrincipal StoreKeeper = User(11, new[] { "Store.Manage" }, new[] { 2 });
        private static readonly ClaimsPrincipal MpSup = User(110, new[] { "Store.Request", "Store.View" }, new[] { 127 });
        private static readonly ClaimsPrincipal SowingSup = User(6, new[] { "Store.Request", "Store.View" }, new[] { 2 });
        private static readonly ClaimsPrincipal Admin = User(21, Array.Empty<string>(), Array.Empty<int>(), fullAccess: true);
        private static readonly ClaimsPrincipal BookingExec = User(15, new[] { "Booking.Enter" }, new[] { 2 });

        private static StoreRequest Req(string status, int area = 127, int requestedBy = 110) => new() { AreaId = area, Status = status, RequestedById = requestedBy };

        [Fact]
        public void StoreKeeper_ManagesEveryAreasRequests_OnlyInsideTheStore()
        {
            var access = new AreaAccessService();
            var auth = new StoreAuthorization(access);
            Assert.Null(auth.AreaScope(StoreKeeper));                       // every Area for store lists
            Assert.True(auth.CanView(StoreKeeper, Req("Pending", 127)));
            Assert.True(auth.CanReview(StoreKeeper, Req("Pending", 127)));
            Assert.True(auth.CanIssue(StoreKeeper, Req("Approved", 127)));
            Assert.True(auth.CanConfirmReturn(StoreKeeper, Req("Issued", 127)));
            // ... but AreaAccessService (every other module) still limits him to his own Area.
            Assert.False(access.CanAccessArea(StoreKeeper, 127));
            Assert.False(access.HasFullAreaAccess(StoreKeeper));
            // He does not settle for an Area (that is the Area's declaration) nor request for it.
            Assert.False(auth.CanSettle(StoreKeeper, Req("Issued", 127)));
            Assert.False(auth.CanRequestFor(StoreKeeper, 127));
        }

        [Fact]
        public void Supervisors_OwnAreasOnly()
        {
            var auth = new StoreAuthorization(new AreaAccessService());
            Assert.Equal(new[] { 127 }, auth.AreaScope(MpSup));
            Assert.True(auth.CanRequestFor(MpSup, 127));
            Assert.False(auth.CanRequestFor(MpSup, 2));
            Assert.True(auth.CanView(MpSup, Req("Pending", 127)));
            Assert.False(auth.CanView(SowingSup, Req("Pending", 127)));
            Assert.True(auth.CanSettle(MpSup, Req("Issued", 127)));
            Assert.False(auth.CanSettle(SowingSup, Req("Issued", 127)));
            Assert.False(auth.CanReview(MpSup, Req("Pending", 127)));
            Assert.False(auth.CanIssue(MpSup, Req("Approved", 127)));
            Assert.False(auth.CanSettle(MpSup, Req("Approved", 127)));    // nothing issued yet
            Assert.False(auth.CanView(BookingExec, Req("Pending", 2)));
            Assert.True(auth.CanView(Admin, Req("Pending", 127)));
            Assert.True(auth.CanReview(Admin, Req("Pending", 127)));
        }

        [Fact]
        public void Cancel_RequesterOrStoreKeeper_BeforeIssue()
        {
            var auth = new StoreAuthorization(new AreaAccessService());
            Assert.True(auth.CanCancel(MpSup, Req("Pending", 127, requestedBy: 110)));
            Assert.True(auth.CanCancel(MpSup, Req("Approved", 127, requestedBy: 110)));
            Assert.False(auth.CanCancel(MpSup, Req("Issued", 127, requestedBy: 110)));
            var otherSameArea = User(111, new[] { "Store.Request" }, new[] { 127 });
            Assert.False(auth.CanCancel(otherSameArea, Req("Pending", 127, requestedBy: 110)));
            Assert.True(auth.CanCancel(StoreKeeper, Req("Approved", 127, requestedBy: 110)));
        }

        // ---- wiring ------------------------------------------------------------------------------

        [Fact]
        public void Pages_AreMappedToStorePermissions_AndInTheMenu()
        {
            Assert.Equal("Store.Manage", FeatureAuthorizationConventions.GetRule("/Store/StockEntry").Read);
            Assert.Equal("Store.Manage", FeatureAuthorizationConventions.GetRule("/Store/Issue").Read);
            Assert.Equal("Store.Request", FeatureAuthorizationConventions.GetRule("/Store/NewRequest").Read);
            Assert.Equal("Store.Manage", FeatureAuthorizationConventions.GetRule("/Store/Items").Write);       // only the Store Keeper changes the master
            Assert.Equal("Store.Manage", FeatureAuthorizationConventions.GetRule("/Store/Transfers").Write);
            Assert.Contains("Store.View", FeatureAuthorizationConventions.GetRule("/Store/Reports").Read);
            var layout = ReadSource("Pages", "Shared", "_Layout.cshtml");
            foreach (var p in new[] { "Index", "Items", "StockEntry", "Requests", "Issue", "Returns", "Transfers", "Reports" })
                Assert.Contains($"nav-page=\"/Store/{p}\"", layout);
        }

        [Fact]
        public void EveryRequestDetailsActionForm_PostsTheRequestId()
        {
            var page = ReadSource("Pages", "Store", "RequestDetails.cshtml");
            var handlers = System.Text.RegularExpressions.Regex.Matches(page, "asp-page-handler=\"[A-Za-z]+\"( asp-route-id=\"@r.Id\")?");
            Assert.Equal(5, handlers.Count);
            Assert.All(handlers, m => Assert.True(m.Groups[1].Success, m.Value));
        }

        [Fact]
        public void Migration_IsGuarded_LeavesLegacyInventoryAlone_AndEnforcesTheLedger()
        {
            var sql = ReadSource("Database", "Migrations", "2026-10-06_StoreInventory.sql");
            Assert.Contains("NOT LIKE N'PlantsIMS2[_]Scratch[_]%'", sql);
            Assert.Contains("@@SERVERNAME <> N'$(TARGET_SERVER)'", sql);
            Assert.Contains("N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')", sql);
            foreach (var legacy in new[] { "ALTER TABLE dbo.Inventory", "INSERT INTO dbo.Inventory", "UPDATE dbo.Inventory", "DELETE FROM dbo.Inventory", "DROP TABLE dbo.Inventory",
                                           "ALTER TABLE dbo.InventoryTransactions", "INSERT INTO dbo.InventoryTransactions", "DELETE FROM dbo.InventoryTransactions" })
                Assert.DoesNotContain(legacy, sql);
            Assert.Contains("REFERENCES dbo.UnitMaster(UnitId)", sql);   // the existing unit master, not a copy
            Assert.Contains("REFERENCES dbo.Vendors(Id)", sql);
            Assert.Contains("CHECK (AvailableQuantity >= 0)", sql);
            Assert.Contains("BalanceAfter = BalanceBefore + Quantity", sql);
            Assert.Contains("IssuedQty <= ISNULL(ApprovedQty, 0)", sql);
            Assert.Contains("ItemType <> 'Returnable' OR ConsumedQty = 0", sql);
            Assert.Contains("ISNULL(CAST(SESSION_CONTEXT(N''StoreTestCleanup'') AS INT), 0) = 1", sql);   // the NULL fix
            Assert.Contains("THROW 51817", sql);   // stock only through the ledger
            var rollback = ReadSource("Database", "Migrations", "2026-10-06_StoreInventory_Rollback.sql");
            Assert.Contains("THROW 51824", rollback);   // refuses while store data exists
            Assert.DoesNotContain("DROP TABLE dbo.Inventory", rollback);
        }

        [Fact]
        public void StoreRecords_BlockAreaDeletion()
        {
            Assert.Contains(DeletionRules.AreaDependencies, d => d.Table == "StoreRequests" && d.Column == "AreaId");
            Assert.Contains(DeletionRules.AreaDependencies, d => d.Table == "StoreTransactions" && d.Column == "AreaId");
        }
    }
}
