#!/usr/bin/env pwsh
# Regenerates every raster icon asset from the source-of-truth SVG (hypertree.svg).
#
#   src/Hypertree.App/Assets/icon.png   256x256 PNG  (window icons + in-app logo)
#   src/Hypertree.App/Assets/icon.ico   multi-res ICO (tray icon + .exe ApplicationIcon)
#   landing-icon.png                    512x512 PNG  (README header)
#
# Windows-only: IconGen renders the SVG through System.Drawing, which only runs on Windows.
#
# Run this after editing hypertree.svg, then commit the regenerated assets.

$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'IconGen'
dotnet run --project $proj -c Release
