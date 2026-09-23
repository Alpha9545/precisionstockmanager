namespace PlantStockManager.Models
{
    public class Permission
    {
        public int Id { get; set; }
        public string Code { get; set; } = string.Empty;
        public string? Description { get; set; }

        // Set only when this Permission is being displayed alongside a
        // particular Role (e.g. on the Roles admin page's checkbox grid).
        // Not persisted.
        public bool IsGrantedToRole { get; set; }
    }
}
