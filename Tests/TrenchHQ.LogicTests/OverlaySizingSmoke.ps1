param([string]$ReportDirectory)
# Packaged UI smoke. Creates its own empty widgets/overlays and deletes only those
# records through the UI. Does not change providers, existing panels or user apps.
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing
Add-Type -TypeDefinition @'
using System;using System.Runtime.InteropServices;
public static class TrenchHQSizingNative {
 [StructLayout(LayoutKind.Sequential)] public struct Rect {public int Left,Top,Right,Bottom;}
 [StructLayout(LayoutKind.Sequential)] public struct Monitor {public int Size;public Rect Bounds,Work;public uint Flags;}
 [StructLayout(LayoutKind.Sequential)] public struct Point {public int X,Y;}
 [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
 [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd,out Rect rect);
 [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd,out Rect rect);
 [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd,ref Point point);
 [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
 [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd,uint flags);
 [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor,ref Monitor info);
 public static Rect Bounds(IntPtr hwnd){var previous=SetThreadDpiAwarenessContext(new IntPtr(-4));try{Rect rect;if(!GetWindowRect(hwnd,out rect))throw new Exception("No window bounds");return rect;}finally{SetThreadDpiAwarenessContext(previous);}}
 public static Rect Work(IntPtr hwnd){var previous=SetThreadDpiAwarenessContext(new IntPtr(-4));try{var info=new Monitor{Size=Marshal.SizeOf(typeof(Monitor))};if(!GetMonitorInfo(MonitorFromWindow(hwnd,2),ref info))throw new Exception("No monitor work area");return info.Work;}finally{SetThreadDpiAwarenessContext(previous);}}
 public static Rect Client(IntPtr hwnd){var previous=SetThreadDpiAwarenessContext(new IntPtr(-4));try{Rect rect;var point=new Point();if(!GetClientRect(hwnd,out rect)||!ClientToScreen(hwnd,ref point))throw new Exception("No client bounds");rect.Left+=point.X;rect.Right+=point.X;rect.Top+=point.Y;rect.Bottom+=point.Y;return rect;}finally{SetThreadDpiAwarenessContext(previous);}}
}
'@
$trenchhq=Get-Process TrenchHQ
if(@($trenchhq).Count -ne 1){throw 'One running TrenchHQ instance is required'}
$desc=[Windows.Automation.TreeScope]::Descendants
function Window($name) {
 $wins=[Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$trenchhq.Id))
 @($wins | Where-Object {$_.Current.Name -eq $name}) | Select-Object -First 1
}
$dashboard=Window 'TrenchHQ'
if(!$dashboard){throw 'Open the TrenchHQ dashboard first'}
function Find($root,$value,[switch]$ByName){
 $property=if($ByName){[Windows.Automation.AutomationElement]::NameProperty}else{[Windows.Automation.AutomationElement]::AutomationIdProperty}
 $root.FindFirst($desc,[Windows.Automation.PropertyCondition]::new($property,$value))
}
function Wait-Control($value,[switch]$ByName){
 for($i=0;$i -lt 50;$i++){
  $e=Find $dashboard $value -ByName:$ByName
  if(!$e){
   $windows=[Windows.Automation.AutomationElement]::RootElement.FindAll(
    [Windows.Automation.TreeScope]::Children,
    [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$trenchhq.Id))
   foreach($window in $windows){$e=Find $window $value -ByName:$ByName;if($e){break}}
  }
  if($e -and $e.Current.IsEnabled){return $e}
  Start-Sleep -Milliseconds 100
 }
 throw "Control unavailable: $value"
}
function Invoke($element){
 if(!$element -or !$element.Current.IsEnabled){throw 'Control unavailable for invocation'}
 $pattern=$null
 if($element.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern,[ref]$pattern)){([Windows.Automation.InvokePattern]$pattern).Invoke()}
 else{([Windows.Automation.SelectionItemPattern]$element.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)).Select()}
 Start-Sleep -Milliseconds 250
}
function Navigate($id){Invoke (Wait-Control $id)}
function Choose($combo,$name){
 $expand=[Windows.Automation.ExpandCollapsePattern]$combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
 $expand.Expand();Start-Sleep -Milliseconds 180
 $items=$combo.FindAll($desc,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem))
 $item=@($items | Where-Object {$_.Current.Name -eq $name})
 if($item.Count -ne 1){throw "Choice not unique: $name"}
 ([Windows.Automation.SelectionItemPattern]$item[0].GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)).Select()
 $expand.Collapse();Start-Sleep -Milliseconds 200
}
function Select-List($id,$name){
 $list=Wait-Control $id
 $items=$list.FindAll($desc,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem))
 $item=@($items | Where-Object {$_.Current.Name -eq $name -or $_.Current.Name.StartsWith($name+',')})
 if($item.Count -ne 1){throw "List selection not unique: $name"}
 ([Windows.Automation.SelectionItemPattern]$item[0].GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)).Select()
 Start-Sleep -Milliseconds 250
}
function Range($id){[Windows.Automation.RangeValuePattern](Wait-Control $id).GetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern)}
function Assert($condition,$label){if(!$condition){throw $label};Write-Output ('PASS '+$label)}
$folder=Join-Path $env:LOCALAPPDATA 'Packages/412fbd4c-a32b-4617-81a9-7b4a67093557_wrg6ggnpey9de/LocalState'
function Setup {Get-Content (Join-Path $folder 'panel_settings.json') -Raw | ConvertFrom-Json}
function Catalog {Get-Content (Join-Path $folder 'widget_catalog.json') -Raw | ConvertFrom-Json}
if(!$ReportDirectory){$ReportDirectory=Join-Path $PSScriptRoot ('bin/overlay-sizing-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))}
New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
$ReportDirectory=(Resolve-Path $ReportDirectory).Path
'Evidence: '+$ReportDirectory
$before=Setup;$beforeWidgets=Catalog
$createdWidgets=@();$createdPanels=@();$results=@()
function Snapshot($window,$name){
 $rect=[TrenchHQSizingNative]::Bounds([IntPtr]$window.Current.NativeWindowHandle)
 $bitmap=[Drawing.Bitmap]::new($rect.Right-$rect.Left,$rect.Bottom-$rect.Top)
 $graphics=[Drawing.Graphics]::FromImage($bitmap)
 try{$graphics.CopyFromScreen($rect.Left,$rect.Top,0,0,$bitmap.Size);$bitmap.Save((Join-Path $ReportDirectory ($name+'.png')),[Drawing.Imaging.ImageFormat]::Png)}finally{$graphics.Dispose();$bitmap.Dispose()}
}
try {
 Navigate 'NavWidgets'
 foreach($kind in @('Price Ticker','Wallet Watcher','X Tracker (official API)','Website')){
  $ids=@((Catalog).Widgets.Id)
  Invoke (Wait-Control 'WidgetAddButton');Invoke (Wait-Control $kind -ByName)
  $added=@((Catalog).Widgets | Where-Object {$_.Id -notin $ids})
  if($added.Count -ne 1){throw 'Expected exactly one new widget'}
  $createdWidgets+=$added[0]
 }
 Assert (!(Find $dashboard 'WebsiteHeightBox')) 'Website has no widget-owned Height control'
 # Empty website is sufficient for sizing; no external page or credentials are used.
 Navigate 'NavDesktopSetup'
 Invoke (Wait-Control 'Add panel' -ByName);Invoke (Wait-Control 'Floating overlay' -ByName)
 $createdPanels=@((Setup).Overlays | Where-Object {$_.Id -notin $before.Overlays.Id})
 if($createdPanels.Count -ne 1){throw 'Expected one test overlay'}
 $panel=$createdPanels[0]
 $cases=@($createdWidgets | Where-Object {$_.Type -ne 'priceTicker'})+@([pscustomobject]@{Name='Application window';Type='application'})+@($createdWidgets | Where-Object {$_.Type -eq 'priceTicker'})
 foreach($case in $cases){
  Choose (Wait-Control 'PanelContentSelector') $case.Name
  $fixed=$case.Type -ne 'priceTicker'
  Assert (([bool](Find $dashboard 'HeightSlider')) -eq $fixed) ($case.Type+' Height visibility')
  Assert ((Range 'WidthSlider').Current.Minimum -eq $(if($case.Type -eq 'website'){320}else{250})) ($case.Type+' width minimum')
  $heights=if($fixed){@(240,520,900)}else{@(0)}
  foreach($height in $heights){
   (Range 'WidthSlider').SetValue(350)
   if($fixed){(Range 'HeightSlider').SetValue($height)}
   Invoke (Wait-Control 'SavePanelButton')
   if((Wait-Control 'ShowHideButton').Current.Name -eq 'Show'){Invoke (Wait-Control 'ShowHideButton')}
   Start-Sleep -Milliseconds 700
   $window=Window $panel.Name
   if(!$window){throw 'Test overlay did not open'}
   $hwnd=[IntPtr]$window.Current.NativeWindowHandle
   $rect=[TrenchHQSizingNative]::Bounds($hwnd);$work=[TrenchHQSizingNative]::Work($hwnd)
   $scale=[TrenchHQSizingNative]::GetDpiForWindow($hwnd)/96.0
   $width=$rect.Right-$rect.Left;$actualHeight=$rect.Bottom-$rect.Top
   $client=[TrenchHQSizingNative]::Client($hwnd)
   Assert ([Math]::Abs($width-[Math]::Ceiling(350*$scale)) -le 2) ($case.Type+' width applies')
   if($fixed){
    $frameHeight=$actualHeight-($client.Bottom-$client.Top)
    $expected=[Math]::Min([Math]::Ceiling(($height+32)*$scale)+$frameHeight,$work.Bottom-$work.Top-16)
    Assert ([Math]::Abs($actualHeight-$expected) -le 3) ($case.Type+' height '+$height+' applies: '+$actualHeight+'px')
    Assert ($rect.Left -ge $work.Left -and $rect.Right -le $work.Right -and $rect.Top -ge $work.Top -and $rect.Bottom -le $work.Bottom) ($case.Type+' fits work area')
    $saved=@((Setup).Overlays | Where-Object {$_.Id -eq $panel.Id})[0]
    Assert ($saved.HeightDip -eq $height) ($case.Type+' saved requested height '+$height)
    $footerId=switch($case.Type){'walletActivity'{'OverlayStatusText'};'website'{'StatusText'};default{''}}
    if($footerId){$footer=Find $window $footerId;Assert ($footer -and $footer.Current.BoundingRectangle.Bottom -le $client.Bottom+1) ($case.Type+' footer stays inside client area')}
    if($case.Type -in @('xTimeline','walletActivity')){
     $scrollId=if($case.Type -eq 'xTimeline'){'FeedScroller'}else{'OverlayScroller'}
     $scroller=Find $window $scrollId
     $scrollPattern=$null
     Assert ($scroller -and $scroller.TryGetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern,[ref]$scrollPattern)) ($case.Type+' has a scrollable viewport')
     Assert ($scroller.Current.BoundingRectangle.Bottom -le $client.Bottom+1) ($case.Type+' scroll viewport is constrained')
    }
   }else{Assert ($actualHeight -lt 240*$scale) 'Ticker restores automatic compact height'}
   $results+=[pscustomobject]@{Type=$case.Type;Height=$height;WidthPx=$width;HeightPx=$actualHeight;Scale=$scale;Passed=$true}
   Snapshot $window ($case.Type+'-'+$height)
  }
 }
 # Create a second overlay using the same Website widget but different dimensions.
 $website=@($createdWidgets | Where-Object {$_.Type -eq 'website'})[0]
 Choose (Wait-Control 'PanelContentSelector') $website.Name
 (Range 'HeightSlider').SetValue(520);Invoke (Wait-Control 'SavePanelButton')
 Invoke (Wait-Control 'Add panel' -ByName);Invoke (Wait-Control 'Floating overlay' -ByName)
 $createdPanels=@((Setup).Overlays | Where-Object {$_.Id -notin $before.Overlays.Id})
 Assert ($createdPanels.Count -eq 2) 'Create adds a second test overlay'
 Choose (Wait-Control 'PanelContentSelector') $website.Name
 (Range 'HeightSlider').SetValue(300);(Range 'WidthSlider').SetValue(540);Invoke (Wait-Control 'SavePanelButton')
 Select-List 'PanelList' $panel.Name
 Assert ((Range 'HeightSlider').Current.Value -eq 520 -and (Range 'WidthSlider').Current.Value -eq 350) 'Shared widget keeps independent overlay dimensions'
 Invoke (Wait-Control 'ShowHideButton');Invoke (Wait-Control 'ShowHideButton')
 Start-Sleep -Milliseconds 500
 Assert (([TrenchHQSizingNative]::Bounds([IntPtr](Window $panel.Name).Current.NativeWindowHandle).Bottom-[TrenchHQSizingNative]::Bounds([IntPtr](Window $panel.Name).Current.NativeWindowHandle).Top) -gt 520) 'Reopening retains requested dimensions'
 'OVERLAY SIZING SMOKE PASSED'
} catch {
 $results+=[pscustomobject]@{Passed=$false;Error=$_.Exception.Message;Stack=$_.ScriptStackTrace}
 throw
} finally {
 try {
  Navigate 'NavDesktopSetup'
  foreach($created in @($createdPanels | Sort-Object Name -Descending)){
   $current=@((Setup).Overlays | Where-Object {$_.Id -eq $created.Id})
   if(!$current.Count){continue}
   if($created.Id -in $before.Overlays.Id){throw 'Refusing to delete an existing panel'}
   Select-List 'PanelList' $current[0].Name
   Invoke (Wait-Control 'Delete panel' -ByName);Invoke (Wait-Control 'Delete' -ByName)
   Start-Sleep -Milliseconds 700
  }
  Navigate 'NavWidgets'
  foreach($created in $createdWidgets){
   $current=@((Catalog).Widgets | Where-Object {$_.Id -eq $created.Id})
   if(!$current.Count){continue}
   if($created.Id -in $beforeWidgets.Widgets.Id){throw 'Refusing to delete an existing widget'}
   Select-List 'WidgetList' $current[0].Name
   Invoke (Wait-Control 'DeleteWidgetButton');Invoke (Wait-Control 'Delete' -ByName)
   Start-Sleep -Milliseconds 700
  }
  $after=Setup;$afterWidgets=Catalog
  Assert (@($after.Overlays).Count -eq @($before.Overlays).Count -and @($afterWidgets.Widgets).Count -eq @($beforeWidgets.Widgets).Count) 'All temporary records removed'
  foreach($existing in $before.Overlays){
   $current=@($after.Overlays | Where-Object {$_.Id -eq $existing.Id})[0]
   Assert ($current.WidthDip -eq $existing.WidthDip -and $current.UseApplicationWindow -eq $existing.UseApplicationWindow -and ($current.WidgetIds -join ',') -eq ($existing.WidgetIds -join ',')) 'Existing overlay content and width preserved'
  }
  Assert (($before.DockedBars | ConvertTo-Json -Depth 10 -Compress) -eq ($after.DockedBars | ConvertTo-Json -Depth 10 -Compress)) 'Screen-edge panel settings preserved'
 } finally {[IO.File]::WriteAllText((Join-Path $ReportDirectory 'results.json'),(ConvertTo-Json -InputObject @($results) -Depth 5))}
}
