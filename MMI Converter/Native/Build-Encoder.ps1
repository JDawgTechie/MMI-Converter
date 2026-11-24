param([string]$Compiler = 'clang.exe')
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
& $Compiler -O3 -municode -static -D_FILE_OFFSET_BITS=64 "$src/encoder.c" "$src/mmi.c" "$src/util.c" "$src/libturbojpeg.a" -o "$src/encoder.exe"
if ($LASTEXITCODE) { exit $LASTEXITCODE }
