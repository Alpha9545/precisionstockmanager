namespace PlantStockManager.Models
{
    public class Role
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool IsSystemRole { get; set; }
        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

        // Display-only aggregates (Phase A Role Management page).
        public int PermissionCount { get; set; }
        public int UserCount { get; set; }
    }
}
