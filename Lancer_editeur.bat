@echo off
rem Lance l'editeur de missions d'OpenFD (OpenFD.Editor.exe).
rem Le jeu est compile en meme temps (bouton "Tester" : OpenFD.exe -test=Mission -windowed).
cd /d "%~dp0"
set JEU=%~dp0..
if not exist "%JEU%\Data\design.pak" set JEU=E:\Gamez\Fire Department Trilogy.Packed\Fire Department 3
where dotnet >nul 2>nul
if errorlevel 1 (
  echo Le kit .NET n'est pas installe. Telechargez ".NET 8 SDK" sur https://dotnet.microsoft.com/download
  pause
  exit /b 1
)
echo Compilation et lancement de l'editeur, patientez...
dotnet run --project src\OpenFD.Editor -c Release -- "%JEU%"
if errorlevel 1 pause
