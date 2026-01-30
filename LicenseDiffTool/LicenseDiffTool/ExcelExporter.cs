using ClosedXML.Excel;
using LicenseDiffTool.AppProcessing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using static LicenseDiffTool.AppProcessing.AppProcessor;

namespace LicenseDiffTool.Reporting
{
    public class ExcelExporter
    {
        public void ExportAppReport(AppResult appResult, string outputDir)
        {
            Directory.CreateDirectory(outputDir);
            var filePath = Path.Combine(outputDir, appResult.AppName + "_DiffReport.xlsx");

            using (var workbook = new XLWorkbook())
            {
                CreateDiffSheet(workbook, appResult);
                CreateCurrentDependenciesSheet(workbook, appResult);

                workbook.SaveAs(filePath);
            }
        }

        public void ExportAggregatedReport(List<AppResult> appResults, string outputDir)
        {
            Directory.CreateDirectory(outputDir);
            var filePath = Path.Combine(outputDir, "AllApps_ConsolidatedReport.xlsx");

            using (var workbook = new XLWorkbook())
            {
                CreateAggregatedCurrentDependenciesSheet(workbook, appResults);
                CreateAggregatedDiffSheet(workbook, appResults);

                workbook.SaveAs(filePath);
            }
        }

        private void CreateDiffSheet(XLWorkbook workbook, AppResult appResult)
        {
            var ws = workbook.Worksheets.Add("Diff");

            // Header
            ws.Cell(1, 1).Value = "PackageManager";
            ws.Cell(1, 2).Value = "PackageName";
            ws.Cell(1, 3).Value = "ChangeType";     // ADDED / REMOVED / LICENSE_CHANGED / VERSION_CHANGED / UNCHANGED
            ws.Cell(1, 4).Value = "FromVersion";
            ws.Cell(1, 5).Value = "FromLicense";
            ws.Cell(1, 6).Value = "ToVersion";
            ws.Cell(1, 7).Value = "ToLicense";
            ws.Cell(1, 8).Value = "LicenseUrl";

            FormatHeaderRow(ws.Row(1));

            var fromMap = appResult.FromDependencies
    .GroupBy(d => d.PackageManager + "|" + d.Name + "|" + (d.License ?? ""))
    .ToDictionary(g => g.Key, g => g.First());

            var toMap = appResult.ToDependencies
                .GroupBy(d => d.PackageManager + "|" + d.Name + "|" + (d.License ?? ""))
                .ToDictionary(g => g.Key, g => g.First());

            var allKeys = new HashSet<string>(fromMap.Keys);
            foreach (var key in toMap.Keys)
                allKeys.Add(key);

            int rowNum = 2;

            foreach (var key in allKeys.OrderBy(k => (toMap.ContainsKey(k) ? toMap[k] : fromMap[k]).Name, StringComparer.OrdinalIgnoreCase))
            {
                fromMap.TryGetValue(key, out var fromDep);
                toMap.TryGetValue(key, out var toDep);

                var baseDep = toDep ?? fromDep;
                var pkgManager = baseDep.PackageManager;
                var name = baseDep.Name;

                var fromVersion = fromDep != null ? fromDep.Version : "";
                var fromLicense = fromDep != null ? fromDep.License : "";
                var toVersion = toDep != null ? toDep.Version : "";
                var toLicense = toDep != null ? toDep.License : "";
                var licenseUrl = toDep?.LicenseUrl ?? fromDep?.LicenseUrl;

                string changeType;

                if (fromDep == null && toDep != null)
                {
                    changeType = "ADDED";
                }
                else if (fromDep != null && toDep == null)
                {
                    changeType = "REMOVED";
                }
                else
                {
                    bool versionChanged = !string.Equals(fromVersion, toVersion, StringComparison.OrdinalIgnoreCase);
                    bool licenseChanged = !string.Equals(fromLicense, toLicense, StringComparison.OrdinalIgnoreCase);

                    if (licenseChanged)
                        changeType = "LICENSE_CHANGED";
                    else if (versionChanged)
                        changeType = "VERSION_CHANGED";
                    else
                        changeType = "UNCHANGED";
                }

                ws.Cell(rowNum, 1).Value = pkgManager;
                ws.Cell(rowNum, 2).Value = name;
                ws.Cell(rowNum, 3).Value = changeType;
                ws.Cell(rowNum, 4).Value = fromVersion;
                ws.Cell(rowNum, 5).Value = fromLicense;
                ws.Cell(rowNum, 6).Value = toVersion;
                ws.Cell(rowNum, 7).Value = toLicense;
                ws.Cell(rowNum, 8).Value = licenseUrl ?? "";

                FormatUrlCell(ws.Cell(rowNum, 8));

                rowNum++;
            }

            ws.Columns().AdjustToContents();
        }


        private void CreateCurrentDependenciesSheet(XLWorkbook workbook, AppResult appResult)
        {
            var ws = workbook.Worksheets.Add("CurrentDependencies");

            // Header
            ws.Cell(1, 1).Value = "PackageManager";
            ws.Cell(1, 2).Value = "PackageName";
            ws.Cell(1, 3).Value = "Version";
            ws.Cell(1, 4).Value = "License";
            ws.Cell(1, 5).Value = "LicenseUrl";

            FormatHeaderRow(ws.Row(1));

            int rowNum = 2;
            var sortedDeps = appResult.ToDependencies
                .OrderBy(d => d.PackageManager)
                .ThenBy(d => d.Name)
                .ToList();

            foreach (var dep in sortedDeps)
            {
                ws.Cell(rowNum, 1).Value = dep.PackageManager;
                ws.Cell(rowNum, 2).Value = dep.Name;
                ws.Cell(rowNum, 3).Value = dep.Version;
                ws.Cell(rowNum, 4).Value = dep.License;
                ws.Cell(rowNum, 5).Value = dep.LicenseUrl ?? "";

                FormatUrlCell(ws.Cell(rowNum, 5));

                rowNum++;
            }

            ws.Columns().AdjustToContents();
        }

        private void CreateAggregatedCurrentDependenciesSheet(XLWorkbook workbook, List<AppResult> appResults)
        {
            var ws = workbook.Worksheets.Add("ConsolidatedDependencies");

            // Header
            ws.Cell(1, 1).Value = "PackageManager";
            ws.Cell(1, 2).Value = "PackageName";
            ws.Cell(1, 3).Value = "FromVersion";
            ws.Cell(1, 4).Value = "ToVersion";
            ws.Cell(1, 5).Value = "FromLicense";
            ws.Cell(1, 6).Value = "ToLicense";
            ws.Cell(1, 7).Value = "HighestVersion";
            ws.Cell(1, 8).Value = "License";
            ws.Cell(1, 9).Value = "LicenseUrl";
            ws.Cell(1, 10).Value = "HasVersionChange";
            ws.Cell(1, 11).Value = "HasLicenseChange";

            FormatHeaderRow(ws.Row(1));

            // Alle PackageSummaries aus allen Apps
            var allSummaries = new List<PackageChangeSummary>();
            foreach (var result in appResults)
            {
                if (result.PackageSummaries != null)
                    allSummaries.AddRange(result.PackageSummaries);
            }

            // Lookup für LicenseUrl aus ToDependencies
            var urlLookup = appResults
                .SelectMany(r => r.ToDependencies)
                .GroupBy(d => d.PackageManager + "|" + d.Name + "|" + (d.License ?? ""))
                .ToDictionary(
                    g => g.Key,
                    g => g.FirstOrDefault(dep => !string.IsNullOrEmpty(dep.LicenseUrl)) != null
                        ? g.First(dep => !string.IsNullOrEmpty(dep.LicenseUrl)).LicenseUrl
                        : null
                );

            // Gruppieren nach (PackageManager, Name, License) – getrennte Zeilen für gleiche Namen mit verschiedenen Lizenzen
            var consolidated = allSummaries
                .GroupBy(s => new { s.PackageManager, s.Name, License = s.ToLicense })
                .Select(g =>
                {
                    var anyWithTo = g.FirstOrDefault(s => !string.IsNullOrEmpty(s.ToVersion)) ?? g.First();

                    var toLicense = anyWithTo.ToLicense ?? "";
                    var licenseKey = anyWithTo.PackageManager + "|" + anyWithTo.Name + "|" + toLicense;

                    return new
                    {
                        anyWithTo.PackageManager,
                        anyWithTo.Name,
                        FromVersion = anyWithTo.FromVersion,
                        ToVersion = anyWithTo.ToVersion,
                        FromLicense = anyWithTo.FromLicense,
                        ToLicense = anyWithTo.ToLicense,
                        HighestVersion = GetHighestVersion(
                            g.SelectMany(s => new[] { s.FromVersion, s.ToVersion })
                             .Where(v => !string.IsNullOrEmpty(v))
                             .ToList()),
                        HasVersionChange = g.Any(s => s.HasVersionChange),
                        HasLicenseChange = g.Any(s => s.HasLicenseChange),
                        License = toLicense,
                        LicenseUrl = urlLookup.ContainsKey(licenseKey) ? urlLookup[licenseKey] : null
                    };
                })
                .OrderBy(x => x.PackageManager)
                .ThenBy(x => x.Name)
                .ThenBy(x => x.License)
                .ToList();

            int rowNum = 2;
            foreach (var item in consolidated)
            {
                ws.Cell(rowNum, 1).Value = item.PackageManager;
                ws.Cell(rowNum, 2).Value = item.Name;
                ws.Cell(rowNum, 3).Value = item.FromVersion;
                ws.Cell(rowNum, 4).Value = item.ToVersion;
                ws.Cell(rowNum, 5).Value = item.FromLicense;
                ws.Cell(rowNum, 6).Value = item.ToLicense;
                ws.Cell(rowNum, 7).Value = item.HighestVersion;
                ws.Cell(rowNum, 8).Value = item.License;
                ws.Cell(rowNum, 9).Value = item.LicenseUrl ?? "";
                ws.Cell(rowNum, 10).Value = item.HasVersionChange ? "Yes" : "No";
                ws.Cell(rowNum, 11).Value = item.HasLicenseChange ? "Yes" : "No";

                FormatUrlCell(ws.Cell(rowNum, 9));

                rowNum++;
            }

            ws.Columns().AdjustToContents();
        }

        private void CreateAggregatedDiffSheet(XLWorkbook workbook, List<AppResult> appResults)
        {
            var ws = workbook.Worksheets.Add("AllDiffs");

            // Header
            ws.Cell(1, 1).Value = "App";
            ws.Cell(1, 2).Value = "PackageManager";
            ws.Cell(1, 3).Value = "PackageName";
            ws.Cell(1, 4).Value = "ChangeType";
            ws.Cell(1, 5).Value = "FromVersion";
            ws.Cell(1, 6).Value = "FromLicense";
            ws.Cell(1, 7).Value = "ToVersion";
            ws.Cell(1, 8).Value = "ToLicense";
            ws.Cell(1, 9).Value = "LicenseUrl";

            FormatHeaderRow(ws.Row(1));

            int rowNum = 2;
            foreach (var result in appResults.OrderBy(r => r.AppName))
            {
                var sortedDiffs = result.DiffEntries
                    .OrderBy(d => d.From != null ? d.From.Name : d.To.Name)
                    .ToList();

                foreach (var diff in sortedDiffs)
                {
                    var packageName = diff.From != null ? diff.From.Name : diff.To.Name;
                    var pkgManager = diff.From != null ? diff.From.PackageManager : diff.To.PackageManager;
                    var licenseUrl = diff.From != null ? diff.From.LicenseUrl : diff.To.LicenseUrl;

                    ws.Cell(rowNum, 1).Value = result.AppName;
                    ws.Cell(rowNum, 2).Value = pkgManager;
                    ws.Cell(rowNum, 3).Value = packageName;
                    ws.Cell(rowNum, 4).Value = diff.ChangeType.ToString();
                    ws.Cell(rowNum, 5).Value = diff.From != null ? diff.From.Version : "";
                    ws.Cell(rowNum, 6).Value = diff.From != null ? diff.From.License : "";
                    ws.Cell(rowNum, 7).Value = diff.To != null ? diff.To.Version : "";
                    ws.Cell(rowNum, 8).Value = diff.To != null ? diff.To.License : "";
                    ws.Cell(rowNum, 9).Value = licenseUrl ?? "";

                    FormatUrlCell(ws.Cell(rowNum, 9));

                    rowNum++;
                }
            }

            ws.Columns().AdjustToContents();
        }

        private void FormatHeaderRow(IXLRow row)
        {
            row.Style.Fill.BackgroundColor = XLColor.DarkGray;
            row.Style.Font.FontColor = XLColor.White;
            row.Style.Font.Bold = true;
            row.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }

        private void FormatUrlCell(IXLCell cell)
        {
            var value = cell.Value.ToString();
            if (!string.IsNullOrEmpty(value) && value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                cell.SetHyperlink(new XLHyperlink(value));
                cell.Style.Font.Underline = XLFontUnderlineValues.Single;
                cell.Style.Font.FontColor = XLColor.Blue;
            }
        }

        private string GetHighestVersion(List<string> versions)
        {
            if (versions == null || versions.Count == 0)
                return "";
            if (versions.Count == 1)
                return versions[0];

            return versions
                .OrderByDescending(v => ParseVersion(v))
                .FirstOrDefault() ?? versions[0];
        }

        private Version ParseVersion(string versionStr)
        {
            if (string.IsNullOrEmpty(versionStr))
                return new Version(0, 0, 0);

            // Entferne Präfixe wie "v" und Suffixe wie "-beta"
            var cleaned = Regex.Replace(versionStr, @"^v|[-\+].*$", "");

            Version result;
            if (Version.TryParse(cleaned, out result))
                return result;

            return new Version(0, 0, 0);
        }
    }
}

