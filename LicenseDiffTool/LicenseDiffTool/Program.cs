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

        private static async Task<int> RunAsync(CliOptions options)
        {
            try
            {
                if (options.Verbose)
                    Console.WriteLine($"[INFO] Lade Config aus '{options.ConfigPath}' ...");

                var config = LoadConfig(options.ConfigPath);

                var workingDir = Path.GetFullPath(config.WorkingDirectory);
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

                // Working Directory löschen
                try
                {
                    if (options.Verbose)
                        Console.WriteLine($"[INFO] Lösche Working Directory '{workingDir}' ...");

                    Directory.Delete(workingDir, recursive: true);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[WARN] Konnte Working Directory nicht löschen: {ex.Message}");
                }

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[FATAL] {ex.Message}");
                return 1;
            }
            finally
            {
                await Task.CompletedTask;
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

                if (string.IsNullOrWhiteSpace(app.FromCommit) || string.IsNullOrWhiteSpace(app.ToCommit))
                    throw new InvalidOperationException($"Config: App '{app.Name}' braucht 'fromCommit' und 'toCommit'.");

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
