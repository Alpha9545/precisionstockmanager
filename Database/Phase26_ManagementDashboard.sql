/* ============================================================
   Phase 26: Management Dashboard (Phase L)
   ------------------------------------------------------------
   INSPECTION SUMMARY (per the standing "inspect before creating"
   rule):

   The Management Dashboard is a READ-ONLY reporting layer over the
   existing authoritative tables -- Area, Polyhouse, GrowingPartner,
   MotherPlant, CuttingStock/CuttingStockTransactions, InternalTransfers,
   PotProduction, PottedPlantStock, EmptyPotInventory,
   PottedPlantBookings, Dispatches, SeedStock, SeedIssues, SeedSowings,
   ReadyStock/ReadyStockTransactions/ReadyConfirmations. NOTHING here
   creates a new table, duplicates a stock/quantity column, or changes
   any existing workflow. This script exists ONLY to add a small,
   genuinely-needed set of indexes that the new aggregate/date-range
   dashboard queries (Data/ManagementDashboardRepository.cs) run
   against, following Step 5's instruction to prefer range predicates
   over CAST(DateColumn AS DATE) and Step 6's instruction to avoid
   table scans for reporting queries.

   Every existing report/workflow page continues to use whatever
   indexes it already had -- this script only ADDS indexes, it never
   drops or alters one, and every block below is guarded (safe to
   re-run), exactly matching the "IF NOT EXISTS (SELECT 1 FROM
   sys.indexes ...)" convention used since Phase 2/8/14/17/19.

   Gaps identified during inspection (columns that ARE queried by the
   new dashboard but had NO supporting index before this script):
     - dbo.PotProduction has indexes on PropagationBatchId,
       MotherPlantId, SpeciesId, PotSize, Status, SourceCuttingStockId
       (Phase 7/19) but NONE on AreaId or ProductionDate -- both are
       used by the Growing Partner Summary (grouped by Area) and the
       Production/Sowing-vs-Ready trend chart (date-range filtered).
     - dbo.Dispatches has indexes on PottedPlantBookingId,
       PottedPlantStockId, Status (Phase 10) but none on DispatchDate --
       used by the Outlet Dispatch trend chart's date-range filter.
     - dbo.SeedSowings has indexes on AreaId, SourceSeedStockId, Status
       (Phase 23) but none on SowingDate -- used by the Seed Summary's
       "Sowing activity" count and the Sowing-vs-Ready chart.
     - dbo.SeedIssues has indexes on Status, SourceAreaId,
       DestinationAreaId (Phase 22) but none on IssueDate -- used by
       the Seed Summary's date-range filter.
     - dbo.ReadyStockTransactions has an index on ReadyStockId
       (Phase 25) but none on TransactionDate -- used by the Ready
       Stock trend chart.
     - dbo.CuttingStockTransactions has indexes on CuttingStockId and
       (ReferenceType, ReferenceId) (Phase 15) but none on
       TransactionDate -- used by the Cutting Summary's "Recent cutting
       activity" feed.
     - dbo.InternalTransfers has an index on Status (Phase 8) plus
       several Source*Id indexes (Phase 8/15) but none on StockType --
       the dashboard groups/filters this table by StockType ('Cutting'
       / 'GrowingPartnerToOutlet' / 'MainOfficeIssue' / ...) constantly,
       almost always together with Status.
     - dbo.PottedPlantBookings has indexes on PottedPlantStockId,
       Status, SpeciesId (Phase 9) but none on AreaId -- used by the
       Outlet/Sales Summary, which groups Bookings by Area (Outlet
       Areas specifically).
   ============================================================ */

-- 1) PotProduction: AreaId (Growing Partner Summary grouping) and
--    ProductionDate (date-range trend queries).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PotProduction_AreaId' AND object_id = OBJECT_ID('dbo.PotProduction'))
BEGIN
    CREATE INDEX IX_PotProduction_AreaId ON dbo.PotProduction(AreaId);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PotProduction_ProductionDate' AND object_id = OBJECT_ID('dbo.PotProduction'))
BEGIN
    CREATE INDEX IX_PotProduction_ProductionDate ON dbo.PotProduction(ProductionDate);
END
GO

-- 2) Dispatches: DispatchDate (Outlet Dispatch trend chart / date filter).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Dispatches_DispatchDate' AND object_id = OBJECT_ID('dbo.Dispatches'))
BEGIN
    CREATE INDEX IX_Dispatches_DispatchDate ON dbo.Dispatches(DispatchDate);
END
GO

-- 3) SeedSowings: SowingDate (Sowing activity KPI / Sowing-vs-Ready chart).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SeedSowings_SowingDate' AND object_id = OBJECT_ID('dbo.SeedSowings'))
BEGIN
    CREATE INDEX IX_SeedSowings_SowingDate ON dbo.SeedSowings(SowingDate);
END
GO

-- 4) SeedIssues: IssueDate (Seed Summary date-range filter).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SeedIssues_IssueDate' AND object_id = OBJECT_ID('dbo.SeedIssues'))
BEGIN
    CREATE INDEX IX_SeedIssues_IssueDate ON dbo.SeedIssues(IssueDate);
END
GO

-- 5) ReadyStockTransactions: TransactionDate (Ready Stock trend chart).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ReadyStockTransactions_TransactionDate' AND object_id = OBJECT_ID('dbo.ReadyStockTransactions'))
BEGIN
    CREATE INDEX IX_ReadyStockTransactions_TransactionDate ON dbo.ReadyStockTransactions(TransactionDate);
END
GO

-- 6) CuttingStockTransactions: TransactionDate (Recent cutting activity feed).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CuttingStockTransactions_TransactionDate' AND object_id = OBJECT_ID('dbo.CuttingStockTransactions'))
BEGIN
    CREATE INDEX IX_CuttingStockTransactions_TransactionDate ON dbo.CuttingStockTransactions(TransactionDate);
END
GO

-- 7) InternalTransfers: StockType (dashboard groups/filters heavily by
--    StockType, almost always alongside the existing Status index).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_InternalTransfers_StockType' AND object_id = OBJECT_ID('dbo.InternalTransfers'))
BEGIN
    CREATE INDEX IX_InternalTransfers_StockType ON dbo.InternalTransfers(StockType);
END
GO

-- 8) PottedPlantBookings: AreaId (Outlet/Sales Summary grouping by Area).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PottedPlantBookings_AreaId' AND object_id = OBJECT_ID('dbo.PottedPlantBookings'))
BEGIN
    CREATE INDEX IX_PottedPlantBookings_AreaId ON dbo.PottedPlantBookings(AreaId);
END
GO

-- ============================================================================
-- End of Phase 26. No tables created, no columns added, no existing
-- workflow/behavior changed -- purely additive indexes supporting the new
-- read-only Management Dashboard's aggregate queries.
-- ============================================================================
