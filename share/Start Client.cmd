@echo off
rem Run this on EVERY OTHER PC. It connects to the host by itself:
rem   1. using host-address.txt in this folder, if present (the host creates it when it starts), or
rem   2. by finding the host on the network automatically.
rem To force an address instead, put it here, e.g.  set "HOST=192.168.1.20"
set "HOST="

cd /d "%~dp0"
start "" "%~dp0VideoSync.exe" --client %HOST% --folder "%~dp0." --fullscreen
