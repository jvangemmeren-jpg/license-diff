###### **Installationshinweise**



**Voraussetzungen:**


* Windows/Linux mit installiertem .NET 8.0



* Git installiert und im PATH



* Optional: Node.js und npm, falls npm‑Abhängigkeiten analysiert werden sollen



* Zugriff auf das Git‑Repository, das analysiert werden soll



**Projekt beziehen \& bauen:**



* Repository klonen



in cmd:



|git clone <git-url> license-diff<br />cd license-diff<br />Tool bauen (Release für .NET 8.0)|
|-|



Tool bauen



in cmd:



|cd src/LicenseDiffTool<br />dotnet build -c Release<br />Tool bauen (Release für .NET 8.0)|
|-|



Das fertige CLI liegt im bin/Debug/net8.0‑Ordner





###### **Ausführung**



* Im Build-Output-Ordner:



in cmd:



|./license-diff --config ./config/config.json --out ./results --verbose|
|-|

###### 



**Beispiel-Konfiguration und Befehle**



**Beispiel einer config.json**



|{<br />&nbsp; "workingDirectory": "./work",<br />&nbsp; "applications": \[    <br />&nbsp;   {<br />&nbsp;     "name": "NugetDemo",<br />&nbsp;     "gitUrl": "https://github.com/dein-org/dein-repo.git",<br />&nbsp;     "fromCommit": "1111111111111111111111111111111111111111",<br />&nbsp;     "toCommit": "2222222222222222222222222222222222222222",<br />&nbsp;     "csprojPaths": \[<br />&nbsp;       "src/NugetDemo/NugetDemo/NugetDemo.csproj"<br />&nbsp;     ],<br />&nbsp;     "npmProjectDirs": \[],<br />&nbsp;     "excludes": {<br />&nbsp;       "nuget": \[<br />&nbsp;         "Microsoft.\*",<br />&nbsp;         "System.\*"<br />&nbsp;       ],<br />&nbsp;       "npm": \[]<br />&nbsp;     }<br />&nbsp;   },<br />&nbsp;   {<br />&nbsp;     "name": "NpmDemo",<br />&nbsp;     "gitUrl": "https://github.com/dein-org/dein-repo.git",<br />&nbsp;     "fromCommit": "1111111111111111111111111111111111111111",<br />&nbsp;     "toCommit": "2222222222222222222222222222222222222222",<br />&nbsp;     "csprojPaths": \[],<br />&nbsp;     "npmProjectDirs": \[<br />&nbsp;       "src/NpmDemo/NpmDemo"<br />&nbsp;     ],<br />&nbsp;     "excludes": {<br />&nbsp;       "nuget": \[],<br />&nbsp;       "npm": \[<br />&nbsp;         "@types/\*"<br />&nbsp;       ]<br />&nbsp;     }<br />&nbsp;   }<br />&nbsp; ]|
|-|





**workingDirectory**



* temporäres Arbeitsverzeichnis, in das Repos geklont und Commits ausgecheckt werden



**Apps/Repos, die ausgewertet werden sollen**



* name: Anzeigename, wird auch im Excel‑Dateinamen verwendet



* gitUrl: Git-URL oder lokaler Pfad zum Repo

​

* fromCommit / toCommit: Git-Commit-Hashes, zwischen denen verglichen wird

​

* csprojPaths: relative Pfade zur .csproj-Datei innerhalb des Repos



* npmProjectDirs: relative Pfade zu npm-Projektverzeichnissen (Verzeichnisse mit package.json)



* excludes.nuget / excludes.npm: Namensmuster oder Regex für zu ignorierende Pakete (siehe unten)



**Excludes (Namensmuster \& Regex)**



* Eintrag ohne \* wird als exakter Paketname interpretiert (intern Regex ^Name$)



* \* wird als Wildcard übersetzt, z.B.: Microsoft.\* → alle Pakete, die mit Microsoft. beginnen



* Beliebige Regex sind möglich (z.B. ".\*Json$"); werden intern in Regex mit IgnoreCase übersetzt



**CLI-Befehle**



* Standardaufruf



in cmd:



|license-diff --config ./config/config.json --out ./results|
|-|



Optionen

&nbsp;  

&nbsp;  → --config, -c: Pfad zur Konfigurationsdatei (JSON). Default: ./config/config.json



&nbsp;  → --out, -o: Output-Ordner für Excel-Reports. Default: ./results



&nbsp;  → --app, -a: Verarbeitet nur die App mit dem angegebenen name aus der Config



&nbsp;  →​ --verbose, -v: Aktiviert ausführliches Logging



**Output**



* Pro App



&nbsp;  → Sheet Diff:



&nbsp;    → Alle Pakete mit ChangeType = ADDED, REMOVED, LICENSE\_CHANGED, VERSION\_CHANGED, UNCHANGED

​

&nbsp;    → Spalten: PackageManager, PackageName, FromVersion, FromLicense, ToVersion, ToLicense, LicenseUrl

​

&nbsp;  → Sheet CurrentDependencies:

&nbsp;    

&nbsp;    → Aktueller Stand (toCommit) aller Dependencies mit Version, Lizenz, Lizenz-URL



* Aggregiert



&nbsp;  → AllApps\_ConsolidatedReport.xlsx mit:



&nbsp;    → Sheet ConsolidatedDependencies:



&nbsp;      → Pro Paketname über alle Apps: FromVersion, ToVersion, FromLicense, ToLicense, HighestVersion, HasVersionChange, HasLicenseChange, LicenseUrl.

​

&nbsp;    → Sheet AllDiffs:



&nbsp;      → Alle Diff-Einträge aller Apps

​



###### **Verwendete Libraries und Lizenzen**



|**Library**|**Zweck**|**Lizenz**|
|-|-|-|
|LibGit2Sharp|Git-Clone, Checkout von Commits|MIT|
|System.CommandLine|CLI-Parsing|MIT|
|ClosedXML|Erzeugen der Excel-Reports|MIT|
|Microsoft.Extensions.Configuration.Json|Laden und Binden der config.json|MIT|





