namespace PlantStockManager.Models
{
    // Material Stock (2026-10-08): growing material -- Cocopeat, Pitmos -- kept per
    // MATERIAL + Main Office AREA + POLYHOUSE (Database/Migrations/2026-10-08_MaterialStock.sql).
    // A separate stock domain: NOT the Inventory Store and NOT dbo.Inventory.

    // dbo.MaterialTypes -- the material master. ConversionFactor turns the entered
    // (dry) quantity into usable stock: Cocopeat 5.2, Pitmos 1.0 (approximate
    // business values, changed as data, never in code).
    public class MaterialType
    {
        public int Id { get; set; }
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Unit { get; set; } = "KG";
        public decimal ConversionFactor { get; set; }
        public bool IsActive { get; set; }
        public int SortOrder { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public string? UpdatedByName { get; set; }
    }

    // dbo.MaterialConsumptionRules -- kg of a material used per tray of a cavity.
    public class MaterialConsumptionRule
    {
        public int Id { get; set; }
        public int MaterialTypeId { get; set; }
        public string MaterialName { get; set; } = string.Empty;
        public string CavityType { get; set; } = string.Empty;
        public decimal QuantityPerTray { get; set; }
        public bool IsActive { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public string? UpdatedByName { get; set; }
    }

    // One dbo.MaterialStock balance.
    public class MaterialStockBalance
    {
        public int Id { get; set; }
        public int MaterialTypeId { get; set; }
        public string MaterialCode { get; set; } = string.Empty;
        public string MaterialName { get; set; } = string.Empty;
        public string Unit { get; set; } = "KG";
        public int AreaId { get; set; }
        public string AreaName { get; set; } = string.Empty;
        public int PolyhouseId { get; set; }
        public string PolyhouseName { get; set; } = string.Empty;
        public decimal CurrentQuantity { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }

    // One dbo.MaterialTransactions ledger row.
    public class MaterialTransaction
    {
        public int Id { get; set; }
        public int MaterialStockId { get; set; }
        public string MaterialName { get; set; } = string.Empty;
        public string Unit { get; set; } = "KG";
        public int AreaId { get; set; }
        public string AreaName { get; set; } = string.Empty;
        public int PolyhouseId { get; set; }
        public string PolyhouseName { get; set; } = string.Empty;
        public string TransactionType { get; set; } = string.Empty;
        public int Direction { get; set; }
        public decimal StockQuantity { get; set; }
        public decimal? InputQuantity { get; set; }
        public decimal? ConversionFactor { get; set; }
        public int? Trays { get; set; }
        public decimal? QuantityPerTray { get; set; }
        public decimal BalanceBefore { get; set; }
        public decimal BalanceAfter { get; set; }
        public string? ReferenceType { get; set; }
        public int? ReferenceId { get; set; }
        public string? SowingCode { get; set; }
        public int? SeedSowingId { get; set; }   // the sowing itself, or the sowing of the approval (overage)
        public bool IsApprovalOverage => ReferenceType == Services.MaterialStockRules.ApprovalReference;
        public string? Remarks { get; set; }
        public int? CreatedById { get; set; }
        public string? CreatedByName { get; set; }
        public DateTime CreatedAt { get; set; }   // UTC -- when the row was RECORDED (audit)
        // 2026-10-12: the BUSINESS instant of the movement (UTC); = CreatedAt unless the movement was
        // entered with an earlier business date (e.g. a back-dated bed transplant).
        public DateTime TransactionDate { get; set; }
        public bool IsBackDated => TransactionDate < CreatedAt.AddMinutes(-1);

        public decimal SignedQuantity => Direction * StockQuantity;
    }

    public class MaterialStockFilter
    {
        public int? MaterialTypeId { get; set; }
        public int? AreaId { get; set; }
        public int? PolyhouseId { get; set; }
        public string? Search { get; set; }
    }

    public class MaterialTransactionFilter
    {
        public int? MaterialTypeId { get; set; }
        public int? AreaId { get; set; }
        public int? PolyhouseId { get; set; }
        public string? TransactionType { get; set; }
        public DateTime? FromDate { get; set; }   // India dates, inclusive
        public DateTime? ToDate { get; set; }
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 50;
        // The consolidated history merges this ledger with Tray Stock's and pages the
        // merged list itself, so it may ask for more than one screen of rows.
        public const int MaxPageSize = 10000;
    }

    // One row of Reports -> Material History (2026-10-08): a Cocopeat / Pitmos ledger row
    // (kg) OR a Tray Stock ledger row (a tray COUNT of one cavity). The two are shown in
    // one list but never in one unit -- Quantity and Balance are in Unit.
    public class MaterialHistoryRow
    {
        public bool IsTray { get; set; }
        public DateTime AtUtc { get; set; }          // BUSINESS instant (UTC) -- the history is ordered by it
        public DateTime? RecordedUtc { get; set; }   // when it was recorded, shown when it differs (back-dated)
        public bool IsBackDated => RecordedUtc.HasValue && AtUtc < RecordedUtc.Value.AddMinutes(-1);
        public string MaterialName { get; set; } = string.Empty;   // Cocopeat / Pitmos / Trays
        public string Unit { get; set; } = "kg";                    // "kg" or "trays"
        public string? Cavity { get; set; }                         // trays only
        public int AreaId { get; set; }
        public string AreaName { get; set; } = string.Empty;
        public int? PolyhouseId { get; set; }
        public string? PolyhouseName { get; set; }
        public string Category { get; set; } = string.Empty;        // RECEIVE / CONSUME / REVERSAL / ADJUSTMENT
        public string TypeLabel { get; set; } = string.Empty;
        public decimal SignedQuantity { get; set; }
        public decimal? BalanceAfter { get; set; }
        public string? Detail { get; set; }                         // "5.00 kg x 5.2", "100 trays x 1.50 kg"
        public string? ReferenceText { get; set; }
        public int? SowingId { get; set; }                          // link target, when the row belongs to a sowing
        public string? UserName { get; set; }
        public string? Remarks { get; set; }
    }

    // Consumption per material and Polyhouse in a period ("which Polyhouse used the most").
    public class MaterialConsumptionSummaryRow
    {
        public string MaterialName { get; set; } = string.Empty;
        public string AreaName { get; set; } = string.Empty;
        public string PolyhouseName { get; set; } = string.Empty;
        public int Sowings { get; set; }
        public int Trays { get; set; }
        public decimal Consumed { get; set; }   // net of reversals
    }

    // Material Stock page KPIs, per material.
    public class MaterialKpi
    {
        public int MaterialTypeId { get; set; }
        public string MaterialName { get; set; } = string.Empty;
        public string Unit { get; set; } = "KG";
        public decimal TotalStock { get; set; }
        public decimal ConsumedToday { get; set; }   // net of reversals, India day
    }

    // The sowing form's live material check (one entry per material).
    public class MaterialSowingPreview
    {
        public int MaterialTypeId { get; set; }
        public string MaterialName { get; set; } = string.Empty;
        public string Unit { get; set; } = "KG";
        public decimal Available { get; set; }
        public decimal? QuantityPerTray { get; set; }   // null: no active rule for this cavity
        public decimal Required { get; set; }
        public decimal RemainingAfter => Available - Required;
        public bool Enough => QuantityPerTray.HasValue && Required <= Available;
    }
}
