@echo off
chcp 65001 >nul
title OFDRDataReceiver 一键启动
cd /d "%~dp0"

echo [1/3] 关闭占用 bin 目录的旧实例...
:: 先按映像名杀掉 apphost 方式运行的进程（OFDRDataReceiver.exe）
taskkill /F /IM OFDRDataReceiver.exe 2>nul

:: 再杀掉以 dotnet 宿主方式运行、命令行含 OFDRDataReceiver.dll 的进程（Get-CimInstance 可读命令行）
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "try { Get-CimInstance Win32_Process -ErrorAction Stop ^| Where-Object { $_.CommandLine -and $_.CommandLine -like '*OFDRDataReceiver*' -and $_.Name -eq 'dotnet.exe' } ^| ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue } } catch {}"

:: 等待进程完全退出，避免 build 时 dll 仍被锁
timeout /t 2 /nobreak >nul

echo [2/3] 重新生成（Debug）...
dotnet build -c Debug
if errorlevel 1 (
    echo.
    echo 生成失败，请查看上方错误。
    pause
    exit /b 1
)

echo [3/3] 启动程序...
:: 优先启动 apphost exe（进程名固定为 OFDRDataReceiver.exe，下次更易清理）；
:: 若不存在（UseAppHost=false 时）则回退到 dotnet 宿主。
if exist "bin\Debug\net6.0-windows\OFDRDataReceiver.exe" (
    start "" "bin\Debug\net6.0-windows\OFDRDataReceiver.exe"
) else (
    start "" dotnet "bin\Debug\net6.0-windows\OFDRDataReceiver.dll"
)
echo 已启动。