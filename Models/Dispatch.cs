namespace PlantStockManager.Models
{
    public class Dispatch
    {
        public int Id { get; set; }
        public string DispatchCode { get; set; } = string.Empty;

        // Exactly one Dispatch per Booking in this phase (full dispatch
        // only -- see Database/Phase10_Dispatch.sql UQ_Dispatches_Booking).
        public int PottedPlantBookingId { get; set; }

        // Denormalized copies of the parent Booking's own stock
        // reference -- always derived server-side from the locked
        // Booking row, never trusted from the caller. See
        // fn_Dispatches_MatchesBooking for the DB-level backstop.
        public int PottedPlantStockId { get; set; }
        public int SpeciesId { get; set; }
        public string PotSize { get; set; } = string.Empty;
        public int? AreaId { get; set; }

        public decimal Quantity { get; set; }

        public DateTime DispatchDate { get; set; } = DateTime.UtcNow;

        // 'Completed' | 'Cancelled'.
        public string Status { get; set; } = "Completed";

        public int? ResponsiblePersonId { get; set; }
        public int? SupervisorId { get; set; }

        public string? Remarks { get; set; }

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public string? ModifiedBy { get; set; }

        // Display-only, populated by joins in DispatchRepository.
        public string? SpeciesName { get; set; }
        public string? PlantTypeName { get; set; }
        public string? AreaName { get; set; }
        public string? BookingCode { get; set; }
        public string? CustomerName { get; set; }
        public string? ResponsiblePersonName { get; set; }
        public string? SupervisorName { get; set; }
    }
}
