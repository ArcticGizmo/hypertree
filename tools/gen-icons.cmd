@echo off
rem Regenerates every raster icon asset from the source-of-truth SVG (hypertree.svg).
rem
rem   src/Hypertree.App/Assets/icon.png   256x256 PNG  (window icons + in-app logo)
rem   src/Hypertree.App/Assets/icon.ico   multi-res ICO (tray icon + .exe ApplicationIcon)
rem   landing-icon.png                    512x512 PNG  (README header)
rem
rem Run this after editing hypertree.svg, then commit the regenerated assets.

dotnet run --project "%~dp0IconGen" -c Release
