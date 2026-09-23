namespace PlantStockManager.Models
{
    public class LabRequest
    {
        public int Id { get; set; }
        public string LabRequestCode { get; set; } = string.Empty;

        // The pool a sample is taken FROM and returned TO -- both
        // movements post against this same pool (see
        // Database/Phase12_LabRequest.sql header comment). SpeciesId/
        // PotSize/AreaId are always derived server-side from the locked
        // stock row, never trusted from the caller.
        public int PottedPlantStockId { get; set; }
        public int SpeciesId { get; set; }
        public string PotSize { get; set; } = string.Empty;
        public int? AreaId { get; set; }

        public string LabName { get; set; } = string.Empty;

        public DateTime SentDate { get; set; } = DateTime.UtcNow;
        public decimal SentQuantity { get; set; }
        public DateTime? ExpectedResultDate { get; set; }

        // 'Sent' | 'Completed' | 'Cancelled'.
        public string Status { get; set; } = "Sent";

        public DateTime? ReceivedDate { get; set; }

        // Deliberately independent of SentQuantity -- the lab
        // multiplies stock (e.g. 10-15 sent, 30-35 received back a few
        // days later), it doesn't return the same amount it was given.
        public decimal? ReceivedQuantity { get; set; }
        public string? ResultNotes { get; set; }

        public int? ResponsiblePersonId { get; set; }
        public string? Remarks { get; set; }

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public string? ModifiedBy { get; set; }

        // Display-only, populated by joins in LabRequestRepository.
        public string? SpeciesName { get; set; }
        public string? PlantTypeName { get; set; }
        public string? AreaName { get; set; }
        public string? ResponsiblePersonName { get; set; }
        public decimal? StockAvailableQuantity { get; set; }
    }
}
