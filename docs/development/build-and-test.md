# Build and test

HelixPython uses Windows Forms and targets .NET Framework 4.7.2. Build it with Visual Studio
MSBuild and the .NET Framework 4.7.2 targeting pack. `dotnet build` does not process this
solution's Windows Forms resources correctly.

## Find MSBuild

Run these commands from a Visual Studio Developer PowerShell or a regular PowerShell session with
Visual Studio installed:

```powershell
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$install = & $vswhere -latest -products '*' `
  -requires Microsoft.Component.MSBuild -property installationPath
$msbuild = Join-Path $install 'MSBuild\Current\Bin\MSBuild.exe'
```

## Build the IDE

Restore and build the IDE project in the configuration you want:

```powershell
$project = 'src\gui\desktop\HelixStudio.App\HelixStudio.App.csproj'
& $msbuild $project -t:Restore -p:Configuration=Debug -m:1 -nodereuse:false
& $msbuild $project -t:Build -p:Configuration=Debug -m:1 -nodereuse:false
& $msbuild $project -t:Build -p:Configuration=Release -m:1 -nodereuse:false
```

The executables are written to `artifacts\bin\HelixStudio.App\Debug\net472` and
`artifacts\bin\HelixStudio.App\Release\net472`.

## Build and run the tests

Build the solution, then run its unit and integration test assemblies with the repository
runsettings:

```powershell
& $msbuild 'HelixPython.sln' -t:Build -p:Configuration=Debug -m:1 -nodereuse:false
if ($LASTEXITCODE -ne 0) { throw 'Solution build failed.' }

$assemblies = Get-ChildItem 'artifacts\bin' -Recurse -Filter '*.dll' |
  Where-Object { $_.Name -match '\.(Tests|IntegrationTests)\.dll$' } |
  Where-Object { $_.FullName -match '\\Debug\\' } |
  Where-Object { $_.FullName -notmatch '\\ref\\' } |
  ForEach-Object { $_.FullName }
if (-not $assemblies) { throw 'No test assemblies were built.' }
$vstest = Join-Path $install 'Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe'
$env:HELIXSTUDIO_STATE_DIR = Join-Path $env:TEMP 'HelixPython-test-state'
& $vstest @assemblies /Settings:HelixStudio.runsettings
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
```

The test command includes the GUI integration tests. Some tests require optional tools or
machine-specific capabilities; they report as inconclusive when those preconditions are unavailable.
