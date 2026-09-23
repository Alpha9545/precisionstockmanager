namespace PlantStockManager.Models
{
    public class LabourLog
    {
        public int Id { get; set; }
        public string LabourLogCode { get; set; } = string.Empty;

        // Field labour is represented as an IMSUsers row, continuing
        // this project's existing convention (every earlier phase's
        // "responsible person" field is already IMSUsers.Id).
        public int WorkerId { get; set; }
        public DateTime WorkDate { get; set; } = DateTime.UtcNow;
        public int? AreaId { get; set; }
        public string WorkType { get; set; } = string.Empty;

        // Optional, loosely-typed pointer to whatever job this labour
        // was for -- no hard FK to a specific table, matching how the
        // stock ledgers themselves treat ReferenceType/ReferenceId.
        public string? ReferenceType { get; set; }
        public int? ReferenceId { get; set; }
        public string? ReferenceCode { get; set; }

        // 'Daily' | 'Hourly'.
        public string WageType { get; set; } = "Daily";
        public decimal WageRate { get; set; }
        public decimal UnitsWorked { get; set; }

        // Computed and stored server-side at save time (WageRate *
        // UnitsWorked) so a later wage-rate change never rewrites a
        // past entry's actual paid amount.
        public decimal TotalWage { get; set; }

        public int? SupervisorId { get; set; }

        // 'Recorded' | 'Cancelled'. No stock ledger involved, so
        // cancelling is a plain status flip -- there is nothing to
        // reverse.
        public string Status { get; set; } = "Recorded";
        public string? Remarks { get; set; }

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public string? ModifiedBy { get; set; }

        // Display-only, populated by joins in LabourLogRepository.
        public string? WorkerName { get; set; }
        public string? AreaName { get; set; }
        public string? SupervisorName { get; set; }
    }
}
