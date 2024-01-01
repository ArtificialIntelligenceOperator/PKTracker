@echo off
rem Запуск PKTracker. Двойной клик — старт трекера (при необходимости соберёт проект).
rem   PKTracker.bat shortcut  — положить ярлык с иконкой на рабочий стол
rem   PKTracker.bat rebuild   — пересобрать перед запуском
chcp 65001 >nul
setlocal
cd /d "%~dp0"
title PKTracker

set "ARGS="
if /i "%~1"=="shortcut" set "ARGS=-Shortcut"
if /i "%~1"=="rebuild"  set "ARGS=-Rebuild"

powershell -NoProfile -ExecutionPolicy Bypass -File "tools\Start-PKTracker.ps1" %ARGS%
if errorlevel 1 (
    echo.
    pause
    exit /b 1
)
exit /b 0
