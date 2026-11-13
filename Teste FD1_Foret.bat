@echo off
dotnet build src\OpenFD.ConvFD1
rmdir ..\Data\Gfx\Mission\FD1_Foret /s /q
rmdir ..\Data\Gfx\Textures\FD1_Foret /s /q
rmdir ..\Data\Design\Mission\FD1_Foret /s /q
rmdir ..\Data\Gfx\Placeable\FD1_Foret /s /q
rmdir ..\Data\Script\Mission\FD1_Foret /s /q
del ..\Data\Localize\Lang_ENG\FD1_Foret.loc
del ..\Data\Localize\Lang_FRA\FD1_Foret.loc
del ..\Data\Localize\Lang_GER\FD1_Foret.loc
del ..\Data\Localize\Lang_ITA\FD1_Foret.loc
del ..\Data\Localize\Lang_SPA\FD1_Foret.loc
dotnet src\OpenFD.ConvFD1\bin\Debug\net8.0\OpenFD.ConvFD1.dll "E:\Gamez\Fire Department Trilogy.Packed\Fire Department" "E:\Gamez\Fire Department Trilogy.Packed\Fire Department 3\Data" Foret
"src\OpenFD.MG\bin\Release\net8.0\OpenFD.exe" "E:\Gamez\Fire Department Trilogy.Packed\Fire Department 3" -test=FD1_Foret -windowed
