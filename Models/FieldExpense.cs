namespace PlantStockManager.Models
{
    // Field Work Phase 4 (2026-11-03): one expense with its bill photo (dbo.FieldExpenses).
    public class FieldExpense
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public string EmployeeName { get; set; } = "";
        public DateTime ExpenseDate { get; set; }
        public string Category { get; set; } = "";
        public decimal Amount { get; set; }
        public string? Note { get; set; }
        public int BillPhotoId { get; set; }
        public string Status { get; set; } = "";
        public string? CancelReason { get; set; }
        public DateTime? CancelledAtUtc { get; set; }
        public int? DecidedByUserId { get; set; }
        public string? DecidedByName { get; set; }
        public DateTime? DecidedAtUtc { get; set; }
        public string? DecisionComment { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }

    public class FieldExpenseEvent
    {
        public int Id { get; set; }
        public string Action { get; set; } = "";
        public string? FromStatus { get; set; }
        public string ToStatus { get; set; } = "";
        public int ByUserId { get; set; }
        public string ByName { get; set; } = "";
        public DateTime AtUtc { get; set; }
        public string? Comment { get; set; }
    }

    // One employee's totals over a filter: approved and pending (Submitted) separately.
    public sealed record FieldExpenseTotal(int UserId, string EmployeeName, decimal Approved, int ApprovedCount, decimal Pending, int PendingCount);
}
