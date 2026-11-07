#!/usr/bin/env bash
# Repo guard: enforces the privacy and safety rules from SPEC.md (S1, S6, S7, S8).
# Works in CI (tracked files) and locally (tracked + untracked, minus ignored).
set -u
cd "$(git rev-parse --show-toplevel)"
fail=0
files() { git ls-files --cached --others --exclude-standard; }

echo "== S8: no trace/telemetry artifacts committed"
bad=$(files | grep -Ei '\.(etl|evtx|dmp)(\.|$)|(^|/)data/runs/|battery-report|energy-report' || true)
if [ -n "$bad" ]; then echo "FAIL: trace-like files:"; echo "$bad"; fail=1; fi

echo "== S8: no secrets files committed"
bad=$(files | grep -E '(^|/)\.env($|\.)' | grep -v '\.env\.example$' | grep -v '^web/\.env\.mocks$' || true)
# the one allowed non-example env file must contain only the mock switch (never a secret)
if [ -f web/.env.mocks ] && grep -qvE '^(VITE_USE_MOCKS=true|#.*|[[:space:]]*)$' web/.env.mocks; then
  bad="$bad
web/.env.mocks (contains something other than VITE_USE_MOCKS=true)"
fi
if [ -n "$bad" ]; then echo "FAIL: env files:"; echo "$bad"; fail=1; fi

echo "== no files over 1 MB (fixtures must stay tiny)"
while IFS= read -r f; do
  [ -f "$f" ] || continue
  size=$(wc -c < "$f")
  case "$f" in
    */package-lock.json|*/uv.lock) limit=3000000 ;;
    *) limit=1000000 ;;
  esac
  if [ "$size" -gt "$limit" ]; then echo "FAIL: $f is $size bytes"; fail=1; fi
done < <(files)

echo "== S6/S7: banned system-modifying calls in collector source"
pattern='setdcvalueindex|setacvalueindex|powercfg[^"]*-?/?setactive|taskkill|Registry\.(SetValue|CreateSubKey|DeleteSubKey|DeleteValue)|OpenBaseKey|OpenSubKey\([^)]*true|RegSetValue|RegCreateKey|RegDeleteKey|RegDeleteValue|PowerSetActiveScheme|PowerWrite(AC|DC)ValueIndex|PowerWriteSettingAttributes|PowerDeleteScheme|SetSuspendState|SetSystemPowerState|SetThreadExecutionState|InvokeMethod|CreateProcess|ChangeServiceConfig|ControlService|SystemParametersInfo|\.Kill\(|Process\.Start|ProcessStartInfo|wpr\.exe|bcdedit|Set-ExecutionPolicy'
hits=$(git grep -nEi "$pattern" -- 'collector/Wtba.Collector/*.cs' 'collector/Wtba.Collector/**/*.cs' 2>/dev/null || true)
if [ -z "$hits" ]; then
  # fall back to the working tree when files are not yet tracked
  hits=$(grep -rnEi --include='*.cs' "$pattern" collector/Wtba.Collector 2>/dev/null | grep -v '/obj/\|/bin/' || true)
fi
if [ -n "$hits" ]; then echo "FAIL: banned calls:"; echo "$hits"; fail=1; fi

echo "== no direct delete calls anywhere; the handle-verified delete lives only in NativeFs.cs"
hits=$(grep -rnE --include='*.cs' 'File\.Delete|Directory\.Delete|\.Delete\(true\)|FileInfo\([^)]*\)\.Delete|DirectoryInfo\([^)]*\)\.Delete|FileOptions\.DeleteOnClose|DeleteFileW|RemoveDirectory' collector/Wtba.Collector 2>/dev/null | grep -v '/obj/\|/bin/' || true)
if [ -n "$hits" ]; then echo "FAIL: direct delete calls (use EtlCleaner/NativeFs.DeleteVerified):"; echo "$hits"; fail=1; fi
hits=$(grep -rnE --include='*.cs' 'SetFileInformationByHandle|DeleteVerified\(' collector/Wtba.Collector 2>/dev/null | grep -v '/obj/\|/bin/' | grep -v 'NativeFs\.cs\|EtlRetention\.cs' || true)
if [ -n "$hits" ]; then echo "FAIL: handle-delete primitive used outside NativeFs.cs/EtlRetention.cs:"; echo "$hits"; fail=1; fi

echo "== real user paths / usernames must not appear in fixtures or docs"
hits=$(files | grep -E '^(fixtures|docs|README\.md|SPEC\.md)' | xargs -r grep -nEi '[A-Za-z]:[\\/]+Users[\\/]+[A-Za-z0-9._~-]+|\\\\[A-Za-z0-9._-]+\\[A-Za-z]\$\\Users\\[A-Za-z0-9._-]+|Documents and Settings[\\/]+[A-Za-z0-9._-]+' 2>/dev/null | grep -v '<USER>' | grep -v 'FakePerson' | grep -vi 'Users\\<name>\|Users\\<user>\|Users[\\/]\*' || true)
if [ -n "$hits" ]; then echo "FAIL: user paths:"; echo "$hits"; fail=1; fi

if [ "$fail" -ne 0 ]; then echo "GUARD FAILED"; exit 1; fi
echo "GUARD OK"
