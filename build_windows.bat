@echo off
rem Compile la version Windows (64 bits) du jeu et de l'editeur de missions dans publish\windows-x64.
rem Copier ensuite ce dossier sur la machine Windows, puis la-bas :
rem   chmod +x OpenFD OpenFD.Editor
rem   ./OpenFD "/chemin/vers/Fire Department 3"
rem Il faut le ".NET 8 Runtime" sur la machine Windows.
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
  echo Le kit .NET n'est pas installe. Telechargez ".NET 8 SDK" sur https://dotnet.microsoft.com/download
  pause
  exit /b 1
)
dotnet publish src\OpenFD.ConvFD1 -c Release -o publish\windows-x64
if errorlevel 1 goto erreur
dotnet publish src\OpenFD.Editor -c Release -o publish\windows-x64
if errorlevel 1 goto erreur
dotnet publish src\OpenFD.MG -c Release -o publish\windows-x64
if errorlevel 1 goto erreur
echo Version windows prete dans publish\windows-x64
pause
exit /b 0
:erreur
echo La compilation a echoue.
pause
exit /b 1
