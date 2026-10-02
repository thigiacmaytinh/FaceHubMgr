$ErrorActionPreference = 'Stop'

$path = '\GPU Engine(*)\Utilization Percentage'

# Rate-style counters need more than one sample. Get-Counter handles the interval
# and returns the second sample, which is the one we report.
$sample = Get-Counter -Counter $path -SampleInterval 1 -MaxSamples 2 |
    Select-Object -Last 1

$rows = foreach ($counter in $sample.CounterSamples) {
    $instance = $counter.InstanceName
    $type = if ($instance -match 'engtype_(.+)$') { $Matches[1] } else { 'Unknown' }

    [pscustomobject]@{
        Instance = $instance
        EngineType = $type
        Utilization = [math]::Round([math]::Max(0, [math]::Min(100, $counter.CookedValue)), 2)
    }
}

$rows |
    Sort-Object EngineType, Utilization -Descending |
    Format-Table EngineType, Utilization, Instance -AutoSize

$cudaRows = $rows | Where-Object { $_.EngineType -match '^CUDA($|_)' }
if (-not $cudaRows) {
    $cudaRows = $rows | Where-Object { $_.EngineType -match '^Compute($|_)' }
}

if ($cudaRows) {
    $cudaMax = ($cudaRows | Measure-Object -Property Utilization -Maximum).Maximum
    Write-Host "`nCUDA/Compute engine visible. Highest sampled instance: $cudaMax%"
} else {
    Write-Host "`nNo CUDA/Compute engine is exposed by the current WDDM driver configuration."
}
