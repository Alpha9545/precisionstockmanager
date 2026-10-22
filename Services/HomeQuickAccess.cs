namespace PlantStockManager.Services
{
    // Company Home page ("/") quick-access tiles: one tile per ERP module, each with the
    // module's main pages. These are the same pages the sidebar menu lists. A tile is shown
    // with only the links the signed-in user may open, decided by the SAME central rule the
    // server enforces (FeatureAccessService -> FeatureAuthorizationConventions), and a tile
    // with no permitted link is not shown at all. Nothing here grants access to anything.
    public static class HomeQuickAccess
    {
        public const string ManagementDashboardPage = "/ManagementDashboard/Index";

        public sealed record Link(string Label, string Page);

        public sealed record Module(string Key, string Title, string Description, string Icon, IReadOnlyList<Link> Links);

        public static readonly IReadOnlyList<Module> Modules = new[]
        {
            new Module("seed", "User Seed Stock", "Seed stock, direct seed sowing, sowing approvals and ready seedlings.", "leaf.svg", new[]
            {
                new Link("Seed Stock", "/Production/SeedStock/Index"),
                new Link("Direct Seed Sowing", "/Production/SeedSowing/Index"),
                new Link("Sowing Approvals", "/Production/ReadyConfirmation/Index"),
                new Link("Ready Seedling Stock", "/Production/ReadyStock/Index"),
                new Link("Ready Alerts", "/Production/ReadyAlerts/Index"),
            }),
            new Module("mother", "Mother Plant & Cutting", "Mother plants, cutting production and cutting stock.", "compass.svg", new[]
            {
                new Link("Mother Plants", "/Production/MotherPlant/Index"),
                new Link("Cutting Production", "/Production/Cutting/Index"),
                new Link("Cutting Stock", "/Production/CuttingStock/Index"),
                new Link("Confirm Cutting Receipt", "/Production/CuttingStock/PendingConfirmations"),
            }),
            new Module("material", "Material Stock", "Cocopeat, Pitmos and trays per Polyhouse, used by sowings.", "leaf.svg", new[]
            {
                new Link("Material Stock", "/Production/MaterialStock/Index"),
                new Link("Add Material", "/Production/MaterialStock/Add"),
                new Link("Material History", "/Production/MaterialStock/History"),
            }),
            new Module("pot", "Pot Production", "Empty pot stock, pot production batches and potted plant stock.", "bulb.svg", new[]
            {
                new Link("Pot Production Batches", "/Production/PotBatch/Index"),
                new Link("Potted Plant Stock", "/Production/PottedPlantStock/Index"),
                new Link("Empty Pot Stock", "/Production/EmptyPotInventory/Index"),
            }),
            new Module("bookings", "Bookings & Dispatch", "Seedling bookings, fulfilment and dispatch; potted plant bookings.", "cart.svg", new[]
            {
                new Link("New Seedling Booking", "/Bookings/Book"),
                new Link("Seedling Fulfilment", "/Bookings/Fulfilment"),
                new Link("Seedling Booking Records", "/Data/BookingsRecord"),
                new Link("Seedling Dispatch Register", "/Bookings/DispatchRegister"),
                new Link("Potted Plant Bookings", "/Production/PottedPlantBooking/Index"),
            }),
            new Module("outlet", "Outlet", "Direct sales, customer bookings and the outlet's sales history.", "credit-card.svg", new[]
            {
                new Link("Direct Sale", "/Production/OutletSale/Create"),
                new Link("Customer Booking", "/Production/OutletBooking/Create"),
                new Link("Sales History", "/Production/OutletSale/Index"),
            }),
            new Module("store", "Inventory Store", "Store items, requests, issues and returns.", "find.svg", new[]
            {
                new Link("Store Dashboard", "/Store/Index"),
                new Link("New Store Request", "/Store/NewRequest"),
                new Link("Store Requests", "/Store/Requests"),
            }),
            new Module("maintenance", "Maintenance", "Raise and follow maintenance tickets.", "cog.svg", new[]
            {
                new Link("Raise Ticket", "/Maintenance/Raise"),
                new Link("My Tickets", "/Maintenance/MyTickets"),
                new Link("Assigned Tickets", "/Maintenance/Assigned"),
            }),
            new Module("labour", "Labour", "Daily attendance and overtime.", "compass.svg", new[]
            {
                new Link("Daily Attendance", "/Production/Labour/Attendance"),
                new Link("Overtime", "/Production/Labour/Overtime"),
                new Link("My Overtime Entries", "/Production/Labour/MyOvertime"),
            }),
            new Module("suggestions", "Suggestions & Problems", "Share a suggestion or report a problem.", "bulb.svg", new[]
            {
                new Link("All Posts", "/Suggestions/Index"),
                new Link("Create Post", "/Suggestions/Create"),
            }),
            new Module("fertilizer", "Fertilizer", "Fertilizer stock, usage and reports.", "leaf.svg", new[]
            {
                new Link("Fertilizer Stock", "/Fertilizer/Stock"),
                new Link("Fertilizer Usage", "/Fertilizer/Usage"),
                new Link("Fertilizer Report", "/Fertilizer/View"),
            }),
            new Module("reports", "Reports", "Stock history, wastage and the monthly sowing report.", "chart.svg", new[]
            {
                new Link("Stock History", "/Data/StockHistory"),
                new Link("Wastage", "/Data/WastedStock"),
                new Link("Sowing Monthly Report", "/Data/SeedSowingMonthlyReport"),
                // 2026-10-20: labour reports, System Administrator only (a link shows only to who may open it)
                new Link("Attendance History", "/Production/Labour/AttendanceHistory"),
                new Link("Overtime History", "/Production/Labour/OvertimeHistory"),
            }),
        };

        // The modules / links this user may open. canAccess is FeatureAccessService.CanAccessPageAsync.
        public static async Task<List<Module>> VisibleAsync(Func<string, Task<bool>> canAccess)
        {
            var result = new List<Module>();
            foreach (var m in Modules)
            {
                var links = new List<Link>();
                foreach (var l in m.Links)
                    if (await canAccess(l.Page))
                        links.Add(l);
                if (links.Count > 0)
                    result.Add(m with { Links = links });
            }
            return result;
        }
    }
}
