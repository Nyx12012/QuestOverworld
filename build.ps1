param([string]$Game = 'C:\Program Files (x86)\Steam\steamapps\common\A Township Tale')
$ErrorActionPreference = 'Stop'

$here  = Split-Path -Parent $MyInvocation.MyCommand.Path
$mgd   = Join-Path $Game 'A Township Tale_Data\Managed'
$ml    = Join-Path $Game 'MelonLoader\net35'
$csc   = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out   = Join-Path $here 'build'
$dll   = Join-Path $out 'QuestOverworld.dll'
$json  = Join-Path $here 'data\forest-p1.json'
$table = Join-Path $here 'data\overworld-table.txt'
$code  = Get-ChildItem (Join-Path $here 'src') -Filter *.cs | ForEach-Object { $_.FullName }

if (-not (Test-Path $mgd)) { throw "no game at $Game, pass -Game <your A Township Tale folder>" }
if (-not (Test-Path (Join-Path $ml 'MelonLoader.dll'))) { throw "no MelonLoader in $Game" }

& py -3 (Join-Path $here 'tools\check_style.py') $here
if ($LASTEXITCODE -ne 0) { throw 'style check failed' }

New-Item -ItemType Directory -Force $out | Out-Null
$refs = @(Get-ChildItem (Join-Path $mgd '*.dll') | ForEach-Object { "/r:$($_.FullName)" })
$refs += "/r:$(Join-Path $ml 'MelonLoader.dll')"
$refs += "/r:$(Join-Path $ml '0Harmony.dll')"
& $csc /nologo /target:library /nostdlib+ /noconfig /unsafe- /warn:1 "/out:$dll" "/resource:$json,forest-p1.json" "/resource:$table,overworld-table.txt" $refs $code
if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE)" }
Get-Item $dll | Select-Object Name, Length
(Get-FileHash $dll -Algorithm SHA256).Hash
