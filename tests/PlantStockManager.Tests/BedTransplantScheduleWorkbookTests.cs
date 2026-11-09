using ClosedXML.Excel;
using PlantStockManager.Services;
using W = PlantStockManager.Services.BedTransplantScheduleWorkbook;

namespace PlantStockManager.Tests
{
    // 2026-11-07 Bed Sowing Alerts -- reading the owner's schedule workbook (no database). Sheets are found by headers;
    // days must be whole numbers 1..365; dates, decimals, text and out-of-range values are invalid and reported; nothing
    // is guessed. Also the generated data-entry script, and the real BedSowing_ReadyDate.xlsx.
    public class BedTransplantScheduleWorkbookTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "psm-bedwb-" + Guid.NewGuid().ToString("N")[..8]);

        public BedTransplantScheduleWorkbookTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }

        // varieties: (group, variety[, per-variety value]); groups: (group, days cell value)
        private string Workbook(IEnumerable<(string Group, string Variety)> varieties, IEnumerable<(string Group, XLCellValue Days)> groups,
            bool withLog = true, bool withDaysSheet = true, Action<IXLWorksheet>? editVarieties = null)
        {
            using var wb = new XLWorkbook();
            var v = wb.AddWorksheet("Any name 1");
            v.Cell(1, 1).Value = "Sr. No."; v.Cell(1, 2).Value = "Variety"; v.Cell(1, 3).Value = "Variety Name"; v.Cell(1, 4).Value = "Bed Sowing Ready Date";
            var r = 2;
            foreach (var (g, name) in varieties)
            {
                v.Cell(r, 1).Value = r - 1; v.Cell(r, 2).Value = g; v.Cell(r, 3).Value = name;
                r++;
            }
            editVarieties?.Invoke(v);
            if (withDaysSheet)
            {
                var d = wb.AddWorksheet("Any name 2");
                d.Cell(1, 1).Value = "Sr.no"; d.Cell(1, 2).Value = " variety "; d.Cell(1, 3).Value = "BED SOWING  READY DATE";
                r = 2;
                foreach (var (g, days) in groups)
                {
                    d.Cell(r, 1).Value = r - 1; d.Cell(r, 2).Value = g; d.Cell(r, 3).Value = days;
                    r++;
                }
            }
            if (withLog)
            {
                var l = wb.AddWorksheet("Log");
                l.Cell(1, 1).Value = "Date"; l.Cell(1, 2).Value = "Variety"; l.Cell(1, 3).Value = "Quantity";
                l.Cell(2, 1).Value = new DateTime(2026, 8, 16); l.Cell(2, 2).Value = "Petunia Red Star"; l.Cell(2, 3).Value = 2000;
            }
            var path = Path.Combine(_dir, Guid.NewGuid().ToString("N")[..8] + ".xlsx");
            wb.SaveAs(path);
            return path;
        }

        private static W.Entry Entry(W.Result r, string variety) => r.Entries.Single(e => W.Key(e.Variety) == W.Key(variety) && e.Status != W.StatusDuplicate);

        [Fact]
        public void MissingFile_IsAnError_NothingRead()
        {
            var r = W.Read(Path.Combine(_dir, "nope.xlsx"));
            Assert.StartsWith("Workbook not found", r.Error);
            Assert.Empty(r.Entries);
            Assert.Throws<InvalidOperationException>(() => W.BuildDataEntrySql(r, "nope.xlsx", "00", DateTime.Now));
        }

        [Fact]
        public void NotAWorkbook_IsAnError()
        {
            var path = Path.Combine(_dir, "bad.xlsx");
            File.WriteAllText(path, "not a zip");
            Assert.StartsWith("Workbook could not be read", W.Read(path).Error);
        }

        [Fact]
        public void MissingDaysSheet_IsAnError()
        {
            var r = W.Read(Workbook(new[] { ("Petunia", "Petunia Red Star") }, Array.Empty<(string, XLCellValue)>(), withDaysSheet: false));
            Assert.Contains("days per group", r.Error);
        }

        [Fact]
        public void SheetsFoundByHeaders_LogIgnored_EachVarietyGetsItsGroupsDays()
        {
            var r = W.Read(Workbook(
                new[] { ("Petunia", "Petunia Red Star"), ("Salvia", "Salvia Salvo"), ("Marigold", "Marigold Taishan Orange") },
                new (string, XLCellValue)[] { ("Petunia", 25), ("Salvia", 20), ("Marigold", Blank.Value) }));
            Assert.Null(r.Error);
            Assert.Equal("Any name 1", r.VarietySheet);
            Assert.Equal("Any name 2", r.DaysSheet);
            Assert.Equal(new[] { "Log" }, r.IgnoredSheets);
            Assert.Equal((25, W.StatusDays), (Entry(r, "Petunia Red Star").Days, Entry(r, "Petunia Red Star").Status));
            Assert.Equal((20, W.StatusDays), (Entry(r, "Salvia Salvo").Days, Entry(r, "Salvia Salvo").Status));
            Assert.Equal(((int?)null, W.StatusNoDays), (Entry(r, "Marigold Taishan Orange").Days, Entry(r, "Marigold Taishan Orange").Status));
            Assert.Empty(r.Issues);
        }

        [Fact]
        public void GroupNames_IgnoreCaseAndExtraSpaces_VarietyNamesTrimmed()
        {
            var r = W.Read(Workbook(new[] { ("  petunia ", "  Petunia   Red Star  ") }, new (string, XLCellValue)[] { ("PETUNIA", 25) }));
            var e = Assert.Single(r.Entries);
            Assert.Equal("Petunia   Red Star", e.Variety);            // as written (trimmed), for the report
            Assert.Equal("PETUNIA RED STAR", W.Key(e.Variety));        // matched with Add Variety's rule
            Assert.Equal(25, e.Days);
        }

        [Fact]
        public void GroupNotOnDaysSheet_IsGroupMissing_NoDays()
        {
            var r = W.Read(Workbook(new[] { ("Chilli", "Chilli Hot") }, new (string, XLCellValue)[] { ("Petunia", 25) }));
            Assert.Equal(((int?)null, W.StatusGroupMissing), (r.Entries[0].Days, r.Entries[0].Status));
        }

        [Fact]
        public void DateCell_IsInvalid_NeverReadAsDays()
        {
            var r = W.Read(Workbook(new[] { ("Begonia", "Begonia Big Red") }, new (string, XLCellValue)[] { ("Begonia", new DateTime(2026, 8, 16)) }));
            var issue = Assert.Single(r.Issues);
            Assert.Equal("InvalidDays", issue.Kind);
            Assert.Contains("16/08/2026 is a date", issue.Message);
            Assert.Equal(((int?)null, W.StatusGroupInvalid), (r.Entries[0].Days, r.Entries[0].Status));
        }

        [Theory]
        [InlineData(22.5, "not a whole number")]
        [InlineData(0d, "outside 1..365")]
        [InlineData(-3d, "outside 1..365")]
        [InlineData(400d, "outside 1..365")]
        public void InvalidNumbers_AreReported(double value, string message)
        {
            var r = W.Read(Workbook(new[] { ("Pansy", "Pansy Blue") }, new (string, XLCellValue)[] { ("Pansy", value) }));
            Assert.Contains(message, Assert.Single(r.Issues).Message);
            Assert.Equal(W.StatusGroupInvalid, r.Entries[0].Status);
        }

        [Theory]
        [InlineData("25", 25, null)]
        [InlineData(" 30 ", 30, null)]
        [InlineData("25 days", null, "is not a whole number")]
        [InlineData("2,5", null, "is not a whole number")]
        [InlineData("-5", null, "is not a whole number")]
        [InlineData("", null, null)]
        public void TextCells_DigitsOnly(string text, int? days, string? error)
        {
            var (d, e) = W.ParseDays(text);
            Assert.Equal(days, d);
            if (error == null) Assert.Null(e); else Assert.Contains(error, e);
        }

        [Fact]
        public void BooleanOrErrorCell_IsInvalid()
        {
            Assert.NotNull(W.ParseDays(true).Error);
            Assert.NotNull(W.ParseDays(XLError.DivisionByZero).Error);
            Assert.Equal(((int?)null, (string?)null), W.ParseDays(Blank.Value));
        }

        [Fact]
        public void DuplicateGroup_SameDays_IsReportedAndStillUsed()
        {
            var r = W.Read(Workbook(new[] { ("Vinca", "Vinca Pacifica Mix") }, new (string, XLCellValue)[] { ("Vinca", 25), ("vinca ", 25) }));
            Assert.Equal("DuplicateGroup", Assert.Single(r.Issues).Kind);
            Assert.Equal(25, r.Entries[0].Days);
        }

        [Fact]
        public void DuplicateGroup_DifferentDays_GivesItsVarietiesNoDays()
        {
            var r = W.Read(Workbook(new[] { ("Gaillardia", "Gaillardia Arizona") }, new (string, XLCellValue)[] { ("Gaillardia", 25), ("Gaillardia", 30) }));
            Assert.Equal("ConflictingGroup", Assert.Single(r.Issues).Kind);
            Assert.Equal(((int?)null, W.StatusGroupInvalid), (r.Entries[0].Days, r.Entries[0].Status));
        }

        [Fact]
        public void DuplicateVariety_SameDays_RepeatIgnored()
        {
            var r = W.Read(Workbook(new[] { ("Salvia", "Salvia Salvo"), ("Salvia", " salvia  SALVO ") }, new (string, XLCellValue)[] { ("Salvia", 20) }));
            Assert.Equal("DuplicateVariety", Assert.Single(r.Issues).Kind);
            Assert.Single(r.WithDays);
            Assert.Equal(W.StatusDuplicate, r.Entries[1].Status);
        }

        [Fact]
        public void DuplicateVariety_DifferentDays_BothGetNoDays()
        {
            var r = W.Read(Workbook(new[] { ("Salvia", "Sharvari Purple"), ("Petunia", "Sharvari Purple") },
                new (string, XLCellValue)[] { ("Salvia", 20), ("Petunia", 22) }));
            Assert.Equal("ConflictingVariety", Assert.Single(r.Issues).Kind);
            Assert.All(r.Entries, e => Assert.Equal(((int?)null, W.StatusConflict), (e.Days, e.Status)));
            Assert.Empty(r.WithDays);
        }

        [Fact]
        public void PerVarietyValue_IsReported_NotUsed()
        {
            var r = W.Read(Workbook(new[] { ("Petunia", "Petunia Red Star") }, new (string, XLCellValue)[] { ("Petunia", 25) },
                editVarieties: ws => ws.Cell(2, 4).Value = 40));
            Assert.Equal("VarietyValueIgnored", Assert.Single(r.Issues).Kind);
            Assert.Equal(25, r.Entries[0].Days);
        }

        [Fact]
        public void BlankRows_AreSkipped_RowWithoutVarietyNameReported()
        {
            var r = W.Read(Workbook(new[] { ("Petunia", "Petunia Red Star"), ("", ""), ("Petunia", "") }, new (string, XLCellValue)[] { ("Petunia", 25), ("", Blank.Value) }));
            Assert.Single(r.Entries);
            Assert.Equal("NoVarietyName", Assert.Single(r.Issues).Kind);
        }

        [Fact]
        public void DataEntrySql_HasOnlyVarietiesWithDays_QuotesEscaped_RowCountChecked()
        {
            var r = W.Read(Workbook(
                new[] { ("Petunia", "Petunia O'Hara Red"), ("Marigold", "Marigold Taishan"), ("Chilli", "Chilli Hot") },
                new (string, XLCellValue)[] { ("Petunia", 25), ("Marigold", Blank.Value) }));
            var sql = W.BuildDataEntrySql(r, "test.xlsx", "ABC", new DateTime(2026, 11, 7, 10, 0, 0));
            Assert.Contains("(2, N'Petunia', N'Petunia O''Hara Red', N'PETUNIA O''HARA RED', 25)", sql);
            Assert.DoesNotContain("Marigold Taishan", sql);
            Assert.DoesNotContain("Chilli Hot", sql);
            Assert.Contains("IF (SELECT COUNT(*) FROM @Rows) <> 1", sql);
            Assert.Contains("THROW 54601", sql);                                 // wrong database refused
            Assert.Contains("TARGET_SERVER", sql);
            Assert.Contains("N'PREVIEW', N'COMMIT'", sql);
            Assert.Contains("Run Database/Migrations/2026-11-07_BedTransplantDays.sql first.", sql);
            Assert.Contains("ISNULL(s.BedTransplantDays, -1) = ISNULL(p.ExistingDays, -1)", sql);   // only what PREVIEW saw
            Assert.DoesNotContain("BedTransplantDays = NULL", sql);                // never clears a value
            Assert.Contains("SHA-256         : ABC", sql);
        }

        // ---- the owner's real workbook -------------------------------------------------------------------------

        private static string RealWorkbook() => Path.Combine(BedTransplantAlertRulesTests.RepoRoot(), "BedSowing_ReadyDate.xlsx");

        [SkippableFact]
        public void RealWorkbook_ReadsAsAnalysed()
        {
            Skip.IfNot(File.Exists(RealWorkbook()), "BedSowing_ReadyDate.xlsx is not in the repository root.");
            var r = W.Read(RealWorkbook());
            Assert.Null(r.Error);
            Assert.Equal("Sowing Ready Dates", r.VarietySheet);
            Assert.Equal("Sheet1", r.DaysSheet);
            Assert.Equal(new[] { "Sheet2" }, r.IgnoredSheets);
            Assert.Equal(466, r.Entries.Count);
            Assert.Equal(355, r.Entries.Count(e => e.Status == W.StatusDays));
            Assert.Equal(106, r.Entries.Count(e => e.Status == W.StatusNoDays));
            Assert.Equal(5, r.Entries.Count(e => e.Status == W.StatusGroupMissing));   // Aronaa, Basil, Chilli, 2x Chrysanthemum
            Assert.Empty(r.Issues);
            Assert.Equal(20, Entry(r, "Salvia Salvo").Days);
            Assert.Equal(45, r.Entries.First(e => e.Group == "Begonia").Days);
            Assert.Null(r.Entries.First(e => e.Group == "Marigold").Days);
        }

        // The committed script must be the one this workbook generates (re-generate after any workbook change).
        [SkippableFact]
        public void CommittedDataEntryScript_MatchesTheWorkbook()
        {
            var script = Path.Combine(BedTransplantAlertRulesTests.RepoRoot(), "Database", "DataEntry", "2026-11-07_BedTransplantDays", "01_BedTransplantDays.sql");
            Skip.IfNot(File.Exists(RealWorkbook()) && File.Exists(script), "Workbook or generated script not present.");
            using var stream = File.OpenRead(RealWorkbook());
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
            var expected = W.BuildDataEntrySql(W.Read(RealWorkbook()), "BedSowing_ReadyDate.xlsx", sha, DateTime.Now);
            static string Body(string s) => string.Join("\n", s.Replace("\r\n", "\n").Split('\n').Where(l => !l.StartsWith("-- Generated")));
            Assert.Equal(Body(expected), Body(File.ReadAllText(script)));
        }
    }
}
