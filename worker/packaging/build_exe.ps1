# REFERENCE ONLY (issue #280/#285): PyInstaller packaging is retired. The worker ships as
# a container image (design D6) and the app installs via Velopack (D2); nothing calls this
# script and it is not part of any CI job. Kept only for its exclude-list discipline (never
# bundle torch/evo2/flash-attn/pyrodigal into a lightweight client) as a reference for the
# worker's local-engine install (design §5.7). `keep_gpu.py` and `keep-gpu.spec`, which this
# script used to also build, were deleted outright (#277/#280) along with the always-on
# keeper they packaged (design D1) — do not re-add a keep-gpu build step here.
#
# If ever revived for a from-source CLI build:  .\packaging\build_exe.ps1
#   dist\dna-entropy.exe  - the client (double-click wizard + full CLI)
# NOTE: packaging\launcher.py still shells out to the retired `cloudrun` CLI command
# (see packaging\launcher.py) and would need updating before this build is usable again.
# Biopython IS bundled (GenBank/FASTA I/O runs locally); it is imported lazily, so we must
# collect its submodules explicitly or PyInstaller misses them.
$ErrorActionPreference = "Stop"

.\.venv\Scripts\python.exe -m pip install --quiet pyinstaller

$common = @(
    "--onefile", "--console", "--noconfirm",
    "--exclude-module", "torch", "--exclude-module", "evo2",
    "--exclude-module", "flash_attn", "--exclude-module", "pyrodigal",
    "--collect-submodules", "Bio",
    "--add-data", "src/dna_entropy;_pkgsrc"
)

# The client.
.\.venv\Scripts\python.exe -m PyInstaller @common --name dna-entropy packaging\launcher.py

Write-Host ""
Write-Host "Built: dist\dna-entropy.exe" -ForegroundColor Green
Write-Host "Distribute via a GitHub Release." -ForegroundColor Cyan
