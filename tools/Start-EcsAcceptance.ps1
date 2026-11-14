param(
    [Parameter(Mandatory=$true)][ValidateSet('pc1','pc2')][string]$Player,
    [string]$Install=$PSScriptRoot
)
$ErrorActionPreference='Stop'
$root=(Resolve-Path -LiteralPath $Install).ProviderPath
$manifest=Get-Content -LiteralPath (Join-Path $root 'acceptance-build.json') -Raw | ConvertFrom-Json
if([string]::IsNullOrWhiteSpace($manifest.host)){throw 'Acceptance manifest has no test host; rebuild with --host.'}
foreach($entry in $manifest.files){
    $path=Join-Path $root $entry.path
    if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256){
        throw ('Build hash mismatch: '+$entry.path)
    }
}
$run=Get-Date -Format 'yyyyMMdd-HHmmss'
$evidence=New-Item -ItemType Directory -Path (Join-Path $root "evidence/$Player-$run") -Force
[ordered]@{utc=[DateTime]::UtcNow.ToString('o');computer=$env:COMPUTERNAME;player=$Player;build=$manifest.version;commit=$manifest.commit;clockStatus=(& w32tm /query /status 2>&1 | Out-String)} |
    ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $evidence.FullName 'machine.json') -Encoding UTF8
$client=Join-Path $root 'client'
$exe=Join-Path $client 'CrazyShooter.exe'
$arguments=@('-screen-fullscreen','0','-screen-width','1280','-screen-height','720',('-lan-host='+$manifest.host),('-lan-profile=accept-'+$Player),'-ecs-test-login-port=43220',('-ecs-diagnostics="'+(Join-Path $evidence.FullName 'frames-connect.log')+'"'),'-logFile',('"'+(Join-Path $evidence.FullName 'unity.log')+'"'))
$game=Start-Process -FilePath $exe -WorkingDirectory $client -ArgumentList $arguments -PassThru
$game.Id | Set-Content -LiteralPath (Join-Path $evidence.FullName 'client.pid')
Write-Host "Started $Player on isolated ECS test ports 432xx. Evidence directory: $($evidence.FullName)"
Write-Host 'Both players click Ready. The host starts the battle after both are ready.'
while(!$game.HasExited){
    $connections=Get-NetTCPConnection -OwningProcess $game.Id -ErrorAction SilentlyContinue |
        Where-Object RemotePort -In @(43220,43223,43234) |
        Select-Object LocalPort,RemotePort,State
    [ordered]@{utc=[DateTime]::UtcNow.ToString('o');connections=@($connections)} |
        ConvertTo-Json -Depth 4 -Compress | Add-Content -LiteralPath (Join-Path $evidence.FullName 'connections.jsonl') -Encoding UTF8
    Start-Sleep -Seconds 2
    $game.Refresh()
}
Write-Host 'Client closed. Run Collect-EcsEvidence.ps1 to create the evidence ZIP.'
