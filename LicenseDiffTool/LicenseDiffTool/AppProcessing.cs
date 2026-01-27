using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using LibGit2Sharp;

namespace LicenseDiffTool.AppProcessing
{
    public class AppConfig
    {
        public string Name { get; set; }
        public string GitUrl { get; set; }
        public string FromCommit { get; set; }
        public string ToCommit { get; set; }
        public ExcludeConfig Excludes { get; set; } = new();
        public List<string> CsprojPaths { get; set; } = new();
        public List<string> NpmProjectDirs { get; set; } = new();
    }

    public class ExcludeConfig
    {
        public List<string> Nuget { get; set; } = new();
        public List<string> Npm { get; set; } = new();
    }

    public class DependencyInfo
    {
        public string Name { get; set; }
        public string Version { get; set; }
        public string PackageManager { get; set; }  // "nuget" oder "npm"
        public string License { get; set; } = "UNKNOWN";
        public string LicenseUrl { get; set; }
    }

    public enum DiffChangeType
    {
        Added,
        Removed,
        LicenseChanged
    }

    public class DiffEntry
    {
        public DependencyInfo From { get; set; }
        public DependencyInfo To { get; set; }
        public DiffChangeType ChangeType { get; set; }
    }

    public class AppResult
    {
        public string AppName { get; set; }
        public List<DependencyInfo> FromDependencies { get; set; }
        public List<DependencyInfo> ToDependencies { get; set; }
        public List<DiffEntry> DiffEntries { get; set; }
    }

    public class AppProcessor
    {
        public AppResult ProcessApp(AppConfig app, string workingBaseDir)
        {
            var repoDir = Path.Combine(workingBaseDir, app.Name);

            CloneOrOpen(app.GitUrl, repoDir);

            Checkout(repoDir, app.FromCommit);
            var fromDeps = AnalyzeAllDependencies(repoDir, app);
            ApplyExcludes(fromDeps, app.Excludes);
            // später: LicenseResolver.Resolve(fromDeps)

            Checkout(repoDir, app.ToCommit);
            var toDeps = AnalyzeAllDependencies(repoDir, app);
            ApplyExcludes(toDeps, app.Excludes);
            // später: LicenseResolver.Resolve(toDeps)

            var diff = CalculateDiff(fromDeps, toDeps);

            return new AppResult
            {
                AppName = app.Name,
                FromDependencies = fromDeps,
                ToDependencies = toDeps,
                DiffEntries = diff
            };
        }

        private void CloneOrOpen(string gitUrl, string repoDir)
        {
            if (Directory.Exists(Path.Combine(repoDir, ".git")))
                return;

            Directory.CreateDirectory(repoDir);
            Repository.Clone(gitUrl, repoDir);
        }

        private void Checkout(string repoDir, string commitHash)
        {
            using var repo = new Repository(repoDir);
            var commit = repo.Lookup<Commit>(commitHash)
                         ?? throw new InvalidOperationException($"Commit {commitHash} not found in {repoDir}");
            Commands.Checkout(repo, commit, new CheckoutOptions
            {
                CheckoutModifiers = CheckoutModifiers.Force
            });
        }

        private List<DependencyInfo> AnalyzeAllDependencies(string repoDir, AppConfig app)
        {
            var result = new List<DependencyInfo>();

            foreach (var relativeCsproj in app.CsprojPaths)
            {
                var csprojPath = Path.Combine(repoDir, relativeCsproj);
                if (!File.Exists(csprojPath)) continue;
                result.AddRange(AnalyzeNuGet(csprojPath));
            }

            foreach (var relativeDir in app.NpmProjectDirs)
            {
                var npmDir = Path.Combine(repoDir, relativeDir);
                if (!Directory.Exists(npmDir)) continue;
                result.AddRange(AnalyzeNpm(npmDir));
            }

            return result;
        }

        private IEnumerable<DependencyInfo> AnalyzeNuGet(string csprojPath)
        {
            var deps = new List<DependencyInfo>();

            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"list \"{csprojPath}\" package --include-transitive --format json",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return deps;

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"dotnet list package failed for {csprojPath}: {error}");

            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;
            if (!root.TryGetProperty("projects", out var projects)) return deps;

            foreach (var project in projects.EnumerateArray())
            {
                if (!project.TryGetProperty("frameworks", out var frameworks)) continue;
                foreach (var fw in frameworks.EnumerateArray())
                {
                    AddNuGetPackagesFromArray(fw, "topLevelPackages", deps);
                    AddNuGetPackagesFromArray(fw, "transitivePackages", deps);
                }
            }

            return deps;
        }

        private void AddNuGetPackagesFromArray(JsonElement fw, string propertyName, List<DependencyInfo> deps)
        {
            if (!fw.TryGetProperty(propertyName, out var pkgs)) return;

            foreach (var pkg in pkgs.EnumerateArray())
            {
                var name = pkg.GetProperty("id").GetString();
                var version = pkg.GetProperty("requestedVersion").GetString()
                              ?? pkg.GetProperty("resolvedVersion").GetString();

                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(version))
                    continue;

                deps.Add(new DependencyInfo
                {
                    Name = name,
                    Version = version,
                    PackageManager = "nuget"
                });
            }
        }

        private IEnumerable<DependencyInfo> AnalyzeNpm(string workingDir)
        {
            var deps = new List<DependencyInfo>();

            var psi = new ProcessStartInfo
            {
                FileName = "npm",
                Arguments = "ls --json --production --all",
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return deps;

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (string.IsNullOrWhiteSpace(output))
                throw new InvalidOperationException($"npm ls failed in {workingDir}: {error}");

            using var doc = JsonDocument.Parse(output);
            ParseNpmDependencies(doc.RootElement, deps);

            return deps;
        }

        private void ParseNpmDependencies(JsonElement element, List<DependencyInfo> deps)
        {
            if (!element.TryGetProperty("dependencies", out var dependencies))
                return;

            foreach (var dep in dependencies.EnumerateObject())
            {
                var name = dep.Name;
                var value = dep.Value;

                if (!value.TryGetProperty("version", out var versionProp))
                    continue;

                var version = versionProp.GetString();
                if (string.IsNullOrEmpty(version)) continue;

                deps.Add(new DependencyInfo
                {
                    Name = name,
                    Version = version,
                    PackageManager = "npm"
                });

                ParseNpmDependencies(value, deps);
            }
        }

        private void ApplyExcludes(List<DependencyInfo> deps, ExcludeConfig excludes)
        {
            deps.RemoveAll(d =>
            {
                if (d.PackageManager == "nuget")
                    return IsExcluded(d.Name, excludes.Nuget);
                if (d.PackageManager == "npm")
                    return IsExcluded(d.Name, excludes.Npm);
                return false;
            });
        }

        private bool IsExcluded(string name, List<string> patterns)
        {
            foreach (var pattern in patterns)
            {
                if (string.IsNullOrWhiteSpace(pattern)) continue;

                if (pattern.EndsWith("*", StringComparison.Ordinal))
                {
                    var prefix = pattern[..^1];
                    if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                else if (string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private List<DiffEntry> CalculateDiff(
            List<DependencyInfo> from,
            List<DependencyInfo> to)
        {
            var result = new List<DiffEntry>();

            var fromMap = from
                .GroupBy(d => (d.PackageManager, d.Name))
                .ToDictionary(g => g.Key, g => g.First());

            var toMap = to
                .GroupBy(d => (d.PackageManager, d.Name))
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var kvp in toMap)
            {
                if (!fromMap.ContainsKey(kvp.Key))
                {
                    result.Add(new DiffEntry
                    {
                        From = null,
                        To = kvp.Value,
                        ChangeType = DiffChangeType.Added
                    });
                }
            }

            foreach (var kvp in fromMap)
            {
                if (!toMap.ContainsKey(kvp.Key))
                {
                    result.Add(new DiffEntry
                    {
                        From = kvp.Value,
                        To = null,
                        ChangeType = DiffChangeType.Removed
                    });
                }
            }

            foreach (var kvp in fromMap)
            {
                if (!toMap.TryGetValue(kvp.Key, out var toDep))
                    continue;

                var fromDep = kvp.Value;
                if (!string.Equals(fromDep.License, toDep.License, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(new DiffEntry
                    {
                        From = fromDep,
                        To = toDep,
                        ChangeType = DiffChangeType.LicenseChanged
                    });
                }
            }

            return result;
        }
    }
}

