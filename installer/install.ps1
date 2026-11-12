# Moon Sweepers VR - installer
#
# What it does, in order:
#   1. Finds the Steam install of Moon Sweepers and checks the game is closed.
#   2. Backs up your save.
#   3. Installs BepInEx 6.0.0-be.791 (downloaded from the official BepInEx build server).
#   4. Installs the "MoonSweepers BepInEx Patcher" 1.0.2 by boverson777. This is NOT included
#      or re-hosted here: you download it yourself from its Nexus Mods page and the installer
#      picks it up from your Downloads folder.
#   5. Installs UUVR (downloaded from Raicuparta's official GitHub releases).
#   6. Installs the Moon Sweepers VR plugin and its settings.
#
# Every download is checked against the exact file the mod was tested with (SHA-256).

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'   # the progress bar makes downloads very slow in Windows PowerShell

# ---------------------------------------------------------------- settings
$AppId    = '5335510'
$ExeName  = 'MoonSweepers.exe'
$ModFiles = Join-Path $PSScriptRoot 'mod'
$WorkDir  = Join-Path $env:TEMP 'MoonSweepersVR-install'

$BepInEx = @{
    Name   = 'BepInEx 6.0.0-be.791'
    File   = 'BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.791+b782da4.zip'
    Urls   = @('https://builds.bepinex.dev/projects/bepinex_be/791/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.791%2Bb782da4.zip')
    Sha256 = 'BB1C510909B69522733791F3A0817A487379A52DA6468A492F1C9C19B3EC6F9A'
    CoreDllSha256 = 'A9C55B457259672EEFC34417E86E627BEAF90ACB11A8AE889191A7FF70F72143'   # BepInEx\core\BepInEx.Core.dll
}

$Uuvr = @{
    Name   = 'UUVR (il2cpp-legacy)'
    File   = 'uuvr-il2cpp-legacy.zip'
    Urls   = @(
        'https://github.com/Raicuparta/uuvr/releases/download/0.4.0/uuvr-il2cpp-legacy.zip',
        'https://github.com/Raicuparta/uuvr/releases/download/v0.4.0/uuvr-il2cpp-legacy.zip',
        'https://github.com/Raicuparta/uuvr/releases/latest/download/uuvr-il2cpp-legacy.zip'
    )
    Sha256 = '282F5FB30F9FD9E918EA673A83BA319025E46B2703D828504E3479EAE817F036'
}

$Patcher = @{
    Name      = 'MoonSweepers BepInEx Patcher 1.0.2 (by boverson777)'
    Page      = 'https://www.nexusmods.com/moonsweepers/mods/9?tab=files'
    DllName   = 'MoonSweepers.ModEngine.dll'
    DllSha256 = '9CEE6092D2E80AC42B6AAFDBB552FE8ADA1F35ED5C851268C66093CCAA762C33'
    Version   = '1.0.2'
}

# ---------------------------------------------------------------- helpers
function Say($text, $colour = 'Gray') { Write-Host $text -ForegroundColor $colour }
function Step($text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }

function Get-Sha256($path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToUpperInvariant() }

function Ask-YesNo($question) {
    while ($true) {
        $a = (Read-Host "$question (Y/N)").Trim().ToUpperInvariant()
        if ($a -eq 'Y' -or $a -eq 'YES') { return $true }
        if ($a -eq 'N' -or $a -eq 'NO')  { return $false }
    }
}

function Get-DownloadsFolder {
    try {
        $p = (New-Object -ComObject Shell.Application).NameSpace('shell:Downloads').Self.Path
        if ($p -and (Test-Path -LiteralPath $p)) { return $p }
    } catch { }
    return (Join-Path $env:USERPROFILE 'Downloads')
}

function Find-Game {
    $steamRoots = @()
    foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam') {
        try {
            $p = Get-ItemProperty -Path $key -ErrorAction Stop
            foreach ($name in 'SteamPath', 'InstallPath') { if ($p.$name) { $steamRoots += ($p.$name -replace '/', '\') } }
        } catch { }
    }
    $steamRoots += 'C:\Program Files (x86)\Steam'
    $libraries = @()
    foreach ($root in ($steamRoots | Select-Object -Unique)) {
        if (-not (Test-Path $root)) { continue }
        $libraries += $root
        $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($line in Get-Content $vdf) {
                if ($line -match '"path"\s+"(.+)"') { $libraries += ($Matches[1] -replace '\\\\', '\') }
            }
        }
    }
    foreach ($lib in ($libraries | Select-Object -Unique)) {
        $manifest = Join-Path $lib "steamapps\appmanifest_$AppId.acf"
        if (-not (Test-Path $manifest)) { continue }
        $dirName = 'Moon Sweepers'
        foreach ($line in Get-Content $manifest) { if ($line -match '"installdir"\s+"(.+)"') { $dirName = $Matches[1] } }
        $dir = Join-Path $lib "steamapps\common\$dirName"
        if (Test-Path (Join-Path $dir $ExeName)) { return $dir }
    }
    return $null
}

# Returns the path of a verified copy of a dependency zip: a local copy if one exists
# (Downloads folder or next to the installer), otherwise downloaded from its official URL.
function Get-Dependency($dep) {
    $searchDirs = @((Get-DownloadsFolder), (Split-Path $PSScriptRoot), $PSScriptRoot)
    foreach ($dir in $searchDirs) {
        $local = Join-Path $dir $dep.File
        if ((Test-Path -LiteralPath $local) -and ((Get-Sha256 $local) -eq $dep.Sha256)) {
            Say "Using the copy already in $dir"
            return $local
        }
    }

    New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
    $target = Join-Path $WorkDir $dep.File
    $untested = $null
    foreach ($url in $dep.Urls) {
        try {
            Say "Downloading from $url"
            Invoke-WebRequest -Uri $url -OutFile $target -UseBasicParsing
        } catch {
            Say "  not available there ($($_.Exception.Message))" 'DarkGray'
            continue
        }
        if ((Get-Sha256 $target) -eq $dep.Sha256) { Say 'Download verified.' 'Green'; return $target }
        Say '  that file is a different version from the one this mod was tested with.' 'Yellow'
        $untested = Join-Path $WorkDir ('untested-' + $dep.File)
        Move-Item -Force $target $untested
    }

    if ($untested) {
        Say "Only a different version of $($dep.Name) could be downloaded." 'Yellow'
        if (Ask-YesNo 'Use it anyway? It may or may not work') { return $untested }
    }
    throw "Couldn't get $($dep.Name). Check your internet connection and run Install.bat again, or put '$($dep.File)' in your Downloads folder first."
}

function Expand-To($zip, $dest) {
    if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
    Expand-Archive -LiteralPath $zip -DestinationPath $dest -Force
}

# ---------------------------------------------------------------- patcher (Nexus Mods)
function Test-PatcherDll($path) {
    # Returns 'ok', 'other:<version>' or 'broken'.
    if (-not (Test-Path -LiteralPath $path)) { return 'broken' }
    if ((Get-Sha256 $path) -eq $Patcher.DllSha256) { return 'ok' }
    try {
        $v = [Reflection.AssemblyName]::GetAssemblyName($path).Version
        return "other:$v"
    } catch { return 'broken' }
}

# Looks inside a zip for the patcher DLL. Returns $true if it is there.
function Test-PatcherZip($zipPath) {
    try {
        $z = [IO.Compression.ZipFile]::OpenRead($zipPath)
        try { return [bool]($z.Entries | Where-Object { $_.Name -eq $Patcher.DllName }) }
        finally { $z.Dispose() }
    } catch { return $false }   # still downloading, or not a zip
}

function Install-PatcherFrom($file, $patcherDir) {
    New-Item -ItemType Directory -Force -Path $patcherDir | Out-Null
    $dest = Join-Path $patcherDir $Patcher.DllName
    if ($file -like '*.dll') { Copy-Item -LiteralPath $file -Destination $dest -Force; return }
    $z = [IO.Compression.ZipFile]::OpenRead($file)
    try {
        $entry = $z.Entries | Where-Object { $_.Name -eq $Patcher.DllName } | Select-Object -First 1
        if (-not $entry) { throw "That file doesn't contain $($Patcher.DllName)." }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dest, $true)
    } finally { $z.Dispose() }
}

function Find-PatcherDownload($downloads, $since) {
    if (-not (Test-Path -LiteralPath $downloads)) { return $null }
    $zips = Get-ChildItem -LiteralPath $downloads -Filter '*.zip' -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Length -lt 50MB -and ($null -eq $since -or $_.LastWriteTime -ge $since -or $_.CreationTime -ge $since) } |
            Sort-Object LastWriteTime -Descending
    foreach ($z in $zips) {
        # Check likely names first so we don't open every zip in the folder.
        if ($null -eq $since -and $z.Name -notmatch '(?i)moon|sweep|patcher|modengine') { continue }
        if (Test-PatcherZip $z.FullName) { return $z.FullName }
    }
    return $null
}

function Pick-PatcherFile {
    try {
        Add-Type -AssemblyName System.Windows.Forms
        $dlg = New-Object System.Windows.Forms.OpenFileDialog
        $dlg.Title  = 'Select the MoonSweepers BepInEx Patcher zip you downloaded from Nexus Mods'
        $dlg.Filter = 'Patcher download (*.zip;*.dll)|*.zip;*.dll|All files (*.*)|*.*'
        $dlg.InitialDirectory = Get-DownloadsFolder
        if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { return $dlg.FileName }
    } catch {
        $typed = (Read-Host 'Paste the full path of the downloaded patcher zip').Trim().Trim('"')
        if ($typed) { return $typed }
    }
    return $null
}

function Install-Patcher($game) {
    $patcherDir = Join-Path $game 'BepInEx\patchers\MoonSweepers.ModEngine'
    $dll = Join-Path $patcherDir $Patcher.DllName

    # 1. Already installed?
    if (Test-Path -LiteralPath $dll) {
        $state = Test-PatcherDll $dll
        if ($state -eq 'ok') { Say "Already installed (version $($Patcher.Version))." 'Green'; return }
        if ($state -like 'other:*') {
            $v = $state.Substring(6)
            Say "Version $v is installed; this mod was tested with $($Patcher.Version)." 'Yellow'
            if (Ask-YesNo 'Keep the installed version?') { return }
        } else {
            Say 'The installed patcher file looks damaged, so it will be replaced.' 'Yellow'
        }
    }

    # 2. Already downloaded?
    $downloads = Get-DownloadsFolder
    $found = Find-PatcherDownload $downloads $null

    # 3. Otherwise, the user downloads it from Nexus Mods themselves.
    if (-not $found) {
        Say ''
        Say 'This mod needs the "MoonSweepers BepInEx Patcher" by boverson777.' 'White'
        Say "It isn't included here: it's someone else's work and is only available from Nexus Mods."
        Say ''
        Say 'Opening its Nexus Mods page in your browser now. On that page:' 'White'
        Say '  1. Log in (a free Nexus account is fine).'
        Say '  2. Under Files, find "MoonSweepers BepInEx Patcher" and click MANUAL DOWNLOAD.'
        Say '  3. If asked, choose SLOW DOWNLOAD (free) and let it save to your Downloads folder.'
        Say ''
        Say "If the page doesn't open, go to: $($Patcher.Page)" 'DarkGray'
        try { Start-Process $Patcher.Page } catch { }
        Say ''
        Say "Waiting for the download to appear in $downloads ..." 'White'
        Say '(Press B to choose the file yourself, or Q to cancel.)' 'DarkGray'

        $since = (Get-Date).AddMinutes(-2)
        $lastNote = Get-Date
        while (-not $found) {
            Start-Sleep -Milliseconds 1500
            $found = Find-PatcherDownload $downloads $since
            if ($found) { break }
            try {
                while ([Console]::KeyAvailable) {
                    $key = [Console]::ReadKey($true).Key
                    if ($key -eq 'Q') { throw 'Cancelled. Nothing else was changed; run Install.bat again when you have the patcher.' }
                    if ($key -eq 'B') {
                        $picked = Pick-PatcherFile
                        if ($picked -and (Test-Path -LiteralPath $picked)) {
                            if ($picked -like '*.dll' -or (Test-PatcherZip $picked)) { $found = $picked }
                            else { Say "That file doesn't contain the patcher. Keep waiting, or press B to try another." 'Yellow' }
                        }
                    }
                }
            } catch [System.InvalidOperationException] { }   # no console keyboard (e.g. run from an editor)
            if (((Get-Date) - $lastNote).TotalSeconds -ge 30) {
                Say '  still waiting for the patcher download...' 'DarkGray'
                $lastNote = Get-Date
            }
        }
        Say 'Download found.' 'Green'
    }

    Say "Installing from: $found"
    Install-PatcherFrom $found $patcherDir

    # 4. Verify before carrying on with the VR mod.
    $state = Test-PatcherDll $dll
    if ($state -eq 'ok') { Say "Patcher installed and verified (version $($Patcher.Version))." 'Green'; return }
    if ($state -like 'other:*') {
        $v = $state.Substring(6)
        Say "That download is patcher version $v; this mod was tested with $($Patcher.Version)." 'Yellow'
        if (Ask-YesNo 'Continue with this version?') { return }
        throw "Stopped. Download version $($Patcher.Version) from $($Patcher.Page) and run Install.bat again."
    }
    throw "The patcher didn't install correctly. Download it again from $($Patcher.Page) and run Install.bat again."
}

# ---------------------------------------------------------------- main
$logPath = Join-Path (Split-Path $PSScriptRoot) 'install-log.txt'
try { Start-Transcript -Path $logPath -Force | Out-Null } catch { }

try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }

    Say ''
    Say '  MOON SWEEPERS VR - installer' 'Cyan'
    Say '  ----------------------------'
    if (-not (Test-Path (Join-Path $ModFiles 'BepInEx\plugins\MoonXrStarter\MoonXrStarter.dll'))) {
        throw "The 'files\mod' folder is missing. Extract the WHOLE zip first, then run Install.bat again."
    }

    # --- game
    Step 'Finding Moon Sweepers'
    $game = Find-Game
    if (-not $game) {
        Say "Couldn't find Moon Sweepers automatically." 'Yellow'
        Say 'In Steam: right-click Moon Sweepers > Manage > Browse local files,'
        Say 'then copy the folder path from the address bar and paste it here.'
        $typed = (Read-Host 'Game folder').Trim().Trim('"')
        if ($typed -and (Test-Path (Join-Path $typed $ExeName))) { $game = $typed }
        else { throw "That folder doesn't contain $ExeName. Nothing was changed." }
    }
    Say "Game found: $game" 'Green'
    if (Get-Process -Name 'MoonSweepers' -ErrorAction SilentlyContinue) {
        throw 'Moon Sweepers is running. Close the game and run Install.bat again.'
    }

    # --- save backup (once per day, never overwrites an existing backup)
    Step 'Backing up your save'
    $saveDir = Join-Path $env:USERPROFILE 'AppData\LocalLow\DragDropGames\MoonSweepers'
    if (Test-Path $saveDir) {
        $backup = Join-Path (Split-Path $saveDir) ('MoonSweepers_save_backup_' + (Get-Date -Format 'yyyy-MM-dd'))
        if (Test-Path $backup) { Say "Already backed up today: $backup" }
        else {
            Copy-Item -Path $saveDir -Destination $backup -Recurse -Force
            Say "Saved to: $backup" 'Green'
        }
    } else { Say 'No save found yet (nothing to back up).' }

    # --- 1. BepInEx
    Step "1/4  $($BepInEx.Name)"
    $coreDll = Join-Path $game 'BepInEx\core\BepInEx.Core.dll'
    if ((Test-Path $coreDll) -and ((Get-Sha256 $coreDll) -eq $BepInEx.CoreDllSha256)) {
        Say 'Already installed.' 'Green'
    } else {
        if (Test-Path $coreDll) { Say 'A different BepInEx version is installed; replacing it with the one this mod needs.' 'Yellow' }
        $zip = Get-Dependency $BepInEx
        $tmp = Join-Path $WorkDir 'bepinex'
        Expand-To $zip $tmp
        Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $tmp 'changelog.txt')
        Copy-Item -Path (Join-Path $tmp '*') -Destination $game -Recurse -Force
        if (-not (Test-Path $coreDll)) { throw 'BepInEx did not install correctly.' }
        Say 'Installed.' 'Green'
    }

    # --- 2. patcher from Nexus Mods (not bundled)
    Step "2/4  $($Patcher.Name)"
    Install-Patcher $game

    # --- 3. UUVR
    Step "3/4  $($Uuvr.Name)"
    $zip = Get-Dependency $Uuvr
    $tmp = Join-Path $WorkDir 'uuvr'
    Expand-To $zip $tmp
    foreach ($part in 'patchers', 'plugins') {
        $src = Join-Path $tmp $part
        if (-not (Test-Path $src)) { throw "The UUVR download is missing its '$part' folder." }
        $dst = Join-Path $game "BepInEx\$part\uuvr-il2cpp-legacy"
        New-Item -ItemType Directory -Force -Path $dst | Out-Null
        Copy-Item -Path (Join-Path $src '*') -Destination $dst -Recurse -Force
    }
    Say 'Installed.' 'Green'

    # --- 4. the VR mod itself
    Step '4/4  Moon Sweepers VR plugin'
    $pluginDir = Join-Path $game 'BepInEx\plugins\MoonXrStarter'
    New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
    Copy-Item -Path (Join-Path $ModFiles 'BepInEx\plugins\MoonXrStarter\*') -Destination $pluginDir -Recurse -Force

    $cfgDir = Join-Path $game 'BepInEx\config'
    New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
    Copy-Item -Path (Join-Path $ModFiles 'BepInEx\config\raicuparta.uuvr-legacy.cfg') -Destination $cfgDir -Force
    $vrCfg = Join-Path $cfgDir 'claude.moonxrstarter.cfg'
    if (Test-Path $vrCfg) { Say 'Kept your existing VR settings (height, menu size etc).' }
    else { Copy-Item -Path (Join-Path $ModFiles 'BepInEx\config\claude.moonxrstarter.cfg') -Destination $cfgDir -Force }

    # Quest controller layout (grips = LB/RB). UUVR copies its CopyToGame folder into the game on
    # every launch, so the layout goes there; also update the game's copy if it already exists.
    $binding = Join-Path $ModFiles 'SteamVR\bindings_oculus_touch.json'
    $dataDir = Join-Path $game ([IO.Path]::GetFileNameWithoutExtension($ExeName) + '_Data')
    foreach ($d in (Join-Path $game 'BepInEx\patchers\uuvr-il2cpp-legacy\CopyToGame\Data\StreamingAssets\SteamVR'),
                   (Join-Path $dataDir 'StreamingAssets\SteamVR')) {
        if ($d -like '*CopyToGame*' -or (Test-Path $d)) {
            New-Item -ItemType Directory -Force -Path $d | Out-Null
            Copy-Item -LiteralPath $binding -Destination $d -Force
        }
    }

    # --- final check
    $missing = @()
    foreach ($rel in 'winhttp.dll',
                     'BepInEx\core\BepInEx.Core.dll',
                     ('BepInEx\patchers\MoonSweepers.ModEngine\' + $Patcher.DllName),
                     'BepInEx\patchers\uuvr-il2cpp-legacy\Uuvr.Patcher.dll',
                     'BepInEx\plugins\uuvr-il2cpp-legacy\Uuvr.dll',
                     'BepInEx\plugins\MoonXrStarter\MoonXrStarter.dll') {
        if (-not (Test-Path (Join-Path $game $rel))) { $missing += $rel }
    }
    if ($missing.Count -gt 0) { throw ('Some files are missing after install: ' + ($missing -join ', ')) }
    try { Get-ChildItem -LiteralPath (Join-Path $game 'BepInEx') -Recurse -File | Unblock-File } catch { }

    Say ''
    Say '  Installed.' 'Green'
    Say ''
    Say '  To play:'
    Say '   1. Connect your headset to the PC and start SteamVR.'
    Say '   2. Launch Moon Sweepers from Steam.'
    Say '      The first launch takes a minute or two longer than normal while'
    Say '      BepInEx prepares the game (it needs an internet connection once).'
    Say ''
    Say '  Controls and troubleshooting are in README.txt.'
}
catch {
    Say ''
    Say ('  Install stopped: ' + $_.Exception.Message) 'Red'
    Say "  A log of this run is in $logPath" 'DarkGray'
}
finally {
    if (Test-Path $WorkDir) { Remove-Item -Recurse -Force $WorkDir -ErrorAction SilentlyContinue }
    try { Stop-Transcript | Out-Null } catch { }
}
