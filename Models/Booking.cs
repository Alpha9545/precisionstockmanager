using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using System.ComponentModel.DataAnnotations;

namespace PlantStockManager.Models
{
    public class Booking
    {
        public int Id { get; set; }
        public int PlantId { get; set; }
        public int SpeciesId { get; set; } // FK to PlantSpecies
        public int Quantity { get; set; }
        public string Status { get; set; } = "Pending";
        public string CustomerName { get; set; }
        public DateTime BookingDate { get; set; } = DateTime.UtcNow;

        public DateTime DeliveryDate { get; set; } 

        public string AddedBy { get; set; }

        public string BookingType { get; set; }



        public string Address { get; set; }

        public string Contact { get; set; }

        public int ReturnedQuantity { get; set; } = 0;

        public DateTime? ActualDeliveryDate { get; set; }


        public bool AdvanceTaken { get; set; } // Changed to bool

        public decimal? AdvanceTakenAmount { get; set; } // Nullable, shown if AdvanceTaken is Yes
        public string? AdvanceTakenDetails { get; set; } // Nullable

        public int? BookedById { get; set; }

        public string? BookedByOther { get; set; }


        // Navigation Property
        public PlantSpecies? Species { get; set; }


        public string? PlantTypeName { get; set; }
        public string? SpeciesName { get; set; }

        public string? BookedByName { get; set; }



        public int? StateId { get; set; }
        public int? DistrictId { get; set; }

        [BindNever, ValidateNever]
        public string? StateName { get; set; }
        [BindNever, ValidateNever]
        public string? DistrictName { get; set; }



    }
}
