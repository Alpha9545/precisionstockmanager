// 2026-11-07 Bed Sowing Alerts -- turns the owner's schedule workbook into the reviewed data-entry script.
//
//   dotnet run --project tools/BedTransplantDaysImport -c Release -- <workbook.xlsx> <output folder>
//
// Reads the workbook (Services/BedTransplantScheduleWorkbook.cs: sheets found by their headers, days validated, nothing
// guessed), then writes into <output folder>:
//   01_BedTransplantDays.sql   PREVIEW / COMMIT script (name-based; the same file runs on local and live)
//   workbook_check.csv         every variety row with its group, days and status, then every issue found
// The workbook is opened read-only and never changed. No database connection is made: matching the names to
// dbo.PlantSpecies happens inside the script (MODE=PREVIEW shows it before anything is written).
// Exit code 0 = written; 1 = the workbook could not be used (nothing written).
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PlantStockManager.Services;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: BedTransplantDaysImport <workbook.xlsx> <output folder>");
    return 1;
}
var workbookPath = Path.GetFullPath(args[0]);
var outDir = Path.GetFullPath(args[1]);

var result = BedTransplantScheduleWorkbook.Read(workbookPath);
if (result.Error != null)
{
    Console.Error.WriteLine("Not written: " + result.Error);
    return 1;
}

string sha;
using (var stream = new FileStream(workbookPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
    sha = Convert.ToHexString(SHA256.HashData(stream));

Directory.CreateDirectory(outDir);
var sql = BedTransplantScheduleWorkbook.BuildDataEntrySql(result, Path.GetFileName(workbookPath), sha, DateTime.Now);
File.WriteAllText(Path.Combine(outDir, "01_BedTransplantDays.sql"), sql, new UTF8Encoding(false));

var csv = new StringBuilder();
csv.AppendLine("Sheet,Row,Group,Variety,Days,Status");
foreach (var e in result.Entries)
    csv.AppendLine(string.Join(",", Csv(result.VarietySheet!), e.Row.ToString(CultureInfo.InvariantCulture), Csv(e.Group), Csv(e.Variety),
        e.Days?.ToString(CultureInfo.InvariantCulture) ?? "", e.Status));
csv.AppendLine();
csv.AppendLine("Sheet,Row,Issue,Message");
foreach (var i in result.Issues)
    csv.AppendLine(string.Join(",", Csv(i.Sheet), i.Row.ToString(CultureInfo.InvariantCulture), i.Kind, Csv(i.Message)));
File.WriteAllText(Path.Combine(outDir, "workbook_check.csv"), csv.ToString(), new UTF8Encoding(true));

Console.WriteLine($"Workbook  : {workbookPath}");
Console.WriteLine($"SHA-256   : {sha}");
Console.WriteLine($"Sheets    : varieties \"{result.VarietySheet}\", days \"{result.DaysSheet}\", ignored: {string.Join(", ", result.IgnoredSheets)}");
Console.WriteLine($"Groups    : {result.Groups.Count} ({result.Groups.Values.Count(g => g.Valid && g.Days != null)} with days)");
foreach (var g in result.Entries.GroupBy(e => e.Status).OrderBy(g => g.Key))
    Console.WriteLine($"  {g.Key,-13}: {g.Count()} varieties");
Console.WriteLine($"Issues    : {result.Issues.Count}");
foreach (var i in result.Issues)
    Console.WriteLine($"  [{i.Sheet} row {i.Row}] {i.Kind}: {i.Message}");
Console.WriteLine($"Written   : {outDir}");
return 0;

static string Csv(string s) => s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
