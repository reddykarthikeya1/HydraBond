# Builds HydraBond.exe (needs only the .NET Framework that ships with Windows)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$fw  = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$wpf = "$fw\WPF"
& "$fw\csc.exe" /nologo /target:winexe /out:HydraBond.exe /win32icon:HydraBond.ico `
  "/r:$wpf\PresentationFramework.dll" "/r:$wpf\PresentationCore.dll" "/r:$wpf\WindowsBase.dll" `
  /r:System.Xaml.dll /r:System.Web.Extensions.dll /r:System.Core.dll HydraBondApp.cs
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "Built HydraBond.exe"
