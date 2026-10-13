@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"
title Compilation de TracciaStems

echo ================================================
echo   TracciaStems - compilation de l'application
echo ================================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 goto :nosdk
dotnet --list-sdks | findstr /r /b "1[0-9]\." >nul
if errorlevel 1 goto :nosdk

if exist "publish" rmdir /s /q "publish"

set SC=-r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true

echo [1/3] Exe autonome (runtime .NET le plus recent)...
echo.
dotnet publish "src\TracciaStems\TracciaStems.csproj" -c Release %SC% -o "publish"
if not errorlevel 1 goto :ok_sc

echo.
echo ------------------------------------------------
echo  Paquets du dernier correctif .NET pas encore disponibles sur nuget.org
echo  (frequent le jour d'une mise a jour de .NET).
echo  Nouvel essai avec le runtime .NET 10 de base...
echo ------------------------------------------------
echo.
echo [2/3] Exe autonome (runtime .NET 10.0.0)...
echo.
if exist "publish" rmdir /s /q "publish"
dotnet publish "src\TracciaStems\TracciaStems.csproj" -c Release %SC% -p:TargetLatestRuntimePatch=false -o "publish"
if not errorlevel 1 goto :ok_sc

echo.
echo [3/3] Version standard (utilise le .NET installe sur ce PC)...
echo.
if exist "publish" rmdir /s /q "publish"
dotnet publish "src\TracciaStems\TracciaStems.csproj" -c Release --self-contained false -o "publish"
if errorlevel 1 (
  echo.
  echo *** ECHEC de la compilation. Copiez le message d'erreur ci-dessus pour le corriger. ***
  pause
  exit /b 1
)
echo.
echo ================================================
echo   OK : publish\TracciaStems.exe  (version standard, PAS autonome)
echo   Gardez tout le dossier "publish" ensemble (exe + dll).
echo   Relancez build.bat plus tard pour obtenir l'exe autonome.
echo ================================================
goto :done

:ok_sc
echo.
echo ================================================
echo   OK : publish\TracciaStems.exe  -- exe AUTONOME
echo   Un seul fichier, copiable sur tout PC Windows 10/11 64 bits.
echo ================================================

:done
start "" explorer "publish"
pause
exit /b 0

:nosdk
echo Le SDK .NET 10 n'est pas installe.
echo.
echo  - Option 1 : dans un terminal, tapez :  winget install Microsoft.DotNet.SDK.10
echo  - Option 2 : https://dotnet.microsoft.com/download/dotnet/10.0  (SDK, Windows x64)
echo.
echo Puis relancez build.bat.
pause
exit /b 1
