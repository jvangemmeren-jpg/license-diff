using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LicenseDiffTool.AppProcessing;
using LicenseDiffTool.Reporting;
using Microsoft.Extensions.Configuration;

namespace LicenseDiffTool.Cli
{
    public class CliOptions
    {
        public string ConfigPath { get; set; } = "./config/config.json";
        public string OutputDir { get; set; } = "./results";
        public string AppFilter { get; set; }  // optional
        public bool Verbose { get; set; }
    }

    public class ToolConfig
    {
        public string WorkingDirectory { get; set; } = "./work";
        public List<AppConfig> Applications { get; set; } = new List<AppConfig>();
    }

    internal static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            var configOption = new Option<string>("--config")
            {
                Description = "Pfad zur Konfigurationsdatei (JSON).",
                DefaultValueFactory = _ => "./config/config.json"
            };
            configOption.Aliases.Add("-c");

            var outOption = new Option<string>("--out")
            {
                Description = "Output-Ordner für Excel-Reports.",
                DefaultValueFactory = _ => "./results"
            };
            outOption.Aliases.Add("-o");

            var appOption = new Option<string>("--app")
            {
                Description = "Nur diese App aus der Config verarbeiten (Name)."
            };
            appOption.Aliases.Add("-a");

            var verboseOption = new Option<bool>("--verbose")
            {
                Description = "Ausführliches Logging aktivieren."
            };
            verboseOption.Aliases.Add("-v");

            var rootCommand = new RootCommand("license-diff: NuGet/npm Lizenz-Diff zwischen zwei Commits.")
            {
                configOption,
                outOption,
                appOption,
                verboseOption
            };

            rootCommand.SetAction(parseResult =>
            {
                var configPath = parseResult.GetValue(configOption);
                var outDir = parseResult.GetValue(outOption);
                var appFilter = parseResult.GetValue(appOption);
                var verbose = parseResult.GetValue(verboseOption);

                var options = new CliOptions
                {
                    ConfigPath = configPath ?? "./config/config.json",
                    OutputDir = outDir ?? "./results",
                    AppFilter = appFilter,
                    Verbose = verbose
                };

                var exitCode = RunAsync(options).GetAwaiter().GetResult();
                return exitCode;
            });

            var parseResult = rootCommand.Parse(args);
            return await parseResult.InvokeAsync();
        }
        // Exclude volle Namen besser regex
        // höchste Version einer Lizenz
        private static async Task<int> RunAsync(CliOptions options)
        {
            string? workingDir = null;

            try
            {
                if (options.Verbose)
                    Console.WriteLine($"[INFO] Lade Config aus '{options.ConfigPath}' ...");

                var config = LoadConfig(options.ConfigPath);

                workingDir = Path.GetFullPath(config.WorkingDirectory);
                var outputDir = Path.GetFullPath(options.OutputDir);

                Directory.CreateDirectory(workingDir);
                Directory.CreateDirectory(outputDir);

                if (options.Verbose)
                {
                    Console.WriteLine($"[INFO] Working Directory: {workingDir}");
                    Console.WriteLine($"[INFO] Output Directory:  {outputDir}");
                }

                var appProcessor = new AppProcessor();
                var excelExporter = new ExcelExporter();

                var appResults = new List<AppResult>();

                var apps = config.Applications.AsEnumerable();
                if (!string.IsNullOrWhiteSpace(options.AppFilter))
                {
                    apps = apps.Where(a =>
                        string.Equals(a.Name, options.AppFilter, StringComparison.OrdinalIgnoreCase));
                }

                foreach (var app in apps)
                {
                    try
                    {
                        if (options.Verbose)
                            Console.WriteLine($"[INFO] Verarbeite App '{app.Name}' ...");

                        var result = appProcessor.ProcessApp(app, workingDir);
                        appResults.Add(result);

                        excelExporter.ExportAppReport(result, outputDir);

                        if (options.Verbose)
                            Console.WriteLine($"[INFO] App '{app.Name}' erfolgreich verarbeitet.");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[ERROR] App '{app.Name}' fehlgeschlagen: {ex.Message}");
                    }
                }

                if (appResults.Any())
                {
                    if (options.Verbose)
                        Console.WriteLine("[INFO] Erstelle aggregierten Gesamt-Report ...");

                    excelExporter.ExportAggregatedReport(appResults, outputDir);
                }

                // am Ende aufräumen (robuster Cleanup mit Retry)
                await CleanupWorkingDirectory(workingDir, options.Verbose);

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[FATAL] {ex.Message}");

                // auch im Fehlerfall versuchen aufzuräumen
                await CleanupWorkingDirectory(workingDir, options.Verbose);
                return 1;
            }
            finally
            {
                await Task.CompletedTask;
            }
        }

        private static async Task CleanupWorkingDirectory(string? workingDir, bool verbose)
        {
            if (string.IsNullOrWhiteSpace(workingDir))
                return;

            if (!Directory.Exists(workingDir))
            {
                if (verbose)
                    Console.WriteLine($"[INFO] Working Directory '{workingDir}' existiert nicht, nichts zu löschen.");
                return;
            }

            const int maxRetries = 3;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    if (verbose)
                        Console.WriteLine($"[INFO] Versuche Working Directory zu löschen (Versuch {attempt}/{maxRetries}): {workingDir}");

                    NormalizeAttributesRecursively(workingDir);

                    Directory.Delete(workingDir, recursive: true);

                    if (verbose)
                        Console.WriteLine("[INFO] Working Directory wurde erfolgreich gelöscht.");
                    return;
                }
                catch (IOException ex)
                {
                    if (attempt == maxRetries)
                    {
                        Console.Error.WriteLine(
                            "[WARN] Konnte Working Directory nicht vollständig löschen: " + ex.Message);
                        return;
                    }

                    await Task.Delay(500);
                }
                catch (UnauthorizedAccessException ex)
                {
                    if (attempt == maxRetries)
                    {
                        Console.Error.WriteLine(
                            "[WARN] Konnte Working Directory nicht vollständig löschen (Access denied): " + ex.Message);
                        return;
                    }

                    await Task.Delay(500);
                }
            }
        }

        private static void NormalizeAttributesRecursively(string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
                return;

            foreach (var file in Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[WARN] Konnte Attribute für Datei nicht zurücksetzen: " + file + " – Fehler: " + ex.Message);
                }
            }
        }

        private static ToolConfig LoadConfig(string configPath)
        {
            if (!File.Exists(configPath))
                throw new FileNotFoundException($"Config-Datei nicht gefunden: {configPath}");

            var fullPath = Path.GetFullPath(configPath);
            var basePath = Path.GetDirectoryName(fullPath);
            var fileName = Path.GetFileName(fullPath);

            var builder = new ConfigurationBuilder();

            if (!string.IsNullOrEmpty(basePath))
            {
                builder.SetBasePath(basePath);
            }

            builder.AddJsonFile(fileName, optional: false, reloadOnChange: false);

            var configRoot = builder.Build();

            var toolConfig = new ToolConfig();
            configRoot.Bind(toolConfig);

            ValidateConfig(toolConfig);

            return toolConfig;
        }

        private static void ValidateConfig(ToolConfig config)
        {
            if (config.Applications == null || config.Applications.Count == 0)
                throw new InvalidOperationException("Config: 'applications' darf nicht leer sein.");

            if (string.IsNullOrWhiteSpace(config.WorkingDirectory))
                throw new InvalidOperationException("Config: 'workingDirectory' darf nicht leer sein.");

            foreach (var app in config.Applications)
            {
                if (string.IsNullOrWhiteSpace(app.Name))
                    throw new InvalidOperationException("Config: Jede App braucht ein 'name'.");

                if (string.IsNullOrWhiteSpace(app.GitUrl))
                    throw new InvalidOperationException($"Config: App '{app.Name}' braucht eine 'gitUrl'.");

                if ((app.CsprojPaths == null || app.CsprojPaths.Count == 0) &&
                    (app.NpmProjectDirs == null || app.NpmProjectDirs.Count == 0))
                {
                    throw new InvalidOperationException(
                        $"Config: App '{app.Name}' braucht mindestens ein csproj oder npmProjectDir.");
                }
            }
        }
    }
}
