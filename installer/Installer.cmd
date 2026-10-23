@echo off
rem Installe Retouche d inventaire pour cet utilisateur et cree les raccourcis.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Installer.ps1"
pause
