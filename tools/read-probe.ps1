param(
    [string]$Path = '',
    [switch]$Timeline
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrEmpty($Path)) {
    $common = 'D:\Software\steamapps\common'
    $gameDir = Get-ChildItem -LiteralPath $common -Directory |
               Where-Object { $_.Name -like 'Darkest Dungeon*II' } |
               Select-Object -First 1
    if (-not $gameDir) { throw "game install not found under $common" }
    $Path = Join-Path $gameDir.FullName 'BepInEx\LogOutput.log'
}

if (-not (Test-Path -LiteralPath $Path)) { throw "log not found: $Path" }

# The game keeps the log open; FileShare.ReadWrite lets us read it while it runs.
$fs = [System.IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite')
try {
    $sr = New-Object System.IO.StreamReader($fs)
    $raw = $sr.ReadToEnd()
    $sr.Dispose()
} finally {
    $fs.Dispose()
}

$tagged = @()
foreach ($line in ($raw -split "`r?`n")) {
    if ($line -match '^\[[^\]]*:\s*Foresee\]\s+(.*)$') { $tagged += $matches[1] }
    elseif ($line -match '^(P |R |SCDRAW |ACDRAW |DRAWSUM |DRAW )') { $tagged += $line }
}

"=== log : $Path"
"=== lines: $($tagged.Count)"

$hooks = $tagged | Where-Object { $_ -match '^P (HOOK|PROBE|WARN)' }
if ($hooks) { ''; '--- hooks ---'; $hooks | ForEach-Object { $_ } }

$replay = $tagged | Where-Object { $_ -match '^R ' }
if ($replay) { ''; '--- replay experiment ---'; $replay | ForEach-Object { $_ } }

# ---------------- precompute summary ----------------
''; '--- precompute (per actor turn) ---'
$rows = @()
$state = '?'
$sk = 0; $all = 0; $res = 0
foreach ($line in $tagged) {
    if ($line -match '^P STATE \S+->(\S+)') { $state = $matches[1]; continue }
    if ($line -match '^SCDRAW ') { $sk++; continue }
    if ($line -match '^DRAW ') { $all++; continue }
    if ($line -match '^P ADDPOT ') {
        $res++
        if ($line -match 'skcost=(\d+)') { $skcost = [int]$matches[1] } else { $skcost = -1 }
        if ($line -match 'allcost=(\d+)') { $allcost = [int]$matches[1] } elseif ($line -match 'cost=(\d+)') { $allcost = [int]$matches[1] } else { $allcost = -1 }
        continue
    }
    if ($line -match '^P PRECOMPUTE performer=(\d+)') {
        $rows += [pscustomobject]@{
            Performer = $matches[1]
            AtState   = $state
            Results   = $res
            SkillDraws = $sk
            AllDraws  = $all
        }
        $sk = 0; $all = 0; $res = 0
    }
}
if ($rows) {
    $rows | Format-Table -AutoSize
} else {
    '(none)'
}

# ---------------- selection windows ----------------
''; '--- selection windows (in_turn_select -> in_turn_calculate) ---'
$inWindow = $false
$streams = @{}
$selects = 0
$winSkill = 0
$wins = @()
foreach ($line in $tagged) {
    if ($line -match '^P STATE \S+->in_turn_select') {
        $inWindow = $true; $streams = @{}; $selects = 0; $winSkill = 0
        continue
    }
    if ($line -match '^P STATE \S+->in_turn_calculate') {
        $inWindow = $false
        $desc = ($streams.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' '
        if ([string]::IsNullOrEmpty($desc)) { $desc = '(no draws)' }
        $wins += "selects=$selects skilldraws=$winSkill | $desc"
        continue
    }
    if (-not $inWindow) { continue }
    if ($line -match '^P SELECT') { $selects++; continue }
    if ($line -match '^DRAW ([A-Z_0-9?]+) ') {
        $s = $matches[1]
        $streams[$s] = 1 + [int]$streams[$s]
        if ($s -eq 'SKILL_CALCULATION') { $winSkill++ }
    }
}
if ($wins) { $wins | ForEach-Object { "  $_" } } else { '  (no complete window)' }

# ---------------- draws between events ----------------
''; '--- draws between probe events (top 25) ---'
$acc = @{}
$prev = 'START'
$printed = 0
foreach ($line in $tagged) {
    if ($line -match '^DRAW ([A-Z_0-9?]+) ') {
        $s = $matches[1]
        $acc[$s] = 1 + [int]$acc[$s]
        continue
    }
    if ($line -match '^P (STATE|ROUND|TURN|SELECT|PRECOMPUTE|ADDPOT)') {
        if ($acc.Count -gt 0 -and $printed -lt 25) {
            $desc = ($acc.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' '
            "  $prev  ->  $($line.Substring(0, [Math]::Min(70, $line.Length)))  [$desc]"
            $printed++
        }
        $acc = @{}
    }
}
if ($printed -eq 0) { '  (no draws observed between events)' }

# ---------------- optional raw timeline ----------------
if ($Timeline) {
    ''; '--- timeline ---'
    $tagged | Where-Object { $_ -match '^(P |SCDRAW )' } | Select-Object -First 80 | ForEach-Object { $_ }
}

''; '--- last DRAWSUM lines ---'
$tagged | Where-Object { $_ -match '^DRAWSUM ' } | Select-Object -Last 5 | ForEach-Object { $_ }
