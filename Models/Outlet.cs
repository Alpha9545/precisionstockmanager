using PlantStockManager.Services;

namespace PlantStockManager.Models
{
    // Phase E: an Outlet is simply an Area with AreaType = 'Outlet' -- there
    // is no separate Outlet master table or Outlet stock system. Outlet
    // potted-plant stock lives in the existing dbo.PottedPlantStock, keyed
    // by AreaId; Outlet tray stock lives in the existing dbo.ReadyStock, the
    // same way. This file only holds the NEW, genuinely new Outlet business
    // objects: buying stock directly (Purchase), a customer transaction
    // covering several potted/tray items at once (Sale / Booking), and
    // recording the Outlet's own loss of stock (Wastage).

    public static class OutletStockType
    {
        public const string Potted = "Potted";
        public const string Tray = "Tray";
    }

    // Potted plants bought by an Outlet directly from an outside supplier --
    // distinct from a Main Office purchase order. Immutable once recorded.
    public class OutletPurchase
    {
        public int Id { get; set; }
        public string PurchaseCode { get; set; } = string.Empty;
        public DateTime PurchaseDate { get; set; } = DateTime.Today;
        public string SupplierName { get; set; } = string.Empty;
        public int OutletAreaId { get; set; }
        public int SpeciesId { get; set; }
        public string PotSize { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public int PottedPlantStockId { get; set; }
        public string? Remarks { get; set; }
        public int? CreatedById { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime CreatedDate { get; set; }

        public string? OutletAreaName { get; set; }
        public string? SpeciesName { get; set; }
        public string? PlantTypeName { get; set; }
        public string? SpeciesColor { get; set; }
    }

    // One item of a Direct Sale, Customer Booking or Wastage record --
    // EXACTLY one of PottedPlantStockId/ReadyStockId is set, matching
    // StockType (enforced by a DB CHECK too). Quantity means pots for
    // 'Potted', WHOLE TRAYS for 'Tray' -- never a converted seedling count;
    // the repository converts trays -> the ReadyStock ledger's own seedling
    // unit only when touching that ledger, using the cavity read from the
    // locked stock row itself (never trusted from the caller).
    public class OutletStockItemBase
    {
        public int Id { get; set; }
        public int OutletAreaId { get; set; }
        public string StockType { get; set; } = OutletStockType.Potted;
        public int? PottedPlantStockId { get; set; }
        public int? ReadyStockId { get; set; }
        public int SpeciesId { get; set; }
        public string? PotSize { get; set; }
        public string? CavityType { get; set; }
        public decimal Quantity { get; set; }
        public DateTime CreatedDate { get; set; }

        public string? SpeciesName { get; set; }
        public string? PlantTypeName { get; set; }
        public string? SpeciesColor { get; set; }
        public bool IsTray => StockType == OutletStockType.Tray;

        // Display only: "20 pots" or "5 trays (Cavity 102)".
        public string QuantityLabel => IsTray
            ? $"{Quantity:N0} tray{(Quantity == 1 ? "" : "s")} ({CavityType})"
            : $"{Quantity:N0} pot{(Quantity == 1 ? "" : "s")} ({PotSize})";
    }

    // A single customer transaction covering one or more potted/tray items,
    // all sold from one Outlet at once (dbo.OutletSales / OutletSaleItems).
    // Immutable once recorded: a completed ledger fact, like a dispatch.
    public class OutletSale
    {
        public int Id { get; set; }
        public string SaleCode { get; set; } = string.Empty;
        public int OutletAreaId { get; set; }
        public string CustomerName { get; set; } = string.Empty;
        public string? CustomerContact { get; set; }
        public DateTime SaleDate { get; set; } = DateTime.Today;
        public string? Remarks { get; set; }
        public int? CreatedById { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime CreatedDate { get; set; }

        public string? OutletAreaName { get; set; }
        public List<OutletSaleItem> Items { get; set; } = new();
    }

    public class OutletSaleItem : OutletStockItemBase
    {
        public int SaleId { get; set; }
    }

    // A customer order for one or more potted/tray items, reserved against
    // Outlet stock and collected (fully or partially) over time
    // (dbo.OutletBookings / OutletBookingItems) -- the same
    // Physical/Reserved/Available reservation shape every other stock in
    // this app already uses, for both stock types.
    public class OutletBooking
    {
        public int Id { get; set; }
        public string BookingCode { get; set; } = string.Empty;
        public int OutletAreaId { get; set; }
        public string CustomerName { get; set; } = string.Empty;
        public string? CustomerContact { get; set; }
        public DateTime BookingDate { get; set; } = DateTime.Today;
        public DateTime? RequiredDate { get; set; }
        public string Status { get; set; } = OutletBookingRules.Pending;
        public string? Remarks { get; set; }
        public int? CreatedById { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime CreatedDate { get; set; }
        public int? CancelledById { get; set; }
        public DateTime? CancelledDate { get; set; }
        public string? CancellationReason { get; set; }
        public string? ModifiedBy { get; set; }
        public DateTime? ModifiedDate { get; set; }

        public string? OutletAreaName { get; set; }
        public List<OutletBookingItem> Items { get; set; } = new();
    }

    public class OutletBookingItem : OutletStockItemBase
    {
        public int BookingId { get; set; }
        public decimal CollectedQuantity { get; set; }
        public decimal OpenQuantity => Quantity - CollectedQuantity;
    }

    // The Outlet's own record of losing some of its stock (breakage,
    // wilting, pests...) -- a different concept from nursery production
    // wastage; no automatic percentage. Immutable once recorded.
    public class OutletWastage : OutletStockItemBase
    {
        public string WastageCode { get; set; } = string.Empty;
        public DateTime WastageDate { get; set; } = DateTime.Today;
        public string Reason { get; set; } = string.Empty;
        public string? Remarks { get; set; }
        public int? CreatedById { get; set; }
        public string? CreatedBy { get; set; }

        public string? OutletAreaName { get; set; }
    }
}
