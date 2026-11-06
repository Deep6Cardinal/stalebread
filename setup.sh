#!/usr/bin/env bash
set -euo pipefail

case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*) ;;
  *) printf '%s\n' 'StaleBread requires Windows. Use setup.ps1 in Windows PowerShell, or this wrapper in Git Bash on Windows.' >&2; exit 1 ;;
esac

cd -- "$(dirname -- "${BASH_SOURCE[0]}")"
if command -v pwsh.exe >/dev/null 2>&1; then
  exec pwsh.exe -NoProfile -File ./setup.ps1
elif command -v powershell.exe >/dev/null 2>&1; then
  exec powershell.exe -NoProfile -File ./setup.ps1
else
  printf '%s\n' 'PowerShell was not found. Run setup.ps1 from a Windows PowerShell terminal.' >&2
  exit 1
fi
