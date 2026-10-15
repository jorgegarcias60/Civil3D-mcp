import { spawn } from "node:child_process";
import { readdir } from "node:fs/promises";
import { join } from "node:path";

// Pass data over stdin, never interpolate paths into PowerShell source.
export async function windowsScript(script: string, input: unknown): Promise<string> {
  return await runPowerShell(["-NoProfile", "-NonInteractive", "-EncodedCommand",
    Buffer.from(`$ErrorActionPreference = 'Stop'\n[Console]::InputEncoding = [Text.UTF8Encoding]::new($false)\n[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)\ntry {\n${script}\n} catch { [Console]::Error.WriteLine($_.Exception.ToString()); exit 1 }`, "utf16le").toString("base64")], input);
}

// A script too long for the command line (-EncodedCommand, 32767 characters) runs as a file; it reads stdin itself.
export async function windowsScriptFile(path: string, input: unknown): Promise<string> {
  return await runPowerShell(["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path], input);
}

async function runPowerShell(args: string[], input: unknown): Promise<string> {
  return await new Promise((resolve, reject) => {
    const child = spawn("powershell.exe", args, { windowsHide: true, stdio: ["pipe", "pipe", "pipe"] });
    let output = "";
    let errors = "";
    const timeout = setTimeout(() => { child.kill(); reject(new Error("Windows instance helper timed out.")); }, 20_000);
    child.on("error", error => { clearTimeout(timeout); reject(error); });
    child.stdout.on("data", d => { output += d; });
    child.stderr.on("data", d => { errors += d; });
    child.on("close", code => {
      clearTimeout(timeout);
      if (code === 0) resolve(output.trim());
      else reject(new Error(`Windows instance helper failed (${code}): ${errors.trim()}`));
    });
    child.stdin.on("error", () => { /* Exit/error reports the helper failure. */ });
    child.stdin.end(JSON.stringify(input));
  });
}

export async function protectInstanceFolders(root: string): Promise<void> {
  await windowsScript(`
$data = [Console]::In.ReadToEnd() | ConvertFrom-Json
$user = [Security.Principal.WindowsIdentity]::GetCurrent().User
foreach ($folder in @($data.root, (Join-Path $data.root 'claims'), (Join-Path $data.root 'instances'))) {
  [IO.Directory]::CreateDirectory($folder) | Out-Null
  $security = New-Object Security.AccessControl.DirectorySecurity
  $security.SetOwner($user)
  $security.SetAccessRuleProtection($true, $false)
  $rule = New-Object Security.AccessControl.FileSystemAccessRule($user, 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow')
  $security.AddAccessRule($rule)
  # Set-Acl also writes the audit (SACL) section, which needs SeSecurityPrivilege (admin);
  # SetAccessControl persists only the owner and access rules changed above.
  [IO.Directory]::SetAccessControl($folder, $security)
}`, { root });
}

export interface CivilShortcut { path: string; year: number; units: string }

export async function findCivilShortcut(year?: number, units = "Imperial", roots = [
  join(process.env.ProgramData ?? "C:\\ProgramData", "Microsoft", "Windows", "Start Menu", "Programs"),
  join(process.env.APPDATA ?? "", "Microsoft", "Windows", "Start Menu", "Programs"),
]): Promise<CivilShortcut> {
  const matches: CivilShortcut[] = [];
  const visit = async (folder: string): Promise<void> => {
    let entries;
    try { entries = await readdir(folder, { withFileTypes: true }); }
    catch (error) { if (["ENOENT", "EACCES"].includes((error as NodeJS.ErrnoException).code ?? "")) return; throw error; }
    for (const entry of entries) {
      if (entry.isSymbolicLink()) continue;
      const path = join(folder, entry.name);
      if (entry.isDirectory()) await visit(path);
      else {
        const match = /^Civil 3D (202[4-7]) (Imperial|Metric)\.lnk$/i.exec(entry.name);
        if (match && (!year || +match[1] === year) && match[2].toLowerCase() === units.toLowerCase()) {
          matches.push({ path, year: +match[1], units: match[2] });
        }
      }
    }
  };
  for (const root of roots) await visit(root);
  matches.sort((a, b) => b.year - a.year || a.path.localeCompare(b.path));
  if (!matches[0]) throw new Error(`Civil 3D ${year ?? "2024–2027"} ${units} Start-menu shortcut not found.`);
  return matches[0];
}

export function shortcutArguments(original: string, drawing: string): string {
  if (/["\r\n\0]/.test(drawing)) throw new Error("Invalid drawing path.");
  return `${original} "${drawing}"`;
}

export interface CivilLaunchProof { pid: number; startedAtUtc: string }

export async function launchedProcessAlive(pid: number, startedAtUtc: string): Promise<boolean> {
  return JSON.parse(await windowsScript(`
$data = [Console]::In.ReadToEnd() | ConvertFrom-Json
try {
  $target = Get-Process -Id $data.pid -ErrorAction Stop
  ($target.StartTime.ToUniversalTime() -eq [DateTime]::Parse($data.startedAtUtc).ToUniversalTime()) | ConvertTo-Json
} catch { 'false' }
`, { pid, startedAtUtc }));
}

export async function stopLaunchedProcess(pid: number, startedAtUtc: string): Promise<void> {
  await windowsScript(`
$data = [Console]::In.ReadToEnd() | ConvertFrom-Json
$target = Get-Process -Id $data.pid -ErrorAction Stop
try {
  # Reading Handle opens and keeps one process handle: the pid cannot be reused while it is open,
  # and StartTime and Kill() below both use that same handle.
  $null = $target.Handle
  if ($target.StartTime.ToUniversalTime() -ne [DateTime]::Parse($data.startedAtUtc).ToUniversalTime()) {
    throw 'Pending launch process identity changed. Nothing was killed.'
  }
  $target.Kill()
} finally { $target.Dispose() }
`, { pid, startedAtUtc });
}

// Also exercised with a harmless PowerShell child by the offline tests.
export const launchProcessScript = `
$start = New-Object Diagnostics.ProcessStartInfo
$start.FileName = $data.target
$start.Arguments = $data.arguments
$start.WorkingDirectory = $data.workingDirectory
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.EnvironmentVariables['CIVIL3D_MCP_LAUNCH_TOKEN'] = $data.launchToken
$child = [Diagnostics.Process]::Start($start)
try {
  $proof = @{ pid = $child.Id; startedAtUtc = $child.StartTime.ToUniversalTime().ToString('o') } | ConvertTo-Json -Compress
  $claim = [IO.File]::ReadAllText($data.claimPath) | ConvertFrom-Json
  if ($claim.launchToken -ne $data.launchToken) { throw 'Launch claim token changed before process proof.' }
  $claim | Add-Member -NotePropertyName launchPid -NotePropertyValue $child.Id
  $claim | Add-Member -NotePropertyName launchStartedAtUtc -NotePropertyValue $child.StartTime.ToUniversalTime().ToString('o')
  $claimTemp = $data.claimPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
  $claimBackup = $claimTemp + '.bak'
  try {
    [IO.File]::WriteAllText($claimTemp, ($claim | ConvertTo-Json -Compress))
    [IO.File]::Replace($claimTemp, $data.claimPath, $claimBackup)
  } finally {
    if ([IO.File]::Exists($claimTemp)) { [IO.File]::Delete($claimTemp) }
    if ([IO.File]::Exists($claimBackup)) { [IO.File]::Delete($claimBackup) }
  }
  [IO.File]::WriteAllText($data.proofPath + '.tmp', $proof)
  [IO.File]::Move($data.proofPath + '.tmp', $data.proofPath)
} finally { $child.Dispose() }
`;

export async function launchCivilShortcut(source: string, destination: string, drawing: string, launchToken: string, claimPath: string): Promise<CivilLaunchProof> {
  // Explorer starts the helper outside the server's job. Only its child inherits
  // the launch token; the installed shortcut's target, profile and language survive.
  shortcutArguments("", drawing);
  const result = await windowsScript(`
$data = [Console]::In.ReadToEnd() | ConvertFrom-Json
$shell = New-Object -ComObject WScript.Shell
$original = $shell.CreateShortcut($data.source)
$proofPath = $data.destination + '.proof.json'
# An extra AutoCAD instance creates its WebView2 cache relative to its working directory
# (".WebView2\\W.60.0.0\\<n>\\EBWebView"); the shortcut's install folder is read-only for the user,
# so agent instances start in a per-user folder instead (verified live 2026-10-15).
$launchDir = Join-Path $env:LOCALAPPDATA 'Civil3DMcpPlugin\\launch'
[IO.Directory]::CreateDirectory($launchDir) | Out-Null
$payload = @{ target = $original.TargetPath; workingDirectory = $launchDir;
  arguments = $original.Arguments + ' "' + $data.drawing + '"'; launchToken = $data.launchToken; proofPath = $proofPath; claimPath = $data.claimPath } | ConvertTo-Json -Compress
$helperPath = $data.destination + '.ps1'
$helper = '$ErrorActionPreference = "Stop"; $data = [IO.File]::ReadAllText($PSCommandPath + ".json") | ConvertFrom-Json; try {' + $data.launchScript + '} catch { [IO.File]::WriteAllText($data.proofPath + ".error", $_.Exception.ToString()); exit 1 }'
[IO.File]::WriteAllText($helperPath + '.json', $payload)
[IO.File]::WriteAllText($helperPath, $helper)
$shortcut = $shell.CreateShortcut($data.destination)
$shortcut.TargetPath = (Join-Path $PSHOME 'powershell.exe')
$shortcut.WorkingDirectory = $original.WorkingDirectory
$shortcut.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $helperPath + '"'
$shortcut.WindowStyle = 7
$shortcut.Save()
Start-Process -FilePath explorer.exe -ArgumentList ('"' + $data.destination + '"') -WindowStyle Hidden
$deadline = [DateTime]::UtcNow.AddSeconds(15)
while (-not [IO.File]::Exists($proofPath)) {
  if ([IO.File]::Exists($proofPath + '.error')) { throw [IO.File]::ReadAllText($proofPath + '.error') }
  if ([DateTime]::UtcNow -ge $deadline) { throw 'No process launch proof received. Inspect instances before retrying.' }
  Start-Sleep -Milliseconds 100
}
[Console]::Write([IO.File]::ReadAllText($proofPath))
`, { source, destination, drawing, launchToken, claimPath, launchScript: launchProcessScript });
  return JSON.parse(result) as CivilLaunchProof;
}

export async function acadPids(): Promise<number[]> {
  try {
    return (await windowsScript("@(Get-Process acad -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id) | ConvertTo-Json -Compress", null))
      .split(/\D+/).filter(Boolean).map(Number);
  } catch { return []; }
}

const startLockScript = `
$data = [Console]::In.ReadToEnd() | ConvertFrom-Json
$hash = [Security.Cryptography.SHA256]::Create()
try { $key = [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($data.lockPath.ToLowerInvariant()))).Replace('-', '') }
finally { $hash.Dispose() }
$mutex = New-Object Threading.Mutex($false, ('Local\\Civil3DMcpStart-' + $key))
$held = $false
try {
  try { $held = $mutex.WaitOne(10000) }
  catch [Threading.AbandonedMutexException] { $held = $true }
  if (-not $held) { throw 'Timed out waiting for the Civil 3D start lock mutex.' }
  $self = [Diagnostics.Process]::GetProcessById([int]$data.pid)
  $selfStart = $self.StartTime.ToUniversalTime().ToString('o')
  if ($data.action -eq 'acquire') {
    if ([IO.File]::Exists($data.lockPath)) {
      $owner = [IO.File]::ReadAllText($data.lockPath) | ConvertFrom-Json
      if (-not $owner.pid -or -not $owner.startedAtUtc) { throw 'Another Civil 3D start is in progress; its lock owner is incomplete.' }
      $ownerPid = [int]$owner.pid
      if ($ownerPid -le 0) { throw 'Another Civil 3D start is in progress; its lock owner is invalid.' }
      $ownerStart = [DateTime]::Parse($owner.startedAtUtc).ToUniversalTime()
      $stale = $false
      try {
        $existing = [Diagnostics.Process]::GetProcessById($ownerPid)
        $stale = $existing.StartTime.ToUniversalTime() -ne $ownerStart
      } catch [ArgumentException] { $stale = $true }
      if (-not $stale) { throw 'Another Civil 3D start is in progress. Retry after it finishes.' }
      [IO.File]::Delete($data.lockPath)
    }
    $lock = @{ pid = [int]$data.pid; startedAtUtc = $selfStart } | ConvertTo-Json -Compress
    $stream = [IO.File]::Open($data.lockPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
      $bytes = [Text.Encoding]::UTF8.GetBytes($lock)
      $stream.Write($bytes, 0, $bytes.Length)
    } finally { $stream.Dispose() }
  } elseif ($data.action -eq 'release') {
    if ([IO.File]::Exists($data.lockPath)) {
      $owner = [IO.File]::ReadAllText($data.lockPath) | ConvertFrom-Json
      if ([int]$owner.pid -ne [int]$data.pid -or [DateTime]::Parse($owner.startedAtUtc).ToUniversalTime() -ne [DateTime]::Parse($selfStart).ToUniversalTime()) {
        throw 'Civil 3D start lock owner changed; refusing to remove it.'
      }
      [IO.File]::Delete($data.lockPath)
    }
  } else { throw 'Invalid Civil 3D start lock action.' }
} finally {
  if ($held) { $mutex.ReleaseMutex() }
  $mutex.Dispose()
}
`;

export async function acquireStartLock(lockPath: string): Promise<void> {
  await windowsScript(startLockScript, { action: "acquire", lockPath, pid: process.pid });
}

export async function releaseStartLock(lockPath: string): Promise<void> {
  await windowsScript(startLockScript, { action: "release", lockPath, pid: process.pid });
}
