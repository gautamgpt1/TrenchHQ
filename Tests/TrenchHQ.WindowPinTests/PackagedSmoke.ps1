param(
    [switch]$VisualStudio,
    [long]$WindowHandle,
    [ValidateSet('Current','Normal','Minimized','Maximized')][string]$StartingState='Current',
    [string]$ReportPath,
    [switch]$CaptureImages
)
# Defaults to a disposable app. -VisualStudio explicitly tests the open IDE without closing or editing it.
# Requires already-shown, empty Application window panels. Does not edit settings.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
if($CaptureImages){if(!$ReportPath){throw 'CaptureImages requires a report path'};Add-Type -AssemblyName System.Drawing}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class TrenchHQPinSmokeNative {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct Placement { public int Length, Flags, ShowCommand; public Point Minimum, Maximum; public Rect Normal; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint relation);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out int value, int size);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr GetProp(IntPtr hwnd, string name);
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] public static extern IntPtr GetStyle(IntPtr hwnd,int index);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd,uint msg,IntPtr w,IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd,StringBuilder text,int size);
    public static string Title(IntPtr hwnd) {var text=new StringBuilder(512);GetWindowText(hwnd,text,text.Capacity);return text.ToString();}
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd,StringBuilder text,int size);
    public static string WindowClass(IntPtr hwnd) {var text=new StringBuilder(256);GetClassName(hwnd,text,text.Capacity);return text.ToString();}
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr hwnd, ref Placement placement);
    [DllImport("user32.dll")] static extern bool SetWindowPlacement(IntPtr hwnd, ref Placement placement);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out UIntPtr result);
    public static Placement CapturePlacement(IntPtr hwnd) {
        var previous=SetThreadDpiAwarenessContext(new IntPtr(-4));
        try { var p=new Placement {Length=Marshal.SizeOf(typeof(Placement))}; if(!GetWindowPlacement(hwnd,ref p))throw new Exception("Cannot capture placement"); return p; }
        finally {SetThreadDpiAwarenessContext(previous);}
    }
    public static void RestorePlacement(IntPtr hwnd, Placement p) {
        var previous=SetThreadDpiAwarenessContext(new IntPtr(-4));
        try { if(!SetWindowPlacement(hwnd,ref p))throw new Exception("Cannot restore placement"); }
        finally {SetThreadDpiAwarenessContext(previous);}
    }
    public static bool Responsive(IntPtr hwnd) {UIntPtr result;return SendMessageTimeout(hwnd,0,IntPtr.Zero,IntPtr.Zero,3,1000,out result)!=IntPtr.Zero;}
    public static void RaiseHost(IntPtr hwnd) {if(!SetWindowPos(hwnd,new IntPtr(-1),0,0,0,0,0x13))throw new Exception("Cannot raise TrenchHQ panel");}
    public static void CheckStacking(IntPtr surface, IntPtr host) {
        var seen=new System.Collections.Generic.HashSet<IntPtr>();
        for(var above=GetWindow(surface,3);above!=IntPtr.Zero && seen.Add(above);above=GetWindow(above,3))
            if(above==host)throw new Exception("Panel covers pinned app. Surface="+surface+" Host="+host+" Topmost="+((GetStyle(surface,-20).ToInt64()&8)!=0));
    }
    public static void CheckLock(IntPtr hwnd) {
        var previous=SetThreadDpiAwarenessContext(new IntPtr(-4));
        try {
            var before=Bounds(hwnd);
            SetWindowPos(hwnd,IntPtr.Zero,before.Left+40,before.Top+30,before.Right-before.Left+30,before.Bottom-before.Top+30,0x14);
            if(!Bounds(hwnd).Equals(before))throw new Exception("Ordinary move/resize escaped the pin");
            foreach(var command in new[]{0xF020,0xF030,0xF120}) {
                UIntPtr result;
                if(SendMessageTimeout(hwnd,0x112,new IntPtr(command),IntPtr.Zero,3,1000,out result)==IntPtr.Zero)throw new Exception("Pinned system command did not respond");
                if(!Bounds(hwnd).Equals(before))throw new Exception("Pinned minimize/maximize/restore changed bounds");
            }
        } finally {SetThreadDpiAwarenessContext(previous);}
    }
    public static Rect Bounds(IntPtr hwnd) {
        var previous=SetThreadDpiAwarenessContext(new IntPtr(-4));
        try { Rect rect; if(!GetWindowRect(hwnd,out rect)) throw new Exception("Cannot read window bounds"); return rect; }
        finally { SetThreadDpiAwarenessContext(previous); }
    }
}
'@
$descendants=[Windows.Automation.TreeScope]::Descendants
function Find-Name($root, $name) {
    $root.FindFirst($descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$name))
}
function Invoke-Control($element) {
    if(!$element -or !$element.Current.IsEnabled){throw 'Required control is absent or disabled.'}
    ([Windows.Automation.InvokePattern]$element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)).Invoke()
}
function Wait-Control($root, $name) {
    for($attempt=0; $attempt -lt 50; $attempt++) {
        $element=Find-Name $root $name
        if($element -and $element.Current.IsEnabled){return $element}
        Start-Sleep -Milliseconds 100
    }
    throw "Control did not become ready: $name"
}
function Wait-Pin($hwnd, $attached) {
    for($attempt=0; $attempt -lt 40; $attempt++) {
        $present=[TrenchHQPinSmokeNative]::GetProp($hwnd,'TrenchHQ.WindowPin.State.v1') -ne [IntPtr]::Zero
        if($present -eq $attached){return}
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out waiting for attached=$attached"
}
$trenchhq=Get-Process TrenchHQ
if(@($trenchhq).Count -ne 1){throw 'Exactly one running TrenchHQ instance is required.'}
$windows=[Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,
    [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$trenchhq.Id))
$dashboard=@($windows | Where-Object {$_.Current.Name -eq 'TrenchHQ'})[0]
if(!$dashboard){throw 'TrenchHQ dashboard is unavailable.'}
# Close only an existing empty picker left by this test's exploratory inspection.
$cancel=Find-Name $dashboard 'Cancel'
if($cancel -and (Find-Name $dashboard 'Pinning method')){Invoke-Control $cancel; Start-Sleep -Milliseconds 250}
$panels=@($windows | Where-Object {Find-Name $_ 'Pin application window'})
if(!$panels.Count){throw 'Show at least one empty Application window panel first.'}
$vsBaseline=$null
$vsDte=$null
$child=$null
$hwnd=[IntPtr]::Zero
if($VisualStudio -and $WindowHandle){throw 'Choose VisualStudio or WindowHandle, not both'}
if($WindowHandle) {
    $hwnd=[IntPtr]::new($WindowHandle)
    $targetPid=[uint32]0
    [void][TrenchHQPinSmokeNative]::GetWindowThreadProcessId($hwnd,[ref]$targetPid)
    $target=Get-Process -Id $targetPid
    if($target.ProcessName -in @('brave','TrenchHQ','explorer') -or $target.Id -eq $PID){throw 'Excluded process: refusing to pin it'}
    if([TrenchHQPinSmokeNative]::GetProp($hwnd,'TrenchHQ.WindowPin.State.v1') -ne [IntPtr]::Zero){throw 'Target already pinned'}
    if($target.ProcessName -eq 'devenv'){$VisualStudio=$true;$WindowHandle=0}
}
if($VisualStudio) {
    $interop='C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\PublicAssemblies\Microsoft.VisualStudio.Interop.dll'
    Add-Type -Path $interop
    Add-Type -ReferencedAssemblies $interop -TypeDefinition @'
using System;
public static class TrenchHQPinSmokeDte {
    public static string Baseline(object raw) {
        var dte=(EnvDTE80.DTE2)raw;
        if(dte.Debugger.CurrentMode!=EnvDTE.dbgDebugMode.dbgDesignMode)throw new Exception("IDE must be in design mode");
        var result=dte.Solution.FullName;
        foreach(EnvDTE.Document doc in dte.Documents)result+="\n"+doc.FullName+" Saved="+doc.Saved;
        return result;
    }
}
'@
    $vsDte=[Runtime.InteropServices.Marshal]::GetActiveObject('VisualStudio.DTE.18.0')
    $vsBaseline=[TrenchHQPinSmokeDte]::Baseline($vsDte)
    $target=Get-Process devenv
    if(@($target).Count -ne 1){throw 'Exactly one Visual Studio instance is required'}
    $hwnd=$target.MainWindowHandle
    if($hwnd -eq [IntPtr]::Zero -or [TrenchHQPinSmokeNative]::GetProp($hwnd,'TrenchHQ.WindowPin.State.v1') -ne [IntPtr]::Zero){throw 'Visual Studio is absent or already pinned'}
}
elseif(!$WindowHandle) {
$start=[Diagnostics.ProcessStartInfo]::new((Join-Path $PSScriptRoot 'bin\x64\Debug\net8.0-windows\TrenchHQ.WindowPinTests.exe'),'--test-window')
$start.UseShellExecute=$false
$start.CreateNoWindow=$true
$start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
$start.RedirectStandardOutput=$true
$child=[Diagnostics.Process]::Start($start)
}
$activePanel=$null
$beforeState=$null
$results=@()
try {
    if($child) {
        $read=$child.StandardOutput.ReadLineAsync()
        if(!$read.Wait(10000)){throw 'Disposable app did not start.'}
        $hwnd=[IntPtr]::new([long]$read.Result)
    }
    $targetPid=[uint32]0
    [void][TrenchHQPinSmokeNative]::GetWindowThreadProcessId($hwnd,[ref]$targetPid)
    $target=Get-Process -Id $targetPid
    $targetStarted=$target.StartTime.ToUniversalTime().Ticks
    $beforeState=[TrenchHQPinSmokeNative]::CapturePlacement($hwnd)
    if($StartingState -ne 'Current') {
        $command=switch($StartingState){'Normal'{9};'Minimized'{6};'Maximized'{3}}
        [void][TrenchHQPinSmokeNative]::ShowWindow($hwnd,$command)
        Start-Sleep -Milliseconds 400
    }
    $original=[TrenchHQPinSmokeNative]::Bounds($hwnd)
    $expectedPlacement=[TrenchHQPinSmokeNative]::CapturePlacement($hwnd)
    $style=[TrenchHQPinSmokeNative]::GetStyle($hwnd,-16)
    foreach($panel in $panels) {
        foreach($method in @('Native lock (default)','Embedded + lock (experimental)')) {
            $currentPid=[uint32]0
            [void][TrenchHQPinSmokeNative]::GetWindowThreadProcessId($hwnd,[ref]$currentPid)
            if($currentPid -ne $targetPid -or (Get-Process -Id $targetPid).StartTime.ToUniversalTime().Ticks -ne $targetStarted){throw 'Test target identity changed'}
            $activePanel=$panel
            Invoke-Control (Find-Name $panel 'Pin application window')
            Start-Sleep -Milliseconds 300
            $combo=Find-Name $dashboard 'Pinning method'
            if(!$combo){throw 'Pinning method selector not found.'}
            if(!(Find-Name $dashboard "Start with Native lock. If an app doesn't work properly, unpin it and try the other method.")){throw 'Method help is missing.'}
            $expand=[Windows.Automation.ExpandCollapsePattern]$combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
            $expand.Expand()
            Start-Sleep -Milliseconds 150
            $methodOptions=$combo.FindAll($descendants,[Windows.Automation.PropertyCondition]::new(
                [Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem))
            if($methodOptions.Count -ne 2 -or (Find-Name $dashboard 'Embedded (experimental)')){throw 'The method selector must expose exactly the two locked options.'}
            $option=$dashboard.FindFirst($descendants,[Windows.Automation.AndCondition]::new(
                [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$method),
                [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem)))
            if(!$option){throw "Method missing: $method"}
            ([Windows.Automation.SelectionItemPattern]$option.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)).Select()
            $expand.Collapse()
            Start-Sleep -Milliseconds 200
            Invoke-Control (Find-Name $dashboard 'Refresh list')
            Start-Sleep -Milliseconds 200
            $items=$dashboard.FindAll($descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem))
            $targetLabel=$target.ProcessName+' '+[char]0x2014+' '+[TrenchHQPinSmokeNative]::Title($hwnd)
            $choice=@($items | Where-Object {
                if($VisualStudio){$_.Current.Name -like '*devenv*Microsoft Visual Studio*'}
                elseif($WindowHandle){$_.Current.Name -eq $targetLabel}
                else {$_.Current.Name -like '*TrenchHQ.WindowPinTests*TrenchHQ temporary window-pin test*'}
            })
            if($choice.Count -ne 1){
                $testNames=@($items | Where-Object {$_.Current.Name -like '*WindowPin*'} | ForEach-Object {$_.Current.Name}) -join '; '
                throw ('The test target is not uniquely available in the picker: ' + $testNames)
            }
            ([Windows.Automation.SelectionItemPattern]$choice[0].GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)).Select()
            Start-Sleep -Milliseconds 100
            if($method -eq 'Embedded + lock (experimental)') {
                $expand.Expand()
                Start-Sleep -Milliseconds 100
                $embedded=Find-Name $combo 'Embedded + lock (experimental)'
                if([TrenchHQPinSmokeNative]::WindowClass($hwnd) -eq 'ApplicationFrameWindow') {
                    $native=Find-Name $combo 'Native lock (default)'
                    if(!$embedded -or $embedded.Current.IsEnabled -or !([Windows.Automation.SelectionItemPattern]$native.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)).Current.IsSelected){throw 'Windows-hosted frame did not switch to Native lock with embedding disabled'}
                    $expand.Collapse()
                    Start-Sleep -Milliseconds 200
                    if(!(Find-Name $dashboard 'This Windows-hosted app uses Native lock.')){throw 'Native-only explanation is missing'}
                    # Switching to a regular app must re-enable embedding, without pinning either app.
                    $other=@($items | Where-Object {$_.Current.Name -like 'WindowsTerminal*' -or $_.Current.Name -like 'devenv*'})
                    if($other.Count) {
                        ([Windows.Automation.SelectionItemPattern]$other[0].GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)).Select()
                        $expand.Expand()
                        Start-Sleep -Milliseconds 100
                        if(!(Find-Name $combo 'Embedded + lock (experimental)').Current.IsEnabled){throw 'Embedding stayed disabled for a regular app'}
                        $expand.Collapse()
                        Start-Sleep -Milliseconds 200
                    }
                    Invoke-Control (Find-Name $dashboard 'Cancel')
                    $unchanged=[TrenchHQPinSmokeNative]::CapturePlacement($hwnd)
                    if([TrenchHQPinSmokeNative]::GetProp($hwnd,'TrenchHQ.WindowPin.State.v1') -ne [IntPtr]::Zero -or !([TrenchHQPinSmokeNative]::Bounds($hwnd)).Equals($original) -or [TrenchHQPinSmokeNative]::GetStyle($hwnd,-16) -ne $style -or $unchanged.ShowCommand -ne $expectedPlacement.ShowCommand -or !$unchanged.Normal.Equals($expectedPlacement.Normal)){throw 'Unavailable method changed the app'}
                    $results+=[pscustomobject]@{App=$target.ProcessName;Pid=$targetPid;Hwnd=$hwnd.ToInt64();State=$StartingState;Panel=$panel.Current.Name;Method=$method;Passed=$null;Outcome='Unavailable';GuardPassed=$true;Error=$null}
                    Write-Output ('PASS availability guard '+$target.ProcessName+' / '+$StartingState+' / '+$panel.Current.Name+' / embedding disabled, Native selected, app unchanged')
                    $activePanel=$null
                    continue
                }
                if(!$embedded -or !$embedded.Current.IsEnabled -or !([Windows.Automation.SelectionItemPattern]$embedded.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)).Current.IsSelected){throw 'Embedding is unavailable or not selected for a regular app'}
                $expand.Collapse()
                Start-Sleep -Milliseconds 200
            }
            Invoke-Control (Find-Name $dashboard 'Pin')
            Wait-Pin $hwnd $true
            # Layout/activation can raise TrenchHQ while asynchronous pin verification is pending.
            [TrenchHQPinSmokeNative]::RaiseHost([IntPtr]$panel.Current.NativeWindowHandle)
            $unpin=Wait-Control $panel 'Unpin application window'
            Start-Sleep -Milliseconds 600
            $parent=[TrenchHQPinSmokeNative]::GetParent($hwnd)
            if(($parent -eq [IntPtr]::Zero) -ne ($method -eq 'Native lock (default)')){throw 'Wrong parent for selected method.'}
            $surface=if($parent -eq [IntPtr]::Zero){$hwnd}else{$parent}
            $cloaked=0
            if([TrenchHQPinSmokeNative]::DwmGetWindowAttribute($surface,14,[ref]$cloaked,4) -ne 0 -or $cloaked -ne 0){throw 'Pinned surface is hidden by Windows.'}
            $actual=[TrenchHQPinSmokeNative]::Bounds($surface)
            $hostBounds=[TrenchHQPinSmokeNative]::Bounds([IntPtr]$panel.Current.NativeWindowHandle)
            if($actual.Left -lt $hostBounds.Left -or $actual.Top -lt $hostBounds.Top -or $actual.Right -gt $hostBounds.Right -or $actual.Bottom -gt $hostBounds.Bottom){throw 'Pinned surface escaped host bounds.'}
            [TrenchHQPinSmokeNative]::CheckLock($hwnd)
            if(![TrenchHQPinSmokeNative]::Responsive($hwnd)){throw 'Pinned app is unresponsive'}
            [TrenchHQPinSmokeNative]::CheckStacking($surface,[IntPtr]$panel.Current.NativeWindowHandle)
            $screenshot=$null
            if($CaptureImages) {
                $screenshot=[IO.Path]::ChangeExtension($ReportPath,$null)+'-'+($panel.Current.Name -replace '[^A-Za-z0-9]','')+'-'+($method -replace '[^A-Za-z0-9]','')+'.png'
                $bitmap=[Drawing.Bitmap]::new($actual.Right-$actual.Left,$actual.Bottom-$actual.Top)
                $graphics=[Drawing.Graphics]::FromImage($bitmap)
                try {
                    $graphics.CopyFromScreen($actual.Left,$actual.Top,0,0,$bitmap.Size)
                    $bitmap.Save($screenshot,[Drawing.Imaging.ImageFormat]::Png)
                } finally {$graphics.Dispose();$bitmap.Dispose()}
            }
            Invoke-Control $unpin
            Wait-Pin $hwnd $false
            Start-Sleep -Milliseconds 200
            if([TrenchHQPinSmokeNative]::GetParent($hwnd) -ne [IntPtr]::Zero -or !([TrenchHQPinSmokeNative]::Bounds($hwnd)).Equals($original) -or [TrenchHQPinSmokeNative]::GetStyle($hwnd,-16) -ne $style){
                $actualRestore=[TrenchHQPinSmokeNative]::Bounds($hwnd)|ConvertTo-Json -Compress
                $expectedRestore=$original|ConvertTo-Json -Compress
                throw ('Unpin restoration mismatch. Actual='+$actualRestore+' Expected='+$expectedRestore+' Style='+[TrenchHQPinSmokeNative]::GetStyle($hwnd,-16).ToInt64().ToString('X')+' ExpectedStyle='+$style.ToInt64().ToString('X')+' Parent='+[TrenchHQPinSmokeNative]::GetParent($hwnd))
            }
            $restored=[TrenchHQPinSmokeNative]::CapturePlacement($hwnd)
            if($restored.ShowCommand -ne $expectedPlacement.ShowCommand -or !$restored.Normal.Equals($expectedPlacement.Normal)){throw 'Unpin did not restore window state or normal placement'}
            if($VisualStudio -and [TrenchHQPinSmokeDte]::Baseline($vsDte) -ne $vsBaseline){throw 'Visual Studio document baseline changed'}
            $results+=[pscustomobject]@{App=$target.ProcessName;Pid=$targetPid;Hwnd=$hwnd.ToInt64();State=$StartingState;Panel=$panel.Current.Name;Method=$method;Passed=$true;Outcome='Pinned';Error=$null;Screenshot=$screenshot}
            Write-Output ('PASS '+$target.ProcessName+' / '+$StartingState+' / '+$panel.Current.Name+' / '+$method+' / picker, visible containment, movement lock, responsiveness, unpin restoration')
            $activePanel=$null
        }
    }
} catch {
    Write-Output $_.ScriptStackTrace
    $results+=[pscustomobject]@{App=$target.ProcessName;Pid=$targetPid;Hwnd=$hwnd.ToInt64();State=$StartingState;Panel=if($activePanel){$activePanel.Current.Name}else{''};Method=$method;Passed=$false;Error=$_.Exception.Message}
    throw
} finally {
  try {
    $cancel=Find-Name $dashboard 'Cancel'
    if($cancel -and (Find-Name $dashboard 'Pinning method')){Invoke-Control $cancel}
    if($activePanel -and [TrenchHQPinSmokeNative]::GetProp($hwnd,'TrenchHQ.WindowPin.State.v1') -ne [IntPtr]::Zero){Invoke-Control (Wait-Control $activePanel 'Unpin application window')}
    if($beforeState -and !$child) {
        Wait-Pin $hwnd $false
        [TrenchHQPinSmokeNative]::RestorePlacement($hwnd,$beforeState)
    }
    if($child) {
        if($hwnd -ne [IntPtr]::Zero){[void][TrenchHQPinSmokeNative]::PostMessage($hwnd,0x10,[IntPtr]::Zero,[IntPtr]::Zero)}
        if(!$child.WaitForExit(3000)){$child.Kill()} # Only the disposable process created above, never Visual Studio.
        $child.Dispose()
    }
    if($VisualStudio -and [TrenchHQPinSmokeDte]::Baseline($vsDte) -ne $vsBaseline){throw 'Visual Studio document baseline changed'}
  } finally {
    if($ReportPath){[IO.File]::WriteAllText($ReportPath,(ConvertTo-Json -InputObject @($results) -Depth 5))}
  }
}
