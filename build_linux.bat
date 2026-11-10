@echo off
rem Compile la version Linux (64 bits) du jeu et de l'editeur de missions dans publish\linux-x64.
rem Copier ensuite ce dossier sur la machine Linux, puis la-bas :
rem   chmod +x OpenFD OpenFD.Editor
rem   ./OpenFD "/chemin/vers/Fire Department 3"
rem Il faut le ".NET 8 Runtime" sur la machine Linux.
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
  echo Le kit .NET n'est pas installe. Telechargez ".NET 8 SDK" sur https://dotnet.microsoft.com/download
  pause
  exit /b 1
)
dotnet publish src\OpenFD.MG -c Release -r linux-x64 --self-contained false -o publish\linux-x64
if errorlevel 1 goto erreur
dotnet publish src\OpenFD.Editor -c Release -r linux-x64 --self-contained false -o publish\linux-x64
if errorlevel 1 goto erreur
echo Version Linux prete dans publish\linux-x64
pause
exit /b 0
:erreur
echo La compilation a echoue.
pause
exit /b 1
