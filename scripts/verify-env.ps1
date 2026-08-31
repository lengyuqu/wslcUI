# =============================================================================
# wslcUI 真机验证 - 前置环境检查
#
# 用法（仓库根目录，PowerShell）：
#   powershell -ExecutionPolicy Bypass -File scripts\verify-env.ps1
#
# 检查项：wslc.exe / CLI UTF-8 输出假设 / WSL 状态 / dotnet SDK 10 /
#         Windows App Runtime 2.4（unpackaged 非自包含模式必需）
# 全部通过后运行完整验证程序：
#   dotnet run --project tools/wslcUI.Verify -p:Platform=x64 -c Debug
# =============================================================================

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$exe = 'C:\Program Files\WSL\wslc.exe'
$script:fail = 0

function Check([string]$Name, [bool]$Cond, [string]$Detail) {
    if ($Cond) {
        Write-Host ("[PASS] " + $Name) -ForegroundColor Green
    } else {
        Write-Host ("[FAIL] " + $Name + " -- " + $Detail) -ForegroundColor Red
        $script:fail++
    }
}

Write-Host "wslcUI 真机验证 - 前置环境检查`n====================================="

# 1. wslc.exe 存在
Check "wslc.exe 存在" (Test-Path $exe) "未找到 $exe（先 wsl --install）"

# 2. wslc 版本
if (Test-Path $exe) {
    Write-Host "`n--- wslc --version ---"
    & $exe --version 2>&1 | Select-Object -First 3 | ForEach-Object { Write-Host ("  " + $_) }
}

# 3. CLI 重定向输出编码假设（RunAsync 显式 UTF-8 的前提）：
#    PowerShell 以 UTF-8 解码 `wslc list -a`，中文表头能读出来才证明输出是 UTF-8。
if (Test-Path $exe) {
    Write-Host "`n--- wslc list -a（UTF-8 解码，表头应含 容器 ID 或 CONTAINER ID）---"
    $lines = & $exe list -a 2>&1 | Select-Object -First 3
    $lines | ForEach-Object { Write-Host ("  " + $_) }
    $header = ($lines | Where-Object { $_ -and $_.ToString().Trim() -ne '' } | Select-Object -First 1).ToString()
    Check "list -a 输出为 UTF-8（表头可读）" ($header -match '容器 ID|CONTAINER ID') "首行: $header -- 输出疑似非 UTF-8，WslcCli 解析会静默失败"
}

# 4. WSL 状态
Write-Host "`n--- wsl --status ---"
try { wsl --status 2>&1 | Select-Object -First 4 | ForEach-Object { Write-Host ("  " + $_) } }
catch { Write-Host "  wsl 命令不可用: $_" }

# 5. dotnet SDK >= 10
$dotnet = (dotnet --version 2>$null)
Check "dotnet SDK >= 10（当前: $dotnet）" ($dotnet -and [version]$dotnet -ge [version]'10.0.0') "项目 TFM 为 net10.0-windows10.0.26100.0"

# 6. Windows App Runtime 2.4（csproj 非自包含 + Bootstrap 2.4）
$runtimes = Get-AppxPackage -Name '*WindowsAppRuntime*' -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty Version
$has24 = $runtimes | Where-Object { $_ -like '2.4*' }
Check "Windows App Runtime 2.4 已安装" ([bool]$has24) "已装: $($runtimes -join ', ')；wslcUI bootstrap 指定 2.4 (0x00020004)"

Write-Host "`n====================================="
if ($script:fail -eq 0) {
    Write-Host "环境就绪。运行完整验证：" -ForegroundColor Green
    Write-Host "  dotnet run --project tools/wslcUI.Verify -p:Platform=x64 -c Debug"
    Write-Host "可选：--container <容器名>（追加指定容器验证）、--skip-pty（跳过 ConPTY 项）"
    exit 0
} else {
    Write-Host "$($script:fail) 项环境检查未通过。" -ForegroundColor Red
    exit 1
}
