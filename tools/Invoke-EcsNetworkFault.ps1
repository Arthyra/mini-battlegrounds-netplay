param(
    [Parameter(Mandatory=$true)][ValidateSet('TCP','UDP')][string]$Protocol,
    [Parameter(Mandatory=$true)][string]$ClientExe,
    [Parameter(Mandatory=$true)][string]$TargetHost,
    [ValidateRange(10,60)][int]$Seconds=25,
    [string]$EvidenceDirectory=(Join-Path $PSScriptRoot 'evidence')
)
$ErrorActionPreference='Stop'
$principal=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if(!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
    throw 'Run this fault script in an administrator PowerShell window.'
}
$parsedHost=$null
if(![Net.IPAddress]::TryParse($TargetHost,[ref]$parsedHost) -or
   $parsedHost.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork){
    throw 'Specify the authorized isolated test server IPv4 explicitly via -TargetHost.'
}
$program=(Resolve-Path -LiteralPath $ClientExe).ProviderPath
if([IO.Path]::GetFileName($program) -ne 'CrazyShooter.exe'){throw 'Expected the acceptance CrazyShooter.exe'}
$port=if($Protocol -eq 'TCP'){43234}else{43235}
$group='MBG-accept-'+[guid]::NewGuid().ToString('N')
$task=$group+'-cleanup'
$evidence=New-Item -ItemType Directory -Path $EvidenceDirectory -Force
$log=Join-Path $evidence.FullName ($group+'.jsonl')
function Write-FaultEvent([string]$Event){
    [ordered]@{utc=[DateTime]::UtcNow.ToString('o');event=$Event;group=$group;protocol=$Protocol;port=$port;seconds=$Seconds} |
        ConvertTo-Json -Compress | Add-Content -LiteralPath $log -Encoding UTF8
}
# Install an independent SYSTEM watchdog before changing any firewall rule.
# It removes only this run's two rules, including if the test process is killed.
$cleanup="Remove-NetFirewallRule -Group '$group' -ErrorAction SilentlyContinue; Unregister-ScheduledTask -TaskName '$task' -Confirm:`$false -ErrorAction SilentlyContinue"
$action=New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoProfile -NonInteractive -WindowStyle Hidden -Command "'+$cleanup+'"')
$trigger=New-ScheduledTaskTrigger -Once -At ((Get-Date).AddSeconds($Seconds+15))
Register-ScheduledTask -TaskName $task -Action $action -Trigger $trigger -User 'SYSTEM' -RunLevel Highest -Force | Out-Null
try {
    foreach($direction in 'Outbound','Inbound'){
        New-NetFirewallRule -DisplayName ($group+'-'+$direction) -Group $group -Direction $direction -Action Block -Enabled True -Profile Any -Protocol $Protocol -RemoteAddress $TargetHost -RemotePort $port -Program $program | Out-Null
    }
    Write-FaultEvent 'block_started'
    Write-Host "$Protocol $port blocked for $Seconds seconds. Keep both clients open."
    Start-Sleep -Seconds $Seconds
}
finally {
    Remove-NetFirewallRule -Group $group -ErrorAction SilentlyContinue
    $remaining=@(Get-NetFirewallRule -Group $group -ErrorAction SilentlyContinue)
    if($remaining.Count -eq 0){
        Write-FaultEvent 'block_removed'
        Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue
    } else {
        Write-FaultEvent 'cleanup_pending_watchdog'
        throw 'Temporary rules remain. The independent watchdog is still scheduled.'
    }
}
