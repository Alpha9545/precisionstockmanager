namespace PlantStockManager.Models
{
    // Field Work Phase 2 (2026-11-03): dbo.FieldCustomers / FieldVisits / FieldCustomerNotes / FieldFollowUps rows. Times
    // are UTC as stored; pages show India time.
    public class FieldCustomer
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string? ContactPerson { get; set; }
        public string Phone { get; set; } = "";
        public string? AlternatePhone { get; set; }
        public string CustomerType { get; set; } = "";
        public string VillageCity { get; set; } = "";
        public string? District { get; set; }
        public string? Address { get; set; }
        public decimal? Latitude { get; set; }
        public decimal? Longitude { get; set; }
        public decimal? AccuracyM { get; set; }
        public string? GpsExceptionReason { get; set; }
        public string? FlagReasons { get; set; }
        public int? PhotoId { get; set; }
        public string LeadStatus { get; set; } = "";
        public int AssignedUserId { get; set; }
        public string AssignedName { get; set; } = "";
        public bool AssignedUserActive { get; set; }
        public int CreatedByUserId { get; set; }
        public string CreatedByName { get; set; } = "";
        public DateTime CreatedAtUtc { get; set; }
        public DateTime? ModifiedAtUtc { get; set; }
        public bool IsActive { get; set; }
        public DateTime? ArchivedAtUtc { get; set; }
        public string? ArchivedByName { get; set; }
        public string? ArchiveReason { get; set; }
    }

    // One row of the customer list (phone already masked when the viewer may not see it).
    public class FieldCustomerListRow
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string? ContactPerson { get; set; }
        public string PhoneShown { get; set; } = "";
        public string CustomerType { get; set; } = "";
        public string VillageCity { get; set; } = "";
        public string? District { get; set; }
        public string LeadStatus { get; set; } = "";
        public int AssignedUserId { get; set; }
        public string AssignedName { get; set; } = "";
        public bool AssignedUserActive { get; set; }
        public bool IsActive { get; set; }
        public DateTime? LastVisitAtUtc { get; set; }
        public DateTime? PendingFollowUpDate { get; set; }
    }

    public class FieldVisit
    {
        public int Id { get; set; }
        public int CustomerId { get; set; }
        public int UserId { get; set; }
        public string EmployeeName { get; set; } = "";
        public int FieldDayId { get; set; }
        public DateTime CheckInAtUtc { get; set; }
        public decimal? Latitude { get; set; }
        public decimal? Longitude { get; set; }
        public decimal? AccuracyM { get; set; }
        public string? GpsExceptionReason { get; set; }
        public string? FlagReasons { get; set; }
        public string Discussed { get; set; } = "";
        public string? Notes { get; set; }
        public string LeadStatusBefore { get; set; } = "";
        public string LeadStatusAfter { get; set; } = "";
        public string? ModifiedByName { get; set; }
        public DateTime? ModifiedAtUtc { get; set; }
        public List<FieldVisitItem> Items { get; set; } = new();
        public List<int> PhotoIds { get; set; } = new();
    }

    public class FieldVisitItem
    {
        public int LineNumber { get; set; }
        public string ItemText { get; set; } = "";
        public decimal? Quantity { get; set; }
    }

    public class FieldCustomerNote
    {
        public int Id { get; set; }
        public int CustomerId { get; set; }
        public int? VisitId { get; set; }
        public int UserId { get; set; }
        public string UserName { get; set; } = "";
        public string NoteType { get; set; } = "";
        public string Text { get; set; } = "";
        public string? LeadStatusBefore { get; set; }
        public string? LeadStatusAfter { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }

    public class FieldFollowUp
    {
        public int Id { get; set; }
        public int CustomerId { get; set; }
        public string CustomerName { get; set; } = "";
        public string VillageCity { get; set; } = "";
        public string LeadStatus { get; set; } = "";
        public int AssignedUserId { get; set; }
        public string AssignedName { get; set; } = "";
        public DateTime DueDate { get; set; }
        public string? Note { get; set; }
        public string Status { get; set; } = "";
        public string CreatedByName { get; set; } = "";
        public DateTime CreatedAtUtc { get; set; }
        public DateTime? ClosedAtUtc { get; set; }
        public string? ClosedByName { get; set; }
        public int? ClosedByVisitId { get; set; }
        public int? ClosedByNoteId { get; set; }
        public string? CancelReason { get; set; }
    }

    public class FieldCustomerEvent
    {
        public int Id { get; set; }
        public int? VisitId { get; set; }
        public int? FollowUpId { get; set; }
        public string EventType { get; set; } = "";
        public string? FromStatus { get; set; }
        public string? ToStatus { get; set; }
        public string? FromUserName { get; set; }
        public string? ToUserName { get; set; }
        public string ByName { get; set; } = "";
        public DateTime AtUtc { get; set; }
        public string? Details { get; set; }
    }

    // Everything the customer page shows, newest first in the page.
    public class FieldCustomerTimeline
    {
        public List<FieldVisit> Visits { get; set; } = new();
        public List<FieldCustomerNote> Notes { get; set; } = new();
        public List<FieldFollowUp> FollowUps { get; set; } = new();
        public List<FieldCustomerEvent> Events { get; set; } = new();
    }

    public sealed record FieldFollowUpCounts(int DueToday, int Overdue)
    {
        public int Total => DueToday + Overdue;
    }
}
