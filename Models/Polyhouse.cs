using System.ComponentModel.DataAnnotations;

namespace PlantStockManager.Models
{
    public class Polyhouse
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;

        // Phase B: the Area (site) this Polyhouse belongs to
        // (dbo.Polyhouses.AreaId). Null until an administrator assigns it.
        public int? AreaId { get; set; }
        public string? AreaName { get; set; }
        public bool AreaIsActive { get; set; }
    }
}
