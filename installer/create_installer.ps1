<#
.SYNOPSIS
    Builds, signs and packages the Coworker Windows installer and prepares its update-feed entry.
    PowerShell twin of Humanlike-apps/coworker/create_dmg.sh.

.DESCRIPTION
    1. reads the version from Coworker.csproj (AppVersion / AppBuild) and checks the build number is new
    2. checks the Ed25519 update key: the public key built into the app must match the signing key
    3. dotnet publish (self-contained, win-x64 or win-arm64) into installer\build\
    4. Authenticode-signs Coworker.exe / Coworker.dll
    5. compiles installer\Coworker.iss with Inno Setup (ISCC) -> release\Coworker-<version>-win-<arch>.exe
    6. Authenticode-signs the installer (with an RFC 3161 timestamp)
    7. signs the installer with the Ed25519 update key (Sparkle edSignature) and verifies the signature
    8. writes release\appcast-item-windows.xml and copies the installer to <web>\downloads\
    9. with -UpdateAppcast, inserts the item into <web>\appcast-windows.xml and refreshes the download page data

    Like create_dmg.sh, publishing is manual by default: it prints the steps (deploy the installer, paste the
    appcast item, deploy the website).

.PARAMETER ReleaseNotesFile
    HTML release notes (env RELEASE_NOTES_FILE). One of the two notes inputs is required.
.PARAMETER ReleaseNotesHtml
    HTML release notes inline (env RELEASE_NOTES_HTML).
.PARAMETER WebPublicDir
    The web app's public/coworker folder (env WEB_PUBLIC_DIR, default ..\..\Humanlike-next\public\coworker).
    Must contain appcast-windows.xml; the installer is copied to its downloads\ folder.
.PARAMETER BaseUrl
    Where the installer will be downloaded from (env COWORKER_INSTALLER_BASE_URL,
    default https://humanlike.co/coworker/downloads). Point it at R2 / GitHub Releases if the installer is
    too big for the website deployment.
.PARAMETER Unsigned
    Local test run: no Authenticode signing, and a missing/mismatching embedded update key is only a warning.
    Never publish the result.
.PARAMETER Critical
    Marks the release as a required update (sparkle:criticalUpdate): running apps show "Update Required".
.PARAMETER InstallerPath
    Use this ready-made installer instead of publishing and running ISCC (tests, re-signing an old build).
.PARAMETER UpdateAppcast
    Insert the item into appcast-windows.xml yourself instead of leaving it as a manual paste.

.NOTES
    Code signing comes from the environment, never from the repo (see installer\README.md):
      COWORKER_SIGN_COMMAND          any signer; {file} is replaced by the path (AzureSignTool, smctl, ...)
      COWORKER_SIGN_TS_DLIB          Azure Trusted Signing: path to Azure.CodeSigning.Dlib.dll
      COWORKER_SIGN_TS_METADATA      Azure Trusted Signing: path to the metadata.json (endpoint, account, profile)
      COWORKER_SIGN_CERT_SHA1        certificate thumbprint in the Windows store (OV/EV token, KeyLocker, eSigner)
      COWORKER_SIGN_PFX / COWORKER_SIGN_PFX_PASSWORD   .pfx file (OV only; prefer the options above)
      COWORKER_SIGN_TIMESTAMP_URL    RFC 3161 server (default depends on the method)
      SIGNTOOL_PATH, ISCC_PATH       tool locations when they are not found automatically
    The Ed25519 update key comes from COWORKER_UPDATE_PRIVATE_KEY_FILE / COWORKER_UPDATE_PRIVATE_KEY, or the
    default file `UpdateSigner keygen` writes outside the repo.

.EXAMPLE
    $env:RELEASE_NOTES_FILE = "$PWD\release-notes.html"
    $env:COWORKER_SIGN_CERT_SHA1 = "<thumbprint>"
    .\installer\create_installer.ps1 -UpdateAppcast

.EXAMPLE
    .\installer\create_installer.ps1 -Unsigned -InstallerPath C:\tmp\Coworker-1.0.0-win-x64.exe -WebPublicDir C:\tmp\web
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [ValidateSet('x64', 'arm64')][string]$Arch = 'x64',
    [string]$ReleaseNotesFile = $env:RELEASE_NOTES_FILE,
    [string]$ReleaseNotesHtml = $env:RELEASE_NOTES_HTML,
    [string]$WebPublicDir = $env:WEB_PUBLIC_DIR,
    [string]$BaseUrl = $env:COWORKER_INSTALLER_BASE_URL,
    [string]$InstallerPath,
    [switch]$Unsigned,
    [switch]$Critical,
    [switch]$UpdateAppcast
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$InstallerDir = $PSScriptRoot
$AppDir = Split-Path -Parent $InstallerDir
$Csproj = Join-Path $AppDir 'Coworker.csproj'
$IssFile = Join-Path $InstallerDir 'Coworker.iss'
$SignerProject = Join-Path $InstallerDir 'tools\UpdateSigner\UpdateSigner.csproj'
$BuildDir = Join-Path $InstallerDir 'build'
$ReleaseDir = Join-Path $InstallerDir 'release'
$Sparkle = 'http://www.andymatuschak.org/xml-namespaces/sparkle'
$Platform = if ($Arch -eq 'arm64') { 'ARM64' } else { 'x64' }
$OsAttribute = "windows-$Arch"
$MinimumWindows = '10.0.17763'

function Write-Step([string]$Text) { Write-Host ''; Write-Host $Text -ForegroundColor Cyan }
function Write-Note([string]$Text) { Write-Host "   $Text" }
function Write-Warn([string]$Text) { Write-Host "   WARNING: $Text" -ForegroundColor Yellow }
function Stop-Release([string]$Text) { Write-Host ''; Write-Host "ERROR: $Text" -ForegroundColor Red; exit 1 }

function Invoke-Native([string]$Exe, [string[]]$Arguments, [string]$Description) {
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { Stop-Release "$Description failed (exit code $LASTEXITCODE)." }
}

# ---------------------------------------------------------------------------------------------------------------
# Inputs
# ---------------------------------------------------------------------------------------------------------------

if (-not $WebPublicDir) { $WebPublicDir = Join-Path $AppDir '..\..\Humanlike-next\public\coworker' }
if (-not $BaseUrl) { $BaseUrl = 'https://humanlike.co/coworker/downloads' }
$BaseUrl = $BaseUrl.TrimEnd('/')

# The Mac script insists on release notes before it does any work; so does this one.
$notesHtml = $null
if ($ReleaseNotesHtml) { $notesHtml = $ReleaseNotesHtml }
elseif ($ReleaseNotesFile -and (Test-Path -LiteralPath $ReleaseNotesFile)) { $notesHtml = [System.IO.File]::ReadAllText((Resolve-Path -LiteralPath $ReleaseNotesFile), [System.Text.Encoding]::UTF8) }
if (-not $notesHtml -or -not $notesHtml.Trim()) {
    Stop-Release 'Provide release notes via RELEASE_NOTES_HTML or RELEASE_NOTES_FILE (-ReleaseNotesHtml / -ReleaseNotesFile) before packaging a release.'
}

if (-not (Test-Path -LiteralPath $WebPublicDir)) { Stop-Release "Web public folder not found: $WebPublicDir`n   Set WEB_PUBLIC_DIR (or -WebPublicDir) if your web app lives somewhere else." }
$WebPublicDir = (Resolve-Path -LiteralPath $WebPublicDir).Path
$AppcastPath = Join-Path $WebPublicDir 'appcast-windows.xml'
$DownloadsDir = Join-Path $WebPublicDir 'downloads'
if (-not (Test-Path -LiteralPath $AppcastPath)) { Stop-Release "Expected the Windows appcast at $AppcastPath (Humanlike-next/public/coworker/appcast-windows.xml)." }

# ---------------------------------------------------------------------------------------------------------------
# Version (single source: Coworker.csproj)
# ---------------------------------------------------------------------------------------------------------------

Write-Step 'Reading version from Coworker.csproj'
$json = & dotnet msbuild $Csproj -nologo "-p:Platform=$Platform" -getProperty:AppVersion -getProperty:AppBuild -getProperty:UpdatePublicKey
if ($LASTEXITCODE -ne 0) { Stop-Release 'dotnet msbuild could not read Coworker.csproj (is the .NET 9 SDK installed?).' }
$props = ($json | Out-String | ConvertFrom-Json).Properties
$Version = $props.AppVersion
$Build = $props.AppBuild
$EmbeddedKey = if ($props.PSObject.Properties['UpdatePublicKey']) { [string]$props.UpdatePublicKey } else { '' }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { Stop-Release "AppVersion '$Version' in Coworker.csproj must be three numeric parts, like 1.2.0." }
if ($Build -notmatch '^\d+$') { Stop-Release "AppBuild '$Build' in Coworker.csproj must be an integer." }
Write-Note "Version $Version, build $Build ($Arch)"

# Every release needs a higher build number than anything already in the feed, or running apps will not see it.
[xml]$feed = [System.IO.File]::ReadAllText($AppcastPath, [System.Text.Encoding]::UTF8)
$published = @()
foreach ($enclosure in $feed.SelectNodes('//enclosure')) {
    $existing = $enclosure.GetAttribute('version', $Sparkle)
    if ($existing -match '^\d+$') { $published += [int]$existing }
}
if ($published.Count -gt 0) {
    $highest = ($published | Measure-Object -Maximum).Maximum
    if ([int]$Build -le $highest) {
        Stop-Release "Build $Build is not newer than build $highest already in appcast-windows.xml. Raise AppBuild in Coworker.csproj."
    }
}

# ---------------------------------------------------------------------------------------------------------------
# Update signing key (Ed25519)
# ---------------------------------------------------------------------------------------------------------------

Write-Step 'Preparing the Ed25519 update signer'
Invoke-Native 'dotnet' @('build', $SignerProject, '-c', 'Release', '-nologo', '-v', 'q') 'Building UpdateSigner'
$SignerDll = Join-Path $InstallerDir 'tools\UpdateSigner\bin\Release\net9.0\UpdateSigner.dll'
$signingPublicKey = (& dotnet $SignerDll public-key 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0) {
    Stop-Release "No Ed25519 update signing key: $signingPublicKey`n   Generate one outside the repo: dotnet `"$SignerDll`" keygen`n   then set UpdatePublicKey in Coworker.csproj to the printed public key."
}
if (-not $EmbeddedKey) {
    $problem = 'Coworker.csproj has no UpdatePublicKey: apps built now cannot verify updates, so updating would be switched off.'
    if ($Unsigned) { Write-Warn $problem } else { Stop-Release "$problem`n   Set <UpdatePublicKey>$signingPublicKey</UpdatePublicKey>." }
}
elseif ($EmbeddedKey.Trim() -ne $signingPublicKey) {
    $problem = 'The public key in Coworker.csproj does not belong to the signing key: installed apps would reject this update.'
    if ($Unsigned) { Write-Warn $problem } else { Stop-Release $problem }
}
else {
    Write-Note 'Signing key matches the public key built into the app.'
}

# ---------------------------------------------------------------------------------------------------------------
# Authenticode
# ---------------------------------------------------------------------------------------------------------------

function Find-SignTool {
    if ($env:SIGNTOOL_PATH -and (Test-Path -LiteralPath $env:SIGNTOOL_PATH)) { return $env:SIGNTOOL_PATH }
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $kits) {
        $found = Get-ChildItem -Path $kits -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } | Sort-Object FullName -Descending | Select-Object -First 1
        if ($found) { return $found.FullName }
    }
    $onPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    return $null
}

function Invoke-CodeSign([string[]]$Files) {
    if ($Unsigned) {
        Write-Warn "-Unsigned: not Authenticode-signing $($Files.Count) file(s). Do not publish this build."
        return
    }

    foreach ($file in $Files) {
        if ($env:COWORKER_SIGN_COMMAND) {
            $command = $env:COWORKER_SIGN_COMMAND.Replace('{file}', "`"$file`"")
            & cmd.exe /c $command
            if ($LASTEXITCODE -ne 0) { Stop-Release "COWORKER_SIGN_COMMAND failed for $file." }
            continue
        }

        $signtool = Find-SignTool
        if (-not $signtool) { Stop-Release 'signtool.exe not found (install the Windows SDK, or set SIGNTOOL_PATH).' }
        $timestamp = $env:COWORKER_SIGN_TIMESTAMP_URL
        $arguments = @('sign', '/fd', 'sha256', '/td', 'sha256')

        if ($env:COWORKER_SIGN_TS_DLIB -and $env:COWORKER_SIGN_TS_METADATA) {
            if (-not $timestamp) { $timestamp = 'http://timestamp.acs.microsoft.com' }
            $arguments += @('/tr', $timestamp, '/dlib', $env:COWORKER_SIGN_TS_DLIB, '/dmdf', $env:COWORKER_SIGN_TS_METADATA)
        }
        elseif ($env:COWORKER_SIGN_CERT_SHA1) {
            if (-not $timestamp) { $timestamp = 'http://timestamp.digicert.com' }
            $arguments += @('/tr', $timestamp, '/sha1', $env:COWORKER_SIGN_CERT_SHA1)
        }
        elseif ($env:COWORKER_SIGN_PFX) {
            if (-not $timestamp) { $timestamp = 'http://timestamp.digicert.com' }
            $arguments += @('/tr', $timestamp, '/f', $env:COWORKER_SIGN_PFX)
            if ($env:COWORKER_SIGN_PFX_PASSWORD) { $arguments += @('/p', $env:COWORKER_SIGN_PFX_PASSWORD) }
        }
        else {
            Stop-Release "No Authenticode signing identity configured. Set COWORKER_SIGN_COMMAND, COWORKER_SIGN_TS_DLIB + COWORKER_SIGN_TS_METADATA, COWORKER_SIGN_CERT_SHA1 or COWORKER_SIGN_PFX (see installer\README.md), or pass -Unsigned for a local test build."
        }

        Invoke-Native $signtool ($arguments + @($file)) "Signing $(Split-Path -Leaf $file)"
        Invoke-Native $signtool @('verify', '/pa', $file) "Verifying the signature of $(Split-Path -Leaf $file)"
    }
}

# ---------------------------------------------------------------------------------------------------------------
# Publish + compile
# ---------------------------------------------------------------------------------------------------------------

New-Item -ItemType Directory -Force -Path $ReleaseDir | Out-Null
$InstallerName = "Coworker-$Version-win-$Arch.exe"

if ($InstallerPath) {
    if (-not (Test-Path -LiteralPath $InstallerPath)) { Stop-Release "Installer not found: $InstallerPath" }
    Write-Step "Using the ready-made installer $InstallerPath (no publish, no ISCC)"
    $InstallerName = Split-Path -Leaf $InstallerPath
    $FinalInstaller = Join-Path $ReleaseDir $InstallerName
    if ((Resolve-Path -LiteralPath $InstallerPath).Path -ne $FinalInstaller) { Copy-Item -LiteralPath $InstallerPath -Destination $FinalInstaller -Force }
}
else {
    $PublishDir = Join-Path $BuildDir "publish-$Arch"
    Write-Step "Publishing $Configuration $Arch (self-contained)"
    if (Test-Path -LiteralPath $PublishDir) { Remove-Item -LiteralPath $PublishDir -Recurse -Force }
    Invoke-Native 'dotnet' @('publish', $Csproj, '-c', $Configuration, "-p:Platform=$Platform", "-p:PublishDir=$PublishDir\", '-nologo', '-v', 'minimal') 'dotnet publish'

    foreach ($required in 'Coworker.exe', 'Coworker.dll', 'Assets\coworker.ico', 'Assets\coworker-icon.png') {
        if (-not (Test-Path -LiteralPath (Join-Path $PublishDir $required))) { Stop-Release "Publish output is missing $required." }
    }
    $fileVersion = (Get-Item (Join-Path $PublishDir 'Coworker.exe')).VersionInfo.FileVersion
    if ($fileVersion -ne "$Version.$Build") { Stop-Release "Coworker.exe reports file version $fileVersion, expected $Version.$Build." }
    Write-Note "Published to $PublishDir (Coworker.exe $fileVersion)"

    Write-Step 'Signing the application binaries'
    Invoke-CodeSign @((Join-Path $PublishDir 'Coworker.exe'), (Join-Path $PublishDir 'Coworker.dll'))

    Write-Step 'Compiling the installer with Inno Setup'
    $iscc = $env:ISCC_PATH
    if (-not $iscc -or -not (Test-Path -LiteralPath $iscc)) {
        $candidates = @(
            (Get-Command iscc.exe -ErrorAction SilentlyContinue | ForEach-Object { $_.Source }),
            (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
        ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }
        $iscc = $candidates | Select-Object -First 1
    }
    if (-not $iscc) {
        Stop-Release "Inno Setup (ISCC.exe) not found. Install Inno Setup 6.3+ (winget install JRSoftware.InnoSetup) or set ISCC_PATH."
    }
    $FinalInstaller = Join-Path $ReleaseDir $InstallerName
    if (Test-Path -LiteralPath $FinalInstaller) { Remove-Item -LiteralPath $FinalInstaller -Force }
    Invoke-Native $iscc @('/Q', "/DAppVersion=$Version", "/DAppBuild=$Build", "/DArch=$Arch", "/DSourceDir=$PublishDir", "/DOutputDir=$ReleaseDir", $IssFile) 'ISCC'
    if (-not (Test-Path -LiteralPath $FinalInstaller)) { Stop-Release "ISCC finished but $FinalInstaller was not created." }
}

Write-Step 'Signing the installer'
Invoke-CodeSign @($FinalInstaller)

# ---------------------------------------------------------------------------------------------------------------
# Update signature + appcast item
# ---------------------------------------------------------------------------------------------------------------

Write-Step 'Signing the installer for the auto-updater (Ed25519)'
$signOutput = (& dotnet $SignerDll sign $FinalInstaller 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { Stop-Release "Could not sign the installer: $signOutput" }
if ($signOutput -notmatch 'sparkle:edSignature="([^"]+)"') { Stop-Release "Could not read the signature from the signer output: $signOutput" }
$Signature = $Matches[1]
$Length = (Get-Item -LiteralPath $FinalInstaller).Length
& dotnet $SignerDll verify $FinalInstaller --signature $Signature --public-key $signingPublicKey | Out-Null
if ($LASTEXITCODE -ne 0) { Stop-Release 'The Ed25519 signature does not verify against the public key.' }
$Sha256 = (Get-FileHash -LiteralPath $FinalInstaller -Algorithm SHA256).Hash.ToLowerInvariant()
$SizeMb = [math]::Round($Length / 1MB, 1)
Write-Note "Signature verified. $SizeMb MB ($Length bytes), sha256 $Sha256"
[System.IO.File]::WriteAllText("$FinalInstaller.sha256", "$Sha256 *$InstallerName`n", (New-Object System.Text.UTF8Encoding($false)))

$PubDate = [DateTime]::UtcNow.ToString('ddd, dd MMM yyyy HH:mm:ss +0000', [System.Globalization.CultureInfo]::InvariantCulture)
$InstallerUrl = "$BaseUrl/$InstallerName"
$notesXml = $notesHtml.Trim().Replace(']]>', ']]]]><![CDATA[>')
$criticalLine = if ($Critical) { "`n  <sparkle:criticalUpdate />" } else { '' }
$item = @"
<item>
  <title>Coworker $Version</title>
  <description><![CDATA[
$notesXml
  ]]></description>
  <pubDate>$PubDate</pubDate>$criticalLine
  <enclosure
    url="$InstallerUrl"
    sparkle:os="$OsAttribute"
    sparkle:version="$Build"
    sparkle:shortVersionString="$Version"
    sparkle:edSignature="$Signature"
    length="$Length"
    type="application/octet-stream" />
  <sparkle:minimumSystemVersion>$MinimumWindows</sparkle:minimumSystemVersion>
</item>
"@

# The item must be well-formed inside a feed before anyone pastes it.
try { [xml]("<rss xmlns:sparkle=`"$Sparkle`"><channel>$item</channel></rss>") | Out-Null }
catch { Stop-Release "The generated appcast item is not valid XML (check the release notes markup): $($_.Exception.Message)" }

$ItemPath = Join-Path $ReleaseDir 'appcast-item-windows.xml'
[System.IO.File]::WriteAllText($ItemPath, $item + "`n", (New-Object System.Text.UTF8Encoding($false)))

New-Item -ItemType Directory -Force -Path $DownloadsDir | Out-Null
$PublishedInstaller = Join-Path $DownloadsDir $InstallerName
Copy-Item -LiteralPath $FinalInstaller -Destination $PublishedInstaller -Force
Copy-Item -LiteralPath "$FinalInstaller.sha256" -Destination "$PublishedInstaller.sha256" -Force   # read by scripts/update-coworker-downloads.py

if ($UpdateAppcast) {
    Write-Step 'Adding the item to appcast-windows.xml'
    $text = [System.IO.File]::ReadAllText($AppcastPath, [System.Text.Encoding]::UTF8)
    $newline = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $indented = ($item -split "`r?`n" | ForEach-Object { if ($_.Length) { "    $_" } else { $_ } }) -join $newline
    $insertAt = $text.IndexOf('<item>')
    if ($insertAt -ge 0) {
        $lineStart = $text.LastIndexOf("`n", $insertAt) + 1
        $text = $text.Insert($lineStart, $indented + $newline + $newline)
    }
    else {
        $closing = $text.IndexOf('</channel>')
        if ($closing -lt 0) { Stop-Release 'appcast-windows.xml has no </channel>.' }
        $lineStart = $text.LastIndexOf("`n", $closing) + 1
        $text = $text.Insert($lineStart, $newline + $indented + $newline)
    }
    try { [xml]$text | Out-Null } catch { Stop-Release "appcast-windows.xml would not be valid XML after the insert: $($_.Exception.Message)" }
    [System.IO.File]::WriteAllText($AppcastPath, $text, (New-Object System.Text.UTF8Encoding($false)))
    Write-Note "Updated $AppcastPath"

    $webRoot = (Resolve-Path (Join-Path $WebPublicDir '..\..') -ErrorAction SilentlyContinue)
    $downloadsScript = if ($webRoot) { Join-Path $webRoot.Path 'scripts\update-coworker-downloads.py' } else { $null }
    $downloadsRefreshed = $false
    $python = Get-Command python -ErrorAction SilentlyContinue
    if (-not $python) { $python = Get-Command python3 -ErrorAction SilentlyContinue }
    if ($downloadsScript -and (Test-Path -LiteralPath $downloadsScript) -and $python) {
        & $python.Source $downloadsScript --public-dir $WebPublicDir
        if ($LASTEXITCODE -eq 0) { $downloadsRefreshed = $true }
        else { Write-Warn 'update-coworker-downloads.py failed; run it by hand before deploying.' }
    }
    else {
        Write-Warn 'Run "python scripts/update-coworker-downloads.py" in the web app (Humanlike-next) to refresh public/coworker/latest.json, the /coworker/download page data.'
    }
}

# ---------------------------------------------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------------------------------------------

Write-Host ''
Write-Host 'Done. The installer is ready to publish.' -ForegroundColor Green
Write-Note "Installer : $FinalInstaller"
Write-Note "Published : $PublishedInstaller"
Write-Note "Appcast   : $ItemPath"
Write-Note "Enclosure : url=$InstallerUrl"
Write-Note "            sparkle:version=$Build sparkle:shortVersionString=$Version sparkle:os=$OsAttribute"
Write-Note "            sparkle:edSignature=$Signature length=$Length"
if ($Critical) { Write-Note '            marked critical: running apps will show "Update Required"' }
if ($Unsigned) { Write-Warn 'Built with -Unsigned: for local tests only.' }
if ($Length -gt 90MB -and $BaseUrl -like 'https://humanlike.co/*') {
    Write-Warn "The installer is $SizeMb MB. Check your host's per-file limit for the website deployment; if it is too big, upload it to R2/GitHub Releases and re-run with -BaseUrl. Never commit it to git."
}

Write-Host ''
Write-Host 'To publish this update:' -ForegroundColor Cyan
$step = 1
Write-Note "$step. Confirm $PublishedInstaller is deployed with the web app (or uploaded to $BaseUrl)"; $step++
if ($UpdateAppcast) {
    Write-Note "$step. appcast-windows.xml was updated: review and commit it"; $step++
    if ($downloadsRefreshed) { Write-Note "$step. public/coworker/latest.json was refreshed: commit it with the appcast"; $step++ }
    else { Write-Note "$step. python scripts/update-coworker-downloads.py   (in Humanlike-next; refreshes /coworker/download), then commit latest.json"; $step++ }
}
else {
    Write-Note "$step. Paste $ItemPath at the top of $AppcastPath (or re-run with -UpdateAppcast)"; $step++
    Write-Note "$step. python scripts/update-coworker-downloads.py   (in Humanlike-next; refreshes /coworker/download), then commit latest.json"; $step++
}
Write-Note "$step. Deploy the website"; $step++
Write-Note "$step. Install the previous build, wait for (or trigger) 'Check for Updates...', and confirm it upgrades to $Version"
