using PlantStockManager.Models;

namespace PlantStockManager.Services
{
    // Management Dashboard: the pure (database-free) calculations behind the
    // overview. Data/ManagementDashboardRepository.GetOverviewAsync reads the
    // rows; everything that ranks, groups or reconciles them lives here so it is
    // unit-tested (tests/.../ManagementDashboardCalculationsTests.cs).
    public static class ManagementDashboardCalculations
    {
        public const string NoPlantType = "(No Plant Type)";
        public const string NoPolyhouse = "(Area level, no Polyhouse)";
        public const string NoVariety = "(Variety not set)";

        // Top-N varieties by quantity, highest first; ties broken by Variety name so the
        // order is stable. Rows of the same Variety are combined first (never ranked twice);
        // varieties with nothing (<= 0) are not ranked. PercentOfTotal is the Variety's share
        // of the total over ALL varieties (not just the top N); null when that total is 0.
        public static List<RankedVariety> RankTop(IEnumerable<VarietyQuantity> rows, int top)
        {
            var combined = rows
                .GroupBy(r => r.SpeciesId.HasValue ? $"id:{r.SpeciesId}" : $"name:{r.VarietyName.Trim()}")
                .Select(g => new VarietyQuantity
                {
                    SpeciesId = g.First().SpeciesId,
                    VarietyName = CleanName(g.First().VarietyName, NoVariety),
                    PlantTypeName = g.Select(r => r.PlantTypeName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))?.Trim(),
                    Quantity = g.Sum(r => r.Quantity),
                })
                .Where(v => v.Quantity > 0)
                .ToList();

            var total = combined.Sum(v => v.Quantity);
            return combined
                .OrderByDescending(v => v.Quantity)
                .ThenBy(v => v.VarietyName, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(0, top))
                .Select((v, i) => new RankedVariety
                {
                    Rank = i + 1,
                    SpeciesId = v.SpeciesId,
                    VarietyName = v.VarietyName,
                    PlantTypeName = v.PlantTypeName,
                    Quantity = v.Quantity,
                    PercentOfTotal = total > 0 ? Math.Round(v.Quantity * 100m / total, 1) : null,
                })
                .ToList();
        }

        // Bar-chart data: the top N varieties plus ONE "Others (k varieties)" bar for the
        // rest, so the bars always add up to the same total as the stock card.
        public static List<CategoryValue> TopWithOthers(IEnumerable<VarietyQuantity> rows, int top)
        {
            var all = RankTop(rows, int.MaxValue);
            var result = all.Take(top).Select(v => new CategoryValue { Label = v.VarietyName, Value = v.Quantity }).ToList();
            var rest = all.Skip(top).ToList();
            if (rest.Count > 0)
                result.Add(new CategoryValue { Label = $"Others ({rest.Count} {(rest.Count == 1 ? "variety" : "varieties")})", Value = rest.Sum(v => v.Quantity) });
            return result;
        }

        public static StockTotals Totals(IEnumerable<ReadyStockSlice> slices)
        {
            var list = slices as ICollection<ReadyStockSlice> ?? slices.ToList();
            return new StockTotals
            {
                Physical = list.Sum(s => s.Physical),
                Reserved = list.Sum(s => s.Reserved),
                Available = list.Sum(s => s.Available),
                Batches = list.Sum(s => s.Batches),
            };
        }

        public static StockTotals Totals(IEnumerable<PottedStockByArea> rows)
        {
            var list = rows as ICollection<PottedStockByArea> ?? rows.ToList();
            return new StockTotals
            {
                Physical = list.Sum(s => s.Physical),
                Reserved = list.Sum(s => s.Reserved),
                Available = list.Sum(s => s.Available),
                InTransit = list.Sum(s => s.InTransit),
            };
        }

        public static List<VarietyQuantity> ReadyPhysicalByVariety(IEnumerable<ReadyStockSlice> slices)
            => slices.Select(s => new VarietyQuantity { SpeciesId = s.SpeciesId, VarietyName = s.VarietyName, PlantTypeName = s.PlantTypeName, Quantity = s.Physical }).ToList();

        // Ready Stock by Plant Type (the crop group), largest first. Varieties = the number
        // of distinct varieties of that Plant Type that currently hold stock.
        public static List<PlantTypeStockRow> ByPlantType(IEnumerable<ReadyStockSlice> slices)
        {
            var list = slices.Where(s => s.Physical != 0 || s.Available != 0).ToList();
            var total = list.Sum(s => s.Physical);
            return list
                .GroupBy(s => CleanName(s.PlantTypeName, NoPlantType))
                .Select(g => new PlantTypeStockRow
                {
                    PlantTypeName = g.Key,
                    Varieties = g.Where(s => s.Physical > 0).Select(s => s.SpeciesId).Distinct().Count(),
                    Physical = g.Sum(s => s.Physical),
                    Available = g.Sum(s => s.Available),
                    PercentOfTotal = total > 0 ? Math.Round(g.Sum(s => s.Physical) * 100m / total, 1) : null,
                })
                .OrderByDescending(r => r.Physical)
                .ThenBy(r => r.PlantTypeName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // Area -> Polyhouse stock table. Ready Stock is grouped by the stock row's own
        // AreaId (so the Area figure always equals the sum of its Polyhouse rows) and
        // Potted Plant Stock by its AreaId (that table has no Polyhouse). A Polyhouse that
        // dbo.Polyhouses assigns to a DIFFERENT Area is still shown under the stock's Area,
        // flagged with the Area it belongs to, rather than silently moved.
        public static List<AreaStockRow> BuildAreaRows(IEnumerable<ReadyStockSlice> slices, IEnumerable<PottedStockByArea> potted)
        {
            var readyList = slices.ToList();
            var areaNames = readyList.GroupBy(s => s.AreaId).ToDictionary(g => g.Key, g => g.First().AreaName);

            var rows = new Dictionary<string, AreaStockRow>();
            AreaStockRow Row(int? areaId, string name, string? type)
            {
                var key = areaId?.ToString() ?? "none";
                if (!rows.TryGetValue(key, out var row))
                    rows[key] = row = new AreaStockRow { AreaId = areaId, AreaName = CleanName(name, "(No Area)"), AreaType = type };
                return row;
            }

            foreach (var area in readyList.GroupBy(s => s.AreaId))
            {
                var first = area.First();
                var row = Row(area.Key, first.AreaName, first.AreaType);
                row.ReadyPhysical = area.Sum(s => s.Physical);
                row.ReadyReserved = area.Sum(s => s.Reserved);
                row.ReadyAvailable = area.Sum(s => s.Available);
                row.Polyhouses = area
                    .GroupBy(s => s.PolyhouseId)
                    .Select(g =>
                    {
                        var p = g.First();
                        var foreign = p.PolyhouseId.HasValue && p.PolyhouseAreaId.HasValue && p.PolyhouseAreaId.Value != area.Key;
                        return new PolyhouseStockRow
                        {
                            PolyhouseId = g.Key,
                            Name = g.Key.HasValue ? CleanName(p.PolyhouseName, $"Polyhouse {g.Key}") : NoPolyhouse,
                            BelongsToAreaName = !foreign ? null
                                : !string.IsNullOrWhiteSpace(p.PolyhouseAreaName) ? p.PolyhouseAreaName
                                : areaNames.TryGetValue(p.PolyhouseAreaId!.Value, out var n) ? n : $"Area {p.PolyhouseAreaId}",
                            Physical = g.Sum(s => s.Physical),
                            Reserved = g.Sum(s => s.Reserved),
                            Available = g.Sum(s => s.Available),
                        };
                    })
                    .OrderByDescending(p => p.Physical)
                    .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            foreach (var p in potted)
            {
                var row = Row(p.AreaId, p.AreaName, p.AreaType);
                row.PottedPhysical += p.Physical;
                row.PottedAvailable += p.Available;
            }

            return rows.Values
                .Where(r => r.ReadyPhysical != 0 || r.ReadyReserved != 0 || r.PottedPhysical != 0)
                .OrderByDescending(r => r.ReadyPhysical + r.PottedPhysical)
                .ThenBy(r => r.AreaName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // Today's labour from the Attendance History rows (DailyLabourAttendanceRepository.
        // SearchDaysAsync) -- Full Day / Half Day come from DailyLabourDay itself
        // (DailyLabourRules.Classify), never recalculated here. The 9:00 AM / 2:00 PM
        // headcounts are the recorded counts as entered.
        public static LabourSnapshot Labour(DateTime date, IEnumerable<DailyLabourDay> days, DailyLabourDaySummary summary, IEnumerable<LabourOvertimeEntry> overtime)
        {
            var dayList = days.ToList();
            var otList = overtime.ToList();
            static int Head(int? m, int? f) => (m ?? 0) + (f ?? 0);
            return new LabourSnapshot
            {
                Date = date.Date,
                Locations = summary.Days,
                IncompleteLocations = summary.IncompleteDays,
                At9 = dayList.Sum(d => d.IsManual ? 0 : Head(d.Male9, d.Female9)),
                At2 = dayList.Sum(d => d.IsManual ? 0 : Head(d.Male2, d.Female2)),
                FullDay = summary.TotalFullDay,
                HalfDay = summary.TotalHalfDay,
                LabourDays = summary.LabourDays,
                OvertimeEntries = otList.Count,
                OvertimeWorkerHours = otList.Sum(o => o.TotalWorkerHours),
                ByArea = dayList
                    .GroupBy(d => CleanName(d.AreaName, $"Area {d.AreaId}"))
                    .Select(g => new AreaLabourRow
                    {
                        AreaName = g.Key,
                        Locations = g.Count(),
                        At9 = g.Sum(d => d.IsManual ? 0 : Head(d.Male9, d.Female9)),
                        At2 = g.Sum(d => d.IsManual ? 0 : Head(d.Male2, d.Female2)),
                        FullDay = g.Sum(d => d.TotalFullDay ?? 0),
                        HalfDay = g.Sum(d => d.TotalHalfDay ?? 0),
                        IncompleteLocations = g.Count(d => !d.IsComplete),
                    })
                    .OrderBy(r => r.AreaName, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            };
        }

        public static MaintenanceSnapshot Maintenance(IEnumerable<MaintenanceTicket> activeTickets, int closedInPeriod, DateTime nowUtc, DateTime todayIndia)
        {
            var list = activeTickets.Where(t => MaintenanceRules.IsActive(t.Status)).ToList();
            return new MaintenanceSnapshot
            {
                Active = list.Count,
                Critical = list.Count(t => t.Priority == "Critical"),
                Urgent = list.Count(t => t.Priority == "Urgent"),
                Overdue = list.Count(t => t.OverdueReason(nowUtc, todayIndia) != null),
                AwaitingConfirmation = list.Count(t => t.Status == MaintenanceRules.Resolved),
                ClosedInPeriod = closedInPeriod,
            };
        }

        // "Attention Required": only conditions the application itself already defines
        // (no new thresholds), only when the count is above zero, most severe first.
        public static List<AttentionItem> BuildAttention(
            DateTime today,
            MaintenanceSnapshot maintenance,
            SeedlingOrderSummary seedlings,
            int sowingsOverdueForReady,
            int potBatchesOverdue,
            int cuttingDeliveriesToConfirm,
            StoreSnapshot store,
            int activeProblems)
        {
            var items = new List<AttentionItem>();
            void Add(int count, string text, string severity, string page, Dictionary<string, string>? query = null)
            {
                if (count > 0)
                    items.Add(new AttentionItem { Count = count, Text = text, Severity = severity, Page = page, Query = query ?? new() });
            }

            var d = "yyyy-MM-dd";
            Add(maintenance.Critical, "Critical maintenance tickets open", "danger", "/Maintenance/MyTickets", new() { ["Priority"] = "Critical" });
            Add(maintenance.Overdue, "Maintenance tickets overdue", "danger", "/Maintenance/Reports");
            Add(seedlings.OverdueOrders, "Pending seedling orders past their expected delivery date", "danger", "/Bookings/Fulfilment",
                new() { ["Status"] = "Pending", ["To"] = today.AddDays(-1).ToString(d) });
            Add(sowingsOverdueForReady, "Sowings overdue for Ready approval", "danger", "/Production/ReadyAlerts/Index");
            Add(potBatchesOverdue, "Pot batches overdue", "danger", "/Production/PotBatch/Index");
            Add(seedlings.DueTodayOrders, "Pending seedling orders due for delivery today", "warning", "/Bookings/Fulfilment",
                new() { ["Status"] = "Pending", ["From"] = today.ToString(d), ["To"] = today.ToString(d) });
            Add(cuttingDeliveriesToConfirm, "Cutting deliveries waiting for confirmation", "warning", "/Production/CuttingStock/PendingConfirmations");
            Add(store.PendingReview, "Store requests waiting for review", "warning", "/Store/Requests", new() { ["Status"] = StoreRules.Pending });
            Add(store.ApprovedAwaitingIssue, "Store requests approved, not yet issued", "warning", "/Store/Requests", new() { ["Status"] = StoreRules.Approved });
            Add(store.LowStockCount, "Store items below minimum stock", "warning", "/Store/Index");
            Add(activeProblems, "Active problems reported", "info", "/Suggestions/Index", new() { ["Type"] = SuggestionRules.Problem });
            return items;
        }

        // Ready Stock recorded under one Area on a Polyhouse that dbo.Polyhouses assigns to a
        // DIFFERENT Area (master data). Reported, never moved: the stock stays counted under
        // the Area it is recorded in. One row per recorded Area + Polyhouse.
        public static List<PolyhouseMismatch> FindPolyhouseMismatches(IEnumerable<ReadyStockSlice> slices)
            => slices
                .Where(s => s.PolyhouseId.HasValue && s.PolyhouseAreaId.HasValue && s.PolyhouseAreaId.Value != s.AreaId)
                .GroupBy(s => (s.AreaId, Polyhouse: s.PolyhouseId!.Value))
                .Select(g =>
                {
                    var f = g.First();
                    return new PolyhouseMismatch
                    {
                        RecordedAreaId = f.AreaId,
                        RecordedAreaName = CleanName(f.AreaName, $"Area {f.AreaId}"),
                        PolyhouseId = g.Key.Polyhouse,
                        PolyhouseName = CleanName(f.PolyhouseName, $"Polyhouse {g.Key.Polyhouse}"),
                        PolyhouseAreaId = f.PolyhouseAreaId!.Value,
                        PolyhouseAreaName = CleanName(f.PolyhouseAreaName, $"Area {f.PolyhouseAreaId}"),
                        Physical = g.Sum(s => s.Physical),
                        Reserved = g.Sum(s => s.Reserved),
                        Batches = g.Sum(s => s.Batches),
                    };
                })
                .OrderByDescending(m => m.Physical)
                .ThenBy(m => m.RecordedAreaName, StringComparer.OrdinalIgnoreCase)
                .ToList();

        // One line of the Recent activity feed, built from the raw columns so a missing value
        // never leaves a stray "--", "()" or ", " behind.
        public static string ActivityText(string kind, int? id, string? code, string? party, string? detail, string? extra, decimal? quantity, int? lines)
        {
            static string? T(string? v) => string.IsNullOrWhiteSpace(v) ? null : CleanName(v, "");
            static string Join(string sep, params string?[] parts) => string.Join(sep, parts.Where(p => !string.IsNullOrWhiteSpace(p)));
            static string? Paren(params string?[] parts) { var inner = Join(", ", parts); return inner.Length == 0 ? null : $"({inner})"; }
            string? Plants(decimal? q) => q.HasValue ? $"{QuantityFormat.Qty(q.Value)} {(q.Value == 1 ? "plant" : "plants")}" : null;

            return kind switch
            {
                "Booking" => Join(" -- ", BookingOrders.LineLabel(id ?? 0, null), T(party),
                    Join(", ", lines.HasValue ? $"{lines} {(lines == 1 ? "variety" : "varieties")}" : null, Plants(quantity))),
                "Seedling Dispatch" => Join(" -- ", T(code), Join(", ", T(party), Plants(quantity))),
                "Potted Dispatch" => Join(" -- ", T(code), Join(", ", Join(" ", T(party), T(detail)), Plants(quantity))),
                "Ready Stock" => Join(" -- ", quantity.HasValue ? $"Approved {QuantityFormat.Qty(quantity.Value)}" : "Approved", Join(" ", T(party), Paren(T(code)))),
                "Maintenance" => Join(" -- ", T(code), Join(" ", T(detail), Paren(T(extra), T(party)))),
                "Store Request" => Join(" -- ", T(code), Join(" ", T(party), Paren(T(detail)))),
                _ => Join(" -- ", T(code), Join(" ", T(detail), Paren(T(party)))),
            };
        }

        // Names in dbo.PlantSpecies / dbo.Area can carry stray spaces or line breaks.
        public static string CleanName(string? name, string fallback)
            => string.IsNullOrWhiteSpace(name) ? fallback : string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
