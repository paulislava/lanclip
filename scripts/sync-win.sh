#!/usr/bin/env bash
set -euo pipefail

PEER="${LANCLIP_PEER:-paulislava@pc}"
DEST='C:/Users/PaulIsLava/lanclip'
HERE="$(cd "$(dirname "$0")/.." && pwd)"

ssh "$PEER" "New-Item -ItemType Directory -Force -Path '$DEST\\win\\src','$DEST\\win\\tests','$DEST\\scripts' | Out-Null" >/dev/null

# scp не удаляет исчезнувшие файлы — чистим целевые папки перед копированием.
ssh "$PEER" "Remove-Item '$DEST\\win\\src\\*.cs','$DEST\\win\\tests\\*.cs' -ErrorAction SilentlyContinue" >/dev/null

# Каталоги src/tests могут быть пустыми на ранних шагах — копируем только то, что есть.
shopt -s nullglob
src_files=("$HERE"/win/src/*.cs)
test_files=("$HERE"/win/tests/*.cs)
shopt -u nullglob

if [ "${#src_files[@]}" -gt 0 ]; then
    scp -q "${src_files[@]}" "$PEER:$DEST/win/src/"
fi
if [ "${#test_files[@]}" -gt 0 ]; then
    scp -q "${test_files[@]}" "$PEER:$DEST/win/tests/"
fi
scp -q "$HERE"/win/build.ps1  "$PEER:$DEST/win/"
scp -q "$HERE"/scripts/install-win.ps1 "$PEER:$DEST/scripts/"

ssh "$PEER" "& '$DEST\\win\\build.ps1'"
