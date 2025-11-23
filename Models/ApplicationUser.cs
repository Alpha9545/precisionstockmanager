namespace PlantStockManager.Models
{
    public class ApplicationUser
    {
        public int Id { get; set; }
        public string UserName { get; set; }
        public string PasswordHash { get; set; }
        public int RoleId { get; set; }
    }

    public class ApplicationRole
    {
        public int Id { get; set; }
        public string RoleName { get; set; }
    }
}
