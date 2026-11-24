@echo off
rem Lance le jeu en version DEMO (version MonoGame : 3D, carte, sons).
rem La premiere fois, la compilation telecharge MonoGame (connexion Internet necessaire).
where dotnet >nul 2>nul
if errorlevel 1 (
  echo Le kit .NET n'est pas installe. Telechargez ".NET 8 SDK" sur https://dotnet.microsoft.com/download
  pause
  exit /b 1
)
echo Compilation et lancement, patientez...
dotnet run --project src\OpenFD.MG -c Release
if errorlevel 1 pause
