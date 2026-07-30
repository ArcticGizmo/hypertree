@echo off
setlocal

:: Build a real Velopack package locally and, optionally, install it.
::
::   publish.bat                 pack only -> releases\
::   publish.bat --install       pack, then run the installer
::   publish.bat 0.2.0           pack a specific version
::   publish.bat 0.2.0 --install
::
:: This produces the SAME package CI does (same packId, same install location), so it is the only way
:: to exercise the parts that only exist inside an installer: the PATH entry going on at install and
:: coming back off at uninstall, and htree landing beside hypertree.exe in the install folder.
::
:: WARNING: --install installs over any existing Hypertree - same packId, same
:: %LocalAppData%\Hypertree. Uninstalling afterwards removes it. Reinstall a real build from the
:: GitHub releases page when you're done testing.

set VERSION=
set DOINSTALL=

:parse
if "%~1"=="" goto parsed
if /i "%~1"=="--install" (set DOINSTALL=1) else (set VERSION=%~1)
shift
goto parse
:parsed

:: Default to the csproj version. It is bumped by the bump-version skill, so a local package always
:: matches what a tag of the same commit would produce.
if "%VERSION%"=="" (
    for /f "tokens=*" %%i in ('powershell -NoProfile -Command "(Select-Xml -Path src\Hypertree.App\Hypertree.App.csproj -XPath \"//Version\").Node.InnerText"') do set VERSION=%%i
)

if "%VERSION%"=="" (
    echo Error: could not read the version. Pass it instead: publish.bat 0.1.6
    exit /b 1
)

echo.
echo === Building Hypertree v%VERSION% ===
echo.

:: Both output folders are cleared every run. publish\ so a stale binary can't be packaged by accident;
:: releases\ because vpk refuses to build a version equal to or older than one already sitting there,
:: which would otherwise block re-packing the same version after a code change - exactly what you do
:: over and over while testing an installer.
if exist publish rmdir /s /q publish
if exist releases rmdir /s /q releases

dotnet publish src\Hypertree.App\Hypertree.App.csproj -c Release -r win-x64 --self-contained true ^
    -p:Version=%VERSION% ^
    -o publish\

if %ERRORLEVEL% neq 0 (
    echo Tray build failed.
    exit /b %ERRORLEVEL%
)

echo.
echo === Building htree CLI ===
echo.

:: htree publishes into the SAME folder as hypertree.exe, so Velopack packs the two together and the
:: tray can advertise the CLI's absolute path in status.json.
::
:: NativeAOT needs the Visual Studio "Desktop development with C++" workload for the native linker.
:: A fresh dev box usually doesn't have it, so fall back to an ordinary self-contained build to keep
:: LOCAL packaging working. CI releases stay AOT.
::
:: The fallback is deliberately NOT single-file: a compressed single-file htree decompresses itself on
:: every run, measured at ~278ms per invocation against ~127ms for this multi-file build - and for a
:: command meant to sit in a shell prompt that is the difference that matters. Multi-file also costs
:: almost nothing in package size here, because the tray is self-contained too and the .NET runtime
:: files are already in publish\; only htree.exe (~200KB) is genuinely new.
dotnet publish src\Hypertree.Cli\Hypertree.Cli.csproj -c Release -r win-x64 ^
    -p:Version=%VERSION% ^
    -o publish\

if %ERRORLEVEL% neq 0 (
    echo.
    echo NativeAOT link failed - falling back to a self-contained htree so local packaging can proceed.
    echo This build starts in ~127ms against single digits for AOT; install the C++ workload for an
    echo AOT-equivalent local build:  https://aka.ms/nativeaot-prerequisites
    echo.
    dotnet publish src\Hypertree.Cli\Hypertree.Cli.csproj -c Release -r win-x64 --self-contained true ^
        -p:Version=%VERSION% ^
        -p:PublishAot=false ^
        -o publish\
)

:: Deliberately checked by existence rather than ERRORLEVEL. Inside the parenthesised block above,
:: %ERRORLEVEL% expands when cmd PARSES the block, not when it runs - so it would still hold the failed
:: AOT attempt's code and report a successful fallback as a failure. The file being there is the thing
:: that actually matters, and publish\ was cleared at the start, so a stale copy can't satisfy it.
if not exist publish\htree.exe (
    echo.
    echo Error: htree.exe is missing from publish\ - the package would ship without the CLI.
    exit /b 1
)

echo.
echo === Packaging ===
echo.

vpk pack ^
    --packId Hypertree ^
    --packTitle "Hypertree" ^
    --packVersion %VERSION% ^
    --packDir publish\ ^
    --mainExe hypertree.exe ^
    --icon src\Hypertree.App\Assets\icon.ico ^
    --outputDir releases\

if %ERRORLEVEL% neq 0 (
    echo Pack failed. Is the vpk CLI installed?  dotnet tool install -g vpk
    exit /b %ERRORLEVEL%
)

echo.
echo === Writing checksums ===
echo.

:: Mirrors the SHA256SUMS.txt that release.yml publishes, so install.ps1 can be pointed at a local pack,
:: tools\test-install.ps1 has a real manifest to check, and a hand-uploaded release still ships checksums.
:: Written LF-terminated with lower-case hex in sha256sum's own format, so `sha256sum -c SHA256SUMS.txt`
:: validates it as-is. The manifest itself is skipped with Where-Object, NOT -Exclude: Get-ChildItem
:: silently IGNORES -Exclude when it is paired with -LiteralPath, so a re-run would otherwise hash the
:: previous manifest into the new one.
powershell -NoProfile -Command "$d = Resolve-Path 'releases'; $lines = Get-ChildItem -File -LiteralPath $d | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object Name | ForEach-Object { '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name }; [System.IO.File]::WriteAllText((Join-Path $d 'SHA256SUMS.txt'), ($lines -join [char]10) + [char]10); Write-Host ('  ' + @($lines).Count + ' files hashed')"

if %ERRORLEVEL% neq 0 (
    echo Checksum generation failed.
    exit /b %ERRORLEVEL%
)

echo.
echo Artifacts in: releases\
echo   Uploading a release by hand? Include SHA256SUMS.txt - install.ps1 refuses a release without it.
echo.

if not defined DOINSTALL (
    echo To install this build:  publish.bat --install
    echo   or run:               releases\Hypertree-win-Setup.exe
    exit /b 0
)

echo === Installing ===
echo.
echo Close any running Hypertree first - the installer replaces the tray in place.
echo.

if not exist releases\Hypertree-win-Setup.exe (
    echo Error: releases\Hypertree-win-Setup.exe not found.
    exit /b 1
)

start /wait "" releases\Hypertree-win-Setup.exe

echo.
echo Installed. Open a NEW terminal - PATH changes don't reach shells that are already open - then:
echo   htree --version
echo   htree list
echo.
echo Uninstall via Settings ^> Apps ^> Hypertree, then confirm the PATH entry is gone:
echo   powershell -NoProfile -Command "[Environment]::GetEnvironmentVariable('PATH','User') -split ';' ^| Select-String Hypertree"
