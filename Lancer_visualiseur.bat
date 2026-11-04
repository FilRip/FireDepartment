@echo off
rem Lance le visualiseur du feu de Fire Department 3 dans le navigateur.
rem Le dossier du jeu est cherche dans le dossier parent, sinon indiquez-le ci-dessous.
cd /d "%~dp0"
set JEU=%~dp0..
if not exist "%JEU%\Data\design.pak" set JEU=E:\Gamez\Fire Department Trilogy.Packed\Fire Department 3
where dotnet >nul 2>nul
if errorlevel 1 (
  echo Le kit .NET n'est pas installe. Telechargez ".NET 8 SDK" sur https://dotnet.microsoft.com/download
  pause
  exit /b 1
)
echo Compilation et lancement, patientez (la premiere fois peut prendre une minute)...
dotnet run --project src\OpenFD.Viewer -c Release -- "%JEU%"
pause
