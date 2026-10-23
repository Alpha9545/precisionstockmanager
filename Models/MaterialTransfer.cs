namespace PlantStockManager.Models
{
    // One Cocopeat / Pitmos transfer (dbo.MaterialTransfers, 2026-10-25_MaterialStockTransfers.sql) with the balance each
    // side was left at (its TRANSFER_OUT / TRANSFER_IN ledger row).
    public class MaterialTransfer
    {
        public int Id { get; set; }
        public string TransferCode { get; set; } = string.Empty;
        public int MaterialTypeId { get; set; }
        public string MaterialName { get; set; } = string.Empty;
        public string Unit { get; set; } = "KG";
        public int SourceAreaId { get; set; }
        public string SourceAreaName { get; set; } = string.Empty;
        public int SourcePolyhouseId { get; set; }
        public string SourcePolyhouseName { get; set; } = string.Empty;
        public int DestinationAreaId { get; set; }
        public string DestinationAreaName { get; set; } = string.Empty;
        public int DestinationPolyhouseId { get; set; }
        public string DestinationPolyhouseName { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public DateTime TransferDate { get; set; }              // India business date
        public string? Remarks { get; set; }
        public int CreatedById { get; set; }
        public string? CreatedByName { get; set; }
        public DateTime CreatedAt { get; set; }                 // UTC, when recorded
        public decimal? SourceBalanceAfter { get; set; }
        public decimal? DestinationBalanceAfter { get; set; }
    }

    public class MaterialTransferRequest
    {
        public int MaterialTypeId { get; set; }
        public int SourceAreaId { get; set; }
        public int SourcePolyhouseId { get; set; }
        public int DestinationAreaId { get; set; }
        public int DestinationPolyhouseId { get; set; }
        public decimal Quantity { get; set; }
        public DateTime TransferDate { get; set; }
        public string? Remarks { get; set; }
        public Guid SubmissionToken { get; set; }
    }

    public class MaterialTransferFilter
    {
        public int? MaterialTypeId { get; set; }
        public int? AreaId { get; set; }          // source OR destination
        public DateTime? FromDate { get; set; }   // India dates, inclusive
        public DateTime? ToDate { get; set; }
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 50;
    }

    // A Polyhouse material may be transferred from / to (Main Office or Mother Plant Area).
    public sealed record MaterialTransferLocation(int AreaId, string AreaName, string AreaType, int PolyhouseId, string PolyhouseName);
}
