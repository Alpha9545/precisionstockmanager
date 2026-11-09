namespace PlantStockManager.Models
{
    // 2026-11-07 Bed Sowing Alerts: one OPEN bed (Pending / Partially transplanted, seedlings still in the bed) with its
    // variety's Bed Transplant Days (dbo.PlantSpecies.BedTransplantDays, read live -- not snapshotted, so a changed
    // schedule applies to open beds at once). Due Date = bed Sowing Date + Bed Transplant Days; null = no schedule.
    // Read-only: nothing here changes a bed. Categories: Services/BedTransplantAlertRules.cs.
    public class BedTransplantAlert
    {
        public int BedSowingId { get; set; }
        public string BedSowingCode { get; set; } = string.Empty;
        public int SpeciesId { get; set; }
        public string SpeciesName { get; set; } = string.Empty;
        public string? PlantTypeName { get; set; }
        public int AreaId { get; set; }
        public string? AreaName { get; set; }
        public string? PolyhouseName { get; set; }
        public string? SeedLotNo { get; set; }
        public DateTime SowingDate { get; set; }
        public decimal Quantity { get; set; }
        public decimal TransplantedQuantity { get; set; }
        public decimal LostQuantity { get; set; }
        public decimal RemainingQuantity => Quantity - TransplantedQuantity - LostQuantity;
        public string Status { get; set; } = BedSowing.StatusPending;
        public int? BedTransplantDays { get; set; }
        public DateTime? DueDate => Services.BedTransplantAlertRules.DueDate(SowingDate, BedTransplantDays);
        public string StatusLabel => Services.BedSowingRules.StatusLabel(Status);
    }

    // The four lists of the Bed Sowing Alerts page; the dashboard counts are taken from the SAME object
    // (BedTransplantAlertRules.Group), so a count can never differ from its list.
    public class BedTransplantAlertGroups
    {
        public List<BedTransplantAlert> Overdue { get; } = new();
        public List<BedTransplantAlert> DueToday { get; } = new();
        public List<BedTransplantAlert> DueSoon { get; } = new();
        public List<BedTransplantAlert> NoSchedule { get; } = new();

        public decimal OverdueQuantity => Overdue.Sum(a => a.RemainingQuantity);
        public decimal DueTodayQuantity => DueToday.Sum(a => a.RemainingQuantity);
        public decimal DueSoonQuantity => DueSoon.Sum(a => a.RemainingQuantity);
        public decimal NoScheduleQuantity => NoSchedule.Sum(a => a.RemainingQuantity);
    }
}
