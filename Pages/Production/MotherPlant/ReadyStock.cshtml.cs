using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using ReadyBatch = PlantStockManager.Models.ReadyStock;
using PottedPlantStockModel = PlantStockManager.Models.PottedPlantStock;

namespace PlantStockManager.Pages.Production.MotherPlant
{
    // 2026-10-17 (owner requirement 2026-10-12): Mother Plant & Cutting > Ready Stock -- the READY stock of the user's
    // Mother Plant Areas in one place, as two SEPARATE stock domains read from their own authoritative rows:
    //   Ready trays  dbo.ReadyStock in a Mother Plant Area (tray batches approved in Sowing Approvals, received batches):
    //                plants = Quantity - Dispatched - In transit, available = also minus Reserved (the existing columns)
    //   Ready pots   dbo.PottedPlantStock in a Mother Plant Area (pot batches confirmed READY): physical / reserved / available
    // Nothing is added across the two (a tray of seedlings and a pot are different units). Read only: every action is the
    // existing page of that stock type, which re-checks the Area and the quantities under lock --
    //   trays -> Send to Main Office / Outlet (ReadyStock/Move, whole sowing batch); customer bookings: Seedling Booking;
    //            Sell to customer (MotherPlant/SellTrays, direct sale in whole trays). 2026-10-19: each action is always shown,
    //            disabled with the reason when the batch is not eligible (AreaReadyStockActionRules).
    //   pots  -> Customer sale or Main Office / Outlet transfer (PottedPlantStock/Move)
    // Area scope: the regular Area rule, always enforced (Mother Plant work has no Development switch).
    public class AreaReadyStockModel : PageModel
    {
        private readonly ReadyStockRepository _readyStockRepo;
        private readonly PottedPlantStockRepository _pottedRepo;
        private readonly MotherPlantTransferRepository _transferRepo;
        private readonly AreaRepository _areaRepo;
        private readonly AreaAccessService _areaAccess;
        private readonly IAuthorizationService _authorization;
        private readonly DatabaseHelper _db;

        public AreaReadyStockModel(ReadyStockRepository readyStockRepo, PottedPlantStockRepository pottedRepo, MotherPlantTransferRepository transferRepo,
            AreaRepository areaRepo, AreaAccessService areaAccess, IAuthorizationService authorization, DatabaseHelper db)
        {
            _db = db;
            _readyStockRepo = readyStockRepo;
            _pottedRepo = pottedRepo;
            _transferRepo = transferRepo;
            _areaRepo = areaRepo;
            _areaAccess = areaAccess;
            _authorization = authorization;
        }

        public List<Area> Areas { get; private set; } = new();
        public List<ReadyBatch> Trays { get; private set; } = new();
        public List<PottedPlantStockModel> Pots { get; private set; } = new();
        public ILookup<int, PottedPlantStockRepository.ReadySource> PotSources { get; private set; } = Enumerable.Empty<PottedPlantStockRepository.ReadySource>().ToLookup(s => s.PoolId);
        public HashSet<int> PotProductionBatchIds { get; private set; } = new();
        public bool CanSendTrays { get; private set; }
        public bool CanMovePots { get; private set; }
        public bool CanBookSeedlings { get; private set; }
        public bool CanSellTrays { get; private set; }   // 2026-10-18: direct tray sale (Outlet.Sell)
        public bool SaleSchemaReady { get; private set; } // 2026-10-18_SeedlingDirectSaleSubmissionToken.sql

        // 2026-10-19: both tray actions, or why not (Services/AreaReadyStockActionRules.cs -- the server re-checks all of it).
        public Services.AreaReadyStockActionRules.Availability SendAction(ReadyBatch t)
            => Services.AreaReadyStockActionRules.SendToOutlet(t, CanSendTrays, PotProductionBatchIds.Contains(t.Id));
        public Services.AreaReadyStockActionRules.Availability SellAction(ReadyBatch t)
            => Services.AreaReadyStockActionRules.SellToCustomer(t, CanSellTrays, PotProductionBatchIds.Contains(t.Id), SaleSchemaReady);

        public async Task OnGetAsync()
        {
            var full = _areaAccess.HasFullAreaAccess(User);
            Areas = (await _areaRepo.GetAllAreas())
                .Where(a => a.IsActive && a.AreaType == MotherPlantAreaSchema.MotherPlantAreaType && (full || _areaAccess.CanAccessArea(User, a.Id)))
                .OrderBy(a => a.Name).ToList();
            var areaIds = Areas.Select(a => a.Id).ToHashSet();

            Trays = (await _readyStockRepo.GetAllAsync())
                .Where(b => areaIds.Contains(b.AreaId) && b.PhysicalQuantity > 0)
                .OrderBy(b => b.AreaName).ThenBy(b => b.SpeciesName).ThenBy(b => b.Id).ToList();
            Pots = (await _pottedRepo.GetAllAsync())
                .Where(p => p.AreaId.HasValue && areaIds.Contains(p.AreaId.Value) && p.PhysicalQuantity > 0)
                .OrderBy(p => p.AreaName).ThenBy(p => p.SpeciesName).ThenBy(p => p.PotSize).ToList();
            PotSources = (await _pottedRepo.GetReadySourcesAsync(Pots.Select(p => p.Id).ToList())).ToLookup(s => s.PoolId);
            PotProductionBatchIds = await _transferRepo.PotProductionBatchIdsAsync();

            CanSendTrays = (await _authorization.AuthorizeAsync(User, Pages.Production.ReadyStock.MoveModel.TransferPolicy)).Succeeded;
            CanMovePots = (await _authorization.AuthorizeAsync(User, Pages.Production.PottedPlantStock.MoveModel.TransferPolicy)).Succeeded
                          || (await _authorization.AuthorizeAsync(User, Pages.Production.PottedPlantStock.MoveModel.SellPolicy)).Succeeded;
            CanBookSeedlings = User.HasPermission("Booking.Enter");
            CanSellTrays = (await _authorization.AuthorizeAsync(User, SellTraysModel.SellPolicy)).Succeeded;
            SaleSchemaReady = await WorkflowCorrectionsSchema.SeedlingSaleTokenAsync(_db);
        }
    }
}
