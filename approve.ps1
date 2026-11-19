# Approval must run on the relay host. Never launch this script through an AI tool.
param(
    [string]$Id,
    [switch]$List,
    [switch]$Cancel
)

$api = $env:RELAY_API_KEY
$approval = $env:RELAY_APPROVAL_KEY
if ([string]::IsNullOrWhiteSpace($api) -or [string]::IsNullOrWhiteSpace($approval)) {
    throw "请在这个终端设置 RELAY_API_KEY 和 RELAY_APPROVAL_KEY 环境变量。"
}
$headers = @{
    'X-Api-Key' = $api
    'X-Approval-Key' = $approval
}
$base = 'http://127.0.0.1:54321/approvals'

if ($List) {
    Invoke-RestMethod -Method Get -Uri "$base/pending" -Headers $headers | ConvertTo-Json -Depth 10
    exit
}
if ([string]::IsNullOrWhiteSpace($Id) -or $Id -notmatch '^[a-fA-F0-9]{32}$') {
    throw '请先执行 .\approve.ps1 -List，或者传入有效的 -Id。'
}
if ($Cancel) {
    Invoke-RestMethod -Method Post -Uri "$base/$Id/cancel" -Headers $headers | ConvertTo-Json -Depth 10
    exit
}

$pending = (Invoke-RestMethod -Method Get -Uri "$base/pending" -Headers $headers).data
$item = @($pending | Where-Object { $_.approval_id -eq $Id })
if ($item.Count -ne 1) { throw '找不到待审批任务，可能已执行或过期。' }
Write-Host "\n请认真核对以下实际操作和参数：\n" -ForegroundColor Yellow
Write-Host $item[0].summary
$answer = Read-Host "\n确定执行请输入大写 YES（其他输入取消）"
if ($answer -cne 'YES') { Write-Host '未批准操作'; exit }
Invoke-RestMethod -Method Post -Uri "$base/$Id/execute" -Headers $headers | ConvertTo-Json -Depth 20
