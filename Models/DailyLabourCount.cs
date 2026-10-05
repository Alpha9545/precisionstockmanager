using PlantStockManager.Services;

namespace PlantStockManager.Models
{
    // One row of dbo.DailyLabourCounts: the daily labour HEADCOUNT of one
    // location (Main Office: Area + Polyhouse; other Areas: Area only; Outlet
    // is not part of Daily Labour).
    // Deliberately no worker names / ids / wages.
    public class DailyLabourCount
    {
        public int Id { get; set; }
        public DateTime LabourDate { get; set; }
        public int AreaId { get; set; }
        public int? PolyhouseId { get; set; }
        public int MaleFullDay { get; set; }
        public int MaleHalfDay { get; set; }
        public int FemaleFullDay { get; set; }
        public int FemaleHalfDay { get; set; }
        public string? Remarks { get; set; }

        public DateTime CreatedDate { get; set; }      // UTC
        public int? CreatedById { get; set; }
        public DateTime? ModifiedDate { get; set; }    // UTC
        public int? ModifiedById { get; set; }

        // Display-only, populated by joins in DailyLabourCountRepository.
        public string? AreaName { get; set; }
        public string? AreaType { get; set; }
        public string? PolyhouseName { get; set; }
        public string? CreatedByName { get; set; }
        public string? ModifiedByName { get; set; }

        public int TotalMale => MaleFullDay + MaleHalfDay;
        public int TotalFemale => FemaleFullDay + FemaleHalfDay;
        public int TotalWorkers => DailyLabourRules.TotalWorkers(MaleFullDay, MaleHalfDay, FemaleFullDay, FemaleHalfDay);
        public decimal LabourDays => DailyLabourRules.LabourDays(MaleFullDay, MaleHalfDay, FemaleFullDay, FemaleHalfDay);
    }

    public class DailyLabourFilter
    {
        public DateTime? FromDate { get; set; }
        public DateTime? ToDate { get; set; }
        public int? AreaId { get; set; }
        public int? PolyhouseId { get; set; }
    }

    // Totals over EVERY row matching a filter (computed in SQL, not just the
    // rows shown on the page).
    public class DailyLabourSummary
    {
        public int Records { get; set; }
        public int MaleFullDay { get; set; }
        public int MaleHalfDay { get; set; }
        public int FemaleFullDay { get; set; }
        public int FemaleHalfDay { get; set; }

        public int TotalMale => MaleFullDay + MaleHalfDay;
        public int TotalFemale => FemaleFullDay + FemaleHalfDay;
        public int TotalWorkers => DailyLabourRules.TotalWorkers(MaleFullDay, MaleHalfDay, FemaleFullDay, FemaleHalfDay);
        public decimal LabourDays => DailyLabourRules.LabourDays(MaleFullDay, MaleHalfDay, FemaleFullDay, FemaleHalfDay);
    }
}
