@echo off
echo === ClipperApp Build ===
echo.

dotnet publish ClipperApp.csproj ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -o publish\

if %ERRORLEVEL% EQU 0 (
    echo.
    echo [OK] ClipperApp.exe liegt in: %~dp0publish\ClipperApp.exe
    echo.
    echo WICHTIG: ffmpeg.exe muss im selben Ordner wie ClipperApp.exe liegen!
    echo Download: https://www.gyan.dev/ffmpeg/builds/
    echo  - "ffmpeg-release-essentials.zip" herunterladen
    echo  - ffmpeg.exe aus dem bin-Ordner in publish\ kopieren
    echo.
    start "" "%~dp0publish\"
) else (
    echo.
    echo [FEHLER] Build fehlgeschlagen!
    echo Stelle sicher, dass das .NET 8 SDK installiert ist:
    echo https://dotnet.microsoft.com/download/dotnet/8.0
)
pause
