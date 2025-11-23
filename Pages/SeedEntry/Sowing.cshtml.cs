using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;
using Microsoft.Data.SqlClient;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PlantStockManager.Pages.SeedEntry
{
    [Authorize]
    public class SeedEntryModel : PageModel
    {
        private readonly SeedSourcesRepository _seedSourcesRepo;
        private readonly PolyhouseRepository _polyhouseRepo;
        private readonly PlantTypeRepository _plantTypeRepo;
        private readonly PlantSpeciesRepository _plantSpeciesRepo;
        private readonly SeedEntryRepository _seedEntryRepo;
        private readonly EmployeeRepository _employeeRepo;
        private readonly SeedBankRepository _seedBankRepo;
        private readonly TransactionRepository _txRepo;

        public SeedEntryModel(
            SeedSourcesRepository seedSourcesRepo,
            PolyhouseRepository polyhouseRepo,
            PlantTypeRepository plantTypeRepo,
            PlantSpeciesRepository plantSpeciesRepo,
            SeedEntryRepository seedEntryRepo,
            EmployeeRepository employeeRepo,
            SeedBankRepository seedBankRepo,
            TransactionRepository txRepo)
        {
            _seedSourcesRepo = seedSourcesRepo;
            _polyhouseRepo = polyhouseRepo;
            _plantTypeRepo = plantTypeRepo;
            _plantSpeciesRepo = plantSpeciesRepo;
            _seedEntryRepo = seedEntryRepo;
            _employeeRepo = employeeRepo;
            _seedBankRepo = seedBankRepo;
            _txRepo = txRepo;
        }

        public List<SeedSource> SeedSources { get; set; }
        public List<Polyhouse> Polyhouses { get; set; }
        public List<PlantType> PlantTypes { get; set; }
        public List<PlantSpecies> Species { get; set; } = new();
        public List<Employee> Employees { get; set; }

        [BindProperty] public SeedEntries SeedEntries { get; set; }

        public async Task OnGet()
        {
            SeedSources = await _seedSourcesRepo.GetAllSeedSources();
            Polyhouses = await _polyhouseRepo.GetAllPolyhouses();
            PlantTypes = await _plantTypeRepo.GetAllPlantTypes();
            Employees = await _employeeRepo.GetAllEmployeesSowing();
        }

        // Species by plant type (AJAX)
        public async Task<JsonResult> OnGetSpeciesByPlantType(int plantTypeId)
        {
            var species = await _plantSpeciesRepo.GetSpeciesByPlantType(plantTypeId);
            return new JsonResult(species);
        }

        // Live availability (AJAX)
        public async Task<JsonResult> OnGetAvailableAsync( int plantId, int speciesId)
        {
            var available = await _seedBankRepo.GetAvailableAsync(plantId, speciesId);
            return new JsonResult(new { available });
        }

        public async Task<IActionResult> OnPostAsync()
        {
            SeedEntries.CreatedBy = User.Identity.Name; // TODO: use User.Identity.Name
            ModelState.Remove("SeedEntries.CreatedBy");

            if (!ModelState.IsValid)
            {
                SeedSources = await _seedSourcesRepo.GetAllSeedSources();
                Polyhouses = await _polyhouseRepo.GetAllPolyhouses();
                PlantTypes = await _plantTypeRepo.GetAllPlantTypes();
                Employees = await _employeeRepo.GetAllEmployeesSowing();
                return Page();
            }

            // "Others" handling (if SeedSourceId == 0, ensure OtherSeedSource provided).
            //if (SeedEntries.SeedSourceId == 0 && string.IsNullOrWhiteSpace(SeedEntries.OtherSeedSource))
            //{
            //    return new JsonResult(new { success = false, message = "Please enter a name for the other seed source." });
            //}
            // Optionally create the "Others" SeedSource here and set SeedEntries.SeedSourceId.

            var requestedQty = SeedEntries.SeedsPlanted;
            if (requestedQty <= 0)
                return new JsonResult(new { success = false, message = "Quantity must be greater than zero." });

            // Server-side availability check
            var available = await _seedBankRepo.GetAvailableAsync(SeedEntries.PlantId, SeedEntries.SpeciesId);
            if (available <= 0)
                return new JsonResult(new { success = false, message = "No seeds/cuttings available for the selected source/plant/variety." });
            if (requestedQty > available)
                return new JsonResult(new { success = false, message = $"Only {available} available. Please reduce quantity." });

            // Atomic: SeedEntry -> Transactions (Sowing) -> SeedBank utilize
            using var conn = _seedBankRepo.Db.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                // 1) Insert SeedEntry
                var seedEntryId = await _seedEntryRepo.InsertSeedEntryReturnIdAsync(conn, tx, SeedEntries);

                // 2) Insert a Sowing transaction
                await _txRepo.InsertSowingTransactionAsync(conn, tx, seedEntryId, requestedQty, SeedEntries.CreatedBy);

                // 3) Deduct from Seed/Cutting Bank FIFO + audit SeedCuttingTx
                await _seedBankRepo.ConsumeAsync(
                    conn, tx,
                    SeedEntries.PlantId, SeedEntries.SpeciesId,
                    requestedQty, seedEntryId, userName: SeedEntries.CreatedBy, remarks: "Sowing");

                tx.Commit();
                return new JsonResult(new { success = true, message = "Sowing entry added!" });
            }
            catch (SqlException ex)
            {
                tx.Rollback();
                return new JsonResult(new { success = false, message = ex.Message });
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return new JsonResult(new { success = false, message = "Unexpected error: " + ex.Message });
            }
        }
    }
}
