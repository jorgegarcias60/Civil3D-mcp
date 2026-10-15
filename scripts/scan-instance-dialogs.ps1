# Input arrives as JSON on stdin from windowsScript. This script also runs with -File.
$ErrorActionPreference = 'Stop'
[Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$data = [Console]::In.ReadToEnd() | ConvertFrom-Json
$targetPid = [int]$data.pid
if ($targetPid -le 0) { throw 'Invalid process ID.' }
$expectedStart = [DateTime]::Parse([string]$data.startedAtUtc).ToUniversalTime()

function Test-ProcessIdentity {
  $process = $null
  try {
    $process = [Diagnostics.Process]::GetProcessById($targetPid)
    return $process.StartTime.ToUniversalTime() -eq $expectedStart
  } catch { return $false }
  finally { if ($null -ne $process) { $process.Dispose() } }
}

if (-not (Test-ProcessIdentity)) {
  @{ dialogs = @(); missingXrefs = @() } | ConvertTo-Json -Depth 8 -Compress
  return
}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$deadline = [DateTime]::UtcNow.AddSeconds(8)
$maxElements = 300
$maxDepth = 5
$maxWindows = 32
$scannedWindows = 0
$visited = 0
$dialogs = New-Object 'System.Collections.Generic.List[object]'
$missing = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$windowType = [Windows.Automation.ControlType]::Window
$buttonType = [Windows.Automation.ControlType]::Button
$textType = [Windows.Automation.ControlType]::Text
$trueCondition = [Windows.Automation.Condition]::TrueCondition

function Get-SafeCurrent($element) {
  try { return $element.Current } catch [Windows.Automation.ElementNotAvailableException] { return $null }
}

# Task dialog command links show up as Pane elements of window class "Button" with no UIA pattern
# (Civil 3D 2026, "References - Not Found Files", verified live 2026-10-15).
function Test-ButtonLike($current) {
  return $current.ControlType -eq $buttonType -or
    ($current.ControlType -eq [Windows.Automation.ControlType]::Pane -and [string]$current.ClassName -eq 'Button')
}

$taskDialogSource = @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class CivilMcpTaskDialog {
  [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr hwnd);
  [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);
  [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
  [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr hwnd);
  [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
  const uint WM_COMMAND = 0x0111;
  const uint BN_CLICKED = 0;
  const uint BM_CLICK = 0x00F5;
  const uint GA_ROOT = 2;
  const uint SMTO_BLOCK_ABORTIFHUNG = 0x0001 | 0x0002;
  static string ClassOf(IntPtr h) { var b = new StringBuilder(256); GetClassName(h, b, b.Capacity); return b.ToString(); }
  static string TextOf(IntPtr h) { var b = new StringBuilder(512); GetWindowText(h, b, b.Capacity); return b.ToString(); }
  static uint PidOf(IntPtr h) { uint pid; GetWindowThreadProcessId(h, out pid); return pid; }
  static bool Closed(IntPtr dialog, int ms) {
    int end = Environment.TickCount + ms;
    while (Environment.TickCount - end < 0) { if (!IsWindow(dialog)) return true; System.Threading.Thread.Sleep(100); }
    return !IsWindow(dialog);
  }
  static string Verify(IntPtr dialog, IntPtr button, IntPtr host, int expectedPid, string expectedTitle) {
    if (dialog == IntPtr.Zero || button == IntPtr.Zero) return "Dialog or button has no window handle.";
    if (PidOf(dialog) != expectedPid || PidOf(button) != expectedPid || PidOf(host) != expectedPid) return "Window belongs to another process.";
    if (ClassOf(dialog) != "#32770" || TextOf(dialog).Trim() != expectedTitle) return "Dialog window changed.";
    if (FindWindowEx(dialog, IntPtr.Zero, "DirectUIHWND", null) == IntPtr.Zero) return "Dialog is not a task dialog.";
    if (GetAncestor(button, GA_ROOT) != dialog || GetAncestor(host, GA_ROOT) != dialog) return "Button is not inside the dialog.";
    if (ClassOf(host) != "CtrlNotifySink") return "Button host changed.";
    if (ClassOf(button) != "Button" || !TextOf(button).TrimStart().StartsWith("Ignore", StringComparison.OrdinalIgnoreCase)) return "Button changed.";
    return null;
  }
  // Returns "ok:<method>" once the dialog has closed, otherwise why it is still open or nothing was sent.
  // Task dialog command links have no control id (seen live 2026-10-15), so TDM_CLICK_BUTTON cannot target them.
  public static string ClickButton(long dialogHandle, long buttonHandle, int expectedPid, string expectedTitle) {
    var dialog = new IntPtr(dialogHandle); var button = new IntPtr(buttonHandle); var host = GetParent(button);
    var issue = Verify(dialog, button, host, expectedPid, expectedTitle);
    if (issue != null) return issue;
    IntPtr result;
    // First the notification the button itself sends when clicked: BN_CLICKED to its own CtrlNotifySink host.
    // Each command link has its own host window, so this cannot reach the other button.
    var wParam = new IntPtr((GetDlgCtrlID(button) & 0xFFFF) | (int)(BN_CLICKED << 16));
    if (SendMessageTimeout(host, WM_COMMAND, wParam, button, SMTO_BLOCK_ABORTIFHUNG, 5000, out result) == IntPtr.Zero)
      return "The button host did not accept the click within 5 seconds.";
    if (Closed(dialog, 2000)) return "ok:button_notify";
    issue = Verify(dialog, button, host, expectedPid, expectedTitle);
    if (issue != null) return issue;
    // Then BM_CLICK, the documented programmatic click of a button, sent to that button only.
    if (SendMessageTimeout(button, BM_CLICK, IntPtr.Zero, IntPtr.Zero, SMTO_BLOCK_ABORTIFHUNG, 5000, out result) == IntPtr.Zero)
      return "The button did not accept BM_CLICK within 5 seconds.";
    if (Closed(dialog, 2000)) return "ok:bm_click";
    return "The dialog stayed open after both click messages.";
  }
}
'@

function Scan-Element($element, $depth, $dialog, $recognized) {
  if ($depth -gt $maxDepth -or $visited -ge $maxElements -or [DateTime]::UtcNow -ge $deadline) { return }
  $script:visited++
  $current = Get-SafeCurrent $element
  if ($null -eq $current -or $current.ProcessId -ne $targetPid) { return }
  # A nested window has its own title and must never inherit its parent's Ignore permission.
  if ($depth -gt 0 -and $current.ControlType -eq $windowType) { return }
  $name = [string]$current.Name
  if ($name.Length -gt 260) { $name = $name.Substring(0, 260) }
  if (Test-ButtonLike $current) {
    if ($name) { [void]$dialog.buttons.Add($name) }
    if ($recognized -and $name -match '^Ignore') { [void]$dialog.ignoreButtons.Add($element) }
  }
  if ($recognized -and $current.ControlType -eq $textType -and $name -and
      $dialog.text.Count -lt 100 -and $name -notmatch '^(References - Not Found Files|Ignore|Cancel|Help)$') {
    [void]$dialog.text.Add($name)
  }
  if ($depth -ge $maxDepth) { return }
  try { $children = $element.FindAll([Windows.Automation.TreeScope]::Children, $trueCondition) }
  catch { [void]$dialog.issues.Add('Could not inspect all dialog controls.'); return }
  for ($i = 0; $i -lt $children.Count; $i++) {
    if ($visited -ge $maxElements -or [DateTime]::UtcNow -ge $deadline) { break }
    Scan-Element $children.Item($i) ($depth + 1) $dialog $recognized
  }
}

function Scan-Window($window) {
  if ([DateTime]::UtcNow -ge $deadline -or $scannedWindows -ge $maxWindows) { return }
  $script:scannedWindows++
  $script:visited = 0
  $current = Get-SafeCurrent $window
  if ($null -eq $current -or $current.ProcessId -ne $targetPid) { return }
  $title = [string]$current.Name
  if ($title.Length -gt 260) { $title = $title.Substring(0, 260) }
  $recognized = $title.Trim() -ieq 'References - Not Found Files'
  if (-not $recognized) {
    $isDialog = [string]$current.ClassName -eq '#32770'
    if (-not $isDialog) {
      $windowPattern = $null
      if ($window.TryGetCurrentPattern([Windows.Automation.WindowPattern]::Pattern, [ref]$windowPattern)) {
        try { $isDialog = ([Windows.Automation.WindowPattern]$windowPattern).Current.IsModal } catch { $isDialog = $false }
      }
    }
    # The main frame, palettes and toolbars are not dialogs; only standard or modal dialogs are reported.
    if (-not $isDialog) { return }
  }
  $dialog = @{ title = $title; className = [string]$current.ClassName;
    buttons = New-Object 'System.Collections.Generic.List[string]';
    ignoreButtons = New-Object 'System.Collections.Generic.List[object]';
    text = New-Object 'System.Collections.Generic.List[string]';
    issues = New-Object 'System.Collections.Generic.List[string]' }
  Scan-Element $window 0 $dialog $recognized
  foreach ($line in $dialog.text) {
    # Lines with a file extension or a folder are the missing references; the rest is the dialog's own text.
    if ($missing.Count -lt 100 -and ($line -match '\.(dwg|dxf|dwf|dwfx|pdf|dgn|png|jpe?g|tiff?|bmp|gif|rcp|rcs|nwd|nwc|ecw|sid)\b' -or $line -match '[\\/]')) {
      [void]$missing.Add($line)
    }
  }
  if ($recognized) {
    if ($dialog.ignoreButtons.Count -eq 1) {
      # PID reuse, changed title, and changed button ownership are checked immediately before Invoke.
      if (-not (Test-ProcessIdentity)) {
        [void]$dialog.issues.Add('Process identity changed before Ignore action.')
      } else {
        $freshWindow = Get-SafeCurrent $window
        $button = $dialog.ignoreButtons[0]
        $freshButton = Get-SafeCurrent $button
        if ($null -eq $freshWindow -or $freshWindow.ProcessId -ne $targetPid -or
            ([string]$freshWindow.Name).Trim() -ine 'References - Not Found Files' -or
            $null -eq $freshButton -or $freshButton.ProcessId -ne $targetPid -or
            -not (Test-ButtonLike $freshButton) -or ([string]$freshButton.Name) -notmatch '^Ignore') {
          [void]$dialog.issues.Add('Dialog or Ignore button changed before action.')
        } else {
          $pattern = $null
          if ($button.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
            try { ([Windows.Automation.InvokePattern]$pattern).Invoke(); $dialog.action = 'ignore_invoked'; $dialog.method = 'uia_invoke' }
            catch { [void]$dialog.issues.Add('Ignore invocation failed.') }
          } else {
            # No UIA pattern: send the button's click messages (CivilMcpTaskDialog above). Window messages to the
            # verified dialog's own button only: no keyboard, mouse or focus change.
            try {
              Add-Type -TypeDefinition $taskDialogSource -ErrorAction Stop
              $issue = [CivilMcpTaskDialog]::ClickButton([int64]$freshWindow.NativeWindowHandle,
                [int64]$freshButton.NativeWindowHandle, $targetPid, 'References - Not Found Files')
            } catch { $issue = 'Task dialog click helper failed.' }
            if ($issue -like 'ok:*') { $dialog.action = 'ignore_invoked'; $dialog.method = $issue.Substring(3) }
            else {
              $dialog.action = 'ignore_unavailable'
              [void]$dialog.issues.Add("Ignore was not sent: $issue")
            }
          }
        }
      }
    } elseif ($dialog.ignoreButtons.Count -eq 0) {
      $dialog.action = 'ignore_unavailable'
      [void]$dialog.issues.Add('Ignore button was not found.')
    } else { [void]$dialog.issues.Add('Multiple Ignore buttons found; no action taken.') }
  }
  $result = @{ title = $dialog.title; className = $dialog.className; buttons = @($dialog.buttons.ToArray()) }
  if ($recognized -and $dialog.text.Count) { $result.text = @($dialog.text | Select-Object -First 20) }
  if ($dialog.action) { $result.action = $dialog.action }
  if ($dialog.method) { $result.method = $dialog.method }
  if ($dialog.issues.Count) { $result.issues = @($dialog.issues.ToArray()) }
  [void]$dialogs.Add($result)
}

$windowListSource = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class CivilMcpProcessWindows {
  delegate bool EnumProc(IntPtr hwnd, IntPtr data);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr data);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
  // Visible top-level windows of one process, in z-order, found without sending a message to any process.
  public static long[] Of(int pid) {
    var found = new List<long>();
    EnumProc callback = (h, d) => {
      uint owner; GetWindowThreadProcessId(h, out owner);
      if (owner == (uint)pid && IsWindowVisible(h)) found.Add(h.ToInt64());
      return true;
    };
    EnumWindows(callback, IntPtr.Zero);
    GC.KeepAlive(callback);
    return found.ToArray();
  }
}
'@
Add-Type -TypeDefinition $windowListSource
# Only the launched process's own windows reach UI Automation; other processes are never queried. An owned dialog
# such as "References - Not Found Files" is a top-level window of its own, above its owner in z-order.
foreach ($handle in [CivilMcpProcessWindows]::Of($targetPid)) {
  if ([DateTime]::UtcNow -ge $deadline -or $scannedWindows -ge $maxWindows) { break }
  try { $window = [Windows.Automation.AutomationElement]::FromHandle([IntPtr]$handle) } catch { continue }
  Scan-Window $window
}
@{ dialogs = @($dialogs.ToArray()); missingXrefs = @($missing) } | ConvertTo-Json -Depth 8 -Compress
