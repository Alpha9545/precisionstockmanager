namespace PlantStockManager.Models
{
    // Phase C: Ready Stock -> seedling Booking (dbo.Bookings) -> batch
    // reservation/allocation -> seedling Dispatch. These models are read by
    // Data/SeedlingFulfilmentRepository.cs. The legacy Models/Booking.cs is
    // left as it is (the existing booking pages keep using it).

    // One dbo.Bookings row seen from the fulfilment side.
    public class SeedlingBookingSummary
    {
        public int Id { get; set; }
        public int PlantId { get; set; }
        public int? SpeciesId { get; set; }
        public string? PlantTypeName { get; set; }
        public string? SpeciesName { get; set; }
        public int Quantity { get; set; }
        public string Status { get; set; } = "Pending";
        public string? CustomerName { get; set; }
        public string? Contact { get; set; }
        public string? Address { get; set; }
        public DateTime? BookingDate { get; set; }
        public DateTime? DeliveryDate { get; set; }
        public DateTime? ActualDeliveryDate { get; set; }
        public string? AddedBy { get; set; }

        public decimal ReservedQuantity { get; set; }
        public decimal DispatchedQuantity { get; set; }
        public string? FulfilmentSource { get; set; }   // null | Legacy | ReadyStock
        public int RevisionNo { get; set; }
        public int? ParentBookingId { get; set; }
        public DateTime? CancelledDate { get; set; }
        public string? CancelledByName { get; set; }
        public string? CancellationReason { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public string? ModifiedBy { get; set; }

        // Quantity fulfilled through the OLD pipeline (dbo.InventoryTransactions
        // 'Allocation' rows written by the removed legacy FulfillBooking page).
        public decimal LegacyFulfilledQuantity { get; set; }
        public bool HadReservations { get; set; }

        public decimal RemainingQuantity => Math.Max(0, Quantity - DispatchedQuantity - LegacyFulfilledQuantity);
        public decimal UnreservedQuantity => Math.Max(0, Quantity - DispatchedQuantity - ReservedQuantity);
        public bool IsLegacyFulfilled => LegacyFulfilledQuantity > 0 || FulfilmentSource == "Legacy";

        public string Stage => PlantStockManager.Services.SeedlingBookingRules.Stage(
            Status, Quantity, ReservedQuantity, DispatchedQuantity, IsLegacyFulfilled, HadReservations);
    }

    public class BookingBatchAllocation
    {
        public int Id { get; set; }
        public int BookingId { get; set; }
        public int ReadyStockId { get; set; }
        public int? BookedSpeciesId { get; set; }
        public int ActualSpeciesId { get; set; }
        public bool IsSubstitution { get; set; }
        public string? SubstitutionReason { get; set; }
        public decimal Quantity { get; set; }
        public decimal DispatchedQuantity { get; set; }
        public decimal ReleasedQuantity { get; set; }
        public decimal OpenQuantity => Quantity - DispatchedQuantity - ReleasedQuantity;
        public string Status { get; set; } = "Active";
        public string? CreatedBy { get; set; }
        public DateTime CreatedDate { get; set; }
        public string? ModifiedBy { get; set; }
        public DateTime? ModifiedDate { get; set; }

        // Batch traceability (ReadyStock -> SeedSowings -> ...)
        public string? BookedSpeciesName { get; set; }
        public string? ActualSpeciesName { get; set; }
        public string? BatchCode { get; set; }        // SeedSowings.SowingCode
        public int SeedSowingId { get; set; }
        public DateTime? SowingDate { get; set; }
        public string? CavityType { get; set; }
        public string? SeedLot { get; set; }
        public int? AreaId { get; set; }
        public string? AreaName { get; set; }
        public string? PolyhouseName { get; set; }
        public string? SupervisorName { get; set; }
        public string? ApprovedByName { get; set; }
    }

    public class BookingRevision
    {
        public int Id { get; set; }
        public int BookingId { get; set; }
        public int RevisionNo { get; set; }
        public int? PreviousPlantId { get; set; }
        public int? NewPlantId { get; set; }
        public int? PreviousSpeciesId { get; set; }
        public int? NewSpeciesId { get; set; }
        public string? PreviousSpeciesName { get; set; }
        public string? NewSpeciesName { get; set; }
        public int PreviousQuantity { get; set; }
        public int NewQuantity { get; set; }
        public DateTime? PreviousDeliveryDate { get; set; }
        public DateTime? NewDeliveryDate { get; set; }
        public decimal ReleasedQuantity { get; set; }
        public int? SplitBookingId { get; set; }
        public string? OtherChanges { get; set; }
        public string Reason { get; set; } = string.Empty;
        public string? ChangedBy { get; set; }
        public DateTime ChangedDate { get; set; }
    }

    public class SeedlingDispatch
    {
        public int Id { get; set; }
        public string DispatchCode { get; set; } = string.Empty;
        public int BookingId { get; set; }
        public DateTime DispatchDate { get; set; }
        public string? CustomerName { get; set; }
        public decimal TotalQuantity { get; set; }
        public string Status { get; set; } = "Completed";
        public int? ResponsiblePersonId { get; set; }
        public string? ResponsiblePersonName { get; set; }
        public string? Remarks { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime CreatedDate { get; set; }
        public List<SeedlingDispatchLine> Lines { get; set; } = new();
    }

    public class SeedlingDispatchLine
    {
        public int Id { get; set; }
        public int SeedlingDispatchId { get; set; }
        public int BookingBatchAllocationId { get; set; }
        public int ReadyStockId { get; set; }
        public int? BookedSpeciesId { get; set; }
        public int ActualSpeciesId { get; set; }
        public bool IsSubstitution { get; set; }
        public string? SubstitutionReason { get; set; }
        public decimal Quantity { get; set; }

        public string? BookedSpeciesName { get; set; }
        public string? ActualSpeciesName { get; set; }
        public string? BatchCode { get; set; }
        public string? AreaName { get; set; }
        public string? PolyhouseName { get; set; }
        public DateTime? SowingDate { get; set; }

        // Register (report) columns
        public string? DispatchCode { get; set; }
        public DateTime? DispatchDate { get; set; }
        public int BookingId { get; set; }
        public string? CustomerName { get; set; }
        public string? DispatchedBy { get; set; }
    }

    // A Ready Stock batch offered for reservation / allocation.
    public class ReadyBatchOption
    {
        public int ReadyStockId { get; set; }
        public int SpeciesId { get; set; }
        public string? SpeciesName { get; set; }
        public int PlantTypeId { get; set; }
        public string? BatchCode { get; set; }
        public DateTime SowingDate { get; set; }
        public DateTime? FirstConfirmationDate { get; set; }
        public string? CavityType { get; set; }
        public int AreaId { get; set; }
        public string? AreaName { get; set; }
        public string? PolyhouseName { get; set; }
        public decimal Quantity { get; set; }
        public decimal ReservedQuantity { get; set; }
        public decimal DispatchedQuantity { get; set; }
        public decimal AvailableQuantity { get; set; }
    }

    // Variety-level Ready Stock totals (booking screen availability panel).
    public class ReadyStockAvailability
    {
        public int SpeciesId { get; set; }
        public decimal ReadyQuantity { get; set; }      // approved
        public decimal PhysicalQuantity { get; set; }   // approved - dispatched
        public decimal ReservedQuantity { get; set; }
        public decimal DispatchedQuantity { get; set; }
        public decimal AvailableQuantity { get; set; }
        public int Batches { get; set; }
    }
}
