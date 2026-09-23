namespace PlantStockManager.Models
{
    public class PropagationBatch
    {
        public int Id { get; set; }
        public string BatchCode { get; set; } = string.Empty;

        // MotherPlantId/SpeciesId are always copied from the selected
        // Cutting Delivery by the repository (never independently
        // chosen) -- see Phase6_PropagationBatch.sql's
        // CK_PropagationBatches_MatchesCuttingDelivery for the DB-level
        // guarantee.
        public int CuttingDeliveryId { get; set; }
        public int MotherPlantId { get; set; }
        public int SpeciesId { get; set; }

        // Optional -- where the propagation trays/beds physically are.
        // Deliberately NOT required to belong to the same Polyhouse as
        // the source Mother Plant (see the SQL migration's header note).
        public int? AreaId { get; set; }

        public DateTime PropagationDate { get; set; } = DateTime.Today;

        // Quantity taken out of the Cutting Delivery's NetQuantity pool
        // for this batch.
        public decimal Quantity { get; set; }

        // Outcome of the propagation stage -- filled in as the batch
        // progresses through its lifecycle. Must reconcile exactly to
        // Quantity before the batch can move to ReadyForPotting/Completed
        // (enforced in PropagationBatchRepository, not just the DB CHECK,
        // which only guarantees Survived + Loss <= Quantity).
        public decimal SurvivedQuantity { get; set; }
        public decimal LossQuantity { get; set; }

        // Propagating -> ReadyForPotting -> Completed, or Cancelled at
        // any point before Completed.
        public string Status { get; set; } = "Propagating";

        public int? ResponsiblePersonId { get; set; }
        public int? SupervisorId { get; set; }
        public string? Remarks { get; set; }

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public string? ModifiedBy { get; set; }

        // Display-only, populated by joins in PropagationBatchRepository.
        public string? CuttingDeliveryCode { get; set; }
        public string? MotherPlantCode { get; set; }
        public string? SpeciesName { get; set; }
        public string? PlantTypeName { get; set; }
        public string? AreaName { get; set; }
        public string? ResponsiblePersonName { get; set; }
        public string? SupervisorName { get; set; }

        // Display-only, computed by PropagationBatchRepository from
        // PotProduction (Phase 7) -- how much of SurvivedQuantity has
        // already been consumed into pot production. Zero / not
        // populated until Phase 7's table exists.
        public decimal PottedQuantityRecorded { get; set; }

        public static readonly string[] ActiveStatuses = { "Propagating", "ReadyForPotting" };

        public static bool CanReconcile(decimal survived, decimal loss, decimal quantity)
            => survived + loss == quantity;
    }
}
