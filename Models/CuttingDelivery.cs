namespace PlantStockManager.Models
{
    public class CuttingDelivery
    {
        public int Id { get; set; }
        public string DeliveryCode { get; set; } = string.Empty;

        // MotherPlantId/SpeciesId are always copied from the selected
        // Actual Cutting by the repository (never independently chosen) --
        // see Phase5_CuttingDelivery.sql's
        // CK_CuttingDeliveries_MatchesActualCutting for the DB-level
        // guarantee.
        public int ActualCuttingId { get; set; }
        public int MotherPlantId { get; set; }
        public int SpeciesId { get; set; }

        public DateTime DeliveryDate { get; set; } = DateTime.Today;

        // Quantity taken out of the Actual Cutting's GoodQuantity pool for
        // this delivery attempt.
        public decimal DeliveredQuantity { get; set; }

        // Outcome buckets for what happened to the delivered quantity
        // during transit/handling to propagation.
        public decimal LossQuantity { get; set; }
        public decimal RemovedQuantity { get; set; }
        public decimal RejectedQuantity { get; set; }
        public decimal DamagedQuantity { get; set; }

        // Computed server-side (mirrors the DB's persisted computed
        // column) -- what actually survives to reach propagation. Phase 6
        // (Propagation Batch) consumes from THIS, never from
        // DeliveredQuantity directly.
        public decimal NetQuantity => DeliveredQuantity - LossQuantity - RemovedQuantity - RejectedQuantity - DamagedQuantity;

        public string Status { get; set; } = "Completed";
        public int? ResponsiblePersonId { get; set; }
        public int? SupervisorId { get; set; }
        public string? Remarks { get; set; }

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public string? ModifiedBy { get; set; }

        // Display-only, populated by joins in CuttingDeliveryRepository.
        public string? ActualCuttingCode { get; set; }
        public string? MotherPlantCode { get; set; }
        public string? SpeciesName { get; set; }
        public string? PlantTypeName { get; set; }
        public string? ResponsiblePersonName { get; set; }
        public string? SupervisorName { get; set; }

        // Display-only, computed by CuttingDeliveryRepository from
        // PropagationBatches (Phase 6) -- sum of Quantity already taken
        // from this record's NetQuantity pool. Zero / not populated
        // until Phase 6's table exists.
        public decimal PropagatedQuantityRecorded { get; set; }

        public static bool IsWithinDelivered(decimal loss, decimal removed, decimal rejected, decimal damaged, decimal delivered)
            => loss + removed + rejected + damaged <= delivered;
    }
}
