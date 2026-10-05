using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using System.ComponentModel.DataAnnotations;

namespace PlantStockManager.Models
{
    public class Booking
    {
        public int Id { get; set; }

        // The customer order this variety line belongs to: the order's first line Id
        // (the Booking Number). NULL = a single-line order numbered by its own Id.
        // Never bound from a form -- set only by BookingRepository.InsertOrderAsync
        // and the Revise "Add variety" split.
        [BindNever, ValidateNever]
        public int? OrderBookingId { get; set; }

        // The Booking Number shown to users: ISNULL(OrderBookingId, Id).
        [BindNever, ValidateNever]
        public int EffectiveOrderId => OrderBookingId ?? Id;

        // How many variety lines the whole order has (display only; a list filter may show fewer).
        [BindNever, ValidateNever]
        public int OrderLineCount { get; set; } = 1;
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
