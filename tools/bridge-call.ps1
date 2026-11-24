# Sends raw bridge protocol requests (one JSON object per line in $Requests) to the running OpenUtau bridge
# and prints each response. Used to exercise bridge ops that have no MCP tool yet.
# Example:
#   .\tools\bridge-call.ps1 -Requests '{"method":"bridge.status"}'
param([Parameter(Mandatory)][string[]]$Requests)

$pipe = [System.IO.Directory]::GetFiles("\\.\pipe\") | Where-Object { $_ -like "*openutau-mcp-*" } | Select-Object -First 1
if (-not $pipe) { throw "No OpenUtau bridge pipe found." }
$name = $pipe.Substring($pipe.LastIndexOf('\') + 1)
# Windows PowerShell 5.1 (.NET Framework) has no PipeOptions.CurrentUserOnly; the bridge (server side) enforces it.
$client = [System.IO.Pipes.NamedPipeClientStream]::new(".", $name, [System.IO.Pipes.PipeDirection]::InOut,
    [System.IO.Pipes.PipeOptions]::None)
$client.Connect(3000)
$utf8 = [System.Text.UTF8Encoding]::new($false)
$writer = [System.IO.StreamWriter]::new($client, $utf8); $writer.NewLine = "`n"; $writer.AutoFlush = $true
$reader = [System.IO.StreamReader]::new($client, $utf8)

function Send($obj) {
    $writer.WriteLine(($obj | ConvertTo-Json -Depth 20 -Compress))
    while ($true) {
        $line = $reader.ReadLine()
        if ($line -notmatch '"method":"event"') { return $line }
    }
}

$id = 1
$null = Send @{ jsonrpc = "2.0"; id = $id; method = "bridge.hello"; params = @{ protocol = @{ min = 1; max = 1 }; client = @{ name = "bridge-call.ps1"; version = "0" } } }
foreach ($r in $Requests) {
    $id++
    $req = $r | ConvertFrom-Json
    $req | Add-Member -NotePropertyName jsonrpc -NotePropertyValue "2.0" -Force
    $req | Add-Member -NotePropertyName id -NotePropertyValue $id -Force
    Send $req
}
$client.Dispose()
