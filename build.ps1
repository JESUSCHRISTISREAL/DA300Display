# Rebuilds DA300Display.exe, foo_da300display.dll and DA300Display-Setup.exe from source.
$ErrorActionPreference = 'Stop'; Set-Location $PSScriptRoot
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /target:winexe /platform:x64 /optimize+ /out:DA300Display.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll DA300Display.cs
if ($LASTEXITCODE) { throw 'app build failed' }
$msb = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
if (Test-Path 'foobar\sdk\pfc') {
  & $msb 'foobar\sdk\foobar2000\foo_da300display\foo_da300display.vcxproj' /m /nologo /v:minimal /p:Configuration=Release /p:Platform=x64 /p:PlatformToolset=v145  # also applied to the foobar2000 SDK projects, which ship set to v142
  if ($LASTEXITCODE) { throw 'component build failed' }
  Copy-Item 'foobar\sdk\foobar2000\foo_da300display\x64\Release\foo_da300display.dll' 'installer\foo_da300display.dll' -Force
} else { Write-Warning 'foobar2000 SDK not found - using existing installer\foo_da300display.dll' }
Copy-Item DA300Display.exe installer\DA300Display.exe -Force
& $csc /nologo /target:winexe /platform:x64 /optimize+ /out:DA300Display-Setup.exe /r:System.Windows.Forms.dll /r:Microsoft.CSharp.dll /r:System.Core.dll "/resource:installer\DA300Display.exe,DA300Display.exe" "/resource:installer\foo_da300display.dll,foo_da300display.dll" installer\Setup.cs
if ($LASTEXITCODE) { throw 'installer build failed' }
'Built: DA300Display.exe, DA300Display-Setup.exe'
