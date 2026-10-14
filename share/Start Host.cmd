@echo off
rem Run this on the ONE PC that controls playback.
rem Plays the first video in this folder, fullscreen, looping.
rem To pick a specific video, put its file name here, e.g.  set "VIDEO=myvideo.mp4"
set "VIDEO="

cd /d "%~dp0"
if defined VIDEO goto run
for %%F in (*.mp4 *.m4v *.mov *.wmv *.mkv *.avi) do if not defined VIDEO set "VIDEO=%%F"
if not defined VIDEO (
    echo No video found. Copy a video file into this folder, next to VideoSync.exe.
    pause
    exit /b 1
)

:run
start "" "%~dp0VideoSync.exe" --host --file "%~dp0%VIDEO%" --loop --play --fullscreen
