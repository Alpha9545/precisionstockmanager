using System.Globalization;
using System.Text;
using ClosedXML.Excel;

namespace PlantStockManager.Services
{
    // 2026-11-07 Bed Sowing Alerts: reads the owner's schedule workbook (BedSowing_ReadyDate.xlsx) and turns it into a
    // REVIEWED, name-based data-entry script for dbo.PlantSpecies.BedTransplantDays. Used only by the offline tool
    // tools/BedTransplantDaysImport -- the web application never opens the workbook (the live server has no copy of it).
    //
    // Workbook layout (found by HEADERS, never by sheet name or position; headers compared with PlantSpeciesNameRules.Key):
    //   variety sheet  headers "Variety" (the variety GROUP, e.g. "Petunia") + "Variety Name" (the variety itself).
    //                  Its own "Bed Sowing Ready Date" column is empty in the owner's file; a value there is NOT used
    //                  (its meaning per variety is not confirmed) and is reported.
    //   days sheet     headers "Variety" (group) + "Bed Sowing Ready Date" and NO "Variety Name": a whole NUMBER OF DAYS
    //                  (owner, 2026-11-07: days from bed sowing until the bed is due for transplanting). Blank = no
    //                  schedule for that group. A date, a decimal, text or a value outside 1..365 is INVALID, never
    //                  converted.
    //   Any other sheet (e.g. the dated sowing log) is ignored.
    // Each variety gets its group's days. Nothing is guessed: a group missing from the days sheet, a blank or invalid
    // value, a group listed twice with different days, or a variety listed twice under different days gives that variety
    // NO days, and every such case is reported.
    public static class BedTransplantScheduleWorkbook
    {
        public const string GroupHeader = "Variety";
        public const string VarietyHeader = "Variety Name";
        public const string DaysHeader = "Bed Sowing Ready Date";

        public const string StatusDays = "Days";
        public const string StatusNoDays = "NoDays";                 // the group's cell is blank
        public const string StatusGroupMissing = "GroupMissing";     // the group is not on the days sheet
        public const string StatusGroupInvalid = "GroupInvalid";     // the group's value is invalid, or the group is listed twice with different days
        public const string StatusNoGroup = "NoGroup";               // the variety row has no group
        public const string StatusConflict = "Conflict";             // the variety is listed twice with different days
        public const string StatusDuplicate = "Duplicate";           // a repeat of an earlier row with the same days (not used again)

        public sealed record Issue(string Sheet, int Row, string Kind, string Message);
        public sealed record Entry(int Row, string Group, string Variety, int? Days, string Status);

        public sealed class Result
        {
            public string? Error { get; set; }
            public string? VarietySheet { get; set; }
            public string? DaysSheet { get; set; }
            public List<string> IgnoredSheets { get; } = new();
            public Dictionary<string, (string Name, int? Days, bool Valid)> Groups { get; } = new();
            public List<Entry> Entries { get; } = new();
            public List<Issue> Issues { get; } = new();
            public IEnumerable<Entry> WithDays => Entries.Where(e => e.Status == StatusDays);
        }

        public static Result Read(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return new Result { Error = $"Workbook not found: {path}" };
            try
            {
                // FileShare.ReadWrite: the workbook may be open in Excel; it is only read, never written.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var workbook = new XLWorkbook(stream);
                return Read(workbook);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
                                          or InvalidOperationException or NotSupportedException or FormatException)
            {
                return new Result { Error = $"Workbook could not be read: {ex.Message}" };
            }
        }

        public static Result Read(XLWorkbook workbook)
        {
            var result = new Result();
            IXLWorksheet? varietySheet = null, daysSheet = null;
            Dictionary<string, int>? varietyCols = null, daysCols = null;
            foreach (var ws in workbook.Worksheets)
            {
                var headers = Headers(ws);
                var hasGroup = headers.ContainsKey(Key(GroupHeader));
                if (hasGroup && headers.ContainsKey(Key(VarietyHeader)) && varietySheet == null)
                    (varietySheet, varietyCols) = (ws, headers);
                else if (hasGroup && headers.ContainsKey(Key(DaysHeader)) && !headers.ContainsKey(Key(VarietyHeader)) && daysSheet == null)
                    (daysSheet, daysCols) = (ws, headers);
                else
                    result.IgnoredSheets.Add(ws.Name);
            }
            if (varietySheet == null || daysSheet == null)
            {
                result.Error = varietySheet == null
                    ? $"No sheet with the headers \"{GroupHeader}\" and \"{VarietyHeader}\" (the variety list)."
                    : $"No sheet with the headers \"{GroupHeader}\" and \"{DaysHeader}\" without \"{VarietyHeader}\" (the days per group).";
                return result;
            }
            result.VarietySheet = varietySheet.Name;
            result.DaysSheet = daysSheet.Name;

            // ---- days per group ----
            var gCol = daysCols![Key(GroupHeader)];
            var dCol = daysCols[Key(DaysHeader)];
            foreach (var row in DataRows(daysSheet))
            {
                var name = Text(row.Cell(gCol).Value);
                var (days, error) = ParseDays(row.Cell(dCol).Value);
                if (name.Length == 0)
                {
                    if (days != null || error != null)
                        result.Issues.Add(new Issue(daysSheet.Name, row.RowNumber(), "NoGroupName", "A value without a group name is not used."));
                    continue;
                }
                if (error != null)
                    result.Issues.Add(new Issue(daysSheet.Name, row.RowNumber(), "InvalidDays", $"{name}: {error}"));
                var key = Key(name);
                if (result.Groups.TryGetValue(key, out var earlier))
                {
                    if (earlier.Valid && error == null && earlier.Days == days)
                        result.Issues.Add(new Issue(daysSheet.Name, row.RowNumber(), "DuplicateGroup", $"{name} is listed again with the same value; the repeat is ignored."));
                    else
                    {
                        result.Issues.Add(new Issue(daysSheet.Name, row.RowNumber(), "ConflictingGroup",
                            $"{name} is listed twice with different values ({Show(earlier.Days, earlier.Valid)} and {Show(days, error == null)}); its varieties get no days."));
                        result.Groups[key] = (earlier.Name, null, false);
                    }
                    continue;
                }
                result.Groups[key] = (name, days, error == null);
            }

            // ---- varieties ----
            var vgCol = varietyCols![Key(GroupHeader)];
            var vnCol = varietyCols[Key(VarietyHeader)];
            var vdCol = varietyCols.TryGetValue(Key(DaysHeader), out var c) ? c : 0;
            var seen = new Dictionary<string, int>();   // variety key -> index in Entries
            foreach (var row in DataRows(varietySheet))
            {
                var group = Text(row.Cell(vgCol).Value);
                var variety = Text(row.Cell(vnCol).Value);
                if (variety.Length == 0)
                {
                    if (group.Length > 0)
                        result.Issues.Add(new Issue(varietySheet.Name, row.RowNumber(), "NoVarietyName", $"Group {group} has a row without a variety name."));
                    continue;
                }
                if (vdCol > 0 && !row.Cell(vdCol).Value.IsBlank)
                    result.Issues.Add(new Issue(varietySheet.Name, row.RowNumber(), "VarietyValueIgnored",
                        $"{variety}: the per-variety \"{DaysHeader}\" value is not used (the days come from the group sheet)."));

                int? days = null;
                string status;
                if (group.Length == 0)
                    status = StatusNoGroup;
                else if (!result.Groups.TryGetValue(Key(group), out var g))
                    status = StatusGroupMissing;
                else if (!g.Valid)
                    status = StatusGroupInvalid;
                else if (g.Days == null)
                    status = StatusNoDays;
                else
                    (days, status) = (g.Days, StatusDays);

                var vKey = Key(variety);
                if (seen.TryGetValue(vKey, out var first))
                {
                    var prior = result.Entries[first];
                    if (prior.Days == days && prior.Status == status)
                    {
                        result.Issues.Add(new Issue(varietySheet.Name, row.RowNumber(), "DuplicateVariety",
                            $"{variety} is listed again (first at row {prior.Row}) with the same value; the repeat is ignored."));
                        result.Entries.Add(new Entry(row.RowNumber(), group, variety, days, StatusDuplicate));
                    }
                    else
                    {
                        result.Issues.Add(new Issue(varietySheet.Name, row.RowNumber(), "ConflictingVariety",
                            $"{variety} is listed twice with different values (row {prior.Row}: {Show(prior.Days, true)}, row {row.RowNumber()}: {Show(days, true)}); it gets no days."));
                        result.Entries[first] = prior with { Days = null, Status = StatusConflict };
                        result.Entries.Add(new Entry(row.RowNumber(), group, variety, null, StatusConflict));
                    }
                    continue;
                }
                seen[vKey] = result.Entries.Count;
                result.Entries.Add(new Entry(row.RowNumber(), group, variety, days, status));
            }
            return result;
        }

        // A whole number of days 1..365, from a number cell or a text cell holding digits only. A date cell is refused
        // explicitly (an Excel date is a day count since 1900 -- reading it as days would invent a schedule).
        public static (int? Days, string? Error) ParseDays(XLCellValue value)
        {
            switch (value.Type)
            {
                case XLDataType.Blank:
                    return (null, null);
                case XLDataType.Number:
                    var n = value.GetNumber();
                    if (n != Math.Floor(n))
                        return (null, $"{n.ToString(CultureInfo.InvariantCulture)} is not a whole number of days.");
                    return Range(n);
                case XLDataType.Text:
                    var t = value.GetText().Trim();
                    if (t.Length == 0)
                        return (null, null);
                    if (!t.All(char.IsAsciiDigit) || !int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                        return (null, $"\"{t}\" is not a whole number of days.");
                    return Range(parsed);
                case XLDataType.DateTime:
                    return (null, $"{value.GetDateTime().ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} is a date; a number of days is expected.");
                default:
                    return (null, $"A {value.Type} value is not a number of days.");
            }

            static (int?, string?) Range(double d)
                => d is >= BedTransplantAlertRules.MinDays and <= BedTransplantAlertRules.MaxDays
                    ? ((int)d, null)
                    : (null, $"{d.ToString(CultureInfo.InvariantCulture)} is outside {BedTransplantAlertRules.MinDays}..{BedTransplantAlertRules.MaxDays} days.");
        }

        // ---- the data-entry script ---------------------------------------------------------------------------------

        // A complete PREVIEW/COMMIT script: only the varieties WITH days; matched to dbo.PlantSpecies by name with Add
        // Variety's rule. Unmatched names, inactive-only matches and varieties already holding another value are REPORTED;
        // a name matching two active varieties stops the run. Varieties not in the workbook are never touched.
        public static string BuildDataEntrySql(Result result, string sourceFile, string sourceSha256, DateTime generatedAt)
        {
            if (result.Error != null)
                throw new InvalidOperationException(result.Error);
            var rows = result.WithDays.ToList();
            var sb = new StringBuilder();
            sb.AppendLine("-- ============================================================================");
            sb.AppendLine("-- 01_BedTransplantDays.sql   (GENERATED by tools/BedTransplantDaysImport -- do not edit by hand; re-generate)");
            sb.AppendLine("--");
            sb.AppendLine($"-- Source workbook : {Comment(sourceFile)}");
            sb.AppendLine($"-- SHA-256         : {sourceSha256}");
            sb.AppendLine($"-- Generated       : {generatedAt:yyyy-MM-dd HH:mm}");
            sb.AppendLine($"-- Sheets          : varieties \"{Comment(result.VarietySheet!)}\", days per group \"{Comment(result.DaysSheet!)}\"; ignored: {Comment(string.Join(", ", result.IgnoredSheets.Select(s => "\"" + s + "\"")))}");
            sb.AppendLine($"-- Varieties       : {result.Entries.Count(e => e.Status != StatusDuplicate)} listed, {rows.Count} with days (only these are in this script).");
            sb.AppendLine("--");
            sb.AppendLine("-- Sets dbo.PlantSpecies.BedTransplantDays (Bed Sowing Alerts: Transplant Due Date = bed Sowing Date + these days)");
            sb.AppendLine("-- for the listed varieties. Matched by NAME with Add Variety's rule (PlantSpeciesNameRules: capitals ignored,");
            sb.AppendLine("-- extra spaces collapsed; spelling and punctuation are NOT ignored -- a near-miss is reported, never guessed).");
            sb.AppendLine("--   SET      the variety has no days yet");
            sb.AppendLine("--   CHANGE   the variety has different days (the workbook wins: this is how a changed workbook is applied)");
            sb.AppendLine("--   SAME     already this value -- untouched");
            sb.AppendLine("--   NOT IN DATABASE / INACTIVE ONLY -- skipped and listed; ERROR (name matches two active varieties) stops the run.");
            sb.AppendLine("-- Varieties NOT in the workbook keep whatever they have (listed at the end, never cleared).");
            sb.AppendLine("-- Requires 2026-11-07_BedTransplantDays.sql. No stock, bed or ledger row is touched.");
            sb.AppendLine("--");
            sb.AppendLine("-- MODE=PREVIEW  read-only: prints the plan, writes nothing.");
            sb.AppendLine("-- MODE=COMMIT   one transaction; any error rolls everything back. Only with the owner's approval for that database.");
            sb.AppendLine("-- Run (PowerShell, from this folder):");
            sb.AppendLine("--   sqlcmd -S <server> -d <db> -E -C -I -b -W -s\"|\" -v MODE=PREVIEW TARGET_SERVER=\"<@@SERVERNAME>\" -i 01_BedTransplantDays.sql");
            sb.AppendLine("-- Verify the identity query (CLAUDE.md C) and take a COPY_ONLY backup before COMMIT.");
            sb.AppendLine("-- ============================================================================");
            sb.AppendLine(":on error exit");
            sb.AppendLine("IF DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'");
            sb.AppendLine("    THROW 54601, '01_BedTransplantDays.sql may only be run against PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy.', 1;");
            sb.AppendLine("IF N'$(MODE)' COLLATE Latin1_General_BIN2 NOT IN (N'PREVIEW', N'COMMIT')");
            sb.AppendLine("    THROW 54602, 'Run with sqlcmd -v MODE=PREVIEW or MODE=COMMIT (exact upper case).', 1;");
            sb.AppendLine("IF @@SERVERNAME <> N'$(TARGET_SERVER)'");
            sb.AppendLine("    THROW 54603, 'TARGET_SERVER does not match @@SERVERNAME -- refusing to run on an unexpected server.', 1;");
            sb.AppendLine("IF COL_LENGTH('dbo.PlantSpecies', 'BedTransplantDays') IS NULL OR OBJECT_ID('dbo.CK_PlantSpecies_BedTransplantDays', 'C') IS NULL");
            sb.AppendLine("    THROW 54604, 'Run Database/Migrations/2026-11-07_BedTransplantDays.sql first.', 1;");
            sb.AppendLine("GO");
            sb.AppendLine("SET QUOTED_IDENTIFIER ON;");
            sb.AppendLine("SET ANSI_NULLS ON;");
            sb.AppendLine("SET NOCOUNT ON;");
            sb.AppendLine("SET XACT_ABORT ON;");
            sb.AppendLine();
            sb.AppendLine("-- RowNo = the workbook row; GroupName / VarietyName as written (trimmed); NameKey = PlantSpeciesNameRules.Key.");
            sb.AppendLine("DECLARE @Rows TABLE (RowNo INT PRIMARY KEY, GroupName NVARCHAR(200), VarietyName NVARCHAR(200), NameKey NVARCHAR(400) NOT NULL UNIQUE, Days INT NOT NULL);");
            for (var i = 0; i < rows.Count; i += 500)   // INSERT ... VALUES takes at most 1000 rows
            {
                sb.AppendLine("INSERT @Rows (RowNo, GroupName, VarietyName, NameKey, Days) VALUES");
                var chunk = rows.Skip(i).Take(500).ToList();
                for (var j = 0; j < chunk.Count; j++)
                {
                    var e = chunk[j];
                    sb.Append(j == 0 ? " " : ",").Append('(').Append(e.Row.ToString(CultureInfo.InvariantCulture)).Append(", ")
                      .Append(Sql(e.Group)).Append(", ").Append(Sql(e.Variety)).Append(", ").Append(Sql(Key(e.Variety))).Append(", ")
                      .Append(e.Days!.Value.ToString(CultureInfo.InvariantCulture)).AppendLine(")");
                }
                sb.AppendLine(";");
            }
            sb.AppendLine($"IF (SELECT COUNT(*) FROM @Rows) <> {rows.Count.ToString(CultureInfo.InvariantCulture)}");
            sb.AppendLine("    THROW 54605, 'List check failed: the row count does not match the generated list.', 1;");
            sb.Append(ScriptBody);
            return sb.ToString();
        }

        private const string ScriptBody = @"
PRINT '=== Server / database: ' + @@SERVERNAME + ' / ' + DB_NAME() + ' -- MODE=$(MODE) ===';

-- Name key = Add Variety's rule (PlantSpeciesNameRules): upper case, trimmed, tabs/line breaks/nbsp -> space, runs collapsed.
IF OBJECT_ID('tempdb..#Species') IS NOT NULL DROP TABLE #Species;
CREATE TABLE #Species (Id INT PRIMARY KEY, Name NVARCHAR(200), PlantTypeName NVARCHAR(200), BedTransplantDays INT NULL,
                       NameKey NVARCHAR(400), IsActive BIT NOT NULL DEFAULT 1);
INSERT #Species (Id, Name, PlantTypeName, NameKey)
SELECT s.Id, s.Name, LTRIM(RTRIM(t.Name)),
       UPPER(LTRIM(RTRIM(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
           REPLACE(REPLACE(REPLACE(REPLACE(s.Name, CHAR(13), N' '), CHAR(10), N' '), CHAR(9), N' '), NCHAR(160), N' '),
           N'      ', N' '), N'     ', N' '), N'    ', N' '), N'   ', N' '), N'  ', N' '), N'  ', N' '), N'  ', N' '))))
FROM dbo.PlantSpecies s LEFT JOIN dbo.PlantTypes t ON t.Id = s.PlantTypeId;
UPDATE x SET BedTransplantDays = s.BedTransplantDays FROM #Species x JOIN dbo.PlantSpecies s ON s.Id = x.Id;
-- PlantSpecies.IsActive exists only after 2026-10-15_PlantSpeciesIsActive.sql; without it every variety is active.
IF COL_LENGTH('dbo.PlantSpecies', 'IsActive') IS NOT NULL
    EXEC (N'UPDATE x SET IsActive = s.IsActive FROM #Species x JOIN dbo.PlantSpecies s ON s.Id = x.Id;');

DECLARE @Plan TABLE (RowNo INT PRIMARY KEY, GroupName NVARCHAR(200), VarietyName NVARCHAR(200), NameKey NVARCHAR(400), Days INT,
                     ActiveMatches INT, InactiveMatches INT, SpeciesId INT NULL, PlantType NVARCHAR(200) NULL, ExistingDays INT NULL,
                     Action NVARCHAR(40));
INSERT @Plan (RowNo, GroupName, VarietyName, NameKey, Days)
SELECT RowNo, GroupName, VarietyName, NameKey, Days FROM @Rows;
UPDATE p SET ActiveMatches   = (SELECT COUNT(*) FROM #Species s WHERE s.NameKey = p.NameKey AND s.IsActive = 1),
             InactiveMatches = (SELECT COUNT(*) FROM #Species s WHERE s.NameKey = p.NameKey AND s.IsActive = 0)
FROM @Plan p;
UPDATE p SET SpeciesId = s.Id, PlantType = s.PlantTypeName, ExistingDays = s.BedTransplantDays
FROM @Plan p JOIN #Species s ON s.NameKey = p.NameKey AND s.IsActive = 1
WHERE p.ActiveMatches = 1;
UPDATE p SET Action = CASE
        WHEN p.ActiveMatches > 1 THEN N'ERROR: name matches 2+ varieties'
        WHEN p.ActiveMatches = 0 AND p.InactiveMatches > 0 THEN N'INACTIVE ONLY - skipped'
        WHEN p.ActiveMatches = 0 THEN N'NOT IN DATABASE - skipped'
        WHEN p.ExistingDays IS NULL THEN N'SET'
        WHEN p.ExistingDays <> p.Days THEN N'CHANGE'
        ELSE N'SAME' END
FROM @Plan p;

SELECT p.RowNo AS [Row], p.GroupName AS [Group], p.VarietyName AS Variety, p.Days, p.SpeciesId, p.PlantType,
       p.ExistingDays, p.Action
FROM @Plan p ORDER BY CASE WHEN p.Action IN (N'SET', N'SAME') THEN 1 ELSE 0 END, p.RowNo;
SELECT p.Action, COUNT(*) AS Varieties FROM @Plan p GROUP BY p.Action ORDER BY p.Action;

PRINT '=== Varieties that already have Bed Transplant Days but are NOT in the workbook (left unchanged) ===';
SELECT s.Id, s.Name, s.PlantTypeName, s.BedTransplantDays
FROM #Species s
WHERE s.BedTransplantDays IS NOT NULL AND NOT EXISTS (SELECT 1 FROM @Plan p WHERE p.SpeciesId = s.Id)
ORDER BY s.Name;

IF EXISTS (SELECT 1 FROM @Plan WHERE Action LIKE N'ERROR%')
    THROW 54610, 'Plan has ERROR rows (see above) -- nothing written. Resolve them (owner decision) before COMMIT.', 1;

IF N'$(MODE)' COLLATE Latin1_General_BIN2 = N'PREVIEW'
BEGIN
    PRINT '=== PREVIEW only -- nothing written. Re-run with MODE=COMMIT to apply. ===';
    RETURN;
END

-- ---- apply (COMMIT) --------------------------------------------------------
DECLARE @Expected INT = (SELECT COUNT(*) FROM @Plan WHERE Action IN (N'SET', N'CHANGE'));
DECLARE @Changed INT;
BEGIN TRANSACTION BedTransplantDays;

-- Lock the varieties and write only where the value is still the one PREVIEW saw (a Plant Master edit meanwhile = stop).
UPDATE s SET BedTransplantDays = p.Days
FROM dbo.PlantSpecies s WITH (UPDLOCK, HOLDLOCK)
JOIN @Plan p ON p.SpeciesId = s.Id
WHERE p.Action IN (N'SET', N'CHANGE') AND ISNULL(s.BedTransplantDays, -1) = ISNULL(p.ExistingDays, -1);
SET @Changed = @@ROWCOUNT;

IF @Changed <> @Expected
BEGIN
    ROLLBACK TRANSACTION BedTransplantDays;
    THROW 54611, 'A variety changed while the script ran -- rolled back, nothing written. Run PREVIEW again.', 1;
END
IF EXISTS (SELECT 1 FROM dbo.PlantSpecies s JOIN @Plan p ON p.SpeciesId = s.Id
           WHERE p.Action IN (N'SET', N'CHANGE', N'SAME') AND ISNULL(s.BedTransplantDays, -1) <> p.Days)
BEGIN
    ROLLBACK TRANSACTION BedTransplantDays;
    THROW 54612, 'Verification failed -- rolled back, nothing written.', 1;
END

COMMIT TRANSACTION BedTransplantDays;
PRINT '=== COMMITTED: ' + CAST(@Expected AS VARCHAR(10)) + ' varieties set or changed. ===';
GO
";

        private static Dictionary<string, int> Headers(IXLWorksheet ws)
        {
            var headers = new Dictionary<string, int>();
            var first = ws.FirstRowUsed();
            if (first == null)
                return headers;
            foreach (var cell in first.CellsUsed())
            {
                var k = Key(Text(cell.Value));
                if (k.Length > 0 && !headers.ContainsKey(k))
                    headers[k] = cell.Address.ColumnNumber;
            }
            return headers;
        }

        private static IEnumerable<IXLRow> DataRows(IXLWorksheet ws)
        {
            var first = ws.FirstRowUsed();
            var last = ws.LastRowUsed();
            if (first == null || last == null)
                yield break;
            for (var r = first.RowNumber() + 1; r <= last.RowNumber(); r++)
                yield return ws.Row(r);
        }

        private static string Text(XLCellValue v) => v.Type switch
        {
            XLDataType.Blank => "",
            XLDataType.Text => v.GetText().Trim(),
            XLDataType.Number => v.GetNumber().ToString(CultureInfo.InvariantCulture),
            _ => v.ToString().Trim(),
        };

        public static string Key(string? s) => PlantSpeciesNameRules.Key(s);

        private static string Show(int? days, bool valid) => !valid ? "invalid" : days?.ToString(CultureInfo.InvariantCulture) ?? "blank";

        // An N'...' literal; quotes doubled. Names come from the owner's file and are data, never SQL.
        public static string Sql(string s) => "N'" + s.Replace("'", "''") + "'";

        private static string Comment(string s) => s.Replace("\r", " ").Replace("\n", " ");
    }
}
