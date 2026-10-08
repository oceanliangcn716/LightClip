# Read-only accessibility API: only LightClip's explicitly named metadata field.
# No screenshots, input simulation, clipboard contents, profile or key access.
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$expected=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs/LightClip/0.3.0/LightClip.exe'
$p=@(Get-Process LightClip -ErrorAction SilentlyContinue | Where-Object {[IO.Path]::GetFullPath($_.Path) -eq [IO.Path]::GetFullPath($expected)})
if($p.Count -ne 1 -or $p[0].MainWindowHandle -eq 0){throw 'Open LightClip settings to make its metadata field available'}
$window=[Windows.Automation.AutomationElement]::FromHandle($p[0].MainWindowHandle)
$condition=New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty,'轻剪通信证据')
$field=$window.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
if(-not $field){throw 'LightClip metadata field unavailable'}
$field.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
