using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Services;
using OvergrowthPage = PlantStockManager.Pages.Production.ReadyStock.OvergrowthModel;

namespace PlantStockManager.Tests
{
    // 2026-10-22: "⚠ OG -- Overgrowth" -- the pure rules, the badge, the page's bound values / permission, and that every screen
    // that shows Ready Stock renders the badge from the right model. No database (see ReadyStockOvergrowthE2ETests for the rest).
    public class ReadyStockOvergrowthTests
    {
        private static string Root()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.csproj")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }

        private static string Source(params string[] path) => File.ReadAllText(Path.Combine(new[] { Root() }.Concat(path).ToArray()));

        [Fact]
        public void Validate_Mark_NeedsAReason_AndPlantsLeft_Unmark_NeedsAMark()
        {
            Assert.Null(OvergrowthRules.Validate(true, false, 240, "leggy"));
            Assert.Equal(OvergrowthRules.ReasonRequiredMessage, OvergrowthRules.Validate(true, false, 240, null));
            Assert.Equal(OvergrowthRules.ReasonRequiredMessage, OvergrowthRules.Validate(true, false, 240, "   "));
            Assert.Equal(OvergrowthRules.NothingLeftMessage, OvergrowthRules.Validate(true, false, 0, "leggy"));
            Assert.Equal(OvergrowthRules.AlreadyMarkedMessage, OvergrowthRules.Validate(true, true, 240, "leggy"));
            Assert.NotNull(OvergrowthRules.Validate(true, false, 240, new string('x', OvergrowthRules.MaxReasonLength + 1)));
            Assert.Null(OvergrowthRules.Validate(true, false, 240, " " + new string('x', OvergrowthRules.MaxReasonLength) + " "));   // trimmed first
            Assert.Null(OvergrowthRules.Validate(false, true, 0, null));          // unmarking an empty batch is fine, a note is optional
            Assert.Equal(OvergrowthRules.NotMarkedMessage, OvergrowthRules.Validate(false, false, 240, null));
        }

        [Fact]
        public void TransplantTrace_Notes_NameTheSourcesOvergrowth_AndFitTheRemarks()
        {
            Assert.Equal("; source batch was ⚠ OG — Overgrowth when transplanted: leggy", OvergrowthRules.TransplantSourceLedgerNote("  leggy "));
            Assert.Equal("; source batch was ⚠ OG — Overgrowth when transplanted", OvergrowthRules.TransplantSourceLedgerNote(null));
            Assert.Contains("starts unmarked", OvergrowthRules.TransplantOutputNote);
            // ledger remark: "<code>: transplanted into <cavity> trays" + note with the longest reason stays inside NVARCHAR(500)
            Assert.True(("TP-2026-00000: transplanted into 150 Cavity trays" + OvergrowthRules.TransplantSourceLedgerNote(new string('x', OvergrowthRules.MaxReasonLength))).Length <= 500);
            // the tray batch note goes BEFORE the free-text notes (TrayBatchWriter truncates remarks at 500)
            var repo = File.ReadAllText(Path.Combine(Root(), "Data", "TransplantRepository.cs"));
            Assert.True(repo.IndexOf("OvergrowthRules.TransplantOutputNote : \"\")   // before the notes", StringComparison.Ordinal)
                        < repo.IndexOf("+ (notes == null ? \"\" : \": \" + notes),", StringComparison.Ordinal));
        }

        [Fact]
        public void Badge_OnlyForOvergrowth_SameMarkupEverywhere_Encoded()
        {
            Assert.Equal("", OvergrowthRules.Badge(false).ToString());
            Assert.Equal("", OvergrowthRules.Badge(false, compact: true).ToString());
            var full = OvergrowthRules.Badge(true).ToString()!;
            var compact = OvergrowthRules.Badge(true, compact: true).ToString()!;
            Assert.Contains("⚠ OG — Overgrowth</span>", full);
            Assert.Contains(">⚠ OG</span>", compact);
            Assert.All(new[] { full, compact }, b =>
            {
                Assert.Contains("data-og=\"1\"", b);
                Assert.Contains("border-warning", b);
                Assert.Contains("title=\"Overgrowth", b);
            });
        }

        [Fact]
        public void Schema_SelectsTheColumns_OrNeverMarkedConstants_WithoutTheMigration()
        {
            Assert.Contains("rs.IsOvergrowth", ReadyStockOvergrowthSchema.Columns(true));
            Assert.Contains("ogu.Name AS OvergrowthMarkedByName", ReadyStockOvergrowthSchema.Columns(true));
            Assert.Contains("JOIN dbo.IMSUsers ogu ON ogu.Id = rs.OvergrowthMarkedById", ReadyStockOvergrowthSchema.Join(true));
            Assert.DoesNotContain("rs.", ReadyStockOvergrowthSchema.Columns(false));
            Assert.Contains("CAST(0 AS BIT) AS IsOvergrowth", ReadyStockOvergrowthSchema.Columns(false));
            Assert.Equal("", ReadyStockOvergrowthSchema.Join(false));
            Assert.Equal("l.IsOvergrowth", ReadyStockOvergrowthSchema.Flag(true, "l"));
            Assert.Equal("CAST(0 AS BIT)", ReadyStockOvergrowthSchema.Flag(false, "l"));
            foreach (var part in new[] { "COL_LENGTH('dbo.ReadyStock', 'IsOvergrowth')", "dbo.ReadyStockOvergrowthLog", "TR_ReadyStockOvergrowthLog_AppendOnly",
                         "COL_LENGTH('dbo.SeedlingDispatchLines', 'IsOvergrowth')", "CK_ReadyStock_Overgrowth" })
                Assert.Contains(part, ReadyStockOvergrowthSchema.ProbeSql);
        }

        [Fact]
        public void Page_NeedsReadyStockOvergrowth_AndBindsOnlyTheReason()
        {
            Assert.Equal(OvergrowthRules.Permission, FeatureAuthorizationConventions.GetRule("/Production/ReadyStock/Overgrowth").Read);
            var bound = typeof(OvergrowthPage).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<BindPropertyAttribute>() != null).Select(p => p.Name).ToArray();
            Assert.Equal(new[] { "Reason" }, bound);                      // never a user, Area, quantity or marker state
            Assert.Null(typeof(OvergrowthPage).GetCustomAttribute<BindPropertiesAttribute>());
            // the page uses the strict Area rule, and the save re-checks the permission in the batch's own Area under its lock
            var page = Source("Pages", "Production", "ReadyStock", "Overgrowth.cshtml.cs");
            Assert.Contains("AreaAccessService _areaAccess", page);
            Assert.Contains("SetOvergrowthAsync(id, mark, Reason,", page);
            Assert.Contains("User.GetUserId(), User.Identity?.Name, User.IsFullAccess()", page);
            var repo = Source("Data", "ReadyStockRepository.cs");
            Assert.Contains("FROM dbo.ReadyStock WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", repo);
            Assert.Contains("HoldsInAreaAsync(conn, tx, actor.UserId, areaId, OvergrowthRules.Permission)", repo);
            Assert.Contains("WHERE ur.UserId = @U AND ur.AreaId = @A", repo);
        }

        // Every Ready Stock screen of the requirement renders the badge from the model row it shows.
        [Theory]
        [InlineData("Pages/Production/ReadyStock/Index.cshtml", "OvergrowthRules.Badge(true)")]                       // list + monitoring
        [InlineData("Pages/Production/ReadyStock/Lineage.cshtml", "OvergrowthRules.Badge(b.IsOvergrowth)")]            // details
        [InlineData("Pages/Production/ReadyStock/_LineageChain.cshtml", "OvergrowthRules.Badge(b.IsOvergrowth, compact: true)")]
        [InlineData("Pages/Production/ReadyStock/Overgrowth.cshtml", "OvergrowthRules.Badge(b.IsOvergrowth)")]
        [InlineData("Pages/Production/ReadyStock/Move.cshtml", "OvergrowthRules.Badge(b.IsOvergrowth)")]               // Send / customer dispatch
        [InlineData("Pages/Production/ReadyStock/TransferToMotherPlant.cshtml", "OvergrowthRules.Badge(b.IsOvergrowth)")]
        [InlineData("Pages/Production/Transplanting/Create.cshtml", "OvergrowthRules.Badge(s.IsOvergrowth)")]
        [InlineData("Pages/Production/MotherPlant/ReadyStock.cshtml", "OvergrowthRules.Badge(t.IsOvergrowth, compact: true)")]
        [InlineData("Pages/Production/MotherPlant/SellTrays.cshtml", "OvergrowthRules.Badge(b.IsOvergrowth)")]
        [InlineData("Pages/Bookings/BookingDetails.cshtml", "OvergrowthRules.Badge(a.IsOvergrowth, compact: true)")]  // allocations
        [InlineData("Pages/Bookings/BookingDetails.cshtml", "OvergrowthRules.Badge(l.IsOvergrowth, compact: true)")]  // dispatch lines
        [InlineData("Pages/Bookings/SeedlingDispatch.cshtml", "OvergrowthRules.Badge(a.IsOvergrowth, compact: true)")] // fulfilment lines
        [InlineData("Pages/Bookings/SeedlingDispatch.cshtml", "OvergrowthRules.Badge(o.IsOvergrowth, compact: true)")] // batch selection
        public void Screens_RenderTheBadge(string view, string call)
            => Assert.Contains(call, Source(view.Split('/')));

        [Fact]
        public void Migration_IsGuarded_AddsNoAutomaticRule_AndHasARollbackThatProtectsData()
        {
            var sql = Source("Database", "Migrations", "2026-10-24_ReadyStockOvergrowth.sql");
            Assert.Contains("*** PREPARED FOR REVIEW -- NOT APPLIED ***", sql);
            foreach (var guard in new[] { "$(TARGET_SERVER)", "$(TARGET_MACHINE)", "$(TARGET_DB)", "$(TARGET_DB_CREATED)", "N'$(MODE)' COLLATE Latin1_General_BIN2 NOT IN (N'DRYRUN', N'COMMIT')" })
                Assert.Contains(guard, sql);
            Assert.Contains("DEFAULT (0)", sql);                                   // existing batches: normal stock
            Assert.DoesNotContain("UPDATE dbo.ReadyStock SET IsOvergrowth = 1", sql); // nothing marked by age / variety
            Assert.Contains("INSTEAD OF UPDATE, DELETE", sql);
            var rollback = Source("Database", "Migrations", "2026-10-24_ReadyStockOvergrowth_Rollback.sql");
            Assert.Contains("DISCARD_OVERGROWTH", rollback);
            Assert.Contains("refusing to discard it", rollback);
            // the safeguards compare CASE-SENSITIVELY: under the database's default (case-insensitive) collation "yes" / "commit"
            // would otherwise pass as YES / COMMIT -- found when verifying the rollback on a scratch copy, 2026-10-22
            foreach (var script in new[] { sql, rollback })
            {
                var comparisons = System.Text.RegularExpressions.Regex.Matches(script, @"N'\$\((MODE|DISCARD_OVERGROWTH)\)'[^\r\n]*?(NOT IN|<>|=)");
                Assert.NotEmpty(comparisons);
                Assert.All(comparisons, m => Assert.Contains("COLLATE Latin1_General_BIN2", m.Value));
            }
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(rollback, @"N'\$\(DISCARD_OVERGROWTH\)' COLLATE Latin1_General_BIN2").Count);
        }
    }
}
