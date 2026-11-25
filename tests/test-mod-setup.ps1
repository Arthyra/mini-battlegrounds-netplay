# Windows CI smoke tests for ModSetup. Only synthetic data; no game assets.
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$setup = Join-Path $root "steam-install\ModSetup.exe"
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $setup)) { throw "ModSetup.exe must be built first" }
$work = Join-Path ([IO.Path]::GetTempPath()) ("mbg-installer-ci-" + [Guid]::NewGuid().ToString("N"))
$payload = Join-Path (Split-Path $setup -Parent) "payload"
function AssertExit([int]$expected, [string[]]$arguments) {
    & $setup @arguments 1>$null 2>$null
    if ($LASTEXITCODE -ne $expected) {
        throw "Expected exit $expected, got $LASTEXITCODE for $($arguments -join ' ')"
    }
}
try {
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    $game = Join-Path $work "OriginalGame"
    $managed = Join-Path $game "CrazyShooter_Data\Managed"
    $data = Join-Path $game "Data"
    New-Item -ItemType Directory -Path $managed, $data, "$payload\tools", "$payload\server" -Force | Out-Null
    [byte[]]$bytes = @()
    $bytes += [Text.Encoding]::Unicode.GetBytes("127.0.0.1")
    $bytes += [byte[]]@(0x20,0x70,0xA8,0x00,0x00)
    $dll = Join-Path $managed "Assembly-CSharp.dll"
    [IO.File]::WriteAllBytes($dll, $bytes)
    [IO.File]::WriteAllBytes((Join-Path $data "cs_scene_102"), [byte[]]@(1,2,3))
    $digest = (Get-FileHash $dll -Algorithm SHA256).Hash.ToLowerInvariant()
    $hashFile = Join-Path $payload "supported-assemblies.sha256"
    [IO.File]::WriteAllText($hashFile, ($digest + "  Assembly-CSharp.dll" + [Environment]::NewLine))

    $target = Join-Path $work "FreshInstall"
    AssertExit 0 @("--game", $game, "--out", $target, "--check-only")
    AssertExit 5 @("--game", $game, "--out", (Join-Path $game "LAN"), "--check-only")
    AssertExit 5 @("--game", $game, "--out", $work, "--check-only")

    [IO.File]::WriteAllText($hashFile, (("0" * 64) + " Assembly-CSharp.dll" + [Environment]::NewLine))
    AssertExit 3 @("--game", $game, "--out", $target, "--check-only")
    [IO.File]::WriteAllText($hashFile, ($digest + "  Assembly-CSharp.dll" + [Environment]::NewLine))

    # Simulate a failed patch process; the installer must roll back.
    $stub = Join-Path $work "PatchStub.cs"
    [IO.File]::WriteAllText($stub, "internal class PatchStub { static int Main() { return 7; } }")
    & $csc /nologo "/out:$(Join-Path $payload 'tools\PatchLanCombat.exe')" $stub
    if ($LASTEXITCODE -ne 0) { throw "patch stub did not compile" }
    foreach ($name in @("tools\Mono.Cecil.dll", "LanRoomUI.dll", "ExtractLocalData.exe",
                          "server\server.exe", "Start Game.exe", "Updater.exe")) {
        [IO.File]::WriteAllBytes((Join-Path $payload $name), [byte[]]@(1,2,3))
    }
    AssertExit 4 @("--game", $game, "--out", $target)
    if (Test-Path $target) { throw "Failed install left final directory behind" }
    if (@(Get-ChildItem $work -Filter "FreshInstall.partial-*" -Force).Count -ne 0) {
        throw "Failed install left temporary directory behind"
    }
    Write-Host "PASS: fingerprint, path safety, existing destination and failure cleanup"
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $payload -Recurse -Force -ErrorAction SilentlyContinue
}
