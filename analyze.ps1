$ErrorActionPreference = "Stop"

$psi = [System.Diagnostics.ProcessStartInfo]::new("dotnet", "run --project c:\lumen-viz\LumenViz\LumenViz.csproj -c Release -- --mcp")
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$psi.WorkingDirectory = "c:\lumen-viz"
$p = [System.Diagnostics.Process]::Start($psi)

function Send($msg) { $p.StandardInput.WriteLine($msg); $p.StandardInput.Flush() }
function Recv() { return $p.StandardOutput.ReadLine() }
function Tool($id, $name, $argsHash) {
    $argsJson = ($argsHash | ConvertTo-Json -Compress)
    $msg = '{"jsonrpc":"2.0","id":' + $id + ',"method":"tools/call","params":{"name":"' + $name + '","arguments":' + $argsJson + '}}'
    Send $msg
    $r = Recv
    $parsed = $r | ConvertFrom-Json
    return ($parsed.result.content | Where-Object { $_.type -eq "text" }).text
}

# Initialize
Send '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"analysis"}}}'
Recv | Out-Null

# Setup window
$null = Tool 2 "create_window" @{title="Wiki-Vote Analysis";width="1400";height="1000"}
$null = Tool 3 "load_graph" @{window="win1";path="c:\lumen-viz\data\examples\wiki-vote.txt"}
$null = Tool 4 "set_edge_alpha" @{window="win1";alpha="0.08"}
$null = Tool 5 "auto_fit" @{window="win1"}

Write-Host "=== GRAPH LOADED ==="

# 1. Top PageRank
$null = Tool 100 "set_color_mode" @{window="win1";node_mode="pagerank"}
$pr = Tool 101 "select_top_nodes" @{window="win1";metric="pagerank";count="20"} | ConvertFrom-Json
Write-Host "`n=== TOP 20 PAGERANK ==="
foreach ($n in $pr.nodes) { Write-Host ("  idx={0,5} label={1,-8} comm={2} pr={3}" -f $n.index, $n.label, $n.community, $n.pagerank) }
Start-Sleep -Seconds 2

# 2. Top In-Degree
$ind = Tool 102 "select_top_nodes" @{window="win1";metric="indegree";count="20"} | ConvertFrom-Json
Write-Host "`n=== TOP 20 IN-DEGREE ==="
foreach ($n in $ind.nodes) { Write-Host ("  idx={0,5} label={1,-8} comm={2} indeg={3}" -f $n.index, $n.label, $n.community, $n.indegree) }
Start-Sleep -Seconds 2

# 3. Top Out-Degree
$null = Tool 103 "set_color_mode" @{window="win1";node_mode="in_out_ratio"}
$outd = Tool 104 "select_top_nodes" @{window="win1";metric="outdegree";count="20"} | ConvertFrom-Json
Write-Host "`n=== TOP 20 OUT-DEGREE ==="
foreach ($n in $outd.nodes) { Write-Host ("  idx={0,5} label={1,-8} comm={2} outdeg={3}" -f $n.index, $n.label, $n.community, $n.outdegree) }
Start-Sleep -Seconds 2

# 4. Top Betweenness
$null = Tool 105 "set_color_mode" @{window="win1";node_mode="betweenness"}
$bt = Tool 106 "select_top_nodes" @{window="win1";metric="betweenness";count="20"} | ConvertFrom-Json
Write-Host "`n=== TOP 20 BETWEENNESS ==="
foreach ($n in $bt.nodes) { Write-Host ("  idx={0,5} label={1,-8} comm={2} between={3}" -f $n.index, $n.label, $n.community, $n.betweenness) }
Start-Sleep -Seconds 2

# 5. Find power brokers: nodes in BOTH top-20 PageRank AND top-20 Betweenness
$prIndices = [System.Collections.Generic.HashSet[string]]::new()
foreach ($n in $pr.nodes) { $prIndices.Add([string]$n.index) | Out-Null }
$btIndices = [System.Collections.Generic.HashSet[string]]::new()
foreach ($n in $bt.nodes) { $btIndices.Add([string]$n.index) | Out-Null }

$overlap = @()
foreach ($idx in $prIndices) {
    if ($btIndices.Contains($idx)) { $overlap += $idx }
}

Write-Host "`n=== POWER BROKERS (top PageRank AND top Betweenness) ==="
Write-Host "  Count: $($overlap.Count)"
foreach ($idx in $overlap) {
    $node = $pr.nodes | Where-Object { [string]$_.index -eq $idx } | Select-Object -First 1
    $btNode = $bt.nodes | Where-Object { [string]$_.index -eq $idx } | Select-Object -First 1
    $prVal = if ($node) { $node.pagerank } else { "?" }
    $btVal = if ($btNode) { $btNode.betweenness } else { "?" }
    Write-Host ("  idx={0,5} label={1,-8} comm={2} pr={3} between={4}" -f $idx, $node.label, $node.community, $prVal, $btVal)
}

# Select the power brokers
if ($overlap.Count -gt 0) {
    $overlapStr = $overlap -join ","
    $null = Tool 200 "set_color_mode" @{window="win1";node_mode="pagerank";edge_mode="community"}
    $null = Tool 201 "set_selection" @{window="win1";indices=$overlapStr}
    Write-Host "`n  >> Power brokers now selected and highlighted in the window <<"
}

# 6. Also check top reciprocity in a non-zero community (smaller tight groups)
Write-Host "`n=== TOP 20 RECIPROCITY (community 1 - tightest mutual-edge group) ==="
$null = Tool 300 "set_color_mode" @{window="win1";edge_mode="reciprocity"}
$recip = Tool 301 "select_top_nodes" @{window="win1";metric="reciprocity";count="20";community="1"} | ConvertFrom-Json
foreach ($n in $recip.nodes) { Write-Host ("  idx={0,5} label={1,-8} comm={2} recip={3}" -f $n.index, $n.label, $n.community, $n.reciprocity) }

Start-Sleep -Seconds 3

# 7. Show k-core: highlight the densest inner core
Write-Host "`n=== TOP 20 K-CORE ==="
$null = Tool 400 "set_color_mode" @{window="win1";node_mode="kcore";edge_mode="uniform"}
$kc = Tool 401 "select_top_nodes" @{window="win1";metric="kcore";count="20"} | ConvertFrom-Json
foreach ($n in $kc.nodes) { Write-Host ("  idx={0,5} label={1,-8} comm={2} kcore={3}" -f $n.index, $n.label, $n.community, $n.kcore) }

Write-Host "`n=== FINAL: Selecting power brokers (PageRank + Betweenness overlap) ==="
if ($overlap.Count -gt 0) {
    $null = Tool 500 "set_color_mode" @{window="win1";node_mode="pagerank";edge_mode="community"}
    $null = Tool 501 "set_selection" @{window="win1";indices=($overlap -join ",")}
}

Write-Host "`n=== ANALYSIS COMPLETE ==="
Write-Host "Window left open - press Ctrl+C to stop."

# Keep process alive so the window stays open
try { $p.WaitForExit() } catch { }
