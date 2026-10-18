@echo off
setlocal
cd /d "%~dp0"

where git >nul 2>nul || (echo Git is niet gevonden op PATH. Installeer Git for Windows. & pause & exit /b 1)
if exist .git (echo Er bestaat hier al een git-repository. & pause & exit /b 1)

rem Oude treemap-control wordt niet meer gebruikt
if exist Controls\TreemapControl.cs del /q Controls\TreemapControl.cs
rmdir Controls 2>nul

rem Build-output en VS-cache horen niet in git (worden ook genegeerd via .gitignore)
rmdir /s /q bin 2>nul
rmdir /s /q obj 2>nul

git init -b main || (pause & exit /b 1)

git config user.name  >nul 2>nul || git config user.name  "Koen"
git config user.email >nul 2>nul || git config user.email "kbijttebier@gmail.com"

git add -A
git reset -q -- setup-git.cmd
git commit -q -m "HandBrake Treemap: mappenboom, map in 1x omzetten, controle en originelen verwijderen" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>" -m "Claude-Session: https://claude.ai/code/session_01Y5SUACRuSBhaBJD9Sxawrk" || (pause & exit /b 1)

echo.
git log --oneline
git status --short
echo.
echo Repository aangemaakt. Dit script verwijdert zichzelf nu.
pause
(goto) 2>nul & del "%~f0"
