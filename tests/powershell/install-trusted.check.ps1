# Offline checks for scripts\install-trusted.ps1, run by tests\powershell_scripts.test.ts (or by hand:
# powershell -NoProfile -ExecutionPolicy Bypass -File tests\powershell\install-trusted.check.ps1).
# Everything is written under a temp folder with a temp APPDATA. Civil 3D and the real profile are never
# changed; full-script installs use fake registry reads for the support path.
# Prints PASS/FAIL lines and exits with the number of failures.
$ErrorActionPreference = 'Stop'
$installer = (Resolve-Path (Join-Path $PSScriptRoot '..\..\scripts\install-trusted.ps1')).Path
$work = Join-Path ([IO.Path]::GetTempPath()) ('c3dmcp-install-check-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $work | Out-Null
$failures = 0

function Check([string] $name, [bool] $ok, [string] $detail = '') {
  if ($ok) { "PASS $name" } else { "FAIL $name $detail"; $script:failures++ }
}
function Hex([byte[]] $bytes) { ($bytes | ForEach-Object { '{0:X2}' -f $_ }) -join ' ' }
function New-CaseFile([string] $name) {
  $dir = Join-Path $work "unit\$name"
  New-Item -ItemType Directory -Force $dir | Out-Null
  Join-Path $dir 'acaddoc.lsp'
}
function Count([string] $text, [string] $part) { ([regex]::Matches($text, [regex]::Escape($part))).Count }

# Return complete top-level LISP forms; parentheses in strings and comments are not syntax.
function Get-LispForms([string] $text) {
  $depth = 0
  $formStart = 0
  $inString = $false
  $inComment = $false
  $escaped = $false
  for ($i = 0; $i -lt $text.Length; $i++) {
    $ch = $text[$i]
    if ($inComment) {
      if ($ch -eq "`n" -or $ch -eq "`r") { $inComment = $false }
      continue
    }
    if ($inString) {
      if ($escaped) { $escaped = $false }
      elseif ($ch -eq '\') { $escaped = $true }
      elseif ($ch -eq '"') { $inString = $false }
      continue
    }
    switch ($ch) {
      ';' { $inComment = $true }
      '"' { $inString = $true }
      '(' {
        if ($depth -eq 0) { $formStart = $i }
        $depth++
      }
      ')' {
        $depth--
        if ($depth -lt 0) { throw "Unexpected LISP closing parenthesis at $i" }
        if ($depth -eq 0) { $text.Substring($formStart, $i - $formStart + 1) }
      }
    }
  }
  if ($inString -or $depth -ne 0) { throw 'Unterminated LISP string or form' }
}

# Evaluate the chain forms and test user assignments. Plugin definitions and startup registration
# are checked separately above; this small LISP subset never invokes AutoCAD or NETLOAD.
function Read-TestLisp([string[]] $tokens, [ref] $position) {
  $token = $tokens[$position.Value]
  $position.Value++
  if ($token -eq '(') {
    $items = New-Object Collections.ArrayList
    while ($position.Value -lt $tokens.Count -and $tokens[$position.Value] -ne ')') {
      $items.Add((Read-TestLisp $tokens $position)) | Out-Null
    }
    if ($position.Value -ge $tokens.Count) { throw 'Unterminated test LISP list' }
    $position.Value++
    return ,($items.ToArray())
  }
  if ($token -eq "'") { return ,@('quote', (Read-TestLisp $tokens $position)) }
  return $token
}
function Invoke-TestLisp($expression, $document) {
  if ($expression -is [string]) {
    if ($expression.StartsWith('"')) { return $expression.Substring(1, $expression.Length - 2) }
    if ($expression -eq 'T') { return $true }
    if ($expression -eq 'nil') { return $null }
    return $document.Symbols[$expression]
  }
  switch ($expression[0]) {
    'quote' { return $expression[1] }
    'not' { return -not (Invoke-TestLisp $expression[1] $document) }
    'member' {
      $key = Invoke-TestLisp $expression[1] $document
      $values = Invoke-TestLisp $expression[2] $document
      if ($values -ccontains $key) { return $true }
      return $null
    }
    'cons' {
      $head = Invoke-TestLisp $expression[1] $document
      $tail = Invoke-TestLisp $expression[2] $document
      if ($null -eq $tail) { return ,@($head) }
      return ,(@($head) + @($tail))
    }
    'setq' {
      $value = Invoke-TestLisp $expression[2] $document
      $document.Symbols[$expression[1]] = $value
      return $value
    }
    'if' {
      if (Invoke-TestLisp $expression[1] $document) { return Invoke-TestLisp $expression[2] $document }
      if ($expression.Count -gt 3) { return Invoke-TestLisp $expression[3] $document }
      return $null
    }
    'progn' {
      foreach ($child in $expression[1..($expression.Count - 1)]) { Invoke-TestLisp $child $document | Out-Null }
      return $null
    }
    'list' {
      $values = @($expression[1..($expression.Count - 1)] | ForEach-Object { Invoke-TestLisp $_ $document })
      return ,$values
    }
    'vl-catch-all-apply' {
      $functionName = Invoke-TestLisp $expression[1] $document
      if ($functionName -ne 'load') { throw "Unsupported test LISP function: $functionName" }
      $arguments = Invoke-TestLisp $expression[2] $document
      Invoke-TestLispFile $arguments[0] $document
      return $null
    }
    default { throw "Unsupported test LISP form: $($expression[0])" }
  }
}
function Invoke-TestLispFile([string] $file, $document) {
  $fullPath = [IO.Path]::GetFullPath($file)
  if (-not $fullPath.StartsWith($work + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Test LISP load outside fake root: $fullPath"
  }
  $document.Loads.Add($fullPath) | Out-Null
  if ($document.Loads.Count -gt 20) { throw 'Test LISP chain recursed past 20 loads' }
  if (-not (Test-Path -LiteralPath $fullPath)) { return }
  foreach ($form in Get-LispForms (Read-TextFile $fullPath).Text) {
    if ($form -notmatch '^\((if|setq)\s') { continue }
    $tokens = @([regex]::Matches($form, '"(?:\\.|[^"\\])*"|[()'']|[^\s()'']+') | ForEach-Object { $_.Value })
    $position = 0
    $expression = Read-TestLisp $tokens ([ref]$position)
    Invoke-TestLisp $expression $document | Out-Null
  }
}

function New-UninstallCase([string] $name) {
  $caseRoot = Join-Path $work "uninstall\$name"
  $caseAppData = Join-Path $caseRoot 'appdata'
  New-Item -ItemType Directory -Force $caseAppData | Out-Null
  @{ AppData = $caseAppData; Destination = Join-Path $caseRoot 'dest' }
}
function New-InstalledRelease($case, [string] $release) {
  $releaseDir = Join-Path $case.Destination $release
  New-Item -ItemType Directory -Force $releaseDir | Out-Null
  [IO.File]::WriteAllBytes((Join-Path $releaseDir 'Civil3DMcpPlugin.dll'), [byte[]](1, 2, 3))
  $releaseDir
}
function New-MarkedSupport($case, [int] $year, [string] $language = 'enu', [string] $userCode = '') {
  $supportDir = Join-Path $case.AppData "Autodesk\C3D $year\$language\Support"
  New-Item -ItemType Directory -Force $supportDir | Out-Null
  $file = Join-Path $supportDir 'acaddoc.lsp'
  [IO.File]::WriteAllText($file, "$userCode$block`r`n")
  $file
}
function Invoke-UninstallCase($case, [string] $source = '') {
  $env:APPDATA = $case.AppData
  $sourceArgs = @()
  if ($source) { $sourceArgs = @('-SourceDir', $source) }
  # Capture a child failure as a result so later regression cases still run.
  $ErrorActionPreference = 'Continue'
  $output = @(& $exe -NoProfile -ExecutionPolicy Bypass -File $installer -Uninstall -Destination $case.Destination @sourceArgs 2>&1)
  @{ ExitCode = $LASTEXITCODE; Output = ($output -join ' | ') }
}

$acp = [int](Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Nls\CodePage').ACP
$cafe = "; caf$([char]0xE9)`r`n"
$oldTime = [datetime]::new(2020, 1, 2, 3, 4, 5, [DateTimeKind]::Utc)
$savedAppData = $env:APPDATA

try {
  # --- Unit cases: the installer's own functions, loaded through the AST, with Find-OtherAcaddoc stubbed.
  $parseErrors = $null
  $ast = [Management.Automation.Language.Parser]::ParseFile($installer, [ref]$null, [ref]$parseErrors)
  Check 'installer parses' ($parseErrors.Count -eq 0) (($parseErrors | ForEach-Object { $_.Message }) -join '; ')
  $top = $ast.EndBlock.Statements
  foreach ($s in $top) {
    if ($s -is [Management.Automation.Language.AssignmentStatementAst] -and $s.Left.Extent.Text -in '$beginMarker', '$endMarker') {
      . ([scriptblock]::Create($s.Extent.Text))
    }
    if ($s -is [Management.Automation.Language.FunctionDefinitionAst] -and $s.Name -in 'Read-TextFile', 'Set-AcaddocBlock', 'Get-CanonicalFolderPath') {
      . ([scriptblock]::Create($s.Extent.Text))
    }
  }
  $fakeOther = $null
  function Find-OtherAcaddoc([string] $exclude) { $script:fakeOther }
  function Get-ProfileSupportFolders { @() }
  $root = 'C:/Fake/plugins/Civil3DMcp'
  foreach ($s in $top) {
    if ($s -is [Management.Automation.Language.AssignmentStatementAst] -and $s.Left.Extent.Text -eq '$block') {
      . ([scriptblock]::Create($s.Extent.Text))
    }
  }
  Check 'block loaded from the installer' ($block -and $block.StartsWith($beginMarker) -and $block.Contains('(cond'))

  # Inspect the generated forms without starting AutoCAD. Failure must stay guarded until explicit retry.
  $forms = @(Get-LispForms $block)
  Check 'generated LISP is balanced, ignoring strings and comments' ($forms.Count -gt 0)
  $loader = ($forms | Where-Object { $_ -match '^\(defun c3dmcp:load\s' }) -replace '\s+', ' '
  $retry = ($forms | Where-Object { $_ -match '^\(defun c:C3DMCPLOAD\s' }) -replace '\s+', ' '
  $guard = "(if (and (not (vl-bb-ref 'c3dmcp:loaded)) (not (getcname `"C3DMCPSTATUS`")))"
  $setFlag = "(vl-bb-set 'c3dmcp:loaded T)"
  $netload = '(command "_.NETLOAD" dll)'
  $statusCheck = '(if (not (getcname "C3DMCPSTATUS"))'
  Check 'load guard allows a missing DLL to reach the failure diagnostic' ($loader.Contains($guard))
  Check 'session flag is set before checking the DLL or running NETLOAD' ($loader.IndexOf($setFlag) -gt $loader.IndexOf($guard) -and $loader.IndexOf($setFlag) -lt $loader.IndexOf('(findfile dll)') -and $loader.IndexOf($setFlag) -lt $loader.IndexOf($netload))
  Check 'NETLOAD runs only for an existing DLL using the startup-safe command form' ($loader.Contains('(if (findfile dll) ' + $netload + ')') -and (Count $block $netload) -eq 1)
  Check 'command registration is checked after NETLOAD returns' ($loader.IndexOf($statusCheck) -gt $loader.IndexOf($netload))
  $diagnostic = [regex]::Match($loader, '\(if \(not \(getcname "C3DMCPSTATUS"\)\) \(princ \(strcat "([^"]*)" dll "([^"]*)"\)\) \)')
  Check 'failed load prints the DLL path and cause/retry instructions; success stays silent' ($diagnostic.Success -and $diagnostic.Groups[1].Value.Contains('the plugin did not load') -and $diagnostic.Groups[2].Value.Contains('fix the cause (TRUSTEDPATHS, a Do Not Load answer, missing files), then type C3DMCPLOAD'))
  $message = $diagnostic.Groups[1].Value + $diagnostic.Groups[2].Value
  Check 'failure diagnostic is one line' ($diagnostic.Success -and $message.StartsWith('\n') -and (Count $message '\n') -eq 1 -and -not $message.Contains('\r'))
  Check 'failed automatic load never clears the session flag' ((Count $loader "(vl-bb-set 'c3dmcp:loaded") -eq 1 -and -not $loader.Contains("(vl-bb-set 'c3dmcp:loaded nil)"))
  Check 'C3DMCPLOAD clears the flag, retries the same loader and exits quietly' ($retry -match '^\(defun c:C3DMCPLOAD \(\) \(vl-bb-set ''c3dmcp:loaded nil\) \(c3dmcp:load\) \(princ\) \)$' -and (Count $block "(vl-bb-set 'c3dmcp:loaded nil)") -eq 1)
  Check 'drawing-open load remains deferred to an argument-less defun-q startup' ($forms -contains '(defun-q c3dmcp:startup () (c3dmcp:load))' -and $forms -notcontains '(c3dmcp:load)')
  Check 'existing list and compiled startup functions are preserved' ($block.Contains('(append S::STARTUP c3dmcp:startup)') -and $block.Contains('(setq c3dmcp:prev S::STARTUP) (defun-q S::STARTUP () (c3dmcp:prev) (c3dmcp:load))'))
  Check 'DLL path follows the running release rather than a fixed year' ($loader.Contains('(setq dll (strcat "' + $root + '/R" (substr (getvar "ACADVER") 1 4) "/Civil3DMcpPlugin.dll"))'))

  # Exercise the real finder with fake registry reads confined to this function's scope.
  function Check-MarkedAcaddocChains {
    foreach ($name in 'Get-ProfileSupportFolders', 'Find-OtherAcaddoc') {
      $definition = $top | Where-Object { $_ -is [Management.Automation.Language.FunctionDefinitionAst] -and $_.Name -eq $name }
      . ([scriptblock]::Create($definition.Extent.Text))
    }
    $series = 'R25.1'
    function Get-ItemProperty([string] $LiteralPath, [string] $ErrorAction) {
      switch ($LiteralPath) {
        "HKCU:\Software\Autodesk\AutoCAD\$series" { @{ CurVer = 'FakeCivil3D' } }
        "HKCU:\Software\Autodesk\AutoCAD\$series\FakeCivil3D\Profiles" { @{ '(default)' = 'FakeProfile' } }
        "HKCU:\Software\Autodesk\AutoCAD\$series\FakeCivil3D\Profiles\FakeProfile\General" { @{ ACAD = $supportPath } }
        default { throw "Unexpected registry read: $LiteralPath" }
      }
    }
    $files = @(New-CaseFile 'marked-first'; New-CaseFile 'marked-second')
    $dirs = @($files | ForEach-Object { Split-Path -Parent $_ })
    $outside = New-CaseFile 'not-on-path'
    [IO.File]::WriteAllText($outside, '(princ "outside")')
    $valid = New-CaseFile 'valid-link'
    [IO.File]::WriteAllText($valid, '(princ "valid")')
    $offline = Join-Path $work 'offline-share\acaddoc.lsp'
    $validLoad = "(vl-catch-all-apply 'load (list `"$($valid -replace '\\', '/')`"))"
    $offlineLoad = "(vl-catch-all-apply 'load (list `"$($offline -replace '\\', '/')`"))"
    foreach ($order in @(@($dirs[0], $dirs[1]), @($dirs[1], $dirs[0]))) {
      $supportPath = $order -join ';'
      $early = Join-Path $order[0] 'acaddoc.lsp'
      $late = Join-Path $order[1] 'acaddoc.lsp'
      $earlyLoad = "(vl-catch-all-apply 'load (list `"$($early -replace '\\', '/')`"))"
      $lateLoad = "(vl-catch-all-apply 'load (list `"$($late -replace '\\', '/')`"))"
      [IO.File]::WriteAllText($early, $block.Replace($beginMarker, (@($beginMarker, $validLoad, $offlineLoad) -join "`r`n")))
      [IO.File]::WriteAllText($late, "(princ `"later user code`")`r`n" + $block.Replace($beginMarker, (@($beginMarker, $earlyLoad, $validLoad, $offlineLoad) -join "`r`n")), [Text.Encoding]::Unicode)
      $before = @(Hex ([IO.File]::ReadAllBytes($early)); Hex ([IO.File]::ReadAllBytes($late)))
      $folders = @(Get-ProfileSupportFolders)
      Check 'profile folders stay normalized and ordered' ($folders.Count -eq 2 -and $folders[0] -ieq $order[0] -and $folders[1] -ieq $order[1])
      Check 'finder selects the first real later acaddoc, even when marked' ((Find-OtherAcaddoc $order[0]) -eq $late)
      Check 'finder rejects a later and an off-path folder' ($null -eq (Find-OtherAcaddoc $order[1]) -and $null -eq (Find-OtherAcaddoc (Split-Path -Parent $outside)))
      Check 'finder leaves both marked files byte-identical' ((Hex ([IO.File]::ReadAllBytes($early))) -eq $before[0] -and (Hex ([IO.File]::ReadAllBytes($late))) -eq $before[1])
      foreach ($run in 1, 2) {
        Set-AcaddocBlock $early $block | Out-Null
        Set-AcaddocBlock $late $block | Out-Null
        $earlyText = (Read-TextFile $early).Text
        $lateText = (Read-TextFile $late).Text
        $earlyGuard = @(Get-LispForms $earlyText | Where-Object { $_ -match '^\(if \(not \(member ' })
        $lateGuard = @(Get-LispForms $lateText | Where-Object { $_ -match '^\(if \(not \(member ' })
        $earlyKey = [IO.Path]::GetFullPath($early).Replace('\', '/').ToUpperInvariant()
        $lateKey = [IO.Path]::GetFullPath($late).Replace('\', '/').ToUpperInvariant()
        Check "run $($run): earlier file chains to later once" ((Count $earlyText $lateLoad) -eq 1)
        Check "run $($run): stale upward link is dropped" (-not $lateText.Contains($earlyLoad))
        Check "run $($run): valid and unavailable links survive" ((Count $earlyText $validLoad) -eq 1 -and (Count $lateText $validLoad) -eq 1 -and (Count $earlyText $offlineLoad) -eq 1 -and (Count $lateText $offlineLoad) -eq 1)
        Check "run $($run): all loads in one per-file document guard" ($earlyGuard.Count -eq 1 -and $lateGuard.Count -eq 1 -and (Count $earlyGuard[0] "'load (list") -eq 3 -and (Count $lateGuard[0] "'load (list") -eq 2 -and $earlyGuard[0].Contains('(member "' + $earlyKey + '" c3dmcp:chain-visited)') -and $lateGuard[0].Contains('(member "' + $lateKey + '" c3dmcp:chain-visited)'))
        Check "run $($run): each file is marked before its first load" ($earlyGuard[0].IndexOf('(setq c3dmcp:chain-visited (cons "' + $earlyKey + '" c3dmcp:chain-visited))') -ge 0 -and $earlyGuard[0].IndexOf('(setq ') -lt $earlyGuard[0].IndexOf("'load (list") -and $lateGuard[0].IndexOf('(setq c3dmcp:chain-visited (cons "' + $lateKey + '" c3dmcp:chain-visited))') -ge 0 -and $lateGuard[0].IndexOf('(setq ') -lt $lateGuard[0].IndexOf("'load (list"))
        Check "run $($run): chain guard uses no blackboard" (-not $earlyGuard[0].Contains('vl-bb-') -and -not $lateGuard[0].Contains('vl-bb-'))
        Check "run $($run): later code and UTF-16LE stay intact" ($lateText.Contains('(princ "later user code")') -and (Hex ([IO.File]::ReadAllBytes($late))[0..1]) -eq 'FF FE')
        Check "run $($run): both LISP files remain balanced" ((Count $earlyText $beginMarker) -eq 1 -and (Count $lateText $beginMarker) -eq 1)
      }
    }
  }
  Check-MarkedAcaddocChains

  # Build A -> marked B -> user C with the real finder, in both support-path orders.
  function Check-DownstreamStartupExecution {
    foreach ($name in 'Get-ProfileSupportFolders', 'Find-OtherAcaddoc') {
      $definition = $top | Where-Object { $_ -is [Management.Automation.Language.FunctionDefinitionAst] -and $_.Name -eq $name }
      . ([scriptblock]::Create($definition.Extent.Text))
    }
    function Get-ItemProperty([string] $LiteralPath, [string] $ErrorAction) {
      switch ($LiteralPath) {
        "HKCU:\Software\Autodesk\AutoCAD\$series" { @{ CurVer = 'FakeCivil3D' } }
        "HKCU:\Software\Autodesk\AutoCAD\$series\FakeCivil3D\Profiles" { @{ '(default)' = 'FakeProfile' } }
        "HKCU:\Software\Autodesk\AutoCAD\$series\FakeCivil3D\Profiles\FakeProfile\General" { @{ ACAD = $supportPath } }
        default { throw "Unexpected registry read: $LiteralPath" }
      }
    }
    $series = 'R25.1'
    $chainFiles = @(New-CaseFile 'downstream-enu'; New-CaseFile 'downstream-esp')
    $corporate = New-CaseFile 'downstream-corporate'
    foreach ($order in @(@($chainFiles[0], $chainFiles[1]), @($chainFiles[1], $chainFiles[0]))) {
      foreach ($chainFile in $chainFiles) { Remove-Item -LiteralPath $chainFile -ErrorAction SilentlyContinue }
      [IO.File]::WriteAllText($corporate, '(setq corporate:ran T)')
      $supportPath = (@($order) + @($corporate) | ForEach-Object { Split-Path -Parent $_ }) -join ';'
      # Installing the later folder first reproduces reverse-alphabetical language path order.
      Set-AcaddocBlock $order[1] $block | Out-Null
      Set-AcaddocBlock $order[0] $block | Out-Null
      foreach ($run in 1, 2) {
        if ($run -eq 2) {
          foreach ($chainFile in $order) { Set-AcaddocBlock $chainFile $block | Out-Null }
          [IO.File]::WriteAllText($corporate, '(setq corporate:edited T)')
        }
        $document = @{ Symbols = @{}; Loads = New-Object Collections.ArrayList }
        Invoke-TestLispFile $order[0] $document
        $sentinel = if ($run -eq 1) { 'corporate:ran' } else { 'corporate:edited' }
        Check "downstream run $($run): A -> B -> C executes live user code" ($document.Symbols[$sentinel] -eq $true -and $document.Loads.Count -eq 3)
        $expectedKeys = @($order | ForEach-Object { [IO.Path]::GetFullPath($_).Replace('\', '/').ToUpperInvariant() })
        $visited = @($document.Symbols['c3dmcp:chain-visited'])
        Check "downstream run $($run): distinct files have normalized document keys" ($visited.Count -eq 2 -and $visited -ccontains $expectedKeys[0] -and $visited -ccontains $expectedKeys[1])
      }
      # Regenerate through a spelling with different case and a dot segment; its visit key stays equal.
      $alternate = Join-Path (Split-Path -Parent $order[1]) '.\ACADDOC.LSP'
      Set-AcaddocBlock $alternate $block | Out-Null
      $lateText = (Read-TextFile $order[1]).Text
      Check 'downstream guard normalizes case and dot segments' ($lateText.Contains('(member "' + $expectedKeys[1] + '" c3dmcp:chain-visited)'))
      # A stale upward line in B must stop at the visited A and still reach C, without reinstalling B.
      [IO.File]::WriteAllText($order[0], '(setq root:count (cons T root:count))' + "`r`n" + (Read-TextFile $order[0]).Text)
      $staleLoad = "(vl-catch-all-apply 'load (list `"$($order[0] -replace '\\', '/')`"))"
      $corporateLoad = "(vl-catch-all-apply 'load (list `"$($corporate -replace '\\', '/')`"))"
      [IO.File]::WriteAllText($order[1], $lateText.Replace($corporateLoad, "$staleLoad`r`n    $corporateLoad"))
      $document = @{ Symbols = @{}; Loads = New-Object Collections.ArrayList }
      Invoke-TestLispFile $order[0] $document
      Check 'downstream raw stale A -> B -> A cycle stops and C still executes' ($document.Symbols['corporate:edited'] -eq $true -and $document.Loads.Count -eq 4 -and @($document.Symbols['root:count']).Count -eq 2 -and @($document.Symbols['c3dmcp:chain-visited']).Count -eq 2)
      $guardedStale = "(if (not (member `"$($expectedKeys[0])`" c3dmcp:loaded-targets)) (progn (setq c3dmcp:loaded-targets (cons `"$($expectedKeys[0])`" c3dmcp:loaded-targets)) $staleLoad))"
      [IO.File]::WriteAllText($order[1], (Read-TextFile $order[1]).Text.Replace($staleLoad, $guardedStale))
      $document = @{ Symbols = @{}; Loads = New-Object Collections.ArrayList }
      Invoke-TestLispFile $order[0] $document
      Check 'downstream guarded upward edge skips A user code and still reaches C' ($document.Symbols['corporate:edited'] -eq $true -and $document.Loads.Count -eq 3 -and @($document.Symbols['root:count']).Count -eq 1)
    }
  }
  Check-DownstreamStartupExecution

  # A first finds C directly. After B is installed, A must retain C and also discover B.
  function Check-RetainedAndNewTargets {
    foreach ($name in 'Get-ProfileSupportFolders', 'Find-OtherAcaddoc') {
      $definition = $top | Where-Object { $_ -is [Management.Automation.Language.FunctionDefinitionAst] -and $_.Name -eq $name }
      . ([scriptblock]::Create($definition.Extent.Text))
    }
    $series = 'R25.1'
    function Get-ItemProperty([string] $LiteralPath, [string] $ErrorAction) {
      switch ($LiteralPath) {
        "HKCU:\Software\Autodesk\AutoCAD\$series" { @{ CurVer = 'FakeCivil3D' } }
        "HKCU:\Software\Autodesk\AutoCAD\$series\FakeCivil3D\Profiles" { @{ '(default)' = 'FakeProfile' } }
        "HKCU:\Software\Autodesk\AutoCAD\$series\FakeCivil3D\Profiles\FakeProfile\General" { @{ ACAD = $supportPath } }
        default { throw "Unexpected registry read: $LiteralPath" }
      }
    }
    $a = New-CaseFile 'retained-enu'
    $b = New-CaseFile 'retained-esp'
    $c = New-CaseFile 'retained-corporate'
    $d = New-CaseFile 'retained-extra'
    [IO.File]::WriteAllText($c, '(setq corporate:count (cons T corporate:count))')
    [IO.File]::WriteAllText($d, '(setq extra:ran T)')
    $supportPath = (@($a, $b, $c) | ForEach-Object { Split-Path -Parent $_ }) -join ';'
    Set-AcaddocBlock $a $block | Out-Null
    $cLoad = "(vl-catch-all-apply 'load (list `"$($c -replace '\\', '/')`"))"
    $bLoad = "(vl-catch-all-apply 'load (list `"$($b -replace '\\', '/')`"))"
    Check 'retained target: A initially loads C' ((Count (Read-TextFile $a).Text $cLoad) -eq 1)
    Set-AcaddocBlock $b $block | Out-Null
    Set-AcaddocBlock $a $block | Out-Null
    $basicText = (Read-TextFile $a).Text
    $basicDocument = @{ Symbols = @{}; Loads = New-Object Collections.ArrayList }
    Invoke-TestLispFile $a $basicDocument
    Check 'retained target: A then B then reinstall A keeps C and adds B' ((Count $basicText $cLoad) -eq 1 -and (Count $basicText $bLoad) -eq 1 -and $basicText.IndexOf($cLoad) -lt $basicText.IndexOf($bLoad))
    Check 'retained target: initial shared C executes once through A and B' (@($basicDocument.Symbols['corporate:count']).Count -eq 1 -and $basicDocument.Loads.Count -eq 3)
    $bText = (Read-TextFile $b).Text
    $alternateC = ((Split-Path -Parent $c).ToUpperInvariant() + '\.\ACADDOC.LSP') -replace '\\', '/'
    $alternateCLoad = "(vl-catch-all-apply 'load (list `"$alternateC`"))"
    $dLoad = "(vl-catch-all-apply 'load (list `"$($d -replace '\\', '/')`"))"
    [IO.File]::WriteAllText($b, '(setq language:ran T)' + "`r`n" + $bText.Replace($cLoad, "$alternateCLoad`r`n        $dLoad"))
    Set-AcaddocBlock $b $block | Out-Null
    Set-AcaddocBlock $a $block | Out-Null
    $aText = (Read-TextFile $a).Text
    Check 'retained target: reinstall A keeps C and adds B' ((Count $aText $cLoad) -eq 1 -and (Count $aText $bLoad) -eq 1 -and $aText.IndexOf($cLoad) -lt $aText.IndexOf($bLoad))
    $document = @{ Symbols = @{}; Loads = New-Object Collections.ArrayList }
    Invoke-TestLispFile $a $document
    Check 'retained target: C runs once while B runs its own downstream load' (@($document.Symbols['corporate:count']).Count -eq 1 -and $document.Symbols['language:ran'] -eq $true -and $document.Symbols['extra:ran'] -eq $true -and $document.Loads.Count -eq 4)
    Check 'retained target: alternate case and dot spelling of C shares the target key' ((Count (Read-TextFile $b).Text $alternateCLoad) -eq 1 -and -not ($document.Loads -contains $alternateC))
    $cKey = [IO.Path]::GetFullPath($c).Replace('\', '/').ToUpperInvariant()
    Check 'retained target: target is marked before load and uses a separate set' ($aText.Contains('(member "' + $cKey + '" c3dmcp:loaded-targets)') -and $aText.IndexOf('(setq c3dmcp:loaded-targets (cons "' + $cKey + '" c3dmcp:loaded-targets))') -lt $aText.IndexOf($cLoad) -and @($document.Symbols['c3dmcp:loaded-targets']) -ccontains $cKey)
    $fresh = @{ Symbols = @{}; Loads = New-Object Collections.ArrayList }
    Invoke-TestLispFile $a $fresh
    Check 'retained target: a fresh document initializes C again' (@($fresh.Symbols['corporate:count']).Count -eq 1 -and $fresh.Loads.Count -eq 4)
  }
  Check-RetainedAndNewTargets

  # Profile, exclude and saved-load parents can spell the same directory differently.
  function Check-CanonicalFolderComparisons {
    foreach ($name in 'Get-ProfileSupportFolders', 'Find-OtherAcaddoc') {
      $definition = $top | Where-Object { $_ -is [Management.Automation.Language.FunctionDefinitionAst] -and $_.Name -eq $name }
      . ([scriptblock]::Create($definition.Extent.Text))
    }
    $series = 'R25.1'
    function Get-ItemProperty([string] $LiteralPath, [string] $ErrorAction) {
      switch ($LiteralPath) {
        "HKCU:\Software\Autodesk\AutoCAD\$series" { @{ CurVer = 'FakeCivil3D' } }
        "HKCU:\Software\Autodesk\AutoCAD\$series\FakeCivil3D\Profiles" { @{ '(default)' = 'FakeProfile' } }
        "HKCU:\Software\Autodesk\AutoCAD\$series\FakeCivil3D\Profiles\FakeProfile\General" { @{ ACAD = $supportPath } }
        default { throw "Unexpected registry read: $LiteralPath" }
      }
    }
    $early = New-CaseFile 'canonical-early'
    $late = New-CaseFile 'canonical-late'
    [IO.File]::WriteAllText($late, '(setq late:ran T)')
    $earlyDir = Split-Path -Parent $early
    $lateDir = Split-Path -Parent $late
    $supportPath = (($earlyDir + '\.\') -replace '\\', '/') + ';' + ((Join-Path $earlyDir '..\canonical-late') -replace '\\', '/')
    $folders = @(Get-ProfileSupportFolders)
    Check 'canonical folders: profile dot, parent and separators resolve' ($folders.Count -eq 2 -and $folders[0] -ieq $earlyDir -and $folders[1] -ieq $lateDir)
    Check 'canonical folders: finder matches alternate exclude spelling' ((Find-OtherAcaddoc ($earlyDir.ToUpperInvariant() + '\.')) -ieq $late)
    Set-AcaddocBlock $early $block | Out-Null
    $document = @{ Symbols = @{}; Loads = New-Object Collections.ArrayList }
    Invoke-TestLispFile $early $document
    Check 'canonical folders: created earlier block reaches hidden later user code' ($document.Symbols['late:ran'] -eq $true -and $document.Loads.Count -eq 2)
    $upward = ($earlyDir.ToUpperInvariant() + '\.\ACADDOC.LSP') -replace '\\', '/'
    $upwardLoad = "(vl-catch-all-apply 'load (list `"$upward`"))"
    [IO.File]::WriteAllText($late, $block.Replace($beginMarker, (@($beginMarker, $upwardLoad) -join "`r`n")) + "`r`n(setq late:ran T)")
    Set-AcaddocBlock $late $block | Out-Null
    Check 'canonical folders: saved upward load with dot and case is pruned' (-not (Read-TextFile $late).Text.Contains($upwardLoad))
    Check 'canonical folders: drive root keeps its separator' ((Get-CanonicalFolderPath 'C:/') -eq 'C:\' -and (Get-CanonicalFolderPath 'C:/folder/..') -eq 'C:\')
  }
  Check-CanonicalFolderComparisons

  function Check-MalformedPathEntries {
    foreach ($name in 'Get-ProfileSupportFolders', 'Find-OtherAcaddoc') {
      $definition = $top | Where-Object { $_ -is [Management.Automation.Language.FunctionDefinitionAst] -and $_.Name -eq $name }
      . ([scriptblock]::Create($definition.Extent.Text))
    }
    $series = 'R25.1'
    function Get-ItemProperty([string] $LiteralPath, [string] $ErrorAction) {
      switch ($LiteralPath) {
        "HKCU:\Software\Autodesk\AutoCAD\$series" { @{ CurVer = 'FakeCivil3D' } }
        "HKCU:\Software\Autodesk\AutoCAD\$series\FakeCivil3D\Profiles" { @{ '(default)' = 'FakeProfile' } }
        "HKCU:\Software\Autodesk\AutoCAD\$series\FakeCivil3D\Profiles\FakeProfile\General" { @{ ACAD = $supportPath } }
        default { throw "Unexpected registry read: $LiteralPath" }
      }
    }
    $early = New-CaseFile 'malformed-early'
    $late = New-CaseFile 'malformed-late'
    $earlyDir = Split-Path -Parent $early
    $lateDir = Split-Path -Parent $late
    [IO.File]::WriteAllText($late, '(setq late:ran T)')
    $badFolders = @("$work\bad|support", "$work\bad:support", 'https://invalid.example/support')
    foreach ($bad in $badFolders) {
      $supportPath = "$bad;$earlyDir;$bad;$lateDir;$bad"
      $failure = $null
      try { $folders = @(Get-ProfileSupportFolders); $other = Find-OtherAcaddoc $earlyDir }
      catch { $failure = $_.Exception.Message }
      Check "malformed support entry skipped: $bad" (-not $failure -and $folders.Count -eq 2 -and $folders[0] -ieq $earlyDir -and $folders[1] -ieq $lateDir -and $other -ieq $late) "$failure"
    }

    # Invalid saved paths stay as live load lines; valid upward paths are still removed.
    $supportPath = "$earlyDir;$lateDir"
    $upwardLoad = "(vl-catch-all-apply 'load (list `"$($early -replace '\\', '/')`"))"
    foreach ($bad in $badFolders) {
      $saved = ($bad -replace '\\', '/') + '/acaddoc.lsp'
      $savedLoad = "(vl-catch-all-apply 'load (list `"$saved`"))"
      [IO.File]::WriteAllText($late, $block.Replace($beginMarker, (@($beginMarker, $savedLoad, $upwardLoad) -join "`r`n")))
      $failure = $null
      try {
        Set-AcaddocBlock $late $block | Out-Null
        Set-AcaddocBlock $late $block | Out-Null
      } catch { $failure = $_.Exception.Message }
      Check "malformed saved path does not abort reinstall: $bad" (-not $failure) "$failure"
      if ($failure) { continue }
      $text = (Read-TextFile $late).Text
      $chain = @(Get-LispForms $text | Where-Object { $_ -match '^\(if \(not \(member ' })
      Check "malformed saved load kept once and upward load dropped: $bad" ((Count $text $savedLoad) -eq 1 -and -not $text.Contains($upwardLoad))
      Check "malformed saved load guarded by its raw key: $bad" ($chain.Count -eq 1 -and $chain[0].Contains('(member "' + $saved.ToUpperInvariant() + '" c3dmcp:loaded-targets)') -and $chain[0].Contains($savedLoad))
    }
  }
  Check-MalformedPathEntries
  # ANSI (BOM-less, not valid UTF-8): read and written back in the system code page, in both editions.
  $ansiBytes = [Text.Encoding]::GetEncoding($acp).GetBytes($cafe)
  $validUtf8 = $true
  try { (New-Object Text.UTF8Encoding($false, $true)).GetString($ansiBytes) | Out-Null } catch { $validUtf8 = $false }
  if ($validUtf8) {
    "SKIP ANSI cases: '; cafe' with an accent is valid UTF-8 in code page $acp"
  } else {
    $f = New-CaseFile 'ansi'
    [IO.File]::WriteAllBytes($f, $ansiBytes)
    Check 'ANSI fallback is the system code page' ((Read-TextFile $f).Encoding.CodePage -eq $acp) "got $((Read-TextFile $f).Encoding.CodePage), ACP $acp"
    Set-AcaddocBlock $f $block | Out-Null
    $after = [IO.File]::ReadAllBytes($f)
    Check 'ANSI file keeps its bytes' ((Hex $after[0..($ansiBytes.Length - 1)]) -eq (Hex $ansiBytes)) (Hex $after[0..15])
    Check 'ANSI file gets the block' ([Text.Encoding]::GetEncoding($acp).GetString($after).Contains($beginMarker))
    Check 'ANSI file backed up before the first change' ((Test-Path "$f.c3dmcp-backup") -and (Hex ([IO.File]::ReadAllBytes("$f.c3dmcp-backup"))) -eq (Hex $ansiBytes))
  }

  # UTF-8 with BOM keeps its BOM; BOM-less UTF-8 stays BOM-less; UTF-16LE keeps FF FE.
  $utf8 = [Text.Encoding]::UTF8.GetBytes($cafe)
  $f = New-CaseFile 'utf8bom'
  [IO.File]::WriteAllBytes($f, [byte[]](@(0xEF, 0xBB, 0xBF) + $utf8))
  Set-AcaddocBlock $f $block | Out-Null
  $after = [IO.File]::ReadAllBytes($f)
  Check 'UTF-8 BOM file keeps BOM and text' ((Hex $after[0..(2 + $utf8.Length)]) -eq (Hex ([byte[]](@(0xEF, 0xBB, 0xBF) + $utf8)))) (Hex $after[0..15])
  Check 'UTF-8 BOM file gets the block once' ((Count ([Text.Encoding]::UTF8.GetString($after)) $beginMarker) -eq 1)

  $f = New-CaseFile 'utf8'
  [IO.File]::WriteAllBytes($f, $utf8)
  Set-AcaddocBlock $f $block | Out-Null
  $after = [IO.File]::ReadAllBytes($f)
  Check 'BOM-less UTF-8 file stays BOM-less UTF-8' ((Hex $after[0..($utf8.Length - 1)]) -eq (Hex $utf8)) (Hex $after[0..15])

  $utf16 = [Text.Encoding]::Unicode.GetBytes($cafe)
  $f = New-CaseFile 'utf16'
  [IO.File]::WriteAllBytes($f, [byte[]](@(0xFF, 0xFE) + $utf16))
  Set-AcaddocBlock $f $block | Out-Null
  $after = [IO.File]::ReadAllBytes($f)
  Check 'UTF-16LE file keeps BOM and text' ((Hex $after[0..(1 + $utf16.Length)]) -eq (Hex ([byte[]](@(0xFF, 0xFE) + $utf16)))) (Hex $after[0..15])
  Check 'UTF-16LE file gets the block' ([Text.Encoding]::Unicode.GetString($after, 2, $after.Length - 2).Contains($beginMarker))

  # Uninstall on a file that never held our block: not rewritten, not re-encoded, not backed up.
  $f = New-CaseFile 'foreign'
  [IO.File]::WriteAllBytes($f, [byte[]](@(0x3B, 0x20, 0x63, 0x61, 0x66, 0xE9, 0x0D, 0x0A)))
  [IO.File]::SetLastWriteTimeUtc($f, $oldTime)
  Set-AcaddocBlock $f '' | Out-Null
  Check 'uninstall leaves a foreign file byte-identical' ((Hex ([IO.File]::ReadAllBytes($f))) -eq '3B 20 63 61 66 E9 0D 0A')
  Check 'uninstall leaves a foreign file unwritten' ([IO.File]::GetLastWriteTimeUtc($f) -eq $oldTime)
  Check 'uninstall backs up nothing' (-not (Test-Path "$f.c3dmcp-backup"))
  $f = New-CaseFile 'missing'
  Set-AcaddocBlock $f '' | Out-Null
  Check 'uninstall creates no file' (-not (Test-Path $f))

  # A hidden acaddoc.lsp is loaded before the (cond that wraps S::STARTUP, and the line survives a re-run.
  $fakeOther = 'C:\Fake\Support\acaddoc.lsp'
  $f = New-CaseFile 'hidden'
  foreach ($run in 1, 2) {
    Set-AcaddocBlock $f $block | Out-Null
    $text = [IO.File]::ReadAllText($f)
    $load = "(vl-catch-all-apply 'load (list `"C:/Fake/Support/acaddoc.lsp`"))"
    Check "run ${run}: hidden acaddoc.lsp loaded exactly once" ((Count $text $load) -eq 1)
    Check "run ${run}: hidden acaddoc.lsp loaded before (cond" ($text.IndexOf($load) -ge 0 -and $text.IndexOf($load) -lt $text.IndexOf('(cond'))
    Check "run ${run}: one block" ((Count $text $beginMarker) -eq 1 -and (Count $text $endMarker) -eq 1)
  }
  Set-AcaddocBlock $f '' | Out-Null
  Check 'uninstall deletes a file that held only our block' (-not (Test-Path $f))

  # A user's existing file never shadowed anything new: no load line, on the first run or a re-run.
  $f = New-CaseFile 'userfile'
  [IO.File]::WriteAllText($f, "(princ `"user`")`r`n")
  foreach ($run in 1, 2) {
    Set-AcaddocBlock $f $block | Out-Null
    $text = [IO.File]::ReadAllText($f)
    Check "run ${run}: user file gets no load line" (-not $text.Contains("'load (list"))
    Check "run ${run}: user file keeps its code and one block" ($text.StartsWith('(princ "user")') -and (Count $text $beginMarker) -eq 1)
  }

  # The user adds code to the file the install created: re-runs keep the load line, with the hidden file in
  # reach or not, and -Uninstall keeps the user's code without it and warns that the file is hidden again.
  $load = "(vl-catch-all-apply 'load (list `"C:/Fake/Support/acaddoc.lsp`"))"
  $fakeOther = 'C:\Fake\Support\acaddoc.lsp'
  $f = New-CaseFile 'userappend'
  Set-AcaddocBlock $f $block | Out-Null
  [IO.File]::AppendAllText($f, "(load `"wsp-core.lsp`")`r`n")
  foreach ($case in @{ Name = 'in reach'; Other = 'C:\Fake\Support\acaddoc.lsp' }, @{ Name = 'out of reach'; Other = $null }) {
    $fakeOther = $case.Other
    Set-AcaddocBlock $f $block | Out-Null
    $text = [IO.File]::ReadAllText($f)
    Check "user code added, hidden file $($case.Name): load line kept once" ((Count $text $load) -eq 1)
    Check "user code added, hidden file $($case.Name): load line before (cond" ($text.IndexOf($load) -ge 0 -and $text.IndexOf($load) -lt $text.IndexOf('(cond'))
    Check "user code added, hidden file $($case.Name): user code and one block" ($text.Contains('(load "wsp-core.lsp")') -and (Count $text $beginMarker) -eq 1)
  }
  $out = @(Set-AcaddocBlock $f '' 3>&1)
  $text = [IO.File]::ReadAllText($f)
  Check 'uninstall keeps the user code, without our block or the load line' ($text.Contains('(load "wsp-core.lsp")') -and -not $text.Contains($beginMarker) -and -not $text.Contains("'load (list"))
  $warnings = @($out | Where-Object { $_ -is [Management.Automation.WarningRecord] })
  Check 'uninstall warns that the hidden file is hidden again' ($warnings.Count -eq 1 -and $warnings[0].Message.Contains('C:/Fake/Support/acaddoc.lsp')) (($out | ForEach-Object { "$_" }) -join ' | ')

  # The hidden file out of reach on a re-run (a share off VPN): the load line stays; with the file back
  # (any case) there is still one; a newly found file is added next to it; -Uninstall of a file that is
  # only ours deletes it without a warning (nothing is hidden any more).
  $f = New-CaseFile 'offline'
  $fakeOther = 'C:\Fake\Support\acaddoc.lsp'
  Set-AcaddocBlock $f $block | Out-Null
  $fakeOther = $null
  Set-AcaddocBlock $f $block | Out-Null
  Check 'hidden file out of reach: load line kept' ((Count ([IO.File]::ReadAllText($f)) $load) -eq 1)
  $fakeOther = 'c:\fake\support\ACADDOC.lsp'
  Set-AcaddocBlock $f $block | Out-Null
  $text = [IO.File]::ReadAllText($f)
  Check 'hidden file back: still one load line' ((Count $text "'load (list") -eq 1 -and (Count $text $load) -eq 1)
  $fakeOther = 'D:\Other\Support\acaddoc.lsp'
  Set-AcaddocBlock $f $block | Out-Null
  $text = [IO.File]::ReadAllText($f)
  $load2 = "(vl-catch-all-apply 'load (list `"D:/Other/Support/acaddoc.lsp`"))"
  Check 'another hidden file found: both loaded once, before (cond' ((Count $text $load) -eq 1 -and (Count $text $load2) -eq 1 -and $text.IndexOf($load2) -lt $text.IndexOf('(cond'))
  Check 'another hidden file found: one block' ((Count $text $beginMarker) -eq 1)
  $out = @(Set-AcaddocBlock $f '' 3>&1)
  Check 'uninstall deletes a file that held only our block, without a warning' ((-not (Test-Path $f)) -and -not ($out | Where-Object { $_ -is [Management.Automation.WarningRecord] }))
  $fakeOther = $null

  # --- Full-script cases with a temp APPDATA: every lock is checked before the first change.
  $appData = Join-Path $work 'appdata'
  $support = Join-Path $appData 'Autodesk\C3D 2026\enu\Support'
  $bundle = Join-Path $appData 'Autodesk\ApplicationPlugins\Civil3DMcp.bundle'
  $bundleDll = Join-Path $bundle 'Contents\Civil3DMcpPlugin.dll'
  $src = Join-Path $work 'src\net8.0-windows'
  $dest = Join-Path $work 'dest'
  $target = Join-Path $dest 'R25.1'
  $acaddoc = Join-Path $support 'acaddoc.lsp'
  foreach ($d in $support, (Split-Path $bundleDll), $src) { New-Item -ItemType Directory -Force $d | Out-Null }
  [IO.File]::WriteAllBytes($bundleDll, [byte[]](1, 2, 3))
  [IO.File]::WriteAllBytes((Join-Path $src 'Civil3DMcpPlugin.dll'), [byte[]](4, 5, 6))
  $env:APPDATA = $appData
  $series = 'R25.1'
  # Include a foreign startup file so the full install must allow one guarded chain form.
  $fullForeignSupport = Join-Path $work 'foreign-support'
  New-Item -ItemType Directory -Force $fullForeignSupport | Out-Null
  [IO.File]::WriteAllText((Join-Path $fullForeignSupport 'acaddoc.lsp'), '(princ "foreign startup")')
  $supportPath = "$work\bad|support;$support;$work\bad:support;https://invalid.example/support;$fullForeignSupport"
  function Get-ItemProperty([string] $LiteralPath, [string] $ErrorAction) {
    switch ($LiteralPath) {
      "HKCU:\Software\Autodesk\AutoCAD\$series" { @{ CurVer = 'FakeCivil3D' } }
      "HKCU:\Software\Autodesk\AutoCAD\$series\FakeCivil3D\Profiles" { @{ '(default)' = 'FakeProfile' } }
      "HKCU:\Software\Autodesk\AutoCAD\$series\FakeCivil3D\Profiles\FakeProfile\General" { @{ ACAD = $supportPath } }
      'HKLM:\SYSTEM\CurrentControlSet\Control\Nls\CodePage' { @{ ACP = $acp } }
      default { throw "Unexpected registry read: $LiteralPath" }
    }
  }

  # A loaded DLL can be read (hashed) but not opened for writing: hold it the same way.
  $threw = $null
  $handle = [IO.File]::Open($bundleDll, 'Open', 'Read', 'Read')
  try { & $installer -SourceDir $src -Destination $dest | Out-Null } catch { $threw = $_.Exception.Message } finally { $handle.Dispose() }
  Check 'locked bundle: install refused' ($threw -and $threw.Contains('Civil3DMcp.bundle')) "threw: $threw"
  Check 'locked bundle: acaddoc.lsp not written' (-not (Test-Path $acaddoc))
  Check 'locked bundle: nothing copied' (-not (Test-Path $target))
  Check 'locked bundle: bundle kept' (Test-Path $bundleDll)

  $out = & $installer -SourceDir $src -Destination $dest
  Check 'unlocked: acaddoc.lsp holds the block' ((Test-Path $acaddoc) -and [IO.File]::ReadAllText($acaddoc).Contains($beginMarker)) ($out -join ' | ')
  $generated = [IO.File]::ReadAllText($acaddoc)
  Check 'full install: retry command and failure diagnostic reach acaddoc.lsp' ($generated.Contains('(defun c:C3DMCPLOAD ()') -and $generated.Contains('then type C3DMCPLOAD'))
  Check 'full install: one guarded form loads the foreign startup file' ($generated.Contains(($fullForeignSupport -replace '\\', '/') + '/acaddoc.lsp') -and @(Get-LispForms $generated).Count -eq $forms.Count + 1 -and $generated.Contains('(if (not (member '))
  Check 'full install: generated LISP stays balanced' (@(Get-LispForms $generated).Count -ge $forms.Count)
  Check 'full install: diagnostic uses the installed root and running release' ($generated.Contains('"' + ($dest -replace '\\', '/') + '/R" (substr (getvar "ACADVER") 1 4) "/Civil3DMcpPlugin.dll"'))
  Check 'unlocked: bundle removed' (-not (Test-Path $bundle))
  Check 'unlocked: DLL installed' ((Test-Path "$target\Civil3DMcpPlugin.dll") -and (Get-FileHash "$target\Civil3DMcpPlugin.dll").Hash -eq (Get-FileHash "$src\Civil3DMcpPlugin.dll").Hash)

  # Each order starts with a fresh empty earlier file and a real later file with user code.
  foreach ($name in 'enu-first', 'esp-first') {
    $mixedAppData = Join-Path $work "mixed\$name\appdata"
    $mixedEnu = Join-Path $mixedAppData 'Autodesk\C3D 2026\enu\Support'
    $mixedEsp = Join-Path $mixedAppData 'Autodesk\C3D 2026\esp\Support'
    $mixedDest = Join-Path $work "mixed\$name\dest"
    foreach ($d in $mixedEnu, $mixedEsp) { New-Item -ItemType Directory -Force $d | Out-Null }
    $order = if ($name -eq 'enu-first') { @($mixedEnu, $mixedEsp) } else { @($mixedEsp, $mixedEnu) }
    $early = Join-Path $order[0] 'acaddoc.lsp'
    $late = Join-Path $order[1] 'acaddoc.lsp'
    $mixedCode = '(defun S::STARTUP () (load "foreign-startup.lsp"))'
    [IO.File]::WriteAllText($late, $mixedCode + "`r`n", [Text.Encoding]::Unicode)
    $env:APPDATA = $mixedAppData
    $supportPath = $order -join ';'
    & $installer -SourceDir $src -Destination $mixedDest -KeepBundle | Out-Null
    $lateLoad = "(vl-catch-all-apply 'load (list `"$($late -replace '\\', '/')`"))"
    $earlyLoad = "(vl-catch-all-apply 'load (list `"$($early -replace '\\', '/')`"))"
    $earlyText = (Read-TextFile $early).Text
    $lateText = (Read-TextFile $late).Text
    $chain = @(Get-LispForms $earlyText | Where-Object { $_ -match '^\(if \(not \(member ' })
    Check "mixed $($name): only earlier real file chains to later" ((Count $earlyText $lateLoad) -eq 1 -and -not $lateText.Contains($earlyLoad))
    Check "mixed $($name): chain is one guarded balanced top-level form" ($chain.Count -eq 1 -and $chain[0].Contains('(setq c3dmcp:chain-visited (cons ') -and @(Get-LispForms $earlyText).Count -eq $forms.Count + 1)
    Check "mixed $($name): both real files hold marked blocks" ((Count $earlyText $beginMarker) -eq 1 -and (Count $lateText $beginMarker) -eq 1)
    Check "mixed $($name): foreign code and UTF-16LE backup survive" ($lateText.Contains($mixedCode) -and (Read-TextFile "$late.c3dmcp-backup").Text.Trim() -eq $mixedCode -and (Hex ([IO.File]::ReadAllBytes("$late.c3dmcp-backup"))[0..1]) -eq 'FF FE')
    $changedForeign = '(princ "updated foreign startup")'
    [IO.File]::WriteAllText($late, $changedForeign + "`r`n" + $lateText, [Text.Encoding]::Unicode)
    Check "mixed $($name): later edit is immediately reachable" ((Count (Read-TextFile $early).Text $lateLoad) -eq 1 -and (Read-TextFile $late).Text.Contains($changedForeign))
    & $installer -SourceDir $src -Destination $mixedDest -KeepBundle | Out-Null
    Check "mixed $($name): later post-install edit stays reachable" ((Count (Read-TextFile $early).Text $lateLoad) -eq 1 -and (Read-TextFile $late).Text.Contains($changedForeign))
  }
  $env:APPDATA = $appData
  $supportPath = $support
  # A changed build while the installed DLL is loaded: refused before acaddoc.lsp or the bundle is touched.
  Remove-Item $acaddoc
  New-Item -ItemType Directory -Force (Split-Path $bundleDll) | Out-Null
  [IO.File]::WriteAllBytes($bundleDll, [byte[]](1, 2, 3))
  [IO.File]::WriteAllBytes((Join-Path $src 'Civil3DMcpPlugin.dll'), [byte[]](7, 8, 9))
  $threw = $null
  $handle = [IO.File]::Open("$target\Civil3DMcpPlugin.dll", 'Open', 'Read', 'Read')
  try { & $installer -SourceDir $src -Destination $dest | Out-Null } catch { $threw = $_.Exception.Message } finally { $handle.Dispose() }
  Check 'locked target: install refused' ($threw -and $threw.Contains('build changed')) "threw: $threw"
  Check 'locked target: acaddoc.lsp not written' (-not (Test-Path $acaddoc))
  Check 'locked target: bundle kept' (Test-Path $bundleDll)
  Check 'locked target: old DLL kept' ((Hex ([IO.File]::ReadAllBytes("$target\Civil3DMcpPlugin.dll"))) -eq '04 05 06')

  # Uninstall (a child process: the script ends with exit) after a second language folder appeared with
  # someone else's acaddoc.lsp: ours is removed, theirs is left byte-for-byte and unwritten.
  & $installer -SourceDir $src -Destination $dest | Out-Null
  $esp = Join-Path $appData 'Autodesk\C3D 2026\esp\Support'
  New-Item -ItemType Directory -Force $esp | Out-Null
  $foreign = Join-Path $esp 'acaddoc.lsp'
  [IO.File]::WriteAllBytes($foreign, [byte[]](@(0x3B, 0x20, 0x63, 0x61, 0x66, 0xE9, 0x0D, 0x0A)))
  [IO.File]::SetLastWriteTimeUtc($foreign, $oldTime)
  $exe = (Get-Process -Id $PID).Path
  $out = & $exe -NoProfile -ExecutionPolicy Bypass -File $installer -Uninstall -SourceDir $src -Destination $dest
  Check 'uninstall exits 0' ($LASTEXITCODE -eq 0) ($out -join ' | ')
  Check 'uninstall removes our file' (-not (Test-Path $acaddoc))
  Check 'uninstall removes the plugin folder' (-not (Test-Path $target))
  Check 'uninstall leaves the foreign file' ((Hex ([IO.File]::ReadAllBytes($foreign))) -eq '3B 20 63 61 66 E9 0D 0A' -and [IO.File]::GetLastWriteTimeUtc($foreign) -eq $oldTime)
  Check 'uninstall backs up no foreign file' (-not (Test-Path "$foreign.c3dmcp-backup"))

  # No SourceDir must find a 2026-only installation, with no 2027 profile or build output.
  $case = New-UninstallCase '2026-only'
  $releaseDir = New-InstalledRelease $case 'R25.1'
  $marked = New-MarkedSupport $case 2026
  $result = Invoke-UninstallCase $case
  Check 'all-release uninstall: 2026-only exits 0' ($result.ExitCode -eq 0) $result.Output
  Check 'all-release uninstall: 2026-only plugin removed' (-not (Test-Path $releaseDir))
  Check 'all-release uninstall: 2026-only block removed' (-not (Test-Path $marked))
  $result = Invoke-UninstallCase $case
  Check 'all-release uninstall: repeated uninstall exits 0' ($result.ExitCode -eq 0) $result.Output

  # NTFS 8.3 names can make -Filter R* return .release-backup. Exercise the real filter when it
  # does, and always replay that result through the exact discovery statement as a regression.
  $case = New-UninstallCase 'short-alias'
  $backupDir = New-InstalledRelease $case '.release-backup'
  $actualAliasMatch = @(Get-ChildItem -LiteralPath $case.Destination -Directory -Force -Filter 'R*' |
    Where-Object { $_.Name -eq '.release-backup' }).Count -gt 0
  function Check-ShortAliasGuard {
    $Destination = $case.Destination
    $uninstallAll = $true
    $discovery = $top | Where-Object {
      $_ -is [Management.Automation.Language.IfStatementAst] -and $_.Extent.Text.Contains("Get-ChildItem -LiteralPath `$Destination -Directory -Force -Filter 'R*'")
    }
    function Get-ChildItem([string] $LiteralPath, [switch] $Directory, [switch] $Force, [string] $Filter) {
      if ($LiteralPath -eq $Destination -and $Filter -eq 'R*') {
        Microsoft.PowerShell.Management\Get-ChildItem -LiteralPath $LiteralPath -Directory -Force
      } else {
        Microsoft.PowerShell.Management\Get-ChildItem @PSBoundParameters
      }
    }
    . ([scriptblock]::Create($discovery.Extent.Text))
    Check 'all-release discovery: short-alias result is rejected by long name' ($targets.Count -eq 0) "selected: $($targets -join ', ')"
  }
  Check-ShortAliasGuard
  $result = Invoke-UninstallCase $case
  Check 'all-release uninstall: .release-backup DLL survives short-alias filter' ($result.ExitCode -eq 0 -and (Test-Path "$backupDir\Civil3DMcpPlugin.dll")) "filter matched alias: $actualAliasMatch; $($result.Output)"

  # Releases and languages are discovered independently; preserve user code and unrelated paths.
  $case = New-UninstallCase 'all-years'
  $releaseDirs = @('R24.3', 'R25.0', 'R25.1', 'R26.0' | ForEach-Object { New-InstalledRelease $case $_ })
  $markedFiles = @(foreach ($year in 2024..2027) {
    foreach ($language in 'enu', 'esp') { New-MarkedSupport $case $year $language }
  })
  $userFile = $markedFiles[0]
  [IO.File]::AppendAllText($userFile, "(princ `"user code`")`r`n")
  [IO.File]::WriteAllText("$userFile.c3dmcp-backup", 'original backup')
  $foreignDir = Join-Path $case.AppData 'Autodesk\C3D 2026\fra\Support'
  New-Item -ItemType Directory -Force $foreignDir | Out-Null
  $foreignFile = Join-Path $foreignDir 'acaddoc.lsp'
  [IO.File]::WriteAllBytes($foreignFile, $ansiBytes)
  [IO.File]::SetLastWriteTimeUtc($foreignFile, $oldTime)
  $unrelatedFiles = @(foreach ($relative in 'R25.2\notes.txt', 'Other\Civil3DMcpPlugin.dll', 'archive\R25.1\Civil3DMcpPlugin.dll', 'Civil3DMcpPlugin.dll') {
    $file = Join-Path $case.Destination $relative
    New-Item -ItemType Directory -Force (Split-Path -Parent $file) | Out-Null
    [IO.File]::WriteAllText($file, 'unrelated')
    $file
  })
  $unrelatedSupport = @(foreach ($profile in 'C3D 2027 backup', 'AutoCAD 2027') {
    $dir = Join-Path $case.AppData "Autodesk\$profile\enu\Support"
    New-Item -ItemType Directory -Force $dir | Out-Null
    $file = Join-Path $dir 'acaddoc.lsp'
    [IO.File]::WriteAllText($file, $block)
    $file
  })
  $result = Invoke-UninstallCase $case
  Check 'all-release uninstall: 2024-2027 exits 0' ($result.ExitCode -eq 0) $result.Output
  Check 'all-release uninstall: every release removed' (@($releaseDirs | Where-Object { Test-Path $_ }).Count -eq 0)
  Check 'all-release uninstall: every language block removed' (@($markedFiles | Select-Object -Skip 1 | Where-Object { Test-Path $_ }).Count -eq 0)
  Check 'all-release uninstall: user code kept without block' ((Test-Path $userFile) -and [IO.File]::ReadAllText($userFile).Trim() -eq '(princ "user code")')
  Check 'all-release uninstall: original backup kept' ([IO.File]::ReadAllText("$userFile.c3dmcp-backup") -eq 'original backup')
  Check 'all-release uninstall: foreign file byte-identical and unwritten' ((Hex ([IO.File]::ReadAllBytes($foreignFile))) -eq (Hex $ansiBytes) -and [IO.File]::GetLastWriteTimeUtc($foreignFile) -eq $oldTime -and -not (Test-Path "$foreignFile.c3dmcp-backup"))
  Check 'all-release uninstall: unrelated destination files kept' (@($unrelatedFiles | Where-Object { -not (Test-Path $_) -or [IO.File]::ReadAllText($_) -ne 'unrelated' }).Count -eq 0)
  Check 'all-release uninstall: unrelated profiles kept' (@($unrelatedSupport | Where-Object { -not (Test-Path $_) -or [IO.File]::ReadAllText($_) -ne $block }).Count -eq 0)

  # An orphan block needs no installed DLL; an installed DLL needs no roaming support folder.
  $case = New-UninstallCase 'orphan-block'
  $marked = New-MarkedSupport $case 2025
  $result = Invoke-UninstallCase $case
  Check 'all-release uninstall: orphan block removed without destination' ($result.ExitCode -eq 0 -and -not (Test-Path $marked) -and -not (Test-Path $case.Destination)) $result.Output
  $case = New-UninstallCase 'plugin-only'
  $releaseDir = New-InstalledRelease $case 'R99.0'
  $result = Invoke-UninstallCase $case
  Check 'all-release uninstall: plugin removed without support folder' ($result.ExitCode -eq 0 -and -not (Test-Path $releaseDir)) $result.Output
  $case = New-UninstallCase 'empty'
  $result = Invoke-UninstallCase $case
  Check 'all-release uninstall: empty installation exits 0 and creates nothing' ($result.ExitCode -eq 0 -and -not (Test-Path $case.Destination) -and -not (Test-Path (Join-Path $case.AppData 'Autodesk'))) $result.Output

  # An explicit SourceDir keeps release-specific behavior, even if the build folder is absent.
  $case = New-UninstallCase 'explicit-release'
  $selectedRelease = New-InstalledRelease $case 'R25.1'
  $otherRelease = New-InstalledRelease $case 'R26.0'
  $selectedBlock = New-MarkedSupport $case 2026
  $otherBlock = New-MarkedSupport $case 2027
  $result = Invoke-UninstallCase $case (Join-Path $work 'missing\net8.0-windows')
  Check 'explicit-release uninstall: exits 0 and removes selected release' ($result.ExitCode -eq 0 -and -not (Test-Path $selectedRelease) -and -not (Test-Path $selectedBlock)) $result.Output
  Check 'explicit-release uninstall: other release and block kept' ((Test-Path "$otherRelease\Civil3DMcpPlugin.dll") -and [IO.File]::ReadAllText($otherBlock).Contains($beginMarker))

  # A locked dependency in the last release must prevent changes to all releases and startup files.
  $case = New-UninstallCase 'locked-release'
  $firstRelease = New-InstalledRelease $case 'R24.3'
  $lastRelease = New-InstalledRelease $case 'R26.0'
  $firstBlock = New-MarkedSupport $case 2024
  $lastBlock = New-MarkedSupport $case 2027
  $dependencyDir = Join-Path $lastRelease 'roslyn'
  New-Item -ItemType Directory -Force $dependencyDir | Out-Null
  $dependency = Join-Path $dependencyDir 'Microsoft.CodeAnalysis.dll'
  [IO.File]::WriteAllBytes($dependency, [byte[]](4, 5, 6))
  $firstBytes = Hex ([IO.File]::ReadAllBytes($firstBlock))
  $lastBytes = Hex ([IO.File]::ReadAllBytes($lastBlock))
  $handle = [IO.File]::Open($dependency, 'Open', 'Read', 'Read')
  try { $result = Invoke-UninstallCase $case } finally { $handle.Dispose() }
  Check 'all-release uninstall: locked release refused' ($result.ExitCode -ne 0 -and $result.Output.Contains('loaded by a running Civil 3D')) $result.Output
  Check 'all-release uninstall: locked release keeps every DLL' ((Test-Path "$firstRelease\Civil3DMcpPlugin.dll") -and (Test-Path "$lastRelease\Civil3DMcpPlugin.dll") -and (Hex ([IO.File]::ReadAllBytes($dependency))) -eq '04 05 06')
  Check 'all-release uninstall: locked release keeps every block byte-identical' ((Hex ([IO.File]::ReadAllBytes($firstBlock))) -eq $firstBytes -and (Hex ([IO.File]::ReadAllBytes($lastBlock))) -eq $lastBytes)

  # Literal paths must be checked for locks and removed even when their names contain brackets.
  $case = New-UninstallCase 'literal[paths]'
  $releaseDir = New-InstalledRelease $case 'R25.1'
  $marked = New-MarkedSupport $case 2026
  $markedBytes = Hex ([IO.File]::ReadAllBytes($marked))
  $handle = [IO.File]::Open("$releaseDir\Civil3DMcpPlugin.dll", 'Open', 'Read', 'Read')
  try { $result = Invoke-UninstallCase $case } finally { $handle.Dispose() }
  Check 'all-release uninstall: literal path lock refused' ($result.ExitCode -ne 0 -and $result.Output.Contains('loaded by a running Civil 3D')) $result.Output
  Check 'all-release uninstall: literal path lock keeps plugin and block' ((Test-Path -LiteralPath $releaseDir) -and (Hex ([IO.File]::ReadAllBytes($marked))) -eq $markedBytes)
  $result = Invoke-UninstallCase $case
  Check 'all-release uninstall: literal paths exit 0 after unlock' ($result.ExitCode -eq 0) $result.Output
  Check 'all-release uninstall: literal plugin and block removed' (-not (Test-Path -LiteralPath $releaseDir) -and -not (Test-Path -LiteralPath $marked))
} catch {
  "FAIL unexpected error: $($_.Exception.Message) at $($_.InvocationInfo.PositionMessage)"
  $failures++
} finally {
  $env:APPDATA = $savedAppData
  Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
"$failures failure(s)"
exit $failures
