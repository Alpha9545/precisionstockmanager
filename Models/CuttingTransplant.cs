namespace PlantStockManager.Models
{
    // Phase 16: dbo.CuttingTransplants -- a 1:1 companion to an
    // InternalTransfers row once a Cutting transfer reaches 'Transplanted'.
    // Kept separate from InternalTransfers itself because that table's
    // SupervisorId/ResponsiblePersonId already mean the SOURCE side;
    // DestinationSupervisorId here is a different person, at the
    // destination Polyhouse. Written exactly once, by
    // InternalTransferRepository.ConfirmTransplantAsync, in the same
    // transaction as the one-sided CuttingStockTransactions ledger row.
    public class CuttingTransplant
    {
        public int Id { get; set; }
        public int InternalTransferId { get; set; }

        public int DestinationSupervisorId { get; set; }
        public string? DestinationSupervisorName { get; set; }

        public DateTime TransplantDate { get; set; }
        public string? Remarks { get; set; }

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; }
    }
}
