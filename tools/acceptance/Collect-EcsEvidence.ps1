param([string]$Install=$PSScriptRoot)
$ErrorActionPreference='Stop'
$root=(Resolve-Path -LiteralPath $Install).ProviderPath
$bundle=Join-Path $root ('ECS-evidence-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.zip')
$files=@((Join-Path $root 'evidence'),(Join-Path $root 'acceptance-build.json'))
$trace=Join-Path $root 'client/mbg-network.log'
if(Test-Path -LiteralPath $trace){$files+=$trace}
Compress-Archive -LiteralPath $files -DestinationPath $bundle
Write-Host $bundle
