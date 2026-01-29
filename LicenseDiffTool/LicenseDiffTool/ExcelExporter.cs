using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using LicenseDiffTool.AppProcessing;

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
            ws.Cell(1, 3).Value = "ChangeType";
            ws.Cell(1, 4).Value = "FromVersion";
            ws.Cell(1, 5).Value = "FromLicense";
            ws.Cell(1, 6).Value = "ToVersion";
            ws.Cell(1, 7).Value = "ToLicense";
            ws.Cell(1, 8).Value = "LicenseUrl";

            FormatHeaderRow(ws.Row(1));

            int rowNum = 2;
            var sortedDiffs = appResult.DiffEntries
                .OrderBy(d => d.From != null ? d.From.Name : d.To.Name)
                .ToList();

            foreach (var diff in sortedDiffs)
            {
                var packageName = diff.From != null ? diff.From.Name : diff.To.Name;
                var pkgManager = diff.From != null ? diff.From.PackageManager : diff.To.PackageManager;
                var licenseUrl = diff.From != null ? diff.From.LicenseUrl : diff.To.LicenseUrl;

                ws.Cell(rowNum, 1).Value = pkgManager;
                ws.Cell(rowNum, 2).Value = packageName;
                ws.Cell(rowNum, 3).Value = diff.ChangeType.ToString();
                ws.Cell(rowNum, 4).Value = diff.From != null ? diff.From.Version : "";
                ws.Cell(rowNum, 5).Value = diff.From != null ? diff.From.License : "";
                ws.Cell(rowNum, 6).Value = diff.To != null ? diff.To.Version : "";
                ws.Cell(rowNum, 7).Value = diff.To != null ? diff.To.License : "";
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
            ws.Cell(1, 3).Value = "HighestVersion";
            ws.Cell(1, 4).Value = "License";
            ws.Cell(1, 5).Value = "LicenseUrl";

            FormatHeaderRow(ws.Row(1));

            // Alle toCommit-Dependencies sammeln
            var allDeps = new List<DependencyInfo>();
            foreach (var result in appResults)
            {
                allDeps.AddRange(result.ToDependencies);
            }

            // Konsolidieren nach (PackageManager, Name, License) => höchste Version
            var consolidated = allDeps
                .GroupBy(d => new { d.PackageManager, d.Name, d.License })
                .Select(g => new
                {
                    g.Key.PackageManager,
                    g.Key.Name,
                    g.Key.License,
                    HighestVersion = GetHighestVersion(g.Select(d => d.Version).ToList()),
                    LicenseUrl = g.FirstOrDefault(d => !string.IsNullOrEmpty(d.LicenseUrl)) != null
                        ? g.First(d => !string.IsNullOrEmpty(d.LicenseUrl)).LicenseUrl
                        : null
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
                ws.Cell(rowNum, 3).Value = item.HighestVersion;
                ws.Cell(rowNum, 4).Value = item.License;
                ws.Cell(rowNum, 5).Value = item.LicenseUrl ?? "";

                FormatUrlCell(ws.Cell(rowNum, 5));

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

