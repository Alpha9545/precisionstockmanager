using System.Globalization;

namespace PlantStockManager.Services
{
    // Material Stock (Cocopeat / Pitmos) -- the pure rules. The conversion factor
    // (dry kg -> usable kg) and the kg per tray are DATA (dbo.MaterialTypes,
    // dbo.MaterialConsumptionRules); this class only applies whatever values it is
    // given, so changing "150 Cavity = 1.25 kg" never needs a code change. The
    // database repeats every rule (CHECK constraints and triggers in
    // Database/Migrations/2026-10-08_MaterialStock.sql).
    public static class MaterialStockRules
    {
        public const string Receive = "RECEIVE";
        public const string Consume = "CONSUME";
        public const string Reversal = "REVERSAL";
        public const string Adjustment = "ADJUSTMENT";
        // 2026-10-10: trays moved between Polyhouses (TransferOut / TransferIn / TransferReturn).
        // A filter category; 2026-10-23: also growing material moved between Polyhouses (ledger types
        // TRANSFER_OUT / TRANSFER_IN, MaterialTransferRules -- 2026-10-25_MaterialStockTransfers.sql).
        public const string Transfer = "TRANSFER";
        public static readonly IReadOnlyList<string> TransactionTypes = new[] { Receive, Consume, Reversal, Adjustment, Transfer };

        public const string SowingReference = "SeedSowing";
        public const string ApprovalReference = "ReadyConfirmation";   // overage trays of a Supervisor Approval
        public const string MainOfficeAreaType = "MainOffice";

        public const int Decimals = 3;                      // DECIMAL(18,3)
        public const decimal MaxQuantity = 1_000_000m;
        public const int MaxRemarksLength = 500;

        public const string MaterialRequiredMessage = "Select the growing material: Cocopeat or Pitmos.";
        public const string OneMaterialOnlyMessage = "Select exactly one growing material.";
        public const string NotAtThisLocationMessage = "Growing material is stocked only at Main Office Polyhouses -- this sowing cannot use one.";
        public const string LocationMessage = "Material stock is kept only at a Polyhouse of an active Main Office Area.";
        public const string PolyhouseNotInAreaMessage = "Selected Polyhouse does not belong to the selected Area.";
        public const string NotAuthorizedAreaMessage = "You are not authorized to manage material stock for the selected Area.";
        public const string ExpiredFormMessage = "This form has expired. Reload the page and enter the stock again.";

        // Usable stock from an entered quantity: input x factor, rounded to the
        // stored 3 decimals (half away from zero -- the same as SQL ROUND, which
        // CK_MaterialTransactions_Receive uses). 5 x 5.2 = 26; 10 x 1.0 = 10.
        public static decimal StockQuantityFor(decimal inputQuantity, decimal conversionFactor)
            => Math.Round(inputQuantity * conversionFactor, Decimals, MidpointRounding.AwayFromZero);

        // Material a sowing needs: physical trays x kg per tray (100 x 1.5 = 150).
        public static decimal RequiredFor(decimal trays, decimal quantityPerTray)
            => Math.Round(trays * quantityPerTray, Decimals, MidpointRounding.AwayFromZero);

        // A quantity entered on a form: > 0, at most 3 decimals, within range.
        public static string? ValidateQuantity(decimal? quantity, string label = "Quantity")
        {
            if (quantity is not > 0)
                return $"{label} must be greater than zero.";
            if (quantity.Value > MaxQuantity)
                return $"{label} is too large (max {Kg(MaxQuantity)}).";
            if (decimal.Round(quantity.Value, Decimals) != quantity.Value)
                return $"{label} can have at most {Decimals} decimal places.";
            return null;
        }

        public static bool IsMainOfficeLocation(string? areaType, bool areaActive)
            => areaActive && areaType == MainOfficeAreaType;

        // "Insufficient Cocopeat stock in PH-03. Available: 100 kg. Required: 150 kg."
        public static string InsufficientMessage(string materialName, string polyhouseName, decimal available, decimal required)
            => $"Insufficient {materialName} stock in {polyhouseName}. Available: {Kg(available)}. Required: {Kg(required)}.";

        public static string NoRuleMessage(string materialName, string? cavityType)
            => $"No {materialName} consumption is configured for {cavityType} trays. Ask the administrator to set it in Material Stock > Settings.";

        // 126 -> "126.00 kg", 2.25 -> "2.25 kg", 0.75 -> "0.75 kg", 0.125 -> "0.125 kg".
        public static string Kg(decimal value, string unit = "KG")
            => value.ToString("#,##0.00#", CultureInfo.CurrentCulture) + " " + unit.ToLowerInvariant();

        public static string Number(decimal value) => value.ToString("#,##0.00#", CultureInfo.CurrentCulture);

        public static string TypeLabel(string? type) => type switch
        {
            Receive => "Received",
            Consume => "Used in sowing",
            Reversal => "Returned (sowing cancelled)",
            Adjustment => "Adjustment",
            Transfer => "Transfer",
            MaterialTransferRules.TransferOut => "Transferred out",
            MaterialTransferRules.TransferIn => "Transferred in",
            _ => type ?? string.Empty,
        };

        public static string TypeBadge(string? type) => type switch
        {
            Receive => "bg-success",
            Consume => "bg-primary",
            Reversal => "bg-info text-dark",
            Adjustment => "bg-warning text-dark",
            Transfer or MaterialTransferRules.TransferOut or MaterialTransferRules.TransferIn => "bg-dark",
            _ => "bg-secondary",
        };

        // A material ledger type in the History page's categories (the two transfer types are one category).
        public static string MaterialCategory(string? type) => type switch
        {
            MaterialTransferRules.TransferOut or MaterialTransferRules.TransferIn => Transfer,
            _ => type ?? string.Empty,
        };

        public static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        // ---- the consolidated Material Stock pages (2026-10-08) ------------------------------
        // The pages' "Material" selector: "" (all), a material Code (COCOPEAT / PITMOS), or
        // TRAYS. Trays are a separate stock (a count per cavity, dbo.TrayStock) -- never kg.
        public const string TraysKind = "TRAYS";
        public const string TrayUnit = "trays";

        // A requested kind the user may see, else "" (all they may see). A user who can
        // see only one domain is put on it.
        public static string NormalizeKind(string? requested, IEnumerable<Models.MaterialType> materials, bool canSeeMaterials, bool canSeeTrays)
        {
            var kind = (requested ?? "").Trim().ToUpperInvariant();
            if (kind == TraysKind) return canSeeTrays ? TraysKind : "";
            if (kind != "" && canSeeMaterials && materials.Any(m => m.Code == kind)) return kind;
            return canSeeTrays && !canSeeMaterials ? TraysKind : "";
        }

        // Tray ledger types (dbo.TrayStockTransactions) in the material ledger's four
        // categories, so one Transaction filter works for both stocks.
        public static string TrayCategory(string? trayTransactionType) => trayTransactionType switch
        {
            "Allocation" => Receive,
            "Sowing" => Consume,
            "Transplant" => Consume,   // 2026-10-10 fix: trays used by Transplanting were shown as an adjustment
            "ReversalReturn" => Reversal,
            "TransferOut" or "TransferIn" or "TransferReturn" => Transfer,
            _ => Adjustment,
        };

        public static string CategoryLabel(string category) => category switch
        {
            Receive => "Added",
            Consume => "Used",
            Reversal => "Returned (cancellation)",
            Adjustment => "Adjustment",
            Transfer => "Transfer",
            _ => category,
        };

        public static string Trays(decimal count) => count.ToString("#,##0.###", System.Globalization.CultureInfo.CurrentCulture) + " " + TrayUnit;
    }
}
