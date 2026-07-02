# Simulates a Claude Code Stop hook: writes a fake transcript, then POSTs the
# hook payload to a running RAIVEN instance. Click the toast to hear a summary.
param([int]$Port = 9876)

$transcript = Join-Path $env:TEMP "raiven-smoke-transcript.jsonl"
$lines = @(
    '{"type":"user","message":{"role":"user","content":"Fix the login bug in auth.cs"},"sessionId":"smoke-001","cwd":"E:\\Repos\\RAIVEN"}'
    '{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Edit","input":{"file_path":"auth.cs"}}]},"sessionId":"smoke-001"}'
    '{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]},"sessionId":"smoke-001"}'
    '{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"I found a null-reference bug in the login handler in auth.cs and fixed it by adding the missing null check. I ran the test suite afterwards and all 42 tests pass."}]},"sessionId":"smoke-001"}'
)
Set-Content -Path $transcript -Value $lines -Encoding UTF8

$payload = @{
    session_id      = "smoke-001"
    transcript_path = $transcript
    cwd             = "E:\Repos\RAIVEN"
    hook_event_name = "Stop"
} | ConvertTo-Json

Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$Port/events" -ContentType "application/json" -Body $payload
Write-Host "Stop event sent. You should hear a chime and see a toast - click 'Play summary'."
