@echo off
dotnet build src\OpenFD.ConvFD1
rmdir ..\Data\Gfx\Mission\FD1_Entrepot /s /q
rmdir ..\Data\Gfx\Textures\FD1_Entrepot /s /q
rmdir ..\Data\Design\Mission\FD1_Entrepot /s /q
rmdir ..\Data\Gfx\Placeable\FD1_Entrepot /s /q
rmdir ..\Data\Script\Mission\FD1_Entrepot /s /q
dotnet src\OpenFD.ConvFD1\bin\Debug\net8.0\OpenFD.ConvFD1.dll "E:\Gamez\Fire Department Trilogy.Packed\Fire Department" "E:\Gamez\Fire Department Trilogy.Packed\Fire Department 3\Data" Entrepot
"src\OpenFD.MG\bin\Release\net8.0\OpenFD.exe" "E:\Gamez\Fire Department Trilogy.Packed\Fire Department 3" -test=FD1_Entrepot -windowed
