@echo off
rem Lance le jeu FD3 (version MonoGame : 3D, carte, sons).
rem La premiere fois, la compilation telecharge MonoGame (connexion Internet necessaire).
cd /d "%~dp0"
set JEU=%~dp0..
if not exist "%JEU%\Data\design.pak" set JEU=E:\Gamez\Fire Department Trilogy.Packed\Fire Department 3
where dotnet >nul 2>nul
if errorlevel 1 (
  echo Le kit .NET n'est pas installe. Telechargez ".NET 8 SDK" sur https://dotnet.microsoft.com/download
  pause
  exit /b 1
)
echo Compilation et lancement, patientez...
dotnet run --project src\FD3.MG -c Release -- "%JEU%"
if errorlevel 1 pause
