# Baut Tests, die Release-Version (self-contained, kein .NET auf dem Zielrechner nötig) und den Installer nach .\dist
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

dotnet test SpeedLimit.slnx -nologo -v q
if ($LASTEXITCODE) { throw 'Tests fehlgeschlagen' }

Remove-Item publish -Recurse -Force -ErrorAction SilentlyContinue
# Sauber bauen: der inkrementelle WPF-Build übernimmt neue x:Name-Felder sonst manchmal nicht in den Release-Zwischenstand
dotnet clean SpeedLimit -c Release -nologo -v q | Out-Null
dotnet publish SpeedLimit -c Release --self-contained -o publish -nologo -v q
if ($LASTEXITCODE) { throw 'Publish fehlgeschlagen' }

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 nicht gefunden (winget install JRSoftware.InnoSetup)' }
& $iscc /Q installer\FlowCap.iss
if ($LASTEXITCODE) { throw 'Installer-Build fehlgeschlagen' }

Get-ChildItem dist\*.exe | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
