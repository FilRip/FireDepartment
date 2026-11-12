@echo off
dotnet build src\OpenFD.ConvFD1
rmdir ..\Data\Gfx\Mission\FD1_Quartier /s /q
rmdir ..\Data\Gfx\Textures\FD1_Quartier /s /q
rmdir ..\Data\Design\Mission\FD1_Quartier /s /q
rmdir ..\Data\Gfx\Placeable\FD1_Quartier /s /q
rmdir ..\Data\Script\Mission\FD1_Quartier /s /q
del ..\Data\Localize\Lang_ENG\FD1_Quartier.loc
del ..\Data\Localize\Lang_FRA\FD1_Quartier.loc
del ..\Data\Localize\Lang_GER\FD1_Quartier.loc
del ..\Data\Localize\Lang_ITA\FD1_Quartier.loc
del ..\Data\Localize\Lang_SPA\FD1_Quartier.loc
dotnet src\OpenFD.ConvFD1\bin\Debug\net8.0\OpenFD.ConvFD1.dll "E:\Gamez\Fire Department Trilogy.Packed\Fire Department" "E:\Gamez\Fire Department Trilogy.Packed\Fire Department 3\Data" Quartier
"src\OpenFD.MG\bin\Release\net8.0\OpenFD.exe" "E:\Gamez\Fire Department Trilogy.Packed\Fire Department 3" -test=FD1_Quartier -windowed
