namespace PlantStockManager.Models
{
    // 2026-10-22: one row of dbo.ReadyStockOvergrowthLog (append-only): a batch marked / unmarked as Overgrowth, or the
    // marker inherited from SourceReadyStockId when a Mother Plant Area received the plants.
    public class ReadyStockOvergrowthEntry
    {
        public int Id { get; set; }
        public int ReadyStockId { get; set; }
        public string Action { get; set; } = string.Empty;
        public string? Reason { get; set; }
        public int? SourceReadyStockId { get; set; }
        public int? UserId { get; set; }
        public string? UserName { get; set; }
        public DateTime CreatedAt { get; set; }   // UTC
    }
}
