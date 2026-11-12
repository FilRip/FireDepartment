@echo off
dotnet build src\OpenFD.ConvFD1
rmdir ..\Data\Gfx\Mission\FD1_Banque /s /q
rmdir ..\Data\Gfx\Textures\FD1_Banque /s /q
rmdir ..\Data\Design\Mission\FD1_Banque /s /q
rmdir ..\Data\Gfx\Placeable\FD1_Banque /s /q
rmdir ..\Data\Script\Mission\FD1_Banque /s /q
del ..\Data\Localize\Lang_ENG\FD1_Banque.loc
del ..\Data\Localize\Lang_FRA\FD1_Banque.loc
del ..\Data\Localize\Lang_GER\FD1_Banque.loc
del ..\Data\Localize\Lang_ITA\FD1_Banque.loc
del ..\Data\Localize\Lang_SPA\FD1_Banque.loc
dotnet src\OpenFD.ConvFD1\bin\Debug\net8.0\OpenFD.ConvFD1.dll "E:\Gamez\Fire Department Trilogy.Packed\Fire Department" "E:\Gamez\Fire Department Trilogy.Packed\Fire Department 3\Data" Banque
"src\OpenFD.MG\bin\Release\net8.0\OpenFD.exe" "E:\Gamez\Fire Department Trilogy.Packed\Fire Department 3" -test=FD1_Banque -windowed
