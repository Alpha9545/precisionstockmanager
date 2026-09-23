namespace PlantStockManager.Models
{
    // Phase 22/Phase H: Main Office -> Polyhouse/Growing Area seed
    // issue. Deliberately its OWN table, not a new StockType on
    // dbo.InternalTransfers -- see Database/Phase22_MainOfficeSeedIssue.sql
    // for why. Lifecycle mirrors Phase 18's MainOfficeIssue exactly:
    // 'PendingConfirmation' -> 'Completed' (destination Seed Stock
    // credited by ConfirmedQuantity) or -> 'Rejected' (no destination
    // stock created, the full sent quantity released back to Main
    // Office's Available). No 'ConfirmedAwaitingTransplant'/'Transplanted'
    // stage -- the destination Area is already fixed at creation.
    public class SeedIssue
    {
        public int Id { get; set; }
        public string IssueCode { get; set; } = string.Empty;

        // The specific Main Office Species+Area+Lot pool this issue
        // draws from. SpeciesId/SourceAreaId are always copied down
        // from the locked SeedStock row server-side (never trusted
        // from the caller).
        public int SourceSeedStockId { get; set; }
        public int SpeciesId { get; set; }
        public int SourceAreaId { get; set; }

        public int DestinationAreaId { get; set; }

        public decimal IssuedQuantity { get; set; }
        public DateTime IssueDate { get; set; } = DateTime.UtcNow;

        // 'PendingConfirmation' | 'Completed' | 'Rejected'.
        public string Status { get; set; } = "PendingConfirmation";

        public decimal? ConfirmedQuantity { get; set; }
        public int? ConfirmedBy { get; set; }
        public DateTime? ConfirmedDate { get; set; }

        // Reused for both the short-receipt discrepancy reason and the
        // rejection reason -- mirrors InternalTransfers.DiscrepancyReason.
        public string? DiscrepancyReason { get; set; }

        public int? ResponsiblePersonId { get; set; }
        public int? SupervisorId { get; set; }
        public string? Remarks { get; set; }

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public string? ModifiedBy { get; set; }

        // Display-only, populated by joins in SeedIssueRepository.
        public string? SpeciesName { get; set; }
        public string? PlantTypeName { get; set; }
        public string? BatchNo { get; set; }
        public string? SeedSourceName { get; set; }
        public string? SourceAreaName { get; set; }
        public string? DestinationAreaName { get; set; }
        public string? ResponsiblePersonName { get; set; }
        public string? SupervisorName { get; set; }
        public string? ConfirmedByName { get; set; }

        // Convenience for the UI, as of the moment this was loaded.
        public decimal? SourceAvailableQuantity { get; set; }
    }
}
