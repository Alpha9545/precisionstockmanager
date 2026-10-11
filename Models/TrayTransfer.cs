namespace PlantStockManager.Models
{
    // One empty-tray transfer between two Polyhouses (dbo.TrayTransfers, 2026-10-10).
    // Services/TrayTransferRules.cs describes the ledger rows of each status.
    public class TrayTransfer
    {
        public int Id { get; set; }
        public string TransferCode { get; set; } = "";
        public int SourceTrayStockId { get; set; }
        public int SourceAreaId { get; set; }
        public string SourceAreaName { get; set; } = "";
        public int SourcePolyhouseId { get; set; }
        public string SourcePolyhouseName { get; set; } = "";
        public int DestinationAreaId { get; set; }
        public string DestinationAreaName { get; set; } = "";
        public int DestinationPolyhouseId { get; set; }
        public string DestinationPolyhouseName { get; set; } = "";
        public int? DestinationTrayStockId { get; set; }
        public string TraySize { get; set; } = "";
        public decimal Quantity { get; set; }
        public DateTime TransferDate { get; set; }
        public string Status { get; set; } = "";
        public string? Remarks { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? ClosedBy { get; set; }
        public DateTime? ClosedAt { get; set; }
        public string? CloseReason { get; set; }
    }

    public sealed class TrayTransferRequest
    {
        public int SourceAreaId { get; set; }
        public int SourcePolyhouseId { get; set; }
        public int DestinationAreaId { get; set; }
        public int DestinationPolyhouseId { get; set; }
        public string? TraySize { get; set; }
        public decimal Quantity { get; set; }
        public DateTime TransferDate { get; set; }
        public string? Remarks { get; set; }
        public Guid SubmissionToken { get; set; }
    }
}
