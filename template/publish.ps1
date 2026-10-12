# Builds the mods and writes .\dist: each mod's DLL and .pdb, its cover and dist\manifest.json (what the mod manager reads).
# Commit and push dist/ to release.
#
#   .\publish.ps1                     every mod
#   .\publish.ps1 -Mods MyMod         only these (comma-separated): every other mod's files and manifest entry stay exactly as they
#                                     are, so players see no update for a mod that didn't change
#   .\publish.ps1 -Check              only check the mods' names (below), build nothing, write nothing
#
# It refuses to publish a mod that still has the template's names (com.yourname.examplemod, ExampleMod, YourName): see Find-Placeholders.
param(
    [string[]]$Mods = @(),
    [switch]$Check
)
$root = $PSScriptRoot
$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force $dist | Out-Null
$manifestFile = Join-Path $dist "manifest.json"

# (-Mods A,B from another PowerShell arrives as one "A,B" string)
$Mods = @($Mods | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$projects = @(Get-ChildItem (Join-Path $root "mods") -Directory | Where-Object { Test-Path (Join-Path $_.FullName "$($_.Name).csproj") })
$unknown = @($Mods | Where-Object { $projects.Name -notcontains $_ })
if ($unknown.Count -gt 0) { Write-Error "No such mod: $($unknown -join ', ') (the folder names in mods/)"; exit 1 }

# The template's names, which a mod must not be published with: its GUID (com.yourname.examplemod), its name, assembly and namespace
# (ExampleMod), the shared namespace (YourName.Shared). Any GUID, name or namespace with "yourname" or "examplemod" in it counts.
# Returns what to rename, one line each (nothing when the mod is fine). Building ExampleMod to try the template is fine: only publishing is checked.
function Find-Placeholders($proj) {
    $isPlaceholder = { param($t) $t -imatch 'yourname|examplemod' }
    $files = @(Get-ChildItem $proj.FullName -Filter *.cs -Recurse | Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' })
    $text = ($files | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
    $found = @()
    $guid = [regex]::Match($text, 'const string Guid\s*=\s*"([^"]+)"').Groups[1].Value
    if ($guid -and (& $isPlaceholder $guid)) { $found += "the GUID '$guid' (the Guid constant in Plugin.cs): make it com.<you>.<modname>" }
    $name = [regex]::Match($text, 'public const string Name\s*=\s*"([^"]+)"').Groups[1].Value
    if ($name -and (& $isPlaceholder $name)) { $found += "the Name '$name' (the Name constant in Plugin.cs): your mod's name" }
    if (& $isPlaceholder $proj.Name) { $found += "the folder and csproj name '$($proj.Name)' (mods/$($proj.Name)): rename both, and <AssemblyName> / <RootNamespace> in the csproj" }
    $namespaces = @([regex]::Matches($text, '(?m)^\s*(?:namespace|using(?:\s+static)?)\s+([A-Za-z0-9_.]+)') | ForEach-Object { $_.Groups[1].Value } |
        Where-Object { & $isPlaceholder $_ } | Select-Object -Unique)
    if ($namespaces.Count -gt 0) { $found += "the namespace $($namespaces -join ', ') (in the .cs files; the Shared code's namespace is in mods/Shared/*.cs): rename it to yours" }
    $csproj = Get-Content (Join-Path $proj.FullName "$($proj.Name).csproj") -Raw
    if ($csproj -imatch '<(AssemblyName|RootNamespace)>[^<]*(yourname|examplemod)[^<]*<') { $found += "the <AssemblyName> / <RootNamespace> in $($proj.Name).csproj: rename it to yours" }
    return $found
}

$toPublish = @($projects | Where-Object { $Mods.Count -eq 0 -or $Mods -contains $_.Name })
$bad = $false
foreach ($proj in $toPublish) {
    $problems = @(Find-Placeholders $proj)
    if ($problems.Count -gt 0) {
        $bad = $true
        Write-Host "$($proj.Name) still has the template's names, so it isn't published. Rename:" -ForegroundColor Red
        $problems | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    }
}
if ($bad) {
    Write-Host "(Just trying the template? Build it with: dotnet build mods/ExampleMod -c Release. Not publishing ExampleMod? Delete its folder, or publish only your mod: .\publish.ps1 -Mods MyMod)" -ForegroundColor Yellow
    Write-Error "Rename the mod before publishing it"; exit 1
}
if ($Check) { Write-Host "Names are fine for: $(($toPublish.Name) -join ', ')"; exit 0 }

# Only some mods: the others keep the entry the manifest already has for them (found by their DLL's name)
$kept = @{}
if ($Mods.Count -gt 0) {
    if (-not (Test-Path $manifestFile)) { Write-Error "No dist\manifest.json yet: run .\publish.ps1 once without -Mods"; exit 1 }
    foreach ($m in (Get-Content $manifestFile -Raw -Encoding UTF8 | ConvertFrom-Json).mods) { $kept[[string]$m.files[0]] = $m }
}

$manifest = @()   # becomes dist\manifest.json, which the in-game mod manager reads

foreach ($proj in $projects) {
    if ($Mods.Count -gt 0 -and $Mods -notcontains $proj.Name) {
        $entry = $kept["$($proj.Name).dll"]
        if ($entry) { $manifest += $entry } else { Write-Warning "$($proj.Name) isn't in the manifest yet and wasn't built: it's left out" }
        continue
    }
    $csproj = Join-Path $proj.FullName "$($proj.Name).csproj"

    Write-Host "Building $($proj.Name)..."
    dotnet build $csproj -c Release --nologo -v q -p:DeployToGame=false
    if ($LASTEXITCODE -ne 0) { Write-Error "Build failed for $($proj.Name)"; exit 1 }

    $out = Join-Path $proj.FullName "bin/Release/net48"
    Copy-Item (Join-Path $out "$($proj.Name).dll") $dist -Force
    Copy-Item (Join-Path $out "$($proj.Name).pdb") $dist -Force   # ScriptEngine needs the .pdb beside the DLL

    # Read guid/name/version from the mod's [BepInPlugin] constants, description from DESCRIPTION.txt (optional).
    $src = (Get-ChildItem $proj.FullName -Filter *.cs -Recurse | Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' } |
            ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
    $guid = [regex]::Match($src, 'const string Guid\s*=\s*"([^"]+)"').Groups[1].Value
    # the plugin's own (public) Name: a mod may have other private Name constants (Parkour's bar)
    $name = [regex]::Match($src, 'public const string Name\s*=\s*"([^"]+)"').Groups[1].Value
    if (-not $name) { $name = [regex]::Match($src, 'const string Name\s*=\s*"([^"]+)"').Groups[1].Value }
    $ver  = [regex]::Match($src, 'const string Version\s*=\s*"([^"]+)"').Groups[1].Value
    # Optional: the mod's GUID before it changed (public const string OldGuid = "..."): the manager treats a copy under it as an older copy.
    $oldGuid = [regex]::Match($src, 'const string OldGuid\s*=\s*"([^"]+)"').Groups[1].Value
    if (-not $guid -or -not $ver) { Write-Error "Couldn't find Guid/Version constants in $($proj.Name)"; exit 1 }
    $descFile = Join-Path $proj.FullName "DESCRIPTION.txt"
    $desc = if (Test-Path $descFile) { (Get-Content $descFile -Raw).Trim() } else { "" }

    # "What's new" shown in the manager when an update is waiting: the top section of CHANGELOG.txt (up to the first blank line).
    $notes = ""
    $logFile = Join-Path $proj.FullName "CHANGELOG.txt"
    if (Test-Path $logFile) {
        $lines = @()
        foreach ($line in (Get-Content $logFile)) {
            if ($line.Trim() -eq "") { if ($lines.Count -gt 0) { break } else { continue } }
            $lines += $line.Trim()
        }
        $notes = ($lines -join "  ")
    }

    # Optional RESTART_REQUIRED.txt: the mod can't be hot-reloaded safely, so the manager asks for a game restart. The file's text is the reason.
    $restartFile = Join-Path $proj.FullName "RESTART_REQUIRED.txt"
    $restart = if (Test-Path $restartFile) { (Get-Content $restartFile -Raw).Trim() } else { "" }

    # Optional cover image (cover.png or cover.jpg in the mod folder), shown on the mod's card in the manager. Keep it small: about 640x360, under 1 MB.
    $cover = ""
    foreach ($ext in @("png", "jpg", "jpeg")) {
        $coverSrc = Join-Path $proj.FullName "cover.$ext"
        if (Test-Path $coverSrc) {
            $cover = "$($proj.Name).cover.$ext"
            Copy-Item $coverSrc (Join-Path $dist $cover) -Force
            if ((Get-Item $coverSrc).Length -gt 1MB) { Write-Warning "$($proj.Name): the cover image is over 1 MB, so the manager will skip it" }
            break
        }
    }

    $manifest += [ordered]@{
        guid = $guid; name = $name; version = $ver; description = $desc; notes = $notes; restart = $restart; cover = $cover
        files = @("$($proj.Name).dll", "$($proj.Name).pdb")
        was = @($oldGuid | Where-Object { $_ })
    }
}

$json = ConvertTo-Json -InputObject ([ordered]@{ mods = @($manifest) }) -Depth 5
[IO.File]::WriteAllText($manifestFile, $json, (New-Object Text.UTF8Encoding($false)))

Write-Host "`nReady in $dist :"
Get-ChildItem $dist | Select-Object Name, Length, LastWriteTime
Write-Host "`nNext: git add dist; git commit -m 'Update mods'; git push"
