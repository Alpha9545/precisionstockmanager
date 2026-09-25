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
                ["/Production/Dashboard/Index"] = R("Reports.View"),

                // ---- Administration ----------------------------------------
                ["/Admin/Users"] = R("Admin.ManageUsers"),
                ["/Admin/Employee"] = R("Admin.ManageUsers"),
                ["/Admin/UserRoles"] = R("Admin.ManageUsers"),
                ["/Admin/Roles"] = R("Admin.ManageRoles"),
                ["/Admin/Area"] = R("Admin.ManageAreas"),
                ["/Admin/Polyhouse"] = R("Admin.ManageAreas"),
                ["/Admin/GrowingPartner"] = R("Admin.ManageAreas"),
                ["/Admin/Plant"] = R("Admin.ManageMasters"),
                ["/Admin/Seedsource"] = R("Admin.ManageMasters"),
                ["/Admin/Vendor"] = R("Purchase.View", "Purchase.Enter"),

                // ---- Seed stock (Main Office) ------------------------------
                ["/Production/SeedStock/Index"] = R("SeedStock.View"),
                ["/Production/SeedStock/Details"] = R("SeedStock.View"),
                ["/Production/SeedStock/Create"] = R("SeedStock.Enter"),
                ["/Production/SeedStock/AddStock"] = R("SeedStock.Enter"),

                // ---- Sowing / Ready stock ----------------------------------
                ["/Production/SeedSowing/Index"] = R("Sowing.View"),
                ["/Production/SeedSowing/Details"] = R("Sowing.View"),
                ["/Production/SeedSowing/Create"] = R("Sowing.Enter"),
                ["/Production/SeedSowing/Edit"] = R("Sowing.Enter"),
                ["/Production/ReadyAlerts/Index"] = R("ReadyStock.View|Sowing.View"),
                ["/Production/ReadyConfirmation/Index"] = R("ReadyStock.View"),
                ["/Production/ReadyConfirmation/History"] = R("ReadyStock.View", "ReadyStock.Confirm"),
                ["/Production/ReadyConfirmation/Confirm"] = R("ReadyStock.Confirm"),   // Phase B: Supervisor Approval
                ["/Production/ReadyStock/Index"] = R("ReadyStock.View"),               // Phase B: approved Ready Stock

                // ---- Cutting Sowing (Phase 5): Cutting Stock -> Tray/Cavity
                // -> Ready Stock, sharing the SAME approval permission as
                // Direct Sowing (ReadyStock.Confirm) but created/viewed by
                // whoever operates the cutting-producing Areas -- the same
                // actor as CuttingStock/EnterCutting (Phase 3/4), not the
                // Sowing.* permissions Direct Sowing itself uses.
                ["/Production/CuttingSowing/Index"] = R("MotherPlant.View|Kunjir.View|Kiran.View"),
                ["/Production/CuttingSowing/Details"] = R("MotherPlant.View|Kunjir.View|Kiran.View"),
                ["/Production/CuttingSowing/Create"] = R("MotherPlant.Enter|Kunjir.Enter|Kiran.Enter"),
                ["/Production/CuttingSowing/Edit"] = R("MotherPlant.Enter|Kunjir.Enter|Kiran.Enter"),
                ["/Production/CuttingSowing/History"] = R("ReadyStock.View", "ReadyStock.Confirm"),
                ["/Production/CuttingSowing/Confirm"] = R("ReadyStock.Confirm"),

                // ---- Mother plant / cutting --------------------------------
                ["/Production/MotherPlant/Index"] = R("MotherPlant.View"),
                ["/Production/MotherPlant/Details"] = R("MotherPlant.View"),
                ["/Production/MotherPlant/Create"] = R("MotherPlant.Enter"),
                ["/Production/MotherPlant/Edit"] = R("MotherPlant.Enter"),
                ["/Production/CuttingPlan/Index"] = R("CuttingPlan.View|MotherPlant.View"),
                ["/Production/CuttingPlan/Details"] = R("CuttingPlan.View|MotherPlant.View"),
                ["/Production/CuttingPlan/Create"] = R("MotherPlant.Enter"),
                ["/Production/CuttingPlan/Edit"] = R("MotherPlant.Enter"),
                ["/Production/ActualCutting/Index"] = R("MotherPlant.View|CuttingPlan.View"),
                ["/Production/ActualCutting/Details"] = R("MotherPlant.View|CuttingPlan.View"),
                ["/Production/ActualCutting/Create"] = R("MotherPlant.Enter"),
                ["/Production/ActualCutting/Edit"] = R("MotherPlant.Enter"),
                ["/Production/CuttingDelivery/Index"] = R("CuttingDelivery.View"),
                ["/Production/CuttingDelivery/Details"] = R("CuttingDelivery.View"),
                ["/Production/CuttingDelivery/Create"] = R("CuttingDelivery.Enter"),
                ["/Production/CuttingDelivery/Edit"] = R("CuttingDelivery.Enter"),
                ["/Production/CuttingStock/EnterCutting"] = R("MotherPlant.Enter|Kunjir.Enter|Kiran.Enter"),
                ["/Production/CuttingStock/GiveToMainOffice"] = R("MotherPlant.Enter|Kunjir.Enter|Kiran.Enter"),
                ["/Production/CuttingStock/MyTransactions"] = R("MotherPlant.View|Kunjir.View|Kiran.View|MainOffice.View"),
                ["/Production/CuttingStock/PendingConfirmations"] = R("MainOffice.View", "MainOffice.Confirm"),
                ["/Production/CuttingStock/ConfirmReceipt"] = R("MainOffice.Confirm"),
                ["/Production/CuttingStock/Transplant"] = R("MainOffice.Confirm"),
                ["/Production/CuttingStock/PendingTransplants"] = R("MainOffice.View"),

                // ---- Propagation / pot & tray production -------------------
                ["/Production/PropagationBatch/Index"] = R("PotProduction.View|CuttingDelivery.View"),
                ["/Production/PropagationBatch/Details"] = R("PotProduction.View|CuttingDelivery.View"),
                ["/Production/PropagationBatch/Create"] = R("PotProduction.Enter"),
                ["/Production/PropagationBatch/Edit"] = R("PotProduction.Enter"),
                ["/Production/PotProduction/Index"] = R("PotProduction.View|Kiran.View"),
                ["/Production/PotProduction/Details"] = R("PotProduction.View|Kiran.View"),
                ["/Production/PotProduction/Create"] = R("PotProduction.Enter|Kiran.Enter"),
                ["/Production/PotProduction/CreateFromCutting"] = R("PotProduction.Enter|Kiran.Enter"),
                ["/Production/PotProduction/Edit"] = R("PotProduction.Enter|Kiran.Enter"),

                // Phase 31 (Phase 6): Cutting to Potted Plant Production
                // batches -- reuses the SAME PotProduction permission
                // codes as the pages above (no new role/permission, per
                // the "no unnecessary approval/permission" instruction).
                // Ready confirmation is gated by the assigned-supervisor
                // check itself (DirectSowingRules.CanApprove), not a
                // separate permission code.
                ["/Production/PotProductionBatch/Index"] = R("PotProduction.View|Kiran.View"),
                ["/Production/PotProductionBatch/Details"] = R("PotProduction.View|Kiran.View"),
                ["/Production/PotProductionBatch/Create"] = R("PotProduction.Enter|Kiran.Enter"),
                ["/Production/PotProductionBatch/DailyEntry"] = R("PotProduction.Enter|Kiran.Enter"),
                ["/Production/PotProductionBatch/ConfirmReady"] = R("PotProduction.Enter|Kiran.Enter"),

                ["/Production/EmptyPotInventory/Index"] = R("PotProduction.View|Purchase.View"),
                ["/Production/EmptyPotInventory/Details"] = R("PotProduction.View|Purchase.View"),
                ["/Production/EmptyPotInventory/Create"] = R("PotProduction.Enter|Purchase.Enter"),
                ["/Production/EmptyPotInventory/AddStock"] = R("PotProduction.Enter|Purchase.Enter"),
                ["/Production/PottedPlantStock/Index"] = R("PotProduction.View|Outlet.View|Booking.View|Dispatch.View|InternalTransfer.View"),
                ["/Production/PottedPlantStock/Details"] = R("PotProduction.View|Outlet.View|Booking.View|Dispatch.View|InternalTransfer.View"),
                // Phase 8: activates the existing (never-written) 'Wastage'
                // ledger type -- reuses the same Enter-level codes as
                // PotProduction/Create, since recording wastage against a
                // pool is a production-area write action.
                ["/Production/PottedPlantStock/RecordWastage"] = R("PotProduction.Enter|Kiran.Enter"),

                // ---- Transfers ---------------------------------------------
                ["/Production/InternalTransfer/Index"] = R("InternalTransfer.View"),
                ["/Production/InternalTransfer/Details"] = R("InternalTransfer.View"),
                ["/Production/InternalTransfer/Create"] = R("InternalTransfer.Enter"),
                ["/Production/InternalTransfer/Edit"] = R("InternalTransfer.Enter"),
                ["/Production/MainOfficeIssue/Create"] = R("MainOffice.Confirm"),
                ["/Production/MainOfficeIssue/MyIssues"] = R("MainOffice.View"),
                ["/Production/MainOfficeIssue/PendingReceipts"] = R("InternalTransfer.View|PotProduction.View", "InternalTransfer.Enter|PotProduction.Enter"),
                ["/Production/MainOfficeIssue/ConfirmReceipt"] = R("InternalTransfer.Enter|PotProduction.Enter"),
                ["/Production/GrowingPartnerToOutlet/Create"] = R("InternalTransfer.Enter"),
                ["/Production/GrowingPartnerToOutlet/MySentTransfers"] = R("InternalTransfer.View"),
                ["/Production/GrowingPartnerToOutlet/PendingReceipts"] = R("Outlet.View", "Outlet.Confirm"),
                ["/Production/GrowingPartnerToOutlet/ConfirmReceipt"] = R("Outlet.Confirm"),
                // Phase 32/Phase 7: mirror image of GrowingPartnerToOutlet
                // above -- sender-side permissions unchanged
                // (InternalTransfer.Enter/.View), receiver-side reuses
                // Main Office's own existing codes (MainOffice.View/.Confirm),
                // the same ones MainOfficeIssue/Create already holds.
                ["/Production/GrowingPartnerToMainOffice/Create"] = R("InternalTransfer.Enter"),
                ["/Production/GrowingPartnerToMainOffice/MySentTransfers"] = R("InternalTransfer.View"),
                ["/Production/GrowingPartnerToMainOffice/PendingReceipts"] = R("MainOffice.View", "MainOffice.Confirm"),
                ["/Production/GrowingPartnerToMainOffice/ConfirmReceipt"] = R("MainOffice.Confirm"),

                // ---- Booking / dispatch / outlet ---------------------------
                ["/Production/PottedPlantBooking/Index"] = R("Booking.View|Outlet.View"),
                ["/Production/PottedPlantBooking/Details"] = R("Booking.View|Outlet.View"),
                ["/Production/PottedPlantBooking/Create"] = R("Booking.Enter|Outlet.Sell"),
                ["/Production/PottedPlantBooking/Edit"] = R("Booking.Enter|Outlet.Sell"),
                ["/Production/Dispatch/Index"] = R("Dispatch.View|Outlet.View"),
                ["/Production/Dispatch/Details"] = R("Dispatch.View|Outlet.View"),
                ["/Production/Dispatch/Create"] = R("Dispatch.Enter|Outlet.Sell"),
                ["/Production/Dispatch/Edit"] = R("Dispatch.Enter|Outlet.Sell"),

                // Seedling booking (dbo.Bookings): New Booking / Edit Booking
                ["/Bookings/Book"] = R("Booking.Enter"),
                ["/Bookings/EditBookingRecords"] = R("Booking.Enter"),
                ["/Bookings/CancleBooking"] = R("Booking.View|Dispatch.View"),
                ["/Bookings/TotalBookings"] = R("Booking.View|Dispatch.View|Reports.View"),

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
                ["/Production/LabourLog/Index"] = R("Labour.View"),
                ["/Production/LabourLog/Details"] = R("Labour.View"),
                ["/Production/LabourLog/Create"] = R("Labour.Enter"),
                ["/Production/LabourLog/Edit"] = R("Labour.Enter"),

                // ---- Fertilizer --------------------------------------------
                ["/Fertilizer/Stock"] = R("Fertilizer.View", "Fertilizer.Enter"),
                ["/Fertilizer/Usage"] = R("Fertilizer.Enter"),
                ["/Fertilizer/View"] = R("Fertilizer.View"),
                ["/Fertilizer/Fertilizer"] = R("Fertilizer.View", "Fertilizer.Manage"),
                ["/Fertilizer/Type"] = R("Fertilizer.View", "Fertilizer.Manage"),
                ["/Fertilizer/UnitMaster"] = R("Fertilizer.View", "Fertilizer.Manage"),
                ["/Fertilizer/Source"] = R("Fertilizer.View", "Fertilizer.Manage"),

                // ---- Reports (/Data) ---------------------------------------
                ["/Data/BookingsRecord"] = R("Booking.View|Booking.Direct|Dispatch.View|Reports.View"),
                ["/Data/BookingHistory"] = R("Booking.View|Booking.Direct|Dispatch.View|Reports.View"),
                ["/Data/SowingPlants"] = R("Sowing.View|Reports.View"),
                ["/Data/MonthWiseSowing"] = R("Sowing.View|Reports.View"),
                ["/Data/SowingByMonth"] = R("Sowing.View|Reports.View"),
                ["/Data/StockHistory"] = R("ReadyStock.View|Reports.View"),
                ["/Data/WastedStock"] = R("ReadyStock.View|Reports.View"),
                // Phase 8: reuses the same read-only reporting codes as
                // the two pages above -- a pure data-integrity report,
                // no write action.
                ["/Data/StockReconciliation"] = R("ReadyStock.View|Reports.View"),
                ["/Data/TotalStockSync"] = R("Reports.View|Sowing.View|Booking.View"),
                ["/Data/SowingBookingSync"] = R("Reports.View|Sowing.View|Booking.View"),
                ["/Data/SowingBookingSummary"] = R("Reports.View|Sowing.View|Booking.View"),

                // ---- Developer / sample pages: System Administrator only ---
                ["/Data/test201225"] = FullAccessOnlyRule,
                ["/SamplePages/SidebarDesign"] = FullAccessOnlyRule,
                ["/random"] = FullAccessOnlyRule,
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
