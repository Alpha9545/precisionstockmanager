using System.Security.Claims;
using PlantStockManager.Authorization;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Management Dashboard -- the pure calculations (Services/ManagementDashboardCalculations.cs),
    // the period presets (DashboardPeriod) and the Administrator-only access rule. The SQL side
    // (unique orders vs. lines, reconciliation against the tables) is ManagementDashboardE2ETests.
    public class ManagementDashboardCalculationsTests
    {
        private static VarietyQuantity V(int id, string name, decimal qty, string? type = "TYPE")
            => new() { SpeciesId = id, VarietyName = name, PlantTypeName = type, Quantity = qty };

        private static ReadyStockSlice Slice(int area, int? polyhouse, int species, decimal physical, decimal reserved = 0,
            string? type = "MERI GOLD", int? polyhouseArea = null, string varietyName = "")
            => new()
            {
                AreaId = area, AreaName = $"Area {area}", PolyhouseId = polyhouse, PolyhouseName = polyhouse.HasValue ? $"P{polyhouse}" : null,
                PolyhouseAreaId = polyhouse.HasValue ? polyhouseArea ?? area : null, SpeciesId = species,
                VarietyName = varietyName == "" ? $"Variety {species}" : varietyName, PlantTypeName = type,
                Physical = physical, Reserved = reserved, Available = physical - reserved, Batches = 1,
            };

        // ---- Top 3 ---------------------------------------------------------------

        [Fact]
        public void Top3_ReturnsTheThreeLargest_InOrder_NeverTheFourth()
        {
            var top = ManagementDashboardCalculations.RankTop(new[]
            {
                V(4, "Variety D", 20), V(2, "Variety B", 80), V(1, "Variety A", 100), V(3, "Variety C", 50),
            }, 3);

            Assert.Equal(new[] { "Variety A", "Variety B", "Variety C" }, top.Select(t => t.VarietyName));
            Assert.Equal(new[] { 1, 2, 3 }, top.Select(t => t.Rank));
            Assert.Equal(new[] { 100m, 80m, 50m }, top.Select(t => t.Quantity));
            Assert.DoesNotContain(top, t => t.VarietyName == "Variety D");
        }

        [Fact]
        public void Top3_Percentages_AreOfTheAllVarietyTotal_NotOfTheTop3()
        {
            var top = ManagementDashboardCalculations.RankTop(new[] { V(1, "A", 100), V(2, "B", 80), V(3, "C", 50), V(4, "D", 20) }, 3);
            // total 250
            Assert.Equal(new decimal?[] { 40.0m, 32.0m, 20.0m }, top.Select(t => t.PercentOfTotal));
        }

        [Fact]
        public void Top3_CombinesRowsOfTheSameVariety_BeforeRanking()
        {
            // Variety B is split over two Areas (2 x 60 = 120): it must rank first, once.
            var top = ManagementDashboardCalculations.RankTop(new[] { V(1, "A", 100), V(2, "B", 60), V(3, "C", 50), V(2, "B", 60) }, 3);
            Assert.Equal(new[] { "B", "A", "C" }, top.Select(t => t.VarietyName));
            Assert.Equal(120m, top[0].Quantity);
            Assert.Single(top, t => t.VarietyName == "B");
        }

        [Fact]
        public void Top3_ShowsTheVarietyName_NotThePlantType()
        {
            var top = ManagementDashboardCalculations.RankTop(new[] { V(7, " Lakhani Yellow\n", 10, "MERI GOLD") }, 3);
            Assert.Equal("Lakhani Yellow", top[0].VarietyName);
            Assert.Equal("MERI GOLD", top[0].PlantTypeName);
        }

        [Fact]
        public void Top3_TiesAreOrderedByName_AndZeroOrNegativeIsNotRanked()
        {
            var top = ManagementDashboardCalculations.RankTop(new[] { V(1, "Zinnia", 50), V(2, "Aster", 50), V(3, "Empty", 0), V(4, "Minus", -5) }, 3);
            Assert.Equal(new[] { "Aster", "Zinnia" }, top.Select(t => t.VarietyName));
        }

        [Fact]
        public void Top3_EmptyInput_GivesEmptyList_AndNoPercentage()
        {
            Assert.Empty(ManagementDashboardCalculations.RankTop(Array.Empty<VarietyQuantity>(), 3));
            Assert.Empty(ManagementDashboardCalculations.RankTop(new[] { V(1, "A", 0) }, 3));
        }

        [Fact]
        public void Top3_NullVarietyAndNullPlantType_AreHandled()
        {
            var top = ManagementDashboardCalculations.RankTop(new[] { new VarietyQuantity { SpeciesId = null, VarietyName = "", PlantTypeName = null, Quantity = 5 } }, 3);
            Assert.Equal(ManagementDashboardCalculations.NoVariety, top[0].VarietyName);
            Assert.Null(top[0].PlantTypeName);
        }

        [Fact]
        public void Chart_TopNPlusOthers_AddsUpToTheSameTotal()
        {
            var rows = Enumerable.Range(1, 14).Select(i => V(i, $"V{i:00}", i * 10)).ToList();
            var chart = ManagementDashboardCalculations.TopWithOthers(rows, 10);
            Assert.Equal(11, chart.Count);
            Assert.Equal("Others (4 varieties)", chart[^1].Label);
            Assert.Equal(rows.Sum(r => r.Quantity), chart.Sum(c => c.Value));
            Assert.Equal(140m, chart[0].Value);
        }

        [Fact]
        public void Chart_OneRemainingVariety_IsSingular_AndNoOthersBarWhenAllFit()
        {
            Assert.Equal("Others (1 variety)", ManagementDashboardCalculations.TopWithOthers(new[] { V(1, "A", 3), V(2, "B", 2) }, 1)[^1].Label);
            Assert.DoesNotContain(ManagementDashboardCalculations.TopWithOthers(new[] { V(1, "A", 3) }, 10), c => c.Label.StartsWith("Others"));
        }

        // ---- stock totals, Plant Type, Area / Polyhouse ---------------------------

        [Fact]
        public void Totals_AvailableIsPhysicalMinusReserved_NothingDoubleCounted()
        {
            var slices = new[] { Slice(1, 10, 100, 500, 50), Slice(1, 11, 100, 300), Slice(2, null, 200, 200, 20) };
            var t = ManagementDashboardCalculations.Totals(slices);
            Assert.Equal(1000m, t.Physical);
            Assert.Equal(70m, t.Reserved);
            Assert.Equal(930m, t.Available);
            Assert.Equal(t.Physical - t.Reserved, t.Available);
        }

        [Fact]
        public void ByPlantType_GroupsVarietiesUnderTheirPlantType()
        {
            var rows = ManagementDashboardCalculations.ByPlantType(new[]
            {
                Slice(1, 10, 1, 300, type: "MERI GOLD"), Slice(2, null, 1, 100, type: "MERI GOLD"),
                Slice(1, 10, 2, 200, type: "MERI GOLD"), Slice(1, 10, 3, 400, type: "ZINNIA"), Slice(1, 10, 4, 0, type: null),
            });
            Assert.Equal(new[] { "MERI GOLD", "ZINNIA" }, rows.Select(r => r.PlantTypeName));
            Assert.Equal(2, rows[0].Varieties);   // variety 1 (two slices) counted once
            Assert.Equal(600m, rows[0].Physical);
            Assert.Equal(60.0m, rows[0].PercentOfTotal);
            Assert.Equal(1000m, rows.Sum(r => r.Physical));
        }

        [Fact]
        public void AreaRows_AreaTotalEqualsItsPolyhouses_AndEveryAreaAddsUpToTheGrandTotal()
        {
            var slices = new[]
            {
                Slice(1, 10, 1, 300, 30), Slice(1, 10, 2, 200), Slice(1, 11, 1, 100), Slice(1, null, 3, 50),
                Slice(2, 20, 1, 400, 40),
            };
            var potted = new[] { new PottedStockByArea { AreaId = 2, AreaName = "Area 2", Physical = 70, Reserved = 20 }, new PottedStockByArea { AreaId = 3, AreaName = "Outlet", Physical = 15 } };

            var rows = ManagementDashboardCalculations.BuildAreaRows(slices, potted);

            var area1 = rows.Single(r => r.AreaId == 1);
            Assert.Equal(650m, area1.ReadyPhysical);
            Assert.Equal(area1.ReadyPhysical, area1.Polyhouses.Sum(p => p.Physical));
            Assert.Equal(area1.ReadyAvailable, area1.Polyhouses.Sum(p => p.Available));
            Assert.Equal(500m, area1.Polyhouses.Single(p => p.PolyhouseId == 10).Physical);
            Assert.Equal(ManagementDashboardCalculations.NoPolyhouse, area1.Polyhouses.Single(p => p.PolyhouseId == null).Name);

            Assert.Equal(ManagementDashboardCalculations.Totals(slices).Physical, rows.Sum(r => r.ReadyPhysical));
            Assert.Equal(85m, rows.Sum(r => r.PottedPhysical));
            Assert.Equal(50m, rows.Single(r => r.AreaId == 2).PottedAvailable);
            Assert.Equal(15m, rows.Single(r => r.AreaId == 3).PottedPhysical);   // potted-only Area still listed
        }

        [Fact]
        public void AreaRows_APolyhouseOfAnotherArea_StaysUnderTheStocksArea_AndIsFlagged()
        {
            var rows = ManagementDashboardCalculations.BuildAreaRows(new[] { Slice(1, 8, 1, 100, polyhouseArea: 2), Slice(2, 9, 1, 10) }, Array.Empty<PottedStockByArea>());
            var p = rows.Single(r => r.AreaId == 1).Polyhouses.Single();
            Assert.Equal(100m, p.Physical);
            Assert.Equal("Area 2", p.BelongsToAreaName);
            Assert.Null(rows.Single(r => r.AreaId == 2).Polyhouses.Single().BelongsToAreaName);
        }

        [Fact]
        public void AreaRows_EmptyData_GivesNoRows()
            => Assert.Empty(ManagementDashboardCalculations.BuildAreaRows(Array.Empty<ReadyStockSlice>(), Array.Empty<PottedStockByArea>()));

        // ---- period ------------------------------------------------------------

        private static readonly DateTime Today = new(2026, 10, 7);

        [Theory]
        [InlineData("today", "2026-10-07", "2026-10-07")]
        [InlineData("7d", "2026-10-01", "2026-10-07")]
        [InlineData("30d", "2026-09-08", "2026-10-07")]
        [InlineData("month", "2026-10-01", "2026-10-07")]
        [InlineData(null, "2026-09-08", "2026-10-07")]          // default = last 30 days
        [InlineData("nonsense", "2026-09-08", "2026-10-07")]
        public void Period_Presets(string? key, string from, string to)
        {
            var p = DashboardPeriod.Resolve(key, null, null, Today);
            Assert.Equal(DateTime.Parse(from), p.From);
            Assert.Equal(DateTime.Parse(to), p.To);
            Assert.Equal(p.To.AddDays(1), p.ToExclusive);
        }

        [Fact]
        public void Period_Custom_SwapsAReversedRange_AndOldDateOnlyLinksAreCustom()
        {
            var p = DashboardPeriod.Resolve("custom", new DateTime(2026, 9, 30), new DateTime(2026, 9, 1), Today);
            Assert.Equal((new DateTime(2026, 9, 1), new DateTime(2026, 9, 30)), (p.From, p.To));

            var old = DashboardPeriod.Resolve(null, new DateTime(2026, 9, 5), null, Today);
            Assert.Equal(DashboardPeriod.Custom, old.Key);
            Assert.Equal((new DateTime(2026, 9, 5), new DateTime(2026, 9, 5)), (old.From, old.To));
        }

        [Fact]
        public void Period_PresetIgnoresStaleDatesInTheQueryString()
        {
            var p = DashboardPeriod.Resolve("today", new DateTime(2020, 1, 1), new DateTime(2020, 1, 31), Today);
            Assert.Equal((Today, Today), (p.From, p.To));
        }

        // ---- maintenance, labour, attention -------------------------------------

        [Fact]
        public void Maintenance_UsesTheModulesOwnOverdueRule_AndIgnoresClosedTickets()
        {
            var now = new DateTime(2026, 10, 7, 6, 0, 0, DateTimeKind.Utc);
            var tickets = new[]
            {
                new MaintenanceTicket { Status = MaintenanceRules.Open, Priority = "Critical", RequestedAt = now.AddHours(-5) },   // > 4 h: overdue
                new MaintenanceTicket { Status = MaintenanceRules.Open, Priority = "Urgent", RequestedAt = now.AddHours(-2) },
                new MaintenanceTicket { Status = MaintenanceRules.Resolved, Priority = "Normal", RequestedAt = now.AddDays(-9) },
                new MaintenanceTicket { Status = MaintenanceRules.Closed, Priority = "Critical", RequestedAt = now.AddDays(-9) },
            };
            var m = ManagementDashboardCalculations.Maintenance(tickets, 4, now, new DateTime(2026, 10, 7));
            Assert.Equal(3, m.Active);
            Assert.Equal(1, m.Critical);
            Assert.Equal(1, m.Urgent);
            Assert.Equal(1, m.Overdue);
            Assert.Equal(1, m.AwaitingConfirmation);
            Assert.Equal(4, m.ClosedInPeriod);
        }

        [Fact]
        public void Labour_UsesTheLabourModulesFullAndHalfDay_AndCountsHeadcounts()
        {
            var days = new List<DailyLabourDay>
            {
                new() { AreaId = 1, AreaName = "Main", Male9 = 10, Female9 = 5, Male2 = 8, Female2 = 5 },   // full 13, half 2
                new() { AreaId = 1, AreaName = "Main", PolyhouseId = 3, Male9 = 4, Female9 = 0 },          // 2 PM not yet in
            };
            var summary = new DailyLabourDaySummary();
            days.ForEach(summary.Add);

            var l = ManagementDashboardCalculations.Labour(Today, days, summary, Array.Empty<LabourOvertimeEntry>());
            Assert.Equal(19, l.At9);
            Assert.Equal(13, l.At2);
            Assert.Equal(13, l.FullDay);
            Assert.Equal(2, l.HalfDay);
            Assert.Equal(14m, l.LabourDays);
            Assert.Equal(1, l.IncompleteLocations);
            var area = Assert.Single(l.ByArea);
            Assert.Equal(2, area.Locations);
            Assert.Equal(1, area.IncompleteLocations);
        }

        [Fact]
        public void Attention_ListsOnlyNonZeroItems_MostSevereFirst_WithFilteredLinks()
        {
            var items = ManagementDashboardCalculations.BuildAttention(Today,
                new MaintenanceSnapshot { Critical = 1, Overdue = 0 },
                new SeedlingOrderSummary { OverdueOrders = 3, DueTodayOrders = 2 },
                sowingsOverdueForReady: 0, potBatchesOverdue: 0, cuttingDeliveriesToConfirm: 0,
                new StoreSnapshot { PendingReview = 4 }, activeProblems: 0);

            Assert.Equal(4, items.Count);
            Assert.Equal(new[] { "danger", "danger", "warning", "warning" }, items.Select(i => i.Severity));
            var overdue = items.Single(i => i.Count == 3);
            Assert.Equal("/Bookings/Fulfilment", overdue.Page);
            Assert.Equal("2026-10-06", overdue.Query["To"]);
            var dueToday = items.Single(i => i.Count == 2);
            Assert.Equal(("2026-10-07", "2026-10-07"), (dueToday.Query["From"], dueToday.Query["To"]));
            Assert.Equal("Pending", items.Single(i => i.Count == 4).Query["Status"]);
        }

        [Fact]
        public void Attention_NothingWaiting_IsEmpty()
            => Assert.Empty(ManagementDashboardCalculations.BuildAttention(Today, new(), new(), 0, 0, 0, new(), 0));

        [Fact]
        public void CleanName_CollapsesLineBreaksAndSpaces()
        {
            Assert.Equal("Bhagyshree White", ManagementDashboardCalculations.CleanName("Bhagyshree White\r\n", "x"));
            Assert.Equal("x", ManagementDashboardCalculations.CleanName("  ", "x"));
            Assert.Equal("x", ManagementDashboardCalculations.CleanName(null, "x"));
        }

        // ---- time zones ----------------------------------------------------------

        [Fact]
        public void IndiaDate_StartsAt1830UtcTheDayBefore()
        {
            Assert.Equal(new DateTime(2026, 10, 6, 18, 30, 0), AppClock.IndiaToUtc(new DateTime(2026, 10, 7)));
            Assert.Equal(330, AppClock.IndiaOffsetMinutes);
        }

        [Fact]
        public void Utc2030_IsTwoAmNextIndiaDay_AndFallsInsideThatDaysUtcBounds()
        {
            var utc = new DateTime(2026, 10, 6, 20, 30, 0);
            Assert.Equal(new DateTime(2026, 10, 7, 2, 0, 0), AppClock.ToIndiaTime(utc));

            var oct7From = AppClock.IndiaToUtc(new DateTime(2026, 10, 7));
            var oct7To = AppClock.IndiaToUtc(new DateTime(2026, 10, 8));
            Assert.True(utc >= oct7From && utc < oct7To);                                                  // counts on 7 Oct
            Assert.False(utc >= AppClock.IndiaToUtc(new DateTime(2026, 10, 6)) && utc < oct7From);         // not on 6 Oct
        }

        [Fact]
        public void Utc1829_IsOneMinuteBeforeIndiaMidnight_AndStaysOnThePreviousDay()
        {
            var utc = new DateTime(2026, 10, 6, 18, 29, 0);
            Assert.Equal(new DateTime(2026, 10, 6, 23, 59, 0), AppClock.ToIndiaTime(utc));
            Assert.True(utc < AppClock.IndiaToUtc(new DateTime(2026, 10, 7)));                            // not on 7 Oct
            Assert.True(utc >= AppClock.IndiaToUtc(new DateTime(2026, 10, 6)));                            // on 6 Oct
            Assert.Equal(new DateTime(2026, 10, 6, 18, 30, 0), AppClock.IndiaToUtc(new DateTime(2026, 10, 7)));   // the exact boundary
        }

        // ---- recent activity text --------------------------------------------------------

        [Fact]
        public void ActivityText_MissingValues_LeaveNoEmptyBracketsOrDashes()
        {
            Assert.Equal("Approved 600 -- Aishwarya Yellow", ManagementDashboardCalculations.ActivityText("Ready Stock", null, "  ", "Aishwarya Yellow\n", null, null, 600, null));
            Assert.Equal("Approved 600 -- Aishwarya Yellow (J-10)", ManagementDashboardCalculations.ActivityText("Ready Stock", null, "J-10", "Aishwarya Yellow", null, null, 600, null));
            Assert.Equal("PS-1 -- Leak", ManagementDashboardCalculations.ActivityText("Problem", 5, "PS-1", null, "Leak", null, null, null));
            Assert.Equal("Leak (Outlet)", ManagementDashboardCalculations.ActivityText("Problem", 5, null, "Outlet", "Leak", null, null, null));
            Assert.Equal("SD-1 -- 1 plant", ManagementDashboardCalculations.ActivityText("Seedling Dispatch", 3, "SD-1", null, null, null, 1, null));
            Assert.Equal("MT-1 -- Pump (Urgent)", ManagementDashboardCalculations.ActivityText("Maintenance", 1, "MT-1", null, "Pump", "Urgent", null, null));
            Assert.Equal("SR-2 -- Outlet (Pending)", ManagementDashboardCalculations.ActivityText("Store Request", 2, "SR-2", "Outlet", "Pending", null, null, null));
            Assert.Equal("PD-1 -- 1 plant", ManagementDashboardCalculations.ActivityText("Potted Dispatch", null, "PD-1", null, null, null, 1, null));
        }

        [Fact]
        public void ActivityText_Booking_IsOneOrderWithItsLinesAndTotal()
        {
            Assert.Equal("Booking #12 -- Ravi -- 3 varieties, 1,500 plants", ManagementDashboardCalculations.ActivityText("Booking", 12, null, "Ravi", null, null, 1500, 3));
            Assert.Equal("Booking #12 -- 1 variety, 10 plants", ManagementDashboardCalculations.ActivityText("Booking", 12, null, null, null, null, 10, 1));
        }

        // ---- master-data mismatch and blank Plant Type ------------------------------------

        [Fact]
        public void Mismatch_ListsRecordedAreaPolyhouseActualAreaAndQuantity_WithoutMovingTheStock()
        {
            var slices = new[]
            {
                Slice(2, 8, 1, 2000, 10, polyhouseArea: 123), Slice(2, 8, 2, 480, polyhouseArea: 123),   // mismatched
                Slice(2, 6, 1, 900), Slice(2, null, 3, 50),                                                // fine
            };
            slices[0].PolyhouseAreaName = slices[1].PolyhouseAreaName = "Samarth Ropvatika";

            var m = Assert.Single(ManagementDashboardCalculations.FindPolyhouseMismatches(slices));
            Assert.Equal(("Area 2", "P8", "Samarth Ropvatika", 2480m, 10m, 2), (m.RecordedAreaName, m.PolyhouseName, m.PolyhouseAreaName, m.Physical, m.Reserved, m.Batches));

            var area = Assert.Single(ManagementDashboardCalculations.BuildAreaRows(slices, Array.Empty<PottedStockByArea>()));
            Assert.Equal(3430m, area.ReadyPhysical);   // still counted under the recorded Area
            Assert.Equal("Samarth Ropvatika", area.Polyhouses.Single(p => p.PolyhouseId == 8).BelongsToAreaName);
        }

        [Fact]
        public void Mismatch_NoneWhenEveryPolyhouseBelongsToItsArea()
            => Assert.Empty(ManagementDashboardCalculations.FindPolyhouseMismatches(new[] { Slice(1, 10, 1, 5), Slice(1, null, 1, 5) }));

        [Fact]
        public void BlankPlantType_IsGroupedAsNoPlantType_NotDropped()
        {
            var rows = ManagementDashboardCalculations.ByPlantType(new[] { Slice(1, null, 1, 30, type: null), Slice(1, null, 2, 70, type: "  ") });
            var row = Assert.Single(rows);
            Assert.Equal((ManagementDashboardCalculations.NoPlantType, 2, 100m, 100.0m), (row.PlantTypeName, row.Varieties, row.Physical, row.PercentOfTotal));
        }

        // ---- access: Administrator only ----------------------------------------------

        private static ClaimsPrincipal User(string[] permissions, bool fullAccess = false)
        {
            var claims = permissions.Select(p => new Claim(MinimumAuthorizationLevelHandler.PermissionClaimType, p)).ToList();
            if (fullAccess) claims.Add(new Claim(ClaimsPrincipalSecurityExtensions.FullAccessClaimType, "true"));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        }

        [Fact]
        public void Dashboard_RequiresTheAdministratorPermission_ServerSide()
        {
            Assert.True(FeatureAuthorizationConventions.IsMapped("/ManagementDashboard/Index"));
            var rule = FeatureAuthorizationConventions.GetRule("/ManagementDashboard/Index");
            Assert.Equal("Admin.ManageAreas", rule.Read);
            Assert.False(rule.IsAuthenticatedOnly);
            Assert.False(rule.IsAnonymous);
        }

        [Fact]
        public void Dashboard_ManagementAndOtherViewers_AreRefused_TheAdministratorIsAllowed()
        {
            var rule = FeatureAuthorizationConventions.GetRule("/ManagementDashboard/Index").Read;
            var manager = User(new[] { "Dashboard.View", "Reports.View", "Booking.View", "Maintenance.Manage", "Store.Manage", "Labour.View" });
            Assert.False(manager.HasPermission(rule));
            Assert.False(new ClaimsPrincipal(new ClaimsIdentity()).HasPermission(rule));   // anonymous
            Assert.True(User(Array.Empty<string>(), fullAccess: true).HasPermission(rule));
            Assert.True(User(new[] { "Admin.ManageAreas" }).HasPermission(rule));
        }

        [Fact]
        public void DashboardPage_HasNoAllowAnonymous()
        {
            var type = typeof(PlantStockManager.Pages.ManagementDashboard.IndexModel);
            Assert.Empty(type.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), true));
        }
    }
}
