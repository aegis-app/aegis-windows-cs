@echo off
rem ========================================
rem 🔨 AEGIS Windows版 一括ビルド (テスト → 単一ファイル exe publish)
rem ========================================
rem 使い方: build.bat
rem 成果物: out\AEGIS.exe (Windows x64、自己完結・圧縮つき)
setlocal
cd /d %~dp0

echo === テスト実行 ===
dotnet run --project src\AegisTest\AegisTest.csproj -c Release
if errorlevel 1 exit /b 1

echo === exe を publish ===
if exist out rmdir /s /q out
dotnet publish src\AegisWin\AegisWin.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o out
if errorlevel 1 exit /b 1

echo === 完了: out\AEGIS.exe ===
dir out\AEGIS.exe
endlocal
