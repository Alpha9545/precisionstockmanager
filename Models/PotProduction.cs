namespace PlantStockManager.Models
{
    public class PotProduction
    {
        public int Id { get; set; }
        public string ProductionCode { get; set; } = string.Empty;

        // Phase 19: exactly ONE of the two source fields below is ever
        // populated (enforced by CK_PotProduction_SourceType) --
        //   PropagationBatchId set  -> legacy path (Phase 6/7), unchanged.
        //   SourceCuttingStockId set -> new path (Phase 19 / "Phase D"):
        //     a Growing Partner's dbo.CuttingStock consumed directly into
        //     Pot Production.
        // MotherPlantId/SpeciesId are always copied server-side from
        // whichever source is used (never independently chosen) -- see
        // Database/Phase7_PotProduction.sql's
        // CK_PotProduction_MatchesPropagationBatch (legacy path) and
        // Data/PotProductionRepository.cs.InsertFromCuttingStockAsync
        // (new path) for where each is derived. MotherPlantId is only
        // ever populated for the legacy path -- a Cutting Stock-sourced
        // row has no Mother Plant lineage at all.
        public int? PropagationBatchId { get; set; }
        public int? MotherPlantId { get; set; }
        public int SpeciesId { get; set; }

        // Phase 19: the alternate source -- a specific dbo.CuttingStock
        // row (Growing Partner cutting stock) consumed into this
        // production, instead of a Propagation Batch. Never both this
        // and PropagationBatchId populated.
        public int? SourceCuttingStockId { get; set; }

        // Phase 19: how many cuttings were consumed to produce Quantity
        // potted plants, for the Cutting-sourced path only (NULL for the
        // legacy path -- it has no separate "cuttings consumed" concept,
        // Quantity alone already means both sides there). Always >=
        // Quantity when set (CK_PotProduction_CuttingQuantityConsumed) --
        // the shortfall is potting loss.
        public decimal? CuttingQuantityConsumed { get; set; }

        // Production loss for the Cutting-sourced path, e.g. 1000
        // cuttings consumed / 570 potted plants produced = 430 loss.
        // Deliberately NOT a stored column -- it is fully derived from
        // the two fields above, so storing it would just be a third
        // quantity column carrying no new information (the "no
        // redundant quantity columns" rule). NULL for the legacy path
        // (no CuttingQuantityConsumed to compare against).
        public decimal? Loss => CuttingQuantityConsumed.HasValue ? CuttingQuantityConsumed.Value - Quantity : (decimal?)null;

        public string PotSize { get; set; } = string.Empty;

        // The specific EmptyPotInventory pool (PotSize + Area) consumed
        // from, and the Area the resulting Potted Plant Stock was
        // produced into (same Area for both -- production happens where
        // the pots physically are). Nullable to mirror
        // EmptyPotInventory.AreaId/PottedPlantStock.AreaId exactly: null
        // means the legacy/"unassigned location" pool, not Area zero.
        // For the Cutting-sourced path this is always the source
        // CuttingStock row's own (non-nullable) AreaId -- the Growing
        // Partner's Area remains the authoritative Area throughout,
        // never independently chosen (Phase 19 spec item 14).
        public int EmptyPotInventoryId { get; set; }
        public int? AreaId { get; set; }

        public DateTime ProductionDate { get; set; } = DateTime.Today;

        // Number of empty pots consumed = number of potted plants
        // produced. Same meaning for BOTH paths -- never redefined.
        public decimal Quantity { get; set; }

        public string Status { get; set; } = "Completed";
        public int? ResponsiblePersonId { get; set; }
        public int? SupervisorId { get; set; }
        public string? Remarks { get; set; }

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public string? ModifiedBy { get; set; }

        // Display-only, populated by joins in PotProductionRepository.
        // PropagationBatchCode/MotherPlantCode are NULL for a
        // Cutting-sourced row (LEFT JOINed, since PropagationBatchId/
        // MotherPlantId are NULL for it).
        public string? PropagationBatchCode { get; set; }
        public string? MotherPlantCode { get; set; }
        public string? SpeciesName { get; set; }
        public string? PlantTypeName { get; set; }
        public string? AreaName { get; set; }
        public string? ResponsiblePersonName { get; set; }
        public string? SupervisorName { get; set; }

        // Phase 19 display-only additions. GrowingPartnerName comes
        // from AreaId -> Area.GrowingPartnerId -> GrowingPartners.Name,
        // so it can be populated for ANY row whose Area belongs to a
        // Growing Partner (in practice, today, only Cutting-sourced
        // rows -- legacy Propagation Batch production happens on
        // internally-run Areas).
        public string? GrowingPartnerName { get; set; }

        // Convenience for views: which source this row actually used.
        public bool IsCuttingSourced => SourceCuttingStockId.HasValue;
    }
}
