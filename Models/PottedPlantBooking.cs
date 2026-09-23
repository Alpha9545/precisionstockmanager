namespace PlantStockManager.Models
{
    public class PottedPlantBooking
    {
        public int Id { get; set; }
        public string BookingCode { get; set; } = string.Empty;

        // The specific Species+PotSize+Area pool this booking reserves
        // against. SpeciesId/PotSize/AreaId are always copied down from
        // the selected PottedPlantStock row server-side (never trusted
        // from the caller) -- see fn_PottedPlantBookings_MatchesStock.
        public int PottedPlantStockId { get; set; }
        public int SpeciesId { get; set; }
        public string PotSize { get; set; } = string.Empty;
        public int? AreaId { get; set; }

        public decimal Quantity { get; set; }

        public string CustomerName { get; set; } = string.Empty;
        public string? Address { get; set; }
        public string? Contact { get; set; }
        public int? StateId { get; set; }
        public int? DistrictId { get; set; }

        public DateTime BookingDate { get; set; } = DateTime.UtcNow;
        public DateTime? DeliveryDate { get; set; }
        public DateTime? ActualDeliveryDate { get; set; }

        // 'Pending' (reserved, awaiting Dispatch) -> 'PartiallyDispatched'
        // (some, not all, of Quantity has been dispatched -- Phase 21/
        // Phase G) -> 'Dispatched' (DispatchedQuantity == Quantity) |
        // 'Cancelled' (from 'Pending' or 'PartiallyDispatched' -- releases
        // whatever is still Reserved). Phase 10 originally only ever
        // dispatched a Booking in one full step; Phase 21 (Phase G) added
        // partial dispatch, hence 'PartiallyDispatched'.
        public string Status { get; set; } = "Pending";

        // Phase 21/Phase G: running total of everything dispatched so far
        // against this Booking, across possibly more than one Dispatch row
        // (partial dispatch). Maintained by DispatchRepository.InsertAsync/
        // CancelAsync in the same transaction as the stock movement --
        // mirrors the established "maintained running-total column"
        // pattern (CuttingStock.InTransitQuantity, PottedPlantStock.
        // InTransitQuantity), rather than a live SUM over dbo.Dispatches.
        public decimal DispatchedQuantity { get; set; }

        // Derived, never a stored column (per the "no redundant quantity
        // columns" convention -- Decision 15): what is still reserved and
        // awaiting dispatch on this Booking right now.
        public decimal RemainingQuantity => Quantity - DispatchedQuantity;

        public bool AdvanceTaken { get; set; }
        public decimal? AdvanceTakenAmount { get; set; }
        public string? AdvanceTakenDetails { get; set; }

        public int? BookedById { get; set; }
        public string? BookedByOther { get; set; }

        public string? Remarks { get; set; }

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public string? ModifiedBy { get; set; }

        // Display-only, populated by joins in PottedPlantBookingRepository.
        public string? SpeciesName { get; set; }
        public string? PlantTypeName { get; set; }
        public string? AreaName { get; set; }
        public string? BookedByName { get; set; }
        public string? StateName { get; set; }
        public string? DistrictName { get; set; }

        // Convenience for the UI: what remains available in the pool
        // this booking reserves against, AS OF the moment it was
        // loaded (populated only where the repository joins it in).
        public decimal? StockAvailableQuantity { get; set; }
    }
}
