namespace PlantStockManager.Models
{
    public class SeedEntries
    {
        public int Id { get; set; }
        public int PlantId { get; set; }
        public int SpeciesId { get; set; } // FK to PlantSpecies
        public int PolyhouseId { get; set; }
        public int SeedSourceId { get; set; }
        // FK to Polyhouses
        public DateTime SeedingDate { get; set; }
        public int SeedsPlanted { get; set; }

        public string? Supervisor { get; set; }

        //public int? SowingById { get; set; }


        public bool ReadyForInventory { get; set; } = false;



        public int? HardeningAlive { get; set; }
        public int? TraysAlive { get; set; }
       
        public int? AliveCount { get; set; } // Nullable, manually entered after 21 days
        public string? CurrentStage { get; set; } 
        public string? CreatedBy { get; set; } // UserId from AspNetUsers
        public DateTime CreatedOn { get; set; } = DateTime.UtcNow;
        public string? OtherSeedSource { get; set; }

        public DateTime? TraysDate { get; set; }
        public DateTime? HardeningDate { get; set; }
        public DateTime? InventoryDate { get; set; }
        public string? Location { get; set; } // "inside" or "outside"
        public string? locationDesc { get; set; }

        public int SowingById { get; set; } // UserId from AspNetUsers




        //Navigation Properties
        public PlantType? PlantType { get; set; }
        public PlantSpecies? Species { get; set; }
        public Polyhouse? Polyhouse { get; set; }



        // 🔹 Additional String Properties to Store Names from SQL Query

        public string? PolyhouseName { get; set; }
        public string? PlantTypeName { get; set; }
        public string? SpeciesName { get; set; }
        public int? DaysPassed { get; set; }

        public string? SeedSourceName { get; set; }

    }
}
