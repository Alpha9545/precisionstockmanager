using System.ComponentModel.DataAnnotations;

namespace PlantStockManager.Models
{
    public class FertilizerType
    {
        public int FertilizerTypeId { get; set; }

        [Required]
        public string TypeName { get; set; }
        public string? Description { get; set; }
    }
}
