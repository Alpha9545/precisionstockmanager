namespace PlantStockManager.Models
{
    // A single row of dbo.UserRoles, joined for display. Represents
    // "this User has this Role, optionally scoped to this Area."
    public class UserRoleAssignment
    {
        public int Id { get; set; }

        public int UserId { get; set; }
        public string? UserName { get; set; }

        public int RoleId { get; set; }
        public string? RoleName { get; set; }

        public int? AreaId { get; set; }
        public string? AreaName { get; set; }

        // Phase 17/B: display-derived only, via Area.GrowingPartnerId --
        // never stored on UserRoles itself (no redundant GrowingPartnerId
        // column here, per the explicit instruction). Null both when the
        // assignment has no Area, and when the Area has no Growing
        // Partner (i.e. it is internally run) -- the two are
        // distinguished in the UI by AreaName instead.
        public string? GrowingPartnerName { get; set; }

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    }
}
