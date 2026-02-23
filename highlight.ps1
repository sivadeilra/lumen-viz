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
    $line = Recv
    if (-not $line) { Write-Host "ERROR: null response for $name"; return $null }
    $parsed = $line | ConvertFrom-Json
    if ($parsed.error) { Write-Host "ERROR: $($parsed.error.message)"; return $null }
    return ($parsed.result.content | Where-Object { $_.type -eq "text" }).text
}

# Initialize
Send '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"demo"}}}'
Recv | Out-Null

# Setup
$null = Tool 2 "create_window" @{title="Wiki-Vote: Power Brokers";width="1400";height="1000"}
$null = Tool 3 "load_graph" @{window="win1";path="c:\lumen-viz\data\examples\wiki-vote.txt"}
$null = Tool 4 "set_edge_alpha" @{window="win1";alpha="0.08"}
$null = Tool 5 "auto_fit" @{window="win1"}

# Set color mode: pagerank nodes + community edges
$null = Tool 6 "set_color_mode" @{window="win1";node_mode="pagerank";edge_mode="community"}

# Select the 6 POWER BROKERS (top in both PageRank AND Betweenness)
$null = Tool 7 "set_selection" @{window="win1";indices="326,409,686,656,666,2"}
Write-Host "Selected 6 power brokers (hot-white nodes with glow)"
Write-Host ""
Write-Host "These nodes rank in BOTH the top-20 PageRank AND top-20 Betweenness:"
Write-Host "  idx=326  label=4037  PageRank=0.004607  Betweenness=111266"
Write-Host "  idx=409  label=15    PageRank=0.003680  Betweenness=80407"
Write-Host "  idx=686  label=2470  PageRank=0.002524  Betweenness=33424"
Write-Host "  idx=656  label=2237  PageRank=0.002497  Betweenness=50761"
Write-Host "  idx=666  label=2328  PageRank=0.002039  Betweenness=45378"
Write-Host "  idx=2    label=3352  PageRank=0.001784  Betweenness=41400"
Write-Host ""
Write-Host "Window is open - press Ctrl+C to close."

# Keep process alive
try { $p.WaitForExit() } catch { }
