param(
    [string]$BaseUrl = 'http://127.0.0.1:54321',
    [Parameter(Mandatory = $true)][string]$ApiKey
)
$ErrorActionPreference = 'Stop'
$headers = @{
    'X-Api-Key' = $ApiKey
    'Accept' = 'application/json, text/event-stream'
    'MCP-Protocol-Version' = '2025-06-18'
}
$init = @{
    jsonrpc = '2.0'
    id = 1
    method = 'initialize'
    params = @{
        protocolVersion = '2025-06-18'
        capabilities = @{}
        clientInfo = @{name = 'RelayMcp-PowerShell-Smoke'; version = '1.0'}
    }
} | ConvertTo-Json -Depth 10 -Compress
Write-Host 'Step 1: MCP initialize'
$r1 = Invoke-WebRequest -Uri ($BaseUrl.TrimEnd('/') + '/mcp') -Method Post `
    -Headers $headers -ContentType 'application/json' -Body $init
$r1.Content
Write-Host 'Step 2: tools/list'
$list = '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
$r2 = Invoke-WebRequest -Uri ($BaseUrl.TrimEnd('/') + '/mcp') -Method Post `
    -Headers $headers -ContentType 'application/json' -Body $list
$r2.Content
Write-Host 'If tools/list shows GetSkillDomains, GetSkillList and ExecuteSkill, the MCP endpoint is working.'
