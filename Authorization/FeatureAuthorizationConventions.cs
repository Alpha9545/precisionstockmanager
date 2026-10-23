using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Filters;

namespace PlantStockManager.Authorization
{
    // ========================================================================
    // Phase A: THE central page -> permission map.
    //
    // Every Razor Page is listed here exactly once with:
    //   Read  -- the permission policy required to open the page at all
    //            (every HTTP verb). A policy is a dbo.Permissions.Code or an
    //            any-of list "A|B" (see PermissionPolicy).
    //   Write -- OPTIONAL extra policy required for state-changing requests
    //            (POST/PUT/PATCH/DELETE) on pages that mix viewing and editing
    //            (e.g. a stock list with an "add" form).
    //
    // Enforcement (server-side, independent of the menu):
    //   * FeatureAuthorizationPageConvention (below) turns Read into endpoint
    //     [Authorize(Policy)] metadata and Write into WritePermissionPageFilter.
    //   * A page that is NOT in this map is "FullAccessOnly": only a
    //     full-access user (System Administrator) can open it. New pages can
    //     therefore never become visible to everyone by accident.
    //   * The navigation (NavAuthorizationTagHelper) evaluates the SAME rule,
    //     so a menu item is shown exactly when the server would allow it.
    //
    // Area scope is NOT decided here -- AreaAccessService still restricts
    // which physical Areas' records a permitted user can see or change.
    // ========================================================================
    public sealed record FeatureRule(string Read, string? Write = null)
    {
        public bool IsAnonymous => Read == FeatureAuthorizationConventions.Anonymous;
        public bool IsAuthenticatedOnly => Read == FeatureAuthorizationConventions.AuthenticatedOnly;
    }

    public static class FeatureAuthorizationConventions
    {
        public const string Anonymous = "@anonymous";
        public const string AuthenticatedOnly = "@authenticated";

        public static readonly FeatureRule FullAccessOnlyRule = new(PermissionPolicy.FullAccessOnly);

        private static FeatureRule R(string read, string? write = null) => new(read, write);

        public static readonly IReadOnlyDictionary<string, FeatureRule> Rules =
            new Dictionary<string, FeatureRule>(StringComparer.OrdinalIgnoreCase)
            {
                // ---- Public / account --------------------------------------
                ["/Account/Login"] = R(Anonymous),
                ["/Account/Logout"] = R(Anonymous),
                ["/Account/AccessDenied"] = R(Anonymous),
                ["/Error"] = R(Anonymous),
                ["/Privacy"] = R(AuthenticatedOnly),
                ["/Account/AddUser"] = R("Admin.ManageUsers"),

                // ---- Dashboard ---------------------------------------------
                ["/Index"] = R("Dashboard.View"),
                ["/ManagementDashboard/Index"] = R("Admin.ManageAreas"),   // unchanged: Phase L administrator-only decision
                // Daily Operations Report: System Administrator (full access) ONLY -- no
                // permission code grants it, so no ordinary role can ever be given it.
                ["/ManagementDashboard/DailyOperationsReport"] = FullAccessOnlyRule,

                // ---- Administration ----------------------------------------
                ["/Admin/Users"] = R("Admin.ManageUsers"),
                ["/Admin/UserRoles"] = R("Admin.ManageUsers"),
                ["/Admin/Roles"] = R("Admin.ManageRoles"),
                ["/Admin/Area"] = R("Admin.ManageAreas"),
                ["/Admin/Polyhouse"] = R("Admin.ManageAreas"),
                ["/Admin/Plant"] = R("Admin.ManageMasters"),
                ["/Admin/PotSizes"] = R("Admin.ManageMasters"),                          // Phase D: pot-size master
                ["/Admin/Seedsource"] = R("Admin.ManageMasters"),
                ["/Admin/Vendor"] = R("Purchase.View", "Purchase.Enter"),

                // ---- Seed stock (Main Office) ------------------------------
                ["/Production/SeedStock/Index"] = R("SeedStock.View"),
                ["/Production/SeedStock/Details"] = R("SeedStock.View"),
                ["/Production/SeedStock/Create"] = R("SeedStock.Enter"),
                ["/Production/SeedStock/AddStock"] = R("SeedStock.Enter"),
                ["/Production/SeedStock/AddSeedStock"] = R("SeedStock.Enter"),   // simplified single-page receipt form
                // 2026-10-20: correcting a receipt (date / quantity / notes) -- System Administrator ONLY, every verb and
                // handler; appended to the ledger as a reversal + corrected receipt (Services/SeedStockEditRules.cs).
                ["/Production/SeedStock/Edit"] = FullAccessOnlyRule,

                // ---- Sowing / Ready stock ----------------------------------
                ["/Production/SeedSowing/Index"] = R("Sowing.View"),
                ["/Production/SeedSowing/Details"] = R("Sowing.View"),
                ["/Production/SeedSowing/Create"] = R("Sowing.Enter"),
                ["/Production/SeedSowing/Edit"] = R("Sowing.Enter"),
                ["/Production/SeedSowing/CreateFromCutting"] = R("Sowing.Enter"),        // Phase D: cutting tray sowing
                // 2026-10-12 Bed Sowing: recorded like a sowing (Sowing.Enter); transplanting bed
                // seedlings into trays / bed losses / reversals ONLY with BedSowing.Transplant (the
                // Bed Sowing Transplanter) -- and BedSowingRepository re-checks that the user holds it
                // for the bed's Area. Viewing: either.
                ["/Production/BedSowing/Index"] = R("Sowing.View|" + Services.BedSowingRules.TransplantPermission),
                ["/Production/BedSowing/Details"] = R("Sowing.View|" + Services.BedSowingRules.TransplantPermission),
                ["/Production/BedSowing/History"] = R("Sowing.View|" + Services.BedSowingRules.TransplantPermission),
                ["/Production/BedSowing/Create"] = R("Sowing.Enter"),
                // 2026-10-22: edit an untouched bed (same permission as Create / Cancel; Area checked in the page and repository)
                ["/Production/BedSowing/Edit"] = R("Sowing.Enter"),
                ["/Production/BedSowing/Transplant"] = R(Services.BedSowingRules.TransplantPermission),
                ["/Production/ReadyAlerts/Index"] = R("ReadyStock.View|Sowing.View"),
                ["/Production/ReadyConfirmation/Index"] = R("ReadyStock.View"),
                ["/Production/ReadyConfirmation/History"] = R("ReadyStock.View", "ReadyStock.Confirm"),
                ["/Production/ReadyConfirmation/Confirm"] = R("ReadyStock.Confirm"),   // Phase B: Supervisor Approval
                ["/Production/ReadyStock/Index"] = R("ReadyStock.View"),               // Phase B: approved Ready Stock
                // Phase D: send trays to Main Office/Outlet; 2026-10-19: or dispatch them to a customer (ReadyStock.DispatchToCustomer --
                // Office Coordinator, 2026-10-23_ReadyStockDispatchToCustomer.sql). The page checks each destination's own permission.
                ["/Production/ReadyStock/Move"] = R("InternalTransfer.Enter|MainOffice.Confirm|ReadyStock.DispatchToCustomer"),
                // 2026-10-09: Main Area Ready Stock -> Mother Plant Area -> transplanting. Every handler
                // re-checks its own permission and the Area (SeedlingAreaScope); these are the page gates.
                ["/Production/ReadyStock/TransferToMotherPlant"] = R("ReadyStock.TransferToMotherPlant"),
                ["/Production/ReadyStock/Lineage"] = R("ReadyStock.View|Transplant.View"),
                ["/Production/MotherPlantTransfers/Index"] = R("ReadyStock.TransferToMotherPlant|ReadyStock.ReceiveTransfer",
                                                               "ReadyStock.TransferToMotherPlant|ReadyStock.ReceiveTransfer"),
                ["/Production/Transplanting/Index"] = R("Transplant.Enter"),
                ["/Production/Transplanting/Create"] = R("Transplant.Enter"),
                ["/Production/Transplanting/History"] = R("Transplant.View"),
                ["/Production/Transplanting/Details"] = R("Transplant.View", "Transplant.Reverse"),

                // ---- Mother plant / cutting --------------------------------
                ["/Production/MotherPlant/Index"] = R("MotherPlant.View"),
                // 2026-10-17: ready trays + ready pots of the user's own Mother Plant / Cutting Areas (read only; Area checked in the page)
                ["/Production/MotherPlant/ReadyStock"] = R("MotherPlant.View|PotProduction.View"),
                // 2026-10-18: direct tray sale to a customer -- the same permission as the potted-plant customer sale (Area checked in the page)
                ["/Production/MotherPlant/SellTrays"] = R("Outlet.Sell"),
                // 2026-10-19: Mother Plant Area transplant batches waiting for approval -- Mother Plant users (as Mother Plants);
                // read only; Area checked in the page; approving stays /Production/ReadyConfirmation/Confirm (ReadyStock.Confirm).
                // Everyone else with ReadyStock.View sees these batches under Seed Production > Sowing Approvals, in scope.
                ["/Production/MotherPlant/SowingApprovals"] = R("MotherPlant.View"),
                // 2026-10-21: Direct (seed) Sowing INTO a Mother Plant Area -- Mother Plant users for their own Areas (strict Area
                // rule, checked in the page and in SeedSowingRepository.InsertMotherPlantDirectAsync); approved under Sowing Approvals.
                ["/Production/MotherPlant/DirectSowing"] = R("MotherPlant.Enter"),
                ["/Production/MotherPlant/Details"] = R("MotherPlant.View"),
                ["/Production/MotherPlant/Create"] = R("MotherPlant.Enter"),
                ["/Production/MotherPlant/Edit"] = R("MotherPlant.Enter"),
                ["/Production/MotherPlant/Delete"] = R("MotherPlant.Enter"),   // dependency-aware delete / deactivate
                // Phase D: Mother Plant -> Cutting Production -> Delivery ->
                // Main Office confirmation -> Cutting Stock. (Cutting Plan /
                // Actual Cutting / Cutting Delivery / Propagation Batch and the
                // separate Transplant step are retired.)
                ["/Production/Cutting/Index"] = R("MotherPlant.View|MainOffice.View|PotProduction.View"),
                ["/Production/Cutting/Create"] = R("MotherPlant.Enter"),
                ["/Production/CuttingStock/Index"] = R("MotherPlant.View|MainOffice.View|PotProduction.View|Sowing.View|Kunjir.View|Kiran.View"),
                ["/Production/CuttingStock/GiveToMainOffice"] = R("MotherPlant.Enter|Kunjir.Enter|Kiran.Enter"),
                ["/Production/CuttingStock/MyTransactions"] = R("MotherPlant.View|Kunjir.View|Kiran.View|MainOffice.View"),
                ["/Production/CuttingStock/PendingConfirmations"] = R("MainOffice.View", "MainOffice.Confirm"),
                ["/Production/CuttingStock/ConfirmReceipt"] = R("MainOffice.Confirm"),

                // ---- Tray Stock: empty trays held per Area + Cavity
                // (2026-10-04 workflow change), consumed automatically by Seed/
                // Cutting Sowing in that Area. Adding trays is the Sowing
                // Supervisor's own action (TrayStock.Enter -- Sowing
                // Supervisor role only); the retired Main Office "Allocate
                // Trays" page no longer exists. Viewing is open to the same
                // roles as before, and every Tray Stock page is limited to the
                // user's own Areas (AreaAccessService).
                // 2026-10-08: the Tray Stock pages are now shown inside Material Stock;
                // these three old URLs only redirect there (same rules as before).
                ["/Production/TrayStock/Index"] = R(MaterialStockAccess.TrayViewOwnAreas),
                ["/Production/TrayStock/Add"] = R(MaterialStockAccess.TrayAdd),
                ["/Production/TrayStock/Transactions"] = R(MaterialStockAccess.TrayViewOwnAreas),
                // 2026-10-14: Add Empty Trays of the Mother Plant / Cutting sections -- a Mother Plant Area's own tray
                // pool, no polyhouse. The existing tray-adding permissions and Area rule (TrayAreaScope); nothing widened.
                ["/Production/MotherPlant/AddEmptyTrays"] = R(MaterialStockAccess.TrayAddAny),

                // ---- Material Stock (2026-10-08): Cocopeat / Pitmos per Main Office
                // Area + Polyhouse, used by sowings there. The Sowing Supervisor holds
                // View / Add / History; Manage (adjustments, conversion factors, kg per
                // tray) is held by no role -- full-access users only. Every page is
                // limited to the user's own Areas (AreaAccessService).
                // 2026-10-08 consolidation: Material Stock also shows / adds / reports
                // Tray Stock. Each page opens for EITHER domain's existing permission
                // and shows (and accepts) only the domain(s) the user holds -- see
                // MaterialStockAccess; no permission was widened.
                ["/Production/MaterialStock/Index"] = R(MaterialStockAccess.MaterialView + "|" + MaterialStockAccess.TrayView),
                ["/Production/MaterialStock/Add"] = R(MaterialStockAccess.MaterialAdd + "|" + MaterialStockAccess.TrayAddAny),
                ["/Production/MaterialStock/History"] = R(MaterialStockAccess.MaterialHistory + "|" + MaterialStockAccess.TrayView),
                ["/Production/MaterialStock/Settings"] = R("MaterialStock.Manage"),
                // 2026-10-10: empty-tray corrections and transfers (2026-10-10_TrayTransfers.sql).
                // Area rules are checked again on every POST and inside the repositories.
                ["/Production/MaterialStock/TrayAdjust"] = R(MaterialStockAccess.TrayAdjust),
                ["/Production/MaterialStock/TrayTransfers"] = R(MaterialStockAccess.TrayTransfersAny),
                // 2026-10-23: Cocopeat / Pitmos transfers (2026-10-25_MaterialStockTransfers.sql). History for either
                // permission; every POST needs MaterialStock.Transfer, and the source Area is checked again on save.
                ["/Production/MaterialStock/Transfers"] = R(MaterialStockAccess.MaterialTransfersAny, MaterialStockAccess.MaterialTransfer),

                // ---- Pot production (Phase D) ------------------------------
                ["/Production/PotBatch/Index"] = R("PotProduction.View|MotherPlant.View|Kiran.View"),
                ["/Production/PotBatch/Details"] = R("PotProduction.View|MotherPlant.View|Kiran.View", "PotProduction.Enter|MotherPlant.Enter|Kiran.Enter"),
                ["/Production/PotBatch/Create"] = R("PotProduction.Enter|MotherPlant.Enter|Kiran.Enter"),
                // 2026-10-15: the same batch flow from Ready Stock received for Pot Production (Area checked in the page)
                ["/Production/PotBatch/CreateFromReadyStock"] = R("PotProduction.Enter|MotherPlant.Enter|Kiran.Enter"),
                ["/Production/EmptyPotInventory/Index"] = R("PotProduction.View|Purchase.View|MotherPlant.View"),
                ["/Production/EmptyPotInventory/Details"] = R("PotProduction.View|Purchase.View|MotherPlant.View"),
                ["/Production/EmptyPotInventory/AddStock"] = R("MotherPlant.Enter"),      // Mother Plant Supervisor adds pots to own Area (Area checked in the page)
                // Retired Main Office workflow: System Administrator only (existing Office stock can still be issued).
                ["/Production/EmptyPotInventory/Purchase"] = FullAccessOnlyRule,
                ["/Production/EmptyPotInventory/Issue"] = FullAccessOnlyRule,
                ["/Production/PottedPlantStock/Index"] = R("PotProduction.View|Outlet.View|Booking.View|Dispatch.View|InternalTransfer.View|MotherPlant.View"),
                ["/Production/PottedPlantStock/Details"] = R("PotProduction.View|Outlet.View|Booking.View|Dispatch.View|InternalTransfer.View|MotherPlant.View"),
                ["/Production/PottedPlantStock/Move"] = R("InternalTransfer.Enter|MainOffice.Confirm|Outlet.Sell"),   // per destination: transfer vs Outlet sale (MoveModel)

                // ---- Stock movement register -------------------------------
                ["/Production/InternalTransfer/Index"] = R("InternalTransfer.View"),
                ["/Production/InternalTransfer/Details"] = R("InternalTransfer.View"),
                ["/Production/InternalTransfer/Edit"] = R("InternalTransfer.Enter"),

                // ---- Booking / dispatch / outlet ---------------------------
                ["/Production/PottedPlantBooking/Index"] = R("Booking.View|Outlet.View"),
                ["/Production/PottedPlantBooking/Details"] = R("Booking.View|Outlet.View"),
                ["/Production/PottedPlantBooking/Create"] = R("Booking.Enter|Outlet.Sell"),
                ["/Production/PottedPlantBooking/Edit"] = R("Booking.Enter|Outlet.Sell"),
                ["/Production/Dispatch/Index"] = R("Dispatch.View|Outlet.View"),
                ["/Production/Dispatch/Details"] = R("Dispatch.View|Outlet.View"),
                ["/Production/Dispatch/Create"] = R("Dispatch.Enter|Outlet.Sell"),
                ["/Production/Dispatch/Edit"] = R("Dispatch.Enter|Outlet.Sell"),

                // ---- Outlet (Phase E): direct purchase, multi-item sale/booking --
                ["/Production/OutletPurchase/Create"] = R("Outlet.Purchase"),
                ["/Production/OutletSale/Index"] = R("Outlet.View"),
                ["/Production/OutletSale/Details"] = R("Outlet.View"),
                ["/Production/OutletSale/Create"] = R("Outlet.Sell"),
                ["/Production/OutletBooking/Index"] = R("Outlet.View"),
                ["/Production/OutletBooking/Details"] = R("Outlet.View", "Outlet.Sell"),
                ["/Production/OutletBooking/Create"] = R("Outlet.Sell"),
                ["/Production/OutletWastage/Create"] = R("Outlet.Sell"),

                // Seedling booking (dbo.Bookings): New Booking / Edit Booking
                ["/Bookings/Book"] = R("Booking.Enter"),
                ["/Bookings/EditBookingRecords"] = R("Booking.Enter"),

                // Phase C: Ready Stock -> seedling booking reservation -> dispatch
                ["/Bookings/Fulfilment"] = R("Booking.View|Dispatch.View|Reports.View"),
                ["/Bookings/BookingDetails"] = R("Booking.View|Dispatch.View", "Booking.Enter"),   // reserve / release / cancel
                ["/Bookings/Revise"] = R("Booking.Enter"),
                ["/Bookings/SeedlingDispatch"] = R("Dispatch.View", "Dispatch.Enter"),            // allocate / substitute / dispatch
                ["/Bookings/DispatchRegister"] = R("Dispatch.View|Booking.View|Reports.View"),

                // ---- Purchase / lab / labour -------------------------------
                ["/Production/PurchaseOrder/Index"] = R("Purchase.View"),
                ["/Production/PurchaseOrder/Details"] = R("Purchase.View", "Purchase.Enter"),
                ["/Production/PurchaseOrder/Create"] = R("Purchase.Enter"),
                ["/Production/PurchaseOrder/Receive"] = R("Purchase.Enter"),
                ["/Production/LabRequest/Index"] = R("Lab.View"),
                ["/Production/LabRequest/Details"] = R("Lab.View", "Lab.Enter"),
                ["/Production/LabRequest/Create"] = R("Lab.Enter"),
                ["/Production/LabRequest/Result"] = R("Lab.Enter"),
                // Daily Labour Count: headcounts only; Area scope via AreaAccessService.
                // Since 2026-10-06 entered as 9:00 AM / 2:00 PM attendance; the manual
                // Full/Half Day pages remain for the older records (correction: full access only, in the page).
                // 2026-10-23: + Labour.Attendance -- daily attendance ONLY, in the user's own Areas (Outlet Sales,
                // 2026-10-26_LabourAttendanceOutlet.sql); it opens no overtime or labour-count page.
                ["/Production/Labour/Attendance"] = R("Labour.Enter|Labour.Attendance"),
                // 2026-10-20: Attendance History and Overtime History are REPORTS (menu: Reports) for the System
                // Administrator ONLY -- page, filters and exports alike. Recording is unchanged (Labour.Enter), and
                // OvertimeDetails stays Labour.View: saving / correcting an overtime entry lands there.
                ["/Production/Labour/AttendanceHistory"] = FullAccessOnlyRule,
                // Overtime (2026-10-06): same permissions and Area scope as Daily Attendance.
                ["/Production/Labour/Overtime"] = R("Labour.Enter"),
                ["/Production/Labour/OvertimeHistory"] = FullAccessOnlyRule,
                // 2026-10-20: My Overtime Entries -- a recorder's OWN entries only (CreatedById = the signed-in user,
                // in their Areas); Details / Edit re-check the same ownership (LabourOvertimeRules.MayAccessEntry).
                ["/Production/Labour/MyOvertime"] = R("Labour.Enter"),
                ["/Production/Labour/OvertimeDetails"] = R("Labour.View"),
                // ---- Maintenance tickets (2026-10-06) ---------------------------
                // Who may see a ticket / take each workflow step is decided per ticket by
                // MaintenanceAuthorization (Area scope, worker trade, assigned worker).
                ["/Maintenance/Raise"] = R("Maintenance.Raise"),
                ["/Maintenance/MyTickets"] = R("Maintenance.View|Maintenance.Raise"),
                ["/Maintenance/Assigned"] = R("Maintenance.Work"),
                ["/Maintenance/Details"] = R("Maintenance.View|Maintenance.Raise|Maintenance.Work|Maintenance.Manage"),
                ["/Maintenance/Reports"] = R("Maintenance.View|Maintenance.Manage"),
                // ---- Inventory Store (2026-10-06) -------------------------------
                // Area scope and each workflow step are decided by StoreAuthorization
                // (Store.Manage = the central store: every Area, inside the Store module only).
                ["/Store/Index"] = R("Store.View|Store.Request|Store.Manage"),
                ["/Store/Items"] = R("Store.View|Store.Request|Store.Manage", "Store.Manage"),
                ["/Store/StockEntry"] = R("Store.Manage"),
                ["/Store/Requests"] = R("Store.View|Store.Request|Store.Manage"),
                ["/Store/NewRequest"] = R("Store.Request"),
                ["/Store/RequestDetails"] = R("Store.View|Store.Request|Store.Manage"),
                ["/Store/Issue"] = R("Store.Manage"),
                ["/Store/Returns"] = R("Store.View|Store.Request|Store.Manage"),
                ["/Store/Transfers"] = R("Store.View|Store.Manage", "Store.Manage"),
                ["/Store/Reports"] = R("Store.View|Store.Manage"),
                // ---- Suggestions & Problems (2026-10-07) ------------------------
                // A GLOBAL module: every signed-in user may view, post, comment and reply
                // (SuggestionAuthorization.GlobalPermissions -- no Area restriction for THIS module
                // only). Lifetime and every action are decided by SuggestionAuthorization;
                // Suggestion.Manage (administrator) is still required for permanent / status /
                // delete / moderation, checked in the page handlers.
                ["/Suggestions/Index"] = R(AuthenticatedOnly),
                ["/Suggestions/Create"] = R(AuthenticatedOnly),
                ["/Suggestions/Details"] = R(AuthenticatedOnly),
                ["/Production/Labour/Index"] = R("Labour.View"),
                ["/Production/Labour/Entry"] = R("Labour.Enter"),

                // ---- Fertilizer --------------------------------------------
                ["/Fertilizer/Stock"] = R("Fertilizer.View", "Fertilizer.Enter"),
                ["/Fertilizer/Usage"] = R("Fertilizer.Enter"),
                ["/Fertilizer/View"] = R("Fertilizer.View"),
                ["/Fertilizer/Transactions"] = R("Fertilizer.View"),   // Correction #8: same permission as the Fertilizer Report
                ["/Fertilizer/Fertilizer"] = R("Fertilizer.View", "Fertilizer.Manage"),
                ["/Fertilizer/Type"] = R("Fertilizer.View", "Fertilizer.Manage"),
                ["/Fertilizer/UnitMaster"] = R("Fertilizer.View", "Fertilizer.Manage"),
                ["/Fertilizer/Source"] = R("Fertilizer.View", "Fertilizer.Manage"),

                // ---- Reports (/Data) ---------------------------------------
                ["/Data/BookingsRecord"] = R("Booking.View|Booking.Direct|Dispatch.View|Reports.View"),
                // Phase D: current-ledger reports.
                ["/Data/StockHistory"] = R("Reports.View|ReadyStock.View|SeedStock.View|PotProduction.View|MotherPlant.View|MainOffice.View"),
                ["/Data/WastedStock"] = R("Reports.View|ReadyStock.View|PotProduction.View|MotherPlant.View"),
                // Phase H (2026-09-30): Month-wise Sowing's replacement --
                // same permission codes as the retired route, over the new
                // dbo.SeedSowings table instead of dbo.SeedEntries.
                ["/Data/SeedSowingMonthlyReport"] = R("Sowing.View|Reports.View"),
            };

        // "/Admin/Users", "/admin/users/", "/Production/X/Index", "/" and
        // "/index" all resolve; unknown pages resolve to FullAccessOnly.
        public static FeatureRule GetRule(string? pagePath)
        {
            var key = Normalize(pagePath);
            return Rules.TryGetValue(key, out var rule) ? rule : FullAccessOnlyRule;
        }

        public static bool IsMapped(string? pagePath) => Rules.ContainsKey(Normalize(pagePath));

        public static string Normalize(string? pagePath)
        {
            var p = (pagePath ?? string.Empty).Trim();
            var q = p.IndexOfAny(new[] { '?', '#' });
            if (q >= 0) p = p[..q];
            p = "/" + p.Trim('/');
            return p == "/" ? "/Index" : p;
        }
    }

    // Applies the map to every discovered Razor Page at startup.
    public sealed class FeatureAuthorizationPageConvention : IPageApplicationModelConvention
    {
        private readonly ILogger? _logger;

        public FeatureAuthorizationPageConvention(ILogger? logger = null) => _logger = logger;

        public void Apply(PageApplicationModel model)
        {
            var path = model.ViewEnginePath;
            if (!FeatureAuthorizationConventions.IsMapped(path))
                _logger?.LogWarning("Page {Page} has no entry in FeatureAuthorizationConventions; it is restricted to full-access users.", path);

            var rule = FeatureAuthorizationConventions.GetRule(path);

            if (rule.IsAnonymous)
            {
                model.EndpointMetadata.Add(new AllowAnonymousAttribute());
                return;
            }

            if (rule.IsAuthenticatedOnly)
            {
                model.EndpointMetadata.Add(new AuthorizeAttribute());
                return;
            }

            model.EndpointMetadata.Add(new AuthorizeAttribute(rule.Read));

            if (!string.IsNullOrWhiteSpace(rule.Write))
                model.Filters.Add(new WritePermissionPageFilter(rule.Write));
        }
    }

    // Enforces a page's Write policy on state-changing requests. GET/HEAD
    // (viewing, AJAX dropdown lookups, PDF/Excel downloads) are governed by
    // the Read policy only.
    public sealed class WritePermissionPageFilter : IAsyncPageFilter
    {
        public string Policy { get; }

        public WritePermissionPageFilter(string policy) => Policy = policy;

        public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

        public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
        {
            var method = context.HttpContext.Request.Method;
            if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
            {
                await next();
                return;
            }

            var authorization = context.HttpContext.RequestServices.GetRequiredService<IAuthorizationService>();
            var result = await authorization.AuthorizeAsync(context.HttpContext.User, Policy);
            if (!result.Succeeded)
            {
                context.Result = context.HttpContext.User.Identity?.IsAuthenticated == true
                    ? new ForbidResult()
                    : new ChallengeResult();
                return;
            }

            await next();
        }
    }
}
