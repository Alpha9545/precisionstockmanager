namespace PlantStockManager.Models
{
    public class Inventory
    {
        public int Id { get; set; }

        public int PlantId { get; set; }
        public int SpeciesId { get; set; } // FK to PlantSpecies
        public int PolyhouseId { get; set; } // FK to Polyhouses
        public int Quantity { get; set; }
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;


        public int SeedEntryId { get; set; }
        // Navigation Properties
        public PlantSpecies? Species { get; set; }
        public Polyhouse? Polyhouse { get; set; }



        public string PolyhouseName { get; set; }
        public string PlantTypeName { get; set; }
        public string SpeciesName { get; set; }

        public int InventoryTransactionId { get; set; }
        public DateTime InventoryTransactionDate { get; set; } 
        public int UtilizedQuantity {  get; set; }
        public string CustomerName { get; set; }

        public DateTime BookingDate { get; set; }

        public DateTime TentativeDeliveryDate { get; set; }

        public DateTime ActualDeliveryDate { get; set; }

        public int BookingId { get; set; }

        public string? LocationDesc { get; set; }

        public DateTime SeedingDate { get; set; }
        public int BookingQuantity { get; set; }

        public string Supervisor { get; set; }


        public int WastedInTrays { get; set; }
        public int WastedInHardening { get; set; }
        public int WastedInInventory { get; set; }
        public int WastedInSorting { get; set; }
        public int TotalWasted  { get; set; } 

        public string SeedsPlanted { get; set; }

        public string? State {get; set; }
        public string? District { get; set; }

        public string? Contact { get; set; }
    }
}
