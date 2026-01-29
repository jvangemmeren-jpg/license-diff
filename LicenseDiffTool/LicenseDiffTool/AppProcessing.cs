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
        public ExcludeConfig Excludes { get; set; } = new ExcludeConfig();
        public List<string> CsprojPaths { get; set; } = new List<string>();
        public List<string> NpmProjectDirs { get; set; } = new List<string>();
    }

    public class ExcludeConfig
    {
        public List<string> Nuget { get; set; } = new List<string>();
        public List<string> Npm { get; set; } = new List<string>();
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

            // fromCommit
            Checkout(repoDir, app.FromCommit);
            var fromDeps = AnalyzeAllDependencies(repoDir, app);
            ApplyExcludes(fromDeps, app.Excludes);
            ResolveLicenses(fromDeps, repoDir);

            // toCommit
            Checkout(repoDir, app.ToCommit);
            var toDeps = AnalyzeAllDependencies(repoDir, app);
            ApplyExcludes(toDeps, app.Excludes);
            ResolveLicenses(toDeps, repoDir);

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
            using (var repo = new Repository(repoDir))
            {
                var commit = repo.Lookup<Commit>(commitHash);
                if (commit == null)
                    throw new InvalidOperationException("Commit " + commitHash + " not found in " + repoDir);

                Commands.Checkout(repo, commit, new CheckoutOptions
                {
                    CheckoutModifiers = CheckoutModifiers.Force
                });
            }
        }

        private List<DependencyInfo> AnalyzeAllDependencies(string repoDir, AppConfig app)
        {
            var result = new List<DependencyInfo>();

            foreach (var relativeCsproj in app.CsprojPaths)
            {
                var csprojPath = Path.Combine(repoDir, relativeCsproj);
                if (!File.Exists(csprojPath))
                {
                    Console.WriteLine("[DEBUG] csproj nicht gefunden: " + csprojPath);
                    continue;
                }

                Console.WriteLine("[DEBUG] Analysiere NuGet für: " + csprojPath);
                result.AddRange(AnalyzeNuGet(csprojPath));
            }

            foreach (var relativeDir in app.NpmProjectDirs)
            {
                var npmDir = Path.Combine(repoDir, relativeDir);
                if (!Directory.Exists(npmDir))
                {
                    Console.WriteLine("[DEBUG] npm-Verzeichnis nicht gefunden: " + npmDir);
                    continue;
                }

                Console.WriteLine("[DEBUG] Analysiere npm für: " + npmDir);
                result.AddRange(AnalyzeNpm(npmDir));
            }

            Console.WriteLine("[DEBUG] Gefundene Dependencies: " + result.Count);
            foreach (var d in result)
            {
                Console.WriteLine("  - " + d.PackageManager + " " + d.Name + " " + d.Version);
            }

            return result;
        }


        private IEnumerable<DependencyInfo> AnalyzeNuGet(string csprojPath)
        {
            var deps = new List<DependencyInfo>();

            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "list \"" + csprojPath + "\" package --include-transitive --format json",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var process = Process.Start(psi))
            {
                if (process == null)
                    return deps;

                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode != 0)
                    throw new InvalidOperationException("dotnet list package failed for " + csprojPath + ": " + error);

                using (var doc = JsonDocument.Parse(output))
                {
                    var root = doc.RootElement;
                    JsonElement projects;
                    if (!root.TryGetProperty("projects", out projects))
                        return deps;

                    foreach (var project in projects.EnumerateArray())
                    {
                        JsonElement frameworks;
                        if (!project.TryGetProperty("frameworks", out frameworks))
                            continue;

                        foreach (var fw in frameworks.EnumerateArray())
                        {
                            AddNuGetPackagesFromArray(fw, "topLevelPackages", deps);
                            AddNuGetPackagesFromArray(fw, "transitivePackages", deps);
                        }
                    }
                }
            }

            return deps;
        }

        private void AddNuGetPackagesFromArray(JsonElement fw, string propertyName, List<DependencyInfo> deps)
        {
            JsonElement pkgs;
            if (!fw.TryGetProperty(propertyName, out pkgs))
                return;

            foreach (var pkg in pkgs.EnumerateArray())
            {
                var name = pkg.GetProperty("id").GetString();
                string version = null;

                JsonElement requested;
                if (pkg.TryGetProperty("requestedVersion", out requested))
                    version = requested.GetString();

                if (version == null)
                {
                    JsonElement resolved;
                    if (pkg.TryGetProperty("resolvedVersion", out resolved))
                        version = resolved.GetString();
                }

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

            using (var process = Process.Start(psi))
            {
                if (process == null)
                    return deps;

                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (string.IsNullOrWhiteSpace(output))
                    throw new InvalidOperationException("npm ls failed in " + workingDir + ": " + error);

                using (var doc = JsonDocument.Parse(output))
                {
                    ParseNpmDependencies(doc.RootElement, deps);
                }
            }

            return deps;
        }

        private void ParseNpmDependencies(JsonElement element, List<DependencyInfo> deps)
        {
            JsonElement dependencies;
            if (!element.TryGetProperty("dependencies", out dependencies))
                return;

            foreach (var dep in dependencies.EnumerateObject())
            {
                var name = dep.Name;
                var value = dep.Value;

                JsonElement versionProp;
                if (!value.TryGetProperty("version", out versionProp))
                    continue;

                var version = versionProp.GetString();
                if (string.IsNullOrEmpty(version))
                    continue;

                deps.Add(new DependencyInfo
                {
                    Name = name,
                    Version = version,
                    PackageManager = "npm"
                });

                // rekursiv für transitive Dependencies
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
                if (string.IsNullOrWhiteSpace(pattern))
                    continue;

                if (pattern.EndsWith("*", StringComparison.Ordinal))
                {
                    var prefix = pattern.Substring(0, pattern.Length - 1);
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

        private List<DiffEntry> CalculateDiff(List<DependencyInfo> from, List<DependencyInfo> to)
        {
            var result = new List<DiffEntry>();

            var fromMap = from
                .GroupBy(d => d.PackageManager + "|" + d.Name)
                .ToDictionary(g => g.Key, g => g.First());

            var toMap = to
                .GroupBy(d => d.PackageManager + "|" + d.Name)
                .ToDictionary(g => g.Key, g => g.First());

            // ADDED
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

            // REMOVED
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

            // LICENSE_CHANGED
            foreach (var kvp in fromMap)
            {
                DependencyInfo toDep;
                if (!toMap.TryGetValue(kvp.Key, out toDep))
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

        // ---------- Lizenzauflösung ----------

        private void ResolveLicenses(List<DependencyInfo> deps, string repoDir)
        {
            foreach (var dep in deps)
            {
                if (dep.PackageManager == "nuget")
                    ResolveNuGetLicense(dep);
                else if (dep.PackageManager == "npm")
                    ResolveNpmLicense(dep, repoDir);
            }
        }

        private void ResolveNuGetLicense(DependencyInfo dep)
        {
            try
            {
                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var packageDir = Path.Combine(userProfile, ".nuget", "packages",
                    dep.Name.ToLowerInvariant(), dep.Version);

                if (!Directory.Exists(packageDir))
                {
                    dep.License = "UNKNOWN";
                    dep.LicenseUrl = "https://www.nuget.org/packages/" + dep.Name + "/" + dep.Version;
                    return;
                }

                var nuspecPath = Directory.GetFiles(packageDir, "*.nuspec").FirstOrDefault();
                if (nuspecPath == null)
                {
                    dep.License = "UNKNOWN";
                    dep.LicenseUrl = "https://www.nuget.org/packages/" + dep.Name + "/" + dep.Version;
                    return;
                }

                var xml = File.ReadAllText(nuspecPath);

                // license type="expression"
                var licenseTag = "<license type=\"expression\">";
                var idx = xml.IndexOf(licenseTag, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var start = idx + licenseTag.Length;
                    var end = xml.IndexOf("</license>", start, StringComparison.OrdinalIgnoreCase);
                    if (end > start)
                    {
                        dep.License = xml.Substring(start, end - start).Trim();
                        return;
                    }
                }

                // legacy licenseUrl
                var urlTag = "<licenseUrl>";
                idx = xml.IndexOf(urlTag, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var start = idx + urlTag.Length;
                    var end = xml.IndexOf("</licenseUrl>", start, StringComparison.OrdinalIgnoreCase);
                    if (end > start)
                    {
                        dep.LicenseUrl = xml.Substring(start, end - start).Trim();
                        dep.License = "UNKNOWN";
                        return;
                    }
                }

                dep.License = "UNKNOWN";
            }
            catch
            {
                dep.License = "UNKNOWN";
            }
        }

        private void ResolveNpmLicense(DependencyInfo dep, string repoDir)
        {
            try
            {
                var packageJsonPath = Path.Combine(repoDir, "node_modules", dep.Name, "package.json");
                if (!File.Exists(packageJsonPath))
                {
                    dep.License = "UNKNOWN";
                    dep.LicenseUrl = "https://www.npmjs.com/package/" + dep.Name;
                    return;
                }

                var json = File.ReadAllText(packageJsonPath);
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;

                    JsonElement licenseProp;
                    if (root.TryGetProperty("license", out licenseProp))
                    {
                        dep.License = licenseProp.GetString() ?? "UNKNOWN";
                    }
                    else
                    {
                        JsonElement licensesProp;
                        if (root.TryGetProperty("licenses", out licensesProp) &&
                            licensesProp.ValueKind == JsonValueKind.Array)
                        {
                            var list = new List<string>();
                            foreach (var lic in licensesProp.EnumerateArray())
                            {
                                JsonElement typeProp;
                                if (lic.TryGetProperty("type", out typeProp))
                                {
                                    var type = typeProp.GetString();
                                    if (!string.IsNullOrEmpty(type))
                                        list.Add(type);
                                }
                            }
                            dep.License = list.Count > 0 ? string.Join(" OR ", list) : "UNKNOWN";
                        }
                    }

                    if (string.IsNullOrEmpty(dep.License) || dep.License == "UNKNOWN")
                    {
                        JsonElement homepageProp;
                        if (root.TryGetProperty("homepage", out homepageProp))
                        {
                            dep.LicenseUrl = homepageProp.GetString();
                        }
                        else
                        {
                            JsonElement repoProp;
                            if (root.TryGetProperty("repository", out repoProp))
                            {
                                if (repoProp.ValueKind == JsonValueKind.String)
                                {
                                    dep.LicenseUrl = repoProp.GetString();
                                }
                                else
                                {
                                    JsonElement urlProp;
                                    if (repoProp.TryGetProperty("url", out urlProp))
                                        dep.LicenseUrl = urlProp.GetString();
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                dep.License = "UNKNOWN";
            }
        }
    }
}
