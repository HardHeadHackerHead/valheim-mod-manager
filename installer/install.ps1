<#
.SYNOPSIS
  One-shot Valheim mod setup for a friend who has never modded before (Windows, Steam).

.DESCRIPTION
  1. Finds the Valheim install.
  2. Installs BepInEx (the mod loader) and ScriptEngine (lets mods reload while the game runs).
  3. Downloads the mod manager (ModUpdater) and the current mods. The manager already knows where to look, so nothing needs configuring.
  After this, pressing F7 in-game opens the mod manager, which pulls updates and reloads them. No restart needed.

.PARAMETER Token
  Optional. Read-only GitHub token, only needed if the mods repo is private.

.PARAMETER ValheimDir
  Only needed if the game can't be found automatically, e.g. "D:\SteamLibrary\steamapps\common\Valheim".

.PARAMETER Force
  Reinstall BepInEx / ScriptEngine even if already present.
#>
param(
    [string]$Token = "",   # only needed if the repo is private
    [string]$ValheimDir,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$ProgressPreference = "SilentlyContinue"   # makes Invoke-WebRequest much faster in Windows PowerShell 5.1

# ---- repo settings (filled in by the repo owner) ---------------------------------------------
$Feeds  = @("HardHeadHackerHead/valheim-mod-manager", "HardHeadHackerHead/valheim-mods")   # the manager first, then the main mods
$Branch = "main"
# ------------------------------------------------------------------------------------------------

function Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Fail($msg) { Write-Host "`nERROR: $msg" -ForegroundColor Red; exit 1 }

$ghHeaders = @{
    Accept                 = "application/vnd.github+json"
    "User-Agent"           = "valheim-mods-installer"
    "X-GitHub-Api-Version" = "2022-11-28"
}
if ($Token) { $ghHeaders.Authorization = "Bearer $Token" }   # public repo: no sign-in needed
$ghRawHeaders = $ghHeaders.Clone(); $ghRawHeaders.Accept = "application/vnd.github.raw+json"

# ---- 1. find Valheim -------------------------------------------------------------------------
Step "Looking for Valheim"
function Find-Valheim {
    $steamPaths = @()
    foreach ($key in "HKCU:\Software\Valve\Steam", "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam", "HKLM:\SOFTWARE\Valve\Steam") {
        $p = (Get-ItemProperty $key -ErrorAction SilentlyContinue)
        if ($p) { $steamPaths += @($p.SteamPath, $p.InstallPath) }
    }
    $libraries = @()
    foreach ($steam in ($steamPaths | Where-Object { $_ } | Select-Object -Unique)) {
        $libraries += $steam
        $vdf = Join-Path $steam "steamapps\libraryfolders.vdf"
        if (Test-Path $vdf) {
            Select-String -Path $vdf -Pattern '"path"\s+"([^"]+)"' | ForEach-Object {
                $libraries += $_.Matches[0].Groups[1].Value -replace '\\\\', '\'
            }
        }
    }
    foreach ($lib in ($libraries | Select-Object -Unique)) {
        $candidate = Join-Path $lib "steamapps\common\Valheim"
        if (Test-Path (Join-Path $candidate "valheim.exe")) { return $candidate }
    }
    return $null
}

if (-not $ValheimDir) { $ValheimDir = Find-Valheim }
if (-not $ValheimDir -or -not (Test-Path (Join-Path $ValheimDir "valheim.exe"))) {
    Fail "Couldn't find Valheim. Re-run with -ValheimDir ""<folder containing valheim.exe>"" (Steam: right-click Valheim > Manage > Browse local files)."
}
Write-Host "Found: $ValheimDir"

if (Get-Process valheim -ErrorAction SilentlyContinue) { Fail "Valheim is running. Close it completely, then run this again." }

# ---- 2. check the token works before changing anything ---------------------------------------
Step "Checking access to the repos"
foreach ($feed in $Feeds) {
    try { Invoke-RestMethod "https://api.github.com/repos/$feed" -Headers $ghHeaders | Out-Null }
    catch { Fail "Can't read $feed ($($_.Exception.Message)). If it is private, pass -Token with a read-only token from its owner." }
}

$tmp = Join-Path $env:TEMP "valheim-mods-install"
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $tmp | Out-Null
$bepinex = Join-Path $ValheimDir "BepInEx"

# ---- 3. BepInEx -------------------------------------------------------------------------------
Step "Installing BepInEx (mod loader)"
if ((Test-Path (Join-Path $bepinex "core\BepInEx.dll")) -and -not $Force) {
    Write-Host "Already installed, skipping (use -Force to reinstall)."
} else {
    $pkg = Invoke-RestMethod "https://thunderstore.io/api/experimental/package/denikson/BepInExPack_Valheim/"
    Write-Host "BepInExPack_Valheim $($pkg.latest.version_number)"
    Invoke-WebRequest $pkg.latest.download_url -OutFile "$tmp\bepinex.zip"
    Expand-Archive "$tmp\bepinex.zip" "$tmp\bepinex" -Force
    Copy-Item "$tmp\bepinex\BepInExPack_Valheim\*" $ValheimDir -Recurse -Force
}

# ---- 4. ScriptEngine --------------------------------------------------------------------------
Step "Installing ScriptEngine (hot reload)"
$seDll = Join-Path $bepinex "plugins\ScriptEngine.dll"
if ((Test-Path $seDll) -and -not $Force) {
    Write-Host "Already installed, skipping."
} else {
    $rel = Invoke-RestMethod "https://api.github.com/repos/BepInEx/BepInEx.Debug/releases/latest" -Headers @{ "User-Agent" = "valheim-mods-installer" }
    $asset = $rel.assets | Where-Object { $_.name -like "ScriptEngine_*.zip" } | Select-Object -First 1
    if (-not $asset) { Fail "Couldn't find the ScriptEngine download." }
    Invoke-WebRequest $asset.browser_download_url -OutFile "$tmp\se.zip"
    Expand-Archive "$tmp\se.zip" "$tmp\se" -Force
    Copy-Item "$tmp\se\BepInEx\*" $bepinex -Recurse -Force
}
New-Item -ItemType Directory -Force (Join-Path $bepinex "scripts") | Out-Null

# ---- 5. ModUpdater ----------------------------------------------------------------------------
Step "Configuring the mod manager (it already knows where the mods are; this just turns on automatic updates)"
$configDir = Join-Path $bepinex "config"
New-Item -ItemType Directory -Force $configDir | Out-Null
$cfg = "[General]`r`nCheckOnStart = true`r`n"
if ($Token) { $cfg += "`r`n[Repo]`r`nToken = $Token`r`n" }
Set-Content (Join-Path $configDir "com.dhack.modupdater.cfg") $cfg -Encoding UTF8

# ---- 6. the mod manager and the mods ----------------------------------------------------------
Step "Downloading the mod manager and the mods"
$scripts = Join-Path $bepinex "scripts"
foreach ($feed in $Feeds) {
    Write-Host "From $feed"
    $files = Invoke-RestMethod "https://api.github.com/repos/$feed/contents/dist?ref=$Branch" -Headers $ghHeaders
    foreach ($f in $files) {
        if ($f.type -ne "file" -or $f.name -notmatch '\.(dll|pdb)$') { continue }
        $target = Join-Path $scripts $f.name
        if ((Test-Path $target) -and ($feed -ne $Feeds[0]) -and ($f.name -like "ModUpdater.*")) { continue }   # the manager comes from its own repo
        Invoke-WebRequest "https://api.github.com/repos/$feed/contents/dist/$($f.name)?ref=$Branch" -Headers $ghRawHeaders -OutFile $target
        Write-Host "  $($f.name)"
    }
}

Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue

# ---- done -------------------------------------------------------------------------------------
Step "Done"
Write-Host @"

Mods installed in: $ValheimDir

Next:
  1. Launch Valheim from Steam (the first launch with mods is slower than usual).
  2. Load into the world. You should see a chat line like:  [Mod]: CraftFromChests v... loaded
  3. Later, press F7 in-game to open the mod manager and fetch the newest mods - they reload instantly, no restart.

Troubleshooting: BepInEx\LogOutput.log in the Valheim folder lists every mod that loaded and any errors.
"@ -ForegroundColor Green
