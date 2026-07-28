param(
  [int]$Trials = 3,
  [string]$Htree = "src\Hypertree.Cli\bin\Debug\net10.0\htree.exe",
  [string]$StatusPath = "$env:APPDATA\hypertree\status.json"
)
$ErrorActionPreference = "Stop"

Add-Type -Namespace HT -Name Win -UsingNamespace System.Text -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr p);
public delegate bool EnumWindowsProc(IntPtr h, IntPtr p);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
[DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder b, int m);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, System.Text.StringBuilder b, int m);
[DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, int f);
[DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
[DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr h);
[DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool f);
[DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
[DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out int v, int s);

public static string Title(IntPtr h){int n=GetWindowTextLength(h); if(n<=0) return ""; var b=new System.Text.StringBuilder(n+1); GetWindowTextW(h,b,b.Capacity); return b.ToString();}
public static string Cls(IntPtr h){var b=new System.Text.StringBuilder(64); GetClassNameW(h,b,b.Capacity); return b.ToString();}
public static uint Pid(IntPtr h){uint p; GetWindowThreadProcessId(h,out p); return p;}
public static int Cloaked(IntPtr h){int v; return DwmGetWindowAttribute(h,14,out v,4)==0 ? v : -1;} // DWMWA_CLOAKED=14
public static bool Countable(IntPtr h, uint ownPid){
  if(!IsWindowVisible(h)) return false;
  if(GetAncestor(h,3)!=h) return false;                       // GA_ROOTOWNER
  if(GetWindowTextLength(h)==0) return false;
  long ex=(long)GetWindowLongPtr(h,-20);                       // GWL_EXSTYLE
  if((ex & 0x80)!=0) return false;                             // WS_EX_TOOLWINDOW
  uint pid; GetWindowThreadProcessId(h,out pid);
  if(pid==ownPid) return false;
  string c=Cls(h);
  if(c=="Progman"||c=="WorkerW"||c=="Shell_TrayWnd"||c=="Shell_SecondaryTrayWnd"||c=="Windows.UI.Core.CoreWindow"||c=="ApplicationManager_DesktopShellWindow") return false;
  return true;
}
public static void ForceForeground(IntPtr h){
  IntPtr fg=GetForegroundWindow();
  uint tmp; uint fgT = fg==IntPtr.Zero ? 0u : GetWindowThreadProcessId(fg, out tmp);
  uint me=GetCurrentThreadId();
  bool att = fgT!=0 && fgT!=me && AttachThreadInput(me,fgT,true);
  try { ShowWindow(h,5); BringWindowToTop(h); SetForegroundWindow(h); SetFocus(h); }
  finally { if(att) AttachThreadInput(me,fgT,false); }
}
'@

# Documented IVirtualDesktopManager (window -> desktop lookup). All calls stay inside compiled C#
# because these IUnknown-only interfaces have no IDispatch, so PowerShell can't late-bind them.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
[ComImport, Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IVDM {
  [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr h, out int onCur);
  [PreserveSig] int GetWindowDesktopId(IntPtr h, out Guid id);
  [PreserveSig] int MoveWindowToDesktop(IntPtr h, ref Guid id);
}
public static class VDM {
  static readonly IVDM M;
  static VDM(){ Type t = Type.GetTypeFromCLSID(new Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A")); M = (IVDM)Activator.CreateInstance(t); }
  public static Guid DesktopOf(IntPtr h){ Guid g; return M.GetWindowDesktopId(h, out g)==0 ? g : Guid.Empty; }
  public static bool OnCurrent(IntPtr h){ int o; return h!=IntPtr.Zero && M.IsWindowOnCurrentVirtualDesktop(h, out o)==0 && o!=0; }
}
'@

function Enum-Windows {
  $own = (Get-Process -Name hypertree -ErrorAction SilentlyContinue | Select-Object -First 1).Id
  $mine = $PID
  $list = New-Object System.Collections.Generic.List[object]
  $cb = [HT.Win+EnumWindowsProc]{
    param($h,$p)
    if (-not [HT.Win]::Countable($h, [uint32]$mine)) { return $true }
    $pid2 = [HT.Win]::Pid($h)
    if ($own -and $pid2 -eq $own) { return $true }   # skip the tray's own windows
    $g = [VDM]::DesktopOf($h)
    if ($g -eq [Guid]::Empty) { return $true }       # unresolved / pinned / all-desktops
    $list.Add([pscustomobject]@{ Hwnd=$h; Title=[HT.Win]::Title($h); Cls=[HT.Win]::Cls($h); Pid=$pid2; Desk=$g; Order=$list.Count })
    return $true
  }
  [HT.Win]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
  return $list
}

# ---- Map desktops from status.json ----
$status = Get-Content $StatusPath -Raw | ConvertFrom-Json
$deskAddr = @{}   # guid -> address string for `htree goto`
foreach ($row in $status.rows) {
  for ($i=0; $i -lt $row.desktops.Count; $i++) {
    $g = [Guid]$row.desktops[$i].id
    $addr = if ($row.kind -eq "main") { "main/$($i+1)" } else { "$($row.id)/$($i+1)" }
    $deskAddr[$g] = [pscustomobject]@{ Addr=$addr; Row=$row.name; Label=$row.desktops[$i].label }
  }
}

# ---- Discover which desktops currently host ordinary windows ----
$wins = Enum-Windows
$byDesk = $wins | Group-Object Desk
Write-Host "Desktops with ordinary windows:" -ForegroundColor Cyan
foreach ($grp in $byDesk) {
  $g = [Guid]$grp.Name
  $a = $deskAddr[$g]
  $where = if ($a) { "$($a.Row)/$($a.Label)  [goto $($a.Addr)]" } else { "(untracked $g)" }
  Write-Host ("  {0,2} win  {1}" -f $grp.Count, $where)
}

# Choose A and B: two distinct, addressable desktops each with >=1 window, preferring different rows.
$cand = $byDesk | Where-Object { $deskAddr.ContainsKey([Guid]$_.Name) } |
  ForEach-Object { [pscustomobject]@{ Desk=[Guid]$_.Name; N=$_.Count; Info=$deskAddr[[Guid]$_.Name] } }
if ($cand.Count -lt 2) { Write-Host "Need >=2 populated, addressable desktops; found $($cand.Count)." -ForegroundColor Red; exit 1 }
$A = $cand[0]
$B = ($cand | Where-Object { $_.Info.Row -ne $A.Info.Row } | Select-Object -First 1)
if (-not $B) { $B = $cand[1] }
Write-Host ""
Write-Host "A = $($A.Info.Row)/$($A.Info.Label)  (goto $($A.Info.Addr))" -ForegroundColor Green
Write-Host "B = $($B.Info.Row)/$($B.Info.Label)  (goto $($B.Info.Addr))" -ForegroundColor Green
Write-Host ""

function Goto($addr){ $out = & $Htree goto $addr "--verbose" 2>&1; return ,@($LASTEXITCODE, ($out -join ' ')) }
function W($h){ if($h -eq [IntPtr]::Zero){return "(none)"}; $t=[HT.Win]::Title($h); if($t.Length -gt 38){$t=$t.Substring(0,38)}; "$([HT.Win]::Cls($h)) '$t' pid=$([HT.Win]::Pid($h))" }
function OnCur($h){ [VDM]::OnCurrent($h) }
# Poll the real OS state until $test is true (or timeout). Returns ms waited, or -1 on timeout.
function WaitUntil([ScriptBlock]$test, [int]$timeoutMs=3000){
  $sw=[Diagnostics.Stopwatch]::StartNew()
  while($sw.ElapsedMilliseconds -lt $timeoutMs){ if(& $test){ return [int]$sw.ElapsedMilliseconds } Start-Sleep -Milliseconds 25 }
  return -1
}

$results = @()
for ($t=1; $t -le $Trials; $t++) {
  # 1. Land on A. Wait for the switch to really complete, then pick a genuinely-on-A window and focus it
  #    (simulating an external activator like Perch setting foreground).
  $gA = Goto $A.Info.Addr
  $onA = $null; $target = [IntPtr]::Zero
  WaitUntil { $script:onA = @(Enum-Windows | Where-Object { $_.Desk -eq $A.Desk -and -not [HT.Win]::IsIconic($_.Hwnd) -and [HT.Win]::Cloaked($_.Hwnd) -eq 0 } | Sort-Object Order); $script:onA.Count -gt 0 } | Out-Null
  if (-not $onA -or $onA.Count -eq 0) { Write-Host "Trial ${t}: no uncloaked window on A, skipping" -ForegroundColor Yellow; continue }
  $target = $onA[0].Hwnd
  [HT.Win]::ForceForeground($target)
  WaitUntil { [HT.Win]::GetForegroundWindow() -eq $target } 1000 | Out-Null
  $fgBefore = [HT.Win]::GetForegroundWindow()

  # 2. Jump away to B. Wait until the switch has genuinely happened — the target window becomes shell-
  #    cloaked (==2) once it's on the desktop we left — THEN measure the foreground. This removes the
  #    race between the async switch and the probe, so a "stuck" reading is real, not premature.
  $gB = Goto $B.Info.Addr
  $waited = WaitUntil { [HT.Win]::Cloaked($target) -eq 2 } 3000
  $fgAfter = [HT.Win]::GetForegroundWindow()

  $results += [pscustomobject]@{
    Trial=$t
    GotoAOk = ($gA[0] -eq 0)
    GotoBOk = ($gB[0] -eq 0)
    SwitchMs = $waited
    FgWasTargetOnA = ($fgBefore -eq $target)
    StuckOnTarget  = ($fgAfter -eq $target)
    NewFgOnB       = (OnCur $fgAfter)
    TargetCloaked  = [HT.Win]::Cloaked($target)
    NewForeground  = (W $fgAfter)
  }
}

Write-Host ""
Write-Host "==== RESULTS ($Trials trials) ====" -ForegroundColor Cyan
$results | Format-Table -AutoSize -Wrap

$pass = ($results | Where-Object { -not $_.StuckOnTarget -and $_.NewFgOnB }).Count
Write-Host ""
Write-Host "Handover succeeded in $pass / $($results.Count) trials (foreground left the stranded window AND landed on the destination desktop)." -ForegroundColor $(if($pass -eq $results.Count -and $results.Count -gt 0){"Green"}else{"Red"})
