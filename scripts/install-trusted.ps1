<#
.SYNOPSIS
  Installs the plugin into a folder Civil 3D trusts and loads it from acaddoc.lsp, so Civil 3D never asks
  about the unsigned DLL.

.DESCRIPTION
  The ApplicationPlugins bundle (install-bundle.ps1) lives under %APPDATA%, which is not a trusted location:
  Civil 3D shows "Security - Unsigned Executable File" for Civil3DMcpPlugin.dll at every start. This script
  instead:
    1. copies the build output to <Destination>\R<series>\ (R25.1 = Civil 3D 2026, R26.0 = 2027), a folder
       that must be under TRUSTEDPATHS (Jorge's machine trusts C:\CAD-Tools\...);
    2. writes a marked block into acaddoc.lsp in the Civil 3D roaming support folder (first on the support
       path and trusted by default), which NETLOADs that DLL once per session, from S::STARTUP;
    3. removes the ApplicationPlugins bundle: it would keep the security prompt at every start, and once
       loaded its build would run instead of the trusted copy (the block NETLOADs only when the plugin's
       commands do not exist yet).

  acaddoc.lsp runs when a drawing opens: on the Start tab, with no drawing, the plugin is not loaded yet.
  core\tools\Start-Civil3D.ps1 (CAD-Tools) opens a drawing in that case.

  Verified on Civil 3D 2026 (2026-10-11): NETLOAD from C:\CAD-Tools showed no prompt. Things that did
  not work: a junction from the bundle folder (Civil 3D checks the path it sees), an absolute ModuleName
  in PackageContents.xml (ignored), a registry demand-load entry and the registry Startup Suite (neither
  was run).

.PARAMETER SourceDir
  Build output folder, named by target framework (net8.0-windows = 2026, net10.0-windows = 2027).
  With -Uninstall, selects only that release. Omit it to uninstall every installed release.

.PARAMETER Destination
  Trusted root for the plugin. Default C:\CAD-Tools\plugins\Civil3DMcp.

.PARAMETER KeepBundle
  Leave the ApplicationPlugins bundle installed (only for testing). Civil 3D then still asks about the
  bundle's DLL at every start; answered Load Once, the bundle's build runs and the acaddoc.lsp block skips
  the trusted copy.

.PARAMETER Uninstall
  Without SourceDir, remove every R* folder under Destination that contains Civil3DMcpPlugin.dll and
  the marked block in every C3D <year> roaming support folder. Missing installations are allowed.
  With SourceDir, remove only that release's block and plugin folder. Reinstall the bundle with
  install-bundle.ps1 if wanted. When acaddoc.lsp keeps other code, it hides again the acaddoc.lsp further
  down the support path that the block loaded; the uninstall warns about that.
#>
param(
  [string] $SourceDir,
  [string] $Destination = 'C:\CAD-Tools\plugins\Civil3DMcp',
  [switch] $KeepBundle,
  [switch] $Uninstall
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$beginMarker = ';; >>> Civil3D-mcp plugin (written by Civil3D-mcp scripts\install-trusted.ps1; remove with -Uninstall)'
$endMarker = ';; <<< Civil3D-mcp plugin'

# Choose all-release uninstall before applying the default build framework.
$uninstallAll = $Uninstall -and -not $SourceDir
if (-not $uninstallAll) {
  if (-not $SourceDir) {
    $SourceDir = Join-Path $repoRoot "Civil3D-MCP-Plugin\bin\Release\net10.0-windows"
  }
  $tfm = Split-Path -Leaf $SourceDir
  $series, $year = switch ($tfm) {
    "net8.0-windows"  { "R25.1", 2026 }
    "net10.0-windows" { "R26.0", 2027 }
    default { throw "Cannot infer the Civil 3D release from '$tfm'. Pass a build output folder named by target framework (net8.0-windows or net10.0-windows)." }
  }
  $target = Join-Path $Destination $series
}

function Get-LockedFile([string] $dir) {
  if (-not (Test-Path -LiteralPath $dir)) { return $null }
  foreach ($f in Get-ChildItem -LiteralPath $dir -Recurse -File -Force -Filter *.dll) {
    $fs = $null
    try { $fs = [System.IO.File]::Open($f.FullName, 'Open', 'ReadWrite', 'None') }
    catch [System.IO.IOException] { return $f.FullName }
    catch [System.UnauthorizedAccessException] { return $f.FullName }
    finally { if ($fs) { $fs.Dispose() } }
  }
  return $null
}

# Reads a text file in the encoding it was written in (BOM, else strict UTF-8, else the ANSI code page),
# so rewriting an existing acaddoc.lsp never mangles its accented characters.
function Read-TextFile([string] $file) {
  $bytes = [IO.File]::ReadAllBytes($file)
  if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
    return @{ Text = [Text.Encoding]::UTF8.GetString($bytes, 3, $bytes.Length - 3); Encoding = New-Object Text.UTF8Encoding($true) }
  }
  if ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) {
    return @{ Text = [Text.Encoding]::Unicode.GetString($bytes, 2, $bytes.Length - 2); Encoding = [Text.Encoding]::Unicode }
  }
  try {
    return @{ Text = (New-Object Text.UTF8Encoding($false, $true)).GetString($bytes); Encoding = New-Object Text.UTF8Encoding($false) }
  } catch {
    # Encoding.Default is the ANSI code page only in Windows PowerShell; in PowerShell 7 it is UTF-8, and
    # the rewrite would turn every accented character into U+FFFD.
    $ansi = if ($PSVersionTable.PSEdition -eq 'Core') {
      [Text.Encoding]::GetEncoding([int](Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Nls\CodePage').ACP)
    } else { [Text.Encoding]::Default }
    return @{ Text = $ansi.GetString($bytes); Encoding = $ansi }
  }
}

function Get-ProfileSupportFolders {
  $releaseKey = "HKCU:\Software\Autodesk\AutoCAD\$series"
  $product = (Get-ItemProperty -LiteralPath $releaseKey -ErrorAction SilentlyContinue).CurVer
  if (-not $product) { return $null }
  $profileName = (Get-ItemProperty -LiteralPath "$releaseKey\$product\Profiles" -ErrorAction SilentlyContinue).'(default)'
  if (-not $profileName) { return $null }
  $supportPath = (Get-ItemProperty -LiteralPath "$releaseKey\$product\Profiles\$profileName\General" -ErrorAction SilentlyContinue).ACAD
  foreach ($folder in "$supportPath" -split ';') {
    $folder = [Environment]::ExpandEnvironmentVariables($folder.Trim())
    if ($folder) {
      $canonical = Get-CanonicalFolderPath $folder
      if ($canonical) { $canonical }
    }
  }
}

function Get-CanonicalFolderPath([string] $folder) {
  # Malformed profile entries cannot name a support folder.
  try { $full = [IO.Path]::GetFullPath($folder) }
  catch { return $null }
  $root = [IO.Path]::GetPathRoot($full)
  if ($full.Length -gt $root.Length) { return $full.TrimEnd('\', '/') }
  return $full
}

# Only a file after ours is hidden by it. Marked files can also hold live user code.
function Find-OtherAcaddoc([string] $exclude) {
  $exclude = Get-CanonicalFolderPath $exclude
  $foundExclude = $false
  foreach ($folder in Get-ProfileSupportFolders) {
    if ($folder -ieq $exclude) { $foundExclude = $true; continue }
    if (-not $foundExclude) { continue }
    $candidate = Join-Path $folder 'acaddoc.lsp'
    if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
  }
  return $null
}

function Set-AcaddocBlock([string] $file, [string] $block) {
  $existing = Test-Path -LiteralPath $file
  $read = if ($existing) { Read-TextFile $file } else { @{ Text = ''; Encoding = New-Object Text.UTF8Encoding($false) } }
  $text = $read.Text
  $pattern = '(?s)\r?\n?' + [regex]::Escape($beginMarker) + '.*?' + [regex]::Escape($endMarker) + '\r?\n?'
  $ours = [regex]::IsMatch($text, $pattern)
  # Nothing of ours to remove: leave the file exactly as it is (no rewrite, re-encoding or backup).
  if (-not $block -and -not $ours) { return }
  # The hidden acaddoc.lsp files our old block loaded. Our file still hides them after the user adds code
  # to it, and while one is out of reach (a share off VPN), so their load lines stay until -Uninstall.
  $loadPattern = '\(vl-catch-all-apply ''load \(list "([^"]*)"\)\)'
  $loads = @([regex]::Matches($text, $pattern) | ForEach-Object { [regex]::Matches($_.Value, $loadPattern) } | ForEach-Object { $_.Groups[1].Value })
  $text = [regex]::Replace($text, $pattern, "`r`n").Trim()
  if ($block -and (-not $existing -or ($ours -and -not $text))) {
    # A file that is only ours (new, or holding nothing but our block) hides an acaddoc.lsp further down
    # the support path. Load that one first, so the block wraps or appends to its S::STARTUP.
    $other = Find-OtherAcaddoc (Split-Path -Parent $file)
    if ($other) { $loads += $other -replace '\\', '/' }
  }
  $earlierFolders = @()
  if ($block) {
    # Uninstall has no need to read a profile; all-release uninstall has no single $series.
    $exclude = Get-CanonicalFolderPath (Split-Path -Parent $file)
    $foundExclude = $false
    foreach ($folder in Get-ProfileSupportFolders) {
      if ($folder -ieq $exclude) { $foundExclude = $true; break }
      $earlierFolders += $folder
    }
    if (-not $foundExclude) { $earlierFolders = @() }
  }
  $unique = @()
  foreach ($load in $loads) {
    # Drop old upward acaddoc links even when the target is currently unavailable.
    $loadPath = Get-CanonicalFolderPath $load
    if ($block -and $loadPath -and [IO.Path]::GetFileName($loadPath) -ieq 'acaddoc.lsp' -and
        $earlierFolders -icontains (Get-CanonicalFolderPath (Split-Path -Parent $loadPath))) {
      continue
    }
    if ($unique -notcontains $load) { $unique += $load }
  }
  if ($block -and $unique) {
    # Source visits stop cycles; loaded targets prevent repeated user initialization in one document.
    $chainKey = [IO.Path]::GetFullPath($file).Replace('\', '/').ToUpperInvariant()
    $lines = @("(if (not (member `"$chainKey`" c3dmcp:chain-visited))", '  (progn',
      "    (setq c3dmcp:chain-visited (cons `"$chainKey`" c3dmcp:chain-visited))",
      "    (if (not (member `"$chainKey`" c3dmcp:loaded-targets))",
      "      (setq c3dmcp:loaded-targets (cons `"$chainKey`" c3dmcp:loaded-targets))")
    $lines += '    )'
    foreach ($load in $unique) {
      $targetKey = Get-CanonicalFolderPath $load
      # Keep malformed saved load lines, using their raw path when normalization fails.
      if (-not $targetKey) { $targetKey = $load }
      $targetKey = $targetKey.Replace('\', '/').ToUpperInvariant()
      $lines += @("    (if (not (member `"$targetKey`" c3dmcp:loaded-targets))", '      (progn',
        "        (setq c3dmcp:loaded-targets (cons `"$targetKey`" c3dmcp:loaded-targets))",
        "        (vl-catch-all-apply 'load (list `"$load`"))", '      )', '    )')
    }
    $lines += @('  )', ')')
    $block = $block.Replace($beginMarker, (@($beginMarker) + $lines) -join "`r`n")
    $unique | ForEach-Object { "  (also loads the acaddoc.lsp further down the support path: $_)" }
  } elseif ($unique -and $text) {
    # -Uninstall keeps a file that holds other code, and that file hides them again.
    Write-Warning "$file keeps other code, so it hides the acaddoc.lsp further down the support path again: $($unique -join ', '). Load that file from it if it should still run."
  }
  if ($existing -and -not $ours -and -not (Test-Path -LiteralPath "$file.c3dmcp-backup")) {
    # Someone else's file: keep a copy of it as it was before the first change.
    Copy-Item -LiteralPath $file -Destination "$file.c3dmcp-backup"
  }
  if ($block) { $text = (($text, $block) | Where-Object { $_ }) -join "`r`n`r`n" }
  if ($text) {
    [IO.File]::WriteAllText($file, $text + "`r`n", $read.Encoding)
  } elseif ($existing) {
    # The file held only our block.
    Remove-Item -LiteralPath $file
  }
}

if ($uninstallAll) {
  # Discover only direct release folders that hold the plugin, and validate every deletion path.
  $targets = @(if (Test-Path -LiteralPath $Destination -PathType Container) {
    $destinationRoot = (Get-Item -LiteralPath $Destination).FullName.TrimEnd('\', '/')
    foreach ($release in Get-ChildItem -LiteralPath $Destination -Directory -Force -Filter 'R*') {
      if ($release.Name -notlike 'R*') { continue }
      if (-not (Test-Path -LiteralPath (Join-Path $release.FullName 'Civil3DMcpPlugin.dll') -PathType Leaf)) { continue }
      $releasePath = [IO.Path]::GetFullPath($release.FullName)
      if ((Split-Path -Parent $releasePath).TrimEnd('\', '/') -ine $destinationRoot -or
          ($release.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to remove release folder '$releasePath': it must be a direct folder under '$destinationRoot', not a link."
      }
      $releasePath
    }
  })
  # Orphan blocks and plugin-only installations are independent; neither requires the other to exist.
  $autodeskRoot = Join-Path $env:APPDATA 'Autodesk'
  $supportDirs = @(if (Test-Path -LiteralPath $autodeskRoot -PathType Container) {
    Get-ChildItem -LiteralPath $autodeskRoot -Directory -Force | Where-Object { $_.Name -match '^C3D \d{4}$' } |
      ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Directory -Force } |
      ForEach-Object { Join-Path $_.FullName 'Support' } |
      Where-Object {
        $acaddoc = Join-Path $_ 'acaddoc.lsp'
        (Test-Path -LiteralPath $acaddoc -PathType Leaf) -and (Read-TextFile $acaddoc).Text.Contains($beginMarker)
      }
  })
} else {
  $targets = @($target)
  # The roaming support folders of the selected Civil 3D release (one per installed language).
  $supportDirs = @(Get-ChildItem (Join-Path $env:APPDATA "Autodesk\C3D $year") -Directory -ErrorAction SilentlyContinue |
    ForEach-Object { Join-Path $_.FullName 'Support' } | Where-Object { Test-Path $_ })
  if ($supportDirs.Count -eq 0) {
    throw "No Civil 3D $year roaming support folder under $env:APPDATA\Autodesk\C3D $year. Start Civil 3D $year once, then re-run."
  }
}

if ($Uninstall) {
  # Check every selected release before changing any plugin folder or startup file.
  foreach ($releaseTarget in $targets) {
    $locked = Get-LockedFile $releaseTarget
    if ($locked) { throw "'$locked' is loaded by a running Civil 3D. Close it completely, then re-run." }
  }
  foreach ($dir in $supportDirs) { Set-AcaddocBlock (Join-Path $dir 'acaddoc.lsp') ''; "Removed the Civil3D-mcp block from $dir\acaddoc.lsp" }
  foreach ($releaseTarget in $targets) {
    if (Test-Path -LiteralPath $releaseTarget) { Remove-Item -LiteralPath $releaseTarget -Recurse -Force; "Removed $releaseTarget" }
  }
  'Restart Civil 3D for the change to take effect.'
  exit 0
}

$dll = Join-Path $SourceDir 'Civil3DMcpPlugin.dll'
if (-not (Test-Path $dll)) { throw "Plugin not built at '$dll'. Build it first (scripts\build-2027.ps1 for 2027)." }

# The six Autodesk references are Private=false and never land in bin\; skip them anyway.
$autodeskRefs = @('accoremgd.dll', 'AcDbMgd.dll', 'acmgd.dll', 'AecBaseMgd.dll', 'AeccDbMgd.dll', 'AeccPressurePipesMgd.dll')
$files = @(Get-ChildItem $SourceDir -File | Where-Object { $autodeskRefs -notcontains $_.Name } | ForEach-Object { @{ From = $_.FullName; To = Join-Path $target $_.Name } })
$roslyn = Join-Path $SourceDir 'roslyn'
if (Test-Path $roslyn) {
  $files += @(Get-ChildItem $roslyn -File -Filter *.dll | ForEach-Object { @{ From = $_.FullName; To = Join-Path $target "roslyn\$($_.Name)" } })
}
# Unchanged files are skipped, so re-running while Civil 3D has the same build loaded only refreshes
# acaddoc.lsp; a changed DLL cannot be replaced while it is loaded.
$changed = @($files | Where-Object { -not (Test-Path $_.To) -or (Get-FileHash $_.From).Hash -ne (Get-FileHash $_.To).Hash })
if ($changed.Count -gt 0) {
  $locked = Get-LockedFile $target
  if ($locked) { throw "'$locked' is loaded by a running Civil 3D and the build changed. Close Civil 3D completely, then re-run." }
}
# The bundles to remove are checked before anything changes too: stopping later would leave the new copy
# and the acaddoc.lsp block in place with the old bundle still prompting and loading at each start.
$bundles = @()
if (-not $KeepBundle) {
  $plugins = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins'
  $bundles = @('Civil3DMcp.bundle', 'Civil3DMcp.bundle.parked' | ForEach-Object { Join-Path $plugins $_ } | Where-Object { Test-Path $_ })
  foreach ($bundle in $bundles) {
    $bundleLocked = Get-LockedFile $bundle
    if ($bundleLocked) { throw "'$bundleLocked' is loaded by a running Civil 3D. Close it completely, then re-run." }
  }
}
New-Item -ItemType Directory -Force (Join-Path $target 'roslyn') | Out-Null
foreach ($f in $changed) { Copy-Item $f.From $f.To -Force }
$copied = $changed.Count

# NETLOAD is a command, so it runs from S::STARTUP (after the drawing is initialized), once per session
# (the blackboard is shared by all drawings) and not when the plugin's commands already exist (loaded by
# hand or by a bundle). The DLL folder is chosen by the running release (ACADVER "25.1s ..." -> R25.1),
# so one block serves 2026 and 2027 side by side. Only an argument-less defun-q is spliced into
# S::STARTUP: one defined by others with defun-q is appended to, one defined with defun (a compiled
# function, not a list) is wrapped. An acaddoc.lsp that our file hides is loaded first (Set-AcaddocBlock
# adds that line), so its S::STARTUP is wrapped or appended to as well.
# Keep failed attempts guarded to avoid repeating security prompts in every drawing. C3DMCPLOAD
# explicitly clears the flag and retries after the user fixes the cause.
$root = ($Destination -replace '\\', '/').TrimEnd('/')
$block = @"
$beginMarker
(vl-load-com)
(defun c3dmcp:load ( / dll)
  (setq dll (strcat "$root/R" (substr (getvar "ACADVER") 1 4) "/Civil3DMcpPlugin.dll"))
  (if (and (not (vl-bb-ref 'c3dmcp:loaded)) (not (getcname "C3DMCPSTATUS")))
    (progn
      (vl-bb-set 'c3dmcp:loaded T)
      (if (findfile dll) (command "_.NETLOAD" dll))
      (if (not (getcname "C3DMCPSTATUS"))
        (princ (strcat "\nCivil3D-mcp: the plugin did not load: " dll "; fix the cause (TRUSTEDPATHS, a Do Not Load answer, missing files), then type C3DMCPLOAD."))
      )
    )
  )
  (princ)
)
(defun c:C3DMCPLOAD ()
  (vl-bb-set 'c3dmcp:loaded nil)
  (c3dmcp:load)
  (princ)
)
(defun-q c3dmcp:startup () (c3dmcp:load))
(cond
  ((or (null S::STARTUP) (listp S::STARTUP)) (setq S::STARTUP (append S::STARTUP c3dmcp:startup)))
  (T (setq c3dmcp:prev S::STARTUP) (defun-q S::STARTUP () (c3dmcp:prev) (c3dmcp:load)))
)
$endMarker
"@
foreach ($dir in $supportDirs) { Set-AcaddocBlock (Join-Path $dir 'acaddoc.lsp') $block; "acaddoc.lsp: $dir\acaddoc.lsp" }

foreach ($bundle in $bundles) {
  Remove-Item $bundle -Recurse -Force
  "Removed the ApplicationPlugins bundle $bundle (the plugin now loads from $target)."
}

"Installed $copied file(s) in $target"
"Restart Civil 3D; the plugin loads when a drawing opens (no security prompt when $Destination is trusted)."
