using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;

namespace PlantStockManager.Pages.Production.Dashboard
{
    // Read-only reporting/dashboard, purely additive: aggregates figures
    // from every phase's own repository (no new tables, no writes, and
    // no changes to any existing report/export page). Traceability
    // through the chain (Mother Plant -> ... -> Dispatch) is available
    // per-record on each module's own Details page; this dashboard is
    // the fleet-level summary view across all of them.
    public class IndexModel : PageModel
    {
        private readonly MotherPlantRepository _motherPlantRepo;
        private readonly CuttingPlanRepository _cuttingPlanRepo;
        private readonly ActualCuttingRepository _actualCuttingRepo;
        private readonly CuttingDeliveryRepository _cuttingDeliveryRepo;
        private readonly PropagationBatchRepository _propagationBatchRepo;
        private readonly PotProductionRepository _potProductionRepo;
        private readonly EmptyPotInventoryRepository _emptyPotInventoryRepo;
        private readonly PottedPlantStockRepository _pottedPlantStockRepo;
        private readonly PottedPlantBookingRepository _pottedPlantBookingRepo;
        private readonly DispatchRepository _dispatchRepo;
        private readonly PurchaseOrderRepository _purchaseOrderRepo;
        private readonly LabRequestRepository _labRequestRepo;
        private readonly LabourLogRepository _labourLogRepo;

        public IndexModel(
            MotherPlantRepository motherPlantRepo,
            CuttingPlanRepository cuttingPlanRepo,
            ActualCuttingRepository actualCuttingRepo,
            CuttingDeliveryRepository cuttingDeliveryRepo,
            PropagationBatchRepository propagationBatchRepo,
            PotProductionRepository potProductionRepo,
            EmptyPotInventoryRepository emptyPotInventoryRepo,
            PottedPlantStockRepository pottedPlantStockRepo,
            PottedPlantBookingRepository pottedPlantBookingRepo,
            DispatchRepository dispatchRepo,
            PurchaseOrderRepository purchaseOrderRepo,
            LabRequestRepository labRequestRepo,
            LabourLogRepository labourLogRepo)
        {
            _motherPlantRepo = motherPlantRepo;
            _cuttingPlanRepo = cuttingPlanRepo;
            _actualCuttingRepo = actualCuttingRepo;
            _cuttingDeliveryRepo = cuttingDeliveryRepo;
            _propagationBatchRepo = propagationBatchRepo;
            _potProductionRepo = potProductionRepo;
            _emptyPotInventoryRepo = emptyPotInventoryRepo;
            _pottedPlantStockRepo = pottedPlantStockRepo;
            _pottedPlantBookingRepo = pottedPlantBookingRepo;
            _dispatchRepo = dispatchRepo;
            _purchaseOrderRepo = purchaseOrderRepo;
            _labRequestRepo = labRequestRepo;
            _labourLogRepo = labourLogRepo;
        }

        // Production pipeline
        public int MotherPlantActiveCount { get; set; }
        public decimal MotherPlantTotalQuantity { get; set; }
        public Dictionary<string, int> CuttingPlanByStatus { get; set; } = new();
        public int ActualCuttingCount { get; set; }
        public decimal ActualCuttingGoodQuantityTotal { get; set; }
        public Dictionary<string, int> CuttingDeliveryByStatus { get; set; } = new();
        public Dictionary<string, int> PropagationBatchByStatus { get; set; } = new();
        public Dictionary<string, int> PotProductionByStatus { get; set; } = new();

        // Stock
        public decimal EmptyPotPhysicalTotal { get; set; }
        public decimal PottedPhysicalTotal { get; set; }
        public decimal PottedReservedTotal { get; set; }
        public decimal PottedAvailableTotal { get; set; }

        // Booking / Dispatch
        public Dictionary<string, int> BookingByStatus { get; set; } = new();
        public decimal BookingReservedTotal { get; set; }
        public int DispatchCompletedCount { get; set; }
        public decimal DispatchQuantityTotal { get; set; }

        // Purchase / Lab / Labour
        public Dictionary<string, int> PurchaseOrderByStatus { get; set; } = new();
        public int LabRequestOutstandingCount { get; set; }
        public decimal LabRequestOutstandingQuantity { get; set; }
        public decimal LabourWageTotal { get; set; }

        public async Task OnGetAsync()
        {
            var motherPlants = await _motherPlantRepo.GetAllAsync();
            MotherPlantActiveCount = motherPlants.Count(m => m.Status == "Active");
            MotherPlantTotalQuantity = motherPlants.Where(m => m.Status == "Active").Sum(m => m.MotherPlantQuantity);

            var cuttingPlans = await _cuttingPlanRepo.GetAllAsync();
            CuttingPlanByStatus = cuttingPlans.GroupBy(c => c.Status).ToDictionary(g => g.Key, g => g.Count());

            var actualCuttings = await _actualCuttingRepo.GetAllAsync();
            ActualCuttingCount = actualCuttings.Count;
            ActualCuttingGoodQuantityTotal = actualCuttings.Sum(a => a.GoodQuantity);

            var cuttingDeliveries = await _cuttingDeliveryRepo.GetAllAsync();
            CuttingDeliveryByStatus = cuttingDeliveries.GroupBy(c => c.Status).ToDictionary(g => g.Key, g => g.Count());

            var propagationBatches = await _propagationBatchRepo.GetAllAsync();
            PropagationBatchByStatus = propagationBatches.GroupBy(p => p.Status).ToDictionary(g => g.Key, g => g.Count());

            var potProductions = await _potProductionRepo.GetAllAsync();
            PotProductionByStatus = potProductions.GroupBy(p => p.Status).ToDictionary(g => g.Key, g => g.Count());

            var emptyPots = await _emptyPotInventoryRepo.GetAllAsync(activeOnly: true);
            EmptyPotPhysicalTotal = emptyPots.Sum(e => e.PhysicalQuantity);

            var pottedStock = await _pottedPlantStockRepo.GetAllAsync();
            PottedPhysicalTotal = pottedStock.Sum(s => s.PhysicalQuantity);
            PottedReservedTotal = pottedStock.Sum(s => s.ReservedQuantity);
            PottedAvailableTotal = pottedStock.Sum(s => s.AvailableQuantity);

            var bookings = await _pottedPlantBookingRepo.GetAllAsync();
            BookingByStatus = bookings.GroupBy(b => b.Status).ToDictionary(g => g.Key, g => g.Count());
            BookingReservedTotal = bookings.Where(b => b.Status == "Pending").Sum(b => b.Quantity);

            var dispatches = await _dispatchRepo.GetAllAsync();
            DispatchCompletedCount = dispatches.Count(d => d.Status == "Completed");
            DispatchQuantityTotal = dispatches.Where(d => d.Status == "Completed").Sum(d => d.Quantity);

            var purchaseOrders = await _purchaseOrderRepo.GetAllAsync();
            PurchaseOrderByStatus = purchaseOrders.GroupBy(p => p.Status).ToDictionary(g => g.Key, g => g.Count());

            var labRequests = await _labRequestRepo.GetAllAsync();
            var outstandingLab = labRequests.Where(l => l.Status == "Sent").ToList();
            LabRequestOutstandingCount = outstandingLab.Count;
            LabRequestOutstandingQuantity = outstandingLab.Sum(l => l.SentQuantity);

            var labourLogs = await _labourLogRepo.GetAllAsync();
            LabourWageTotal = labourLogs.Where(l => l.Status == "Recorded").Sum(l => l.TotalWage);
        }
    }
}
