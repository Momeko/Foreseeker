@echo off
rem Build + deploy without touching the machine-wide execution policy.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
