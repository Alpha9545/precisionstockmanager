namespace PlantStockManager.Models
{
    // One Main Area -> Mother Plant transfer (dbo.InternalTransfers, StockType
    // 'MotherPlantTransfer'), as shown on the transfer screens.
    public class MotherPlantTransfer
    {
        public int Id { get; set; }
        public string TransferCode { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string StatusLabel => PlantStockManager.Services.MotherPlantTransferRules.StatusLabel(Status);
        public DateTime TransferDate { get; set; }

        public int SourceReadyStockId { get; set; }
        public int SourceAreaId { get; set; }
        public string? SourceAreaName { get; set; }
        public string? SourcePolyhouseName { get; set; }
        public int DestinationAreaId { get; set; }
        public string? DestinationAreaName { get; set; }
        public int? DestinationPolyhouseId { get; set; }   // 2026-10-14: none for a transfer to the Area
        public string? DestinationPolyhouseName { get; set; }
        // 2026-10-14: MotherPlantTransferRules.PurposePotProduction / PurposeTrayTransplant; null = not recorded (older transfers).
        public string? TransferPurpose { get; set; }
        public string PurposeLabel => PlantStockManager.Services.MotherPlantTransferRules.PurposeLabel(TransferPurpose);
        public bool ForTrayTransplant => TransferPurpose != PlantStockManager.Services.MotherPlantTransferRules.PurposePotProduction;
        // 2026-10-15: received for Pot Production -> Pot Production from Ready Stock (PotBatch/CreateFromReadyStock)
        public bool ForPotProduction => TransferPurpose == PlantStockManager.Services.MotherPlantTransferRules.PurposePotProduction;

        public string? SpeciesName { get; set; }
        public string? PlantTypeName { get; set; }
        public string? BatchNo { get; set; }
        public string? SowingCode { get; set; }
        public string? CavityType { get; set; }
        public int? CavitySize => PlantStockManager.Services.DirectSowingRules.CavityCount(CavityType);

        public decimal Quantity { get; set; }            // plants sent
        public decimal? ReceivedQuantity { get; set; }   // plants received (Completed)
        public decimal? LossQuantity => ReceivedQuantity.HasValue ? Quantity - ReceivedQuantity.Value : null;
        public decimal? Trays => CavitySize is > 0 ? Quantity / CavitySize.Value : null;
        public int? ReceivedReadyStockId { get; set; }   // the batch created at the destination

        public string? Remarks { get; set; }
        public string? DiscrepancyReason { get; set; }   // shortfall reason (received) / rejection reason
        public string? CancelReason { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime CreatedDate { get; set; }
        public string? ConfirmedByName { get; set; }
        public DateTime? ConfirmedDate { get; set; }    // RECORDED time of the receipt / rejection (UTC)
        // 2026-10-10: BUSINESS date the plants arrived (2026-10-11_ReadyStockReceiptDate.sql). NULL for receipts
        // recorded before receipt dates existed -- those are dated when recorded; nothing is invented for them.
        public DateTime? ReceivedDate { get; set; }
        public string? ReceiptDateReason { get; set; }
        public bool ReceiptDateRecorded => ReceivedDate.HasValue;
        public string? CancelledByName { get; set; }
        public DateTime? CancelledDate { get; set; }
    }

    // One transplanting (dbo.ReadyStockTransplants).
    public class ReadyStockTransplant
    {
        public int Id { get; set; }
        public string TransplantCode { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTime TransplantDate { get; set; }

        public int SourceReadyStockId { get; set; }
        public int? OutputReadyStockId { get; set; }
        // 2026-10-13: the output is a growing tray batch in Sowing Approvals (Ready Stock only once approved).
        public int? OutputSeedSowingId { get; set; }
        public string? OutputSowingCode { get; set; }
        public string? OutputSowingStatus { get; set; }
        public DateTime? OutputReadyDate { get; set; }     // transplant date + Ready Days
        public int? OutputReadyDays { get; set; }
        public decimal OutputApprovedQuantity { get; set; }
        public bool OutputIsTrayBatch => OutputSeedSowingId.HasValue;
        public string? SourceOriginType { get; set; }
        public string? SourceAreaName { get; set; }
        public string? SourcePolyhouseName { get; set; }
        public string? SourceCavityType { get; set; }
        public int AreaId { get; set; }                 // target Mother Plant Area
        public string? AreaName { get; set; }
        public int? PolyhouseId { get; set; }           // target Polyhouse (2026-10-14: none -- the Area's trays)
        public string? PolyhouseName { get; set; }
        public string TargetCavityType { get; set; } = string.Empty;

        public int? SpeciesId { get; set; }
        public string? SpeciesName { get; set; }
        public string? PlantTypeName { get; set; }
        public string? BatchNo { get; set; }
        public string? SowingCode { get; set; }

        public decimal SelectedQuantity { get; set; }
        public decimal TransplantedQuantity { get; set; }
        public decimal LostQuantity { get; set; }
        // 2026-10-16 (2026-10-16_MotherPlantTransplantWaste.sql): why the untransplanted remainder / the discarded
        // seedlings (LostQuantity) were wasted. NULL for older rows. WasteOnly: no tray batch -- all seedlings discarded.
        public string? WasteReason { get; set; }
        public bool WasteOnly => TransplantedQuantity == 0 && LostQuantity > 0;
        public int OutputTrays { get; set; }
        public int? TargetCavitySize => PlantStockManager.Services.DirectSowingRules.CavityCount(TargetCavityType);
        public decimal PlantsInLastTray => TargetCavitySize is > 0 && OutputTrays > 0
            ? TransplantedQuantity - (OutputTrays - 1) * TargetCavitySize.Value : 0;

        public string? Notes { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; }
        public string? ReversedByName { get; set; }
        public DateTime? ReversedAt { get; set; }
        public string? ReversalReason { get; set; }
    }

    // Transplanting history filters (all optional).
    public class TransplantFilter
    {
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
        public int? AreaId { get; set; }
        public int? PolyhouseId { get; set; }
        public int? SpeciesId { get; set; }
        public string? CavityType { get; set; }
        public string? BatchNo { get; set; }
        public string? Status { get; set; }
    }

    // Management Dashboard: Mother Plant seedling flow for one India day.
    public class TransplantDaySummary
    {
        public decimal InTransitPlants { get; set; }
        public int PendingTransfers { get; set; }
        public decimal MotherPlantAreaReadyStock { get; set; }   // physical, plants, in Mother Plant Areas
        public decimal TransplantedToday { get; set; }
        public decimal LostToday { get; set; }
        public decimal TransferLossToday { get; set; }
        public List<(string CavityType, decimal Plants, int Trays)> OutputByCavityToday { get; } = new();
    }
}
