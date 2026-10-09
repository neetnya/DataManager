# 轻量数据管理工具 - 运行环境检查脚本
# 本软件不引用任何 NuGet 包（SQLite 使用 Windows 自带的 winsqlite3.dll），
# 正常情况下无需安装任何依赖。此脚本仅用于确认运行环境是否满足要求。
# 用法：powershell -ExecutionPolicy Bypass -File .\install-deps.ps1

$ErrorActionPreference = 'Stop'
$fail = 0

# Windows 10 或更高
if ([System.Environment]::OSVersion.Version.Major -ge 10) {
    Write-Host 'OK   Windows 10+'
} else {
    Write-Host 'MISS Windows 10+'
    $fail = 1
}

# .NET 9 桌面运行时
$runtimes = & dotnet --list-runtimes 2>$null
if ($runtimes | Where-Object { $_ -match 'Microsoft\.WindowsDesktop\.App 9\.' }) {
    Write-Host 'OK   .NET 9 Desktop Runtime'
} else {
    Write-Host 'MISS .NET 9 Desktop Runtime'
    $fail = 1
}

# 系统自带的 SQLite
$sqlite = Join-Path $env:SystemRoot 'System32\winsqlite3.dll'
if (Test-Path $sqlite) {
    Write-Host 'OK   winsqlite3.dll'
} else {
    Write-Host 'MISS winsqlite3.dll'
    $fail = 1
}

if ($fail -ne 0) {
    Write-Host ''
    Write-Host 'Missing dependency. Install the .NET 9 Desktop Runtime:'
    Write-Host 'https://dotnet.microsoft.com/download/dotnet/9.0'
    exit 1
}

Write-Host ''
Write-Host 'Environment OK. No dependencies to install.'
exit 0
