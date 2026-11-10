<#
.SYNOPSIS
	Measures the CPU, GPU and memory used by the running Arka Hyte instance.

.DESCRIPTION
	Samples ArkaHyte.exe and every WebView2 process it spawned (browser, GPU, renderer, utilities)
	for the requested duration, then prints one line per process plus the total.
	Other applications' WebView2 processes are ignored: only descendants of ArkaHyte.exe count.

	CPU is reported in % of one logical core (100 % = one core fully busy) and in % of the whole
	machine, which is what Task Manager shows. Each process's CPU time is spread over the whole
	run, so short-lived processes count for what they really cost and the lines add up to the total. GPU is the busiest engine (3D, Copy, Video...)
	over the run, like Task Manager. Memory columns:
		Private WS  private working set at the end, the "Memory" column of Task Manager
		Peak WS     highest working set seen during the run (shared pages included)
		Private     committed private bytes at the end

	Run it while the display is in the state to measure; switch the tray menu "demo mode" on to
	measure the stress scenario.

.EXAMPLE
	.\Measure-ArkaHyte.ps1
	.\Measure-ArkaHyte.ps1 -Seconds 300 -OutFile bench.json
#>
[CmdletBinding()]
param(
	[ValidateRange(2, 86400)]
	[int]$Seconds = 60,
	[ValidateRange(0.5, 60)]
	[double]$Interval = 1,
	# Optional JSON report (summary + per-process lines)
	[string]$OutFile = ''
)

$ErrorActionPreference = 'Stop'

# Budget from CLAUDE.md: the display runs permanently and must stay around this load
$cpuTargetPercentOfCore = 16
$coreCount = [Environment]::ProcessorCount

function Get-ProcessRole([object]$process, [int]$rootId) {
	if ($process.ProcessId -eq $rootId) {
		return 'host'
	}
	if ($process.Name -ne 'msedgewebview2.exe') {
		# e.g. the short-lived ArkaHyte.exe that lists applications for the shortcut editor
		return [IO.Path]::GetFileNameWithoutExtension($process.Name) + ' (child)'
	}
	$commandLine = [string]$process.CommandLine
	if ($commandLine -notmatch '--type=(\S+)') {
		return 'webview-browser'
	}
	$role = $Matches[1]
	if ($commandLine -match '--utility-sub-type=(\S+)') {
		# network.mojom.NetworkService -> NetworkService
		$role += ' ' + ($Matches[1] -split '\.')[-1]
	}
	return $role
}

# ArkaHyte.exe and its descendants; creation dates guard against reused parent ids
function Get-ArkaHyteTree {
	$all = @(Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, Name, CommandLine, CreationDate)
	$root = $all | Where-Object Name -eq 'ArkaHyte.exe' | Select-Object -First 1
	if (-not $root) {
		return @()
	}
	$tree = New-Object System.Collections.Generic.List[object]
	$queue = New-Object System.Collections.Generic.Queue[object]
	$queue.Enqueue($root)
	while ($queue.Count -gt 0) {
		$parent = $queue.Dequeue()
		$tree.Add([pscustomobject]@{
			Id = [int]$parent.ProcessId
			Role = Get-ProcessRole $parent ([int]$root.ProcessId)
		})
		$all | Where-Object { $_.ParentProcessId -eq $parent.ProcessId -and $_.CreationDate -ge $parent.CreationDate } |
			ForEach-Object { $queue.Enqueue($_) }
	}
	return $tree
}

# Raw GPU engine running times (100 ns units) for the given processes, keyed by counter instance
function Get-GpuRunningTimes([int[]]$ids) {
	$pattern = '^pid_(' + ($ids -join '|') + ')_.*engtype_(.+)$'
	$times = @{}
	try {
		foreach ($engine in Get-CimInstance Win32_PerfRawData_GPUPerformanceCounters_GPUEngine -Property Name, UtilizationPercentage, Timestamp_Sys100NS) {
			if ($engine.Name -match $pattern) {
				$times[$engine.Name] = [pscustomobject]@{
					Id = [int]$Matches[1]
					EngineType = $Matches[2]
					Running = [double]$engine.UtilizationPercentage
					Timestamp = [double]$engine.Timestamp_Sys100NS
				}
			}
		}
	}
	catch {
		# GPU counters missing (old driver, remote session): GPU column stays empty
	}
	return $times
}

# GPU % of the busiest engine type between two snapshots, optionally for one process only
function Measure-GpuPercent([hashtable]$first, [hashtable]$last, [int]$id = 0) {
	$busyByType = @{}
	$elapsed = 0.0
	foreach ($name in $last.Keys) {
		$end = $last[$name]
		$start = $first[$name]
		if (-not $start -or ($id -ne 0 -and $end.Id -ne $id)) {
			continue
		}
		$busyByType[$end.EngineType] = [double]$busyByType[$end.EngineType] + ($end.Running - $start.Running)
		$elapsed = [Math]::Max($elapsed, $end.Timestamp - $start.Timestamp)
	}
	if ($elapsed -le 0 -or $busyByType.Count -eq 0) {
		return $null
	}
	$busiest = ($busyByType.Values | Measure-Object -Maximum).Maximum
	return [Math]::Min(100, 100 * $busiest / $elapsed)
}

function Format-Number($value, [int]$decimals = 1) {
	if ($null -eq $value) {
		return '--'
	}
	return ([double]$value).ToString("F$decimals", [Globalization.CultureInfo]::InvariantCulture)
}

if (-not (Get-ArkaHyteTree)) {
	throw 'ArkaHyte.exe is not running.'
}

Write-Host "Measuring Arka Hyte for $Seconds s (sample every $Interval s, $coreCount logical cores)..."

# Per process: first/last CPU time and wall clock, peak working set, last memory figures
$stats = @{}
$totalPeakCpu = 0.0
$previousTotalCpu = $null
$previousWall = $null
$gpuFirst = $null
$gpuLast = @{}
$clock = [Diagnostics.Stopwatch]::StartNew()
$runStart = Get-Date
$deadline = [TimeSpan]::FromSeconds($Seconds)

while ($true) {
	$tree = @(Get-ArkaHyteTree)
	if ($tree.Count -eq 0) {
		throw 'ArkaHyte.exe exited during the measurement.'
	}
	$wall = $clock.Elapsed.TotalMilliseconds
	$totalCpu = 0.0
	foreach ($node in $tree) {
		$process = Get-Process -Id $node.Id -ErrorAction SilentlyContinue
		if (-not $process) {
			continue
		}
		$cpu = $process.TotalProcessorTime.TotalMilliseconds
		$entry = $stats[$node.Id]
		if (-not $entry) {
			# A process started during the run owes all its CPU time to the run
			$firstCpu = $cpu
			if ($process.StartTime -gt $runStart) {
				$firstCpu = 0
			}
			$entry = [pscustomobject]@{
				Id = $node.Id; Role = $node.Role
				FirstCpu = $firstCpu; LastCpu = $cpu
				PeakWorkingSet = 0L; PrivateBytes = 0L
			}
			$stats[$node.Id] = $entry
		}
		$entry.LastCpu = $cpu
		$entry.PeakWorkingSet = [Math]::Max($entry.PeakWorkingSet, $process.WorkingSet64)
		$entry.PrivateBytes = $process.PrivateMemorySize64
		$totalCpu += $cpu
	}

	$gpuNow = Get-GpuRunningTimes ($tree | ForEach-Object Id)
	if ($null -eq $gpuFirst) {
		$gpuFirst = $gpuNow
	}
	else {
		# Engines of processes started mid-run only count from their first snapshot
		foreach ($name in $gpuNow.Keys) {
			if (-not $gpuFirst.ContainsKey($name)) {
				$gpuFirst[$name] = $gpuNow[$name]
			}
		}
	}
	$gpuLast = $gpuNow

	# Instant total load; processes appearing or exiting between samples skew one interval only
	if ($null -ne $previousTotalCpu -and $wall -gt $previousWall) {
		$instant = [Math]::Max(0, 100 * ($totalCpu - $previousTotalCpu) / ($wall - $previousWall))
		$totalPeakCpu = [Math]::Max($totalPeakCpu, $instant)
		$memory = ($stats.Values | Measure-Object PrivateBytes -Sum).Sum / 1MB
		Write-Host -NoNewline ("`r  {0,4:F0} s   CPU {1,6} % of one core   private {2,6} MB   " -f `
			$clock.Elapsed.TotalSeconds, (Format-Number $instant), (Format-Number $memory 0))
	}
	$previousTotalCpu = $totalCpu
	$previousWall = $wall

	if ($clock.Elapsed -ge $deadline) {
		break
	}
	Start-Sleep -Milliseconds ([int]($Interval * 1000))
}
Write-Host ''

# Private working set (Task Manager "Memory") comes from the perf counters, read once at the end
$privateWorkingSet = @{}
try {
	$filter = ($stats.Keys | ForEach-Object { "IDProcess = $_" }) -join ' OR '
	foreach ($counter in Get-CimInstance Win32_PerfRawData_PerfProc_Process -Filter $filter -Property IDProcess, WorkingSetPrivate) {
		$privateWorkingSet[[int]$counter.IDProcess] = [double]$counter.WorkingSetPrivate
	}
}
catch {
	Write-Warning "Private working set unavailable: $($_.Exception.Message)"
}

$runMs = $clock.Elapsed.TotalMilliseconds
$rows = foreach ($entry in $stats.Values) {
	$cpuAverage = 100 * ($entry.LastCpu - $entry.FirstCpu) / $runMs
	$privateWs = $null
	if ($privateWorkingSet.ContainsKey($entry.Id)) {
		$privateWs = $privateWorkingSet[$entry.Id] / 1MB
	}
	[pscustomobject]@{
		Process = $entry.Role
		PID = $entry.Id
		'CPU % core' = $cpuAverage
		'GPU %' = Measure-GpuPercent $gpuFirst $gpuLast $entry.Id
		'Private WS MB' = $privateWs
		'Peak WS MB' = $entry.PeakWorkingSet / 1MB
		'Private MB' = $entry.PrivateBytes / 1MB
	}
}
$rows = @($rows | Sort-Object { if ($_.Process -eq 'host') { 0 } else { 1 } }, Process, PID)

$sum = { param($column) ($rows | Where-Object { $null -ne $_.$column } | Measure-Object $column -Sum).Sum }
$totalCpuAverage = & $sum 'CPU % core'
$totalGpu = Measure-GpuPercent $gpuFirst $gpuLast
$total = [pscustomobject]@{
	Process = 'TOTAL'
	PID = $null
	'CPU % core' = $totalCpuAverage
	'GPU %' = $totalGpu
	'Private WS MB' = & $sum 'Private WS MB'
	'Peak WS MB' = & $sum 'Peak WS MB'
	'Private MB' = & $sum 'Private MB'
}

@($rows) + $total | Format-Table -AutoSize @(
	'Process', 'PID',
	@{ Label = 'CPU % core'; Expression = { Format-Number $_.'CPU % core' }; Align = 'Right' },
	@{ Label = 'GPU %'; Expression = { Format-Number $_.'GPU %' }; Align = 'Right' },
	@{ Label = 'Private WS MB'; Expression = { Format-Number $_.'Private WS MB' 0 }; Align = 'Right' },
	@{ Label = 'Peak WS MB'; Expression = { Format-Number $_.'Peak WS MB' 0 }; Align = 'Right' },
	@{ Label = 'Private MB'; Expression = { Format-Number $_.'Private MB' 0 }; Align = 'Right' }
) | Out-Host

$machinePercent = $totalCpuAverage / $coreCount
$verdict = 'within'
if ($totalCpuAverage -gt $cpuTargetPercentOfCore) {
	$verdict = 'ABOVE'
}
Write-Host ("CPU average {0} % of one core = {1} % of the machine (peak {2} % of one core), {3} the {4} % target" -f `
	(Format-Number $totalCpuAverage), (Format-Number $machinePercent 2), (Format-Number $totalPeakCpu), $verdict, $cpuTargetPercentOfCore)
Write-Host ("GPU {0} %   Memory (Task Manager) {1} MB" -f (Format-Number $totalGpu), (Format-Number $total.'Private WS MB' 0))

if ($OutFile) {
	$report = [ordered]@{
		date = (Get-Date).ToString('o')
		seconds = $Seconds
		interval = $Interval
		logicalCores = $coreCount
		cpuPercentOfCore = $totalCpuAverage
		cpuPercentOfMachine = $machinePercent
		cpuPeakPercentOfCore = $totalPeakCpu
		gpuPercent = $totalGpu
		privateWorkingSetMB = $total.'Private WS MB'
		peakWorkingSetMB = $total.'Peak WS MB'
		privateMB = $total.'Private MB'
		processes = $rows
	}
	$report | ConvertTo-Json -Depth 4 | Set-Content -Path $OutFile -Encoding UTF8
	Write-Host "Report written to $OutFile"
}
