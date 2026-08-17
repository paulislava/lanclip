$ErrorActionPreference = "Stop"
$fw   = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
$csc  = Join-Path $fw "csc.exe"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$out  = Join-Path $root "out"
New-Item -ItemType Directory -Force -Path $out | Out-Null

$refs = @("System.dll","System.Core.dll","System.Windows.Forms.dll","System.Drawing.dll",
          "System.Web.Extensions.dll","System.Runtime.Serialization.dll") |
        ForEach-Object { "/r:" + (Join-Path $fw $_) }

$src   = Get-ChildItem (Join-Path $root "src")   -Filter *.cs | ForEach-Object { $_.FullName }
$tests = Get-ChildItem (Join-Path $root "tests") -Filter *.cs | ForEach-Object { $_.FullName }

# Агент: консольное приложение — подкоманды status/get/pull печатают в консоль.
# Окно резидентного serve прячется настройкой задачи планировщика, а не /target:winexe.
& $csc /nologo /warnaserror- /target:exe /out:"$out\lanclipd.exe" $refs $src
if ($LASTEXITCODE -ne 0) { throw "сборка агента упала: $LASTEXITCODE" }

# Тесты: агент без Program.cs плюс тестовые файлы.
$srcNoMain = $src | Where-Object { $_ -notmatch "Program\.cs$" }
& $csc /nologo /warnaserror- /target:exe /out:"$out\lanclip-tests.exe" $refs $srcNoMain $tests
if ($LASTEXITCODE -ne 0) { throw "сборка тестов упала: $LASTEXITCODE" }

& "$out\lanclip-tests.exe"
exit $LASTEXITCODE
