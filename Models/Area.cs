namespace PlantStockManager.Models
{
    public class Area
    {
        public int Id { get; set; }

        // Polyhouses belong to an Area through dbo.Polyhouses.AreaId (the
        // retired dbo.Area.PolyhouseId column is not mapped).

        // Explicit default (was missing before Phase 14) so an unbound/
        // not-yet-set Name is an empty string, not null -- this is what
        // makes the framework's OWN implicit-required check for this
        // non-nullable reference type land on ValidateArea()'s friendly
        // "Area Name is required." message instead of surfacing first as
        // the framework's generic "The Name field is required." (see the
        // redesign plan's Section A9 for the full root-cause explanation).
        public string Name { get; set; } = string.Empty;
        public string? AreaCode { get; set; }

        public decimal? AreaSize { get; set; }
        public string? AreaUnit { get; set; }

        public decimal? Capacity { get; set; }
        public string? CapacityUnit { get; set; }

        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Phase 14: role/workflow redesign fields.
        // AreaType: 'MotherPlant' | 'Kunjir' | 'Kiran' | 'Outlet' | 'MainOffice' | null (plain/legacy Area).
        public string? AreaType { get; set; }
        public int? SupervisorId { get; set; }
        public string? Location { get; set; }
        public string? Remarks { get; set; }

        // Phase 17: nullable so all existing Area rows are unaffected. NULL
        // = internally run (today's behavior, unchanged). Not NULL = this
        // Area's stock/production belongs to the referenced Growing
        // Partner. See GrowingPartner.cs for why this is deliberately NOT
        // dbo.Vendors.
        public int? GrowingPartnerId { get; set; }

        // Display-only, populated by joins in AreaRepository. Not persisted.
        public string? PolyhouseName { get; set; }
        public string? SupervisorName { get; set; }
        public string? GrowingPartnerName { get; set; }
    }
}
