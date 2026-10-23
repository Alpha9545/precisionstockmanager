using System.Security.Claims;

namespace PlantStockManager.Authorization
{
    // Material Stock (2026-10-08 consolidation) shows two SEPARATE stock domains on one
    // set of pages: growing material (Cocopeat / Pitmos, kg, dbo.MaterialStock ledger)
    // and empty trays (count per cavity, dbo.TrayStock ledger). Each domain keeps the
    // permission it had before the pages were merged -- nothing is widened:
    //   material  view / add / history / manage  = MaterialStock.View / .Add / .History / .Manage
    //   trays     view (incl. history) / add     = the old Tray Stock page rules
    // A page opens for either domain's permission (FeatureAuthorizationConventions) and
    // then shows -- and on POST accepts -- only the domain(s) the user holds.
    public static class MaterialStockAccess
    {
        public const string MaterialView = "MaterialStock.View";
        public const string MaterialAdd = "MaterialStock.Add";
        public const string MaterialHistory = "MaterialStock.History";
        public const string MaterialManage = "MaterialStock.Manage";
        // 2026-10-23: move Cocopeat / Pitmos between Polyhouses FROM an Area the user is assigned to
        // (2026-10-25_MaterialStockTransfers.sql; Sowing Supervisor). Transfer history: this or MaterialStock.History.
        public const string MaterialTransfer = "MaterialStock.Transfer";
        public const string MaterialTransfersAny = MaterialTransfer + "|" + MaterialHistory;

        // 2026-10-11: TrayStock.ViewMotherPlant (Mother Plant Supervisor) is READ-ONLY and only for the
        // user's own Mother Plant Areas (TrayAreaScope); it opens the tray parts of Material Stock and
        // Material History, never add / adjust / transfer / growing material.
        public const string TrayViewMotherPlant = "TrayStock.ViewMotherPlant";
        // The permissions that see a user's own Areas' trays (the rule before 2026-10-11).
        public const string TrayViewOwnAreas = "MainOffice.View|Sowing.View|Sowing.Enter|TrayStock.Enter|" + TrayAddMotherPlant;
        public const string TrayView = TrayViewOwnAreas + "|" + TrayViewMotherPlant;
        public const string TrayAdd = "TrayStock.Enter";
        // 2026-10-09 (owner decision): add EMPTY trays to any active Mother Plant Area polyhouse only
        // (Office Coordinator, Booking Executive) -- per-Area rule in TrayAreaScope.
        public const string TrayAddMotherPlant = "TrayStock.EnterMotherPlant";
        public const string TrayAddAny = TrayAdd + "|" + TrayAddMotherPlant;
        // 2026-10-10 (owner decisions): corrections and transfers of EMPTY trays.
        //   Adjust          : Sowing Supervisor, own Areas (Admin: all)
        //   Transfer        : send / cancel from an Area the user may stock trays in (TrayAreaScope)
        //   ReceiveTransfer : receive / reject in an Area the user is assigned to (Mother Plant Supervisor)
        public const string TrayAdjust = "TrayStock.Adjust";
        public const string TrayTransfer = "TrayStock.Transfer";
        public const string TrayReceiveTransfer = "TrayStock.ReceiveTransfer";
        // 2026-10-10: Main Office Areas are received by their own tray role (location-aware, TrayTransferRules)
        public const string TrayReceiveTransferMainArea = "TrayStock.ReceiveTransferMainArea";
        public const string TrayReceiveAny = TrayReceiveTransfer + "|" + TrayReceiveTransferMainArea;
        public const string TrayTransfersAny = TrayTransfer + "|" + TrayReceiveAny;

        public static bool CanViewMaterials(this ClaimsPrincipal user) => user.HasPermission(MaterialView);
        public static bool CanAddMaterials(this ClaimsPrincipal user) => user.HasPermission(MaterialAdd);
        public static bool CanSeeMaterialHistory(this ClaimsPrincipal user) => user.HasPermission(MaterialHistory);
        public static bool CanTransferMaterials(this ClaimsPrincipal user) => user.HasPermission(MaterialTransfer);
        public static bool CanViewTrays(this ClaimsPrincipal user) => user.HasPermission(TrayView);
        // May add trays SOMEWHERE; which Areas exactly is TrayAreaScope's rule.
        public static bool CanAddTrays(this ClaimsPrincipal user) => user.HasPermission(TrayAddAny);
        public static bool CanAddMotherPlantTrays(this ClaimsPrincipal user) => user.HasPermission(TrayAddMotherPlant);
        public static bool CanAdjustTrays(this ClaimsPrincipal user) => user.HasPermission(TrayAdjust);
        public static bool CanSendTrayTransfers(this ClaimsPrincipal user) => user.HasPermission(TrayTransfer);
        // May receive SOMEWHERE; which Areas exactly is TrayAreaScope.IsReceivableBy (location-aware).
        public static bool CanReceiveTrayTransfers(this ClaimsPrincipal user) => user.HasPermission(TrayReceiveAny);
    }
}
