#!/usr/bin/env bash
# Кросс-машинный smoke-тест lanclip: /health в обе стороны, затем `lanclipd get`
# с Mac и `lanclipd.exe get` по SSH на ПК. Каждая проверка печатает PASS/FAIL и
# дословный вывод команды — этого достаточно, чтобы понять, что именно сломалось,
# не перезапуская вручную.
#
# ВАЖНО про прокси: в окружении Mac обычно выставлены HTTP_PROXY/http_proxy на
# корпоративный прокси, а локальная подсеть не входит в NO_PROXY. Без
# `--noproxy '*'` curl уводит запрос к соседу через прокси и получает 503 от
# самого прокси, а не ответ агента — ложный отказ. Здесь и далее все curl-вызовы
# идут с `--noproxy '*'`.
set -euo pipefail

MAC_CONFIG="${LANCLIP_MAC_CONFIG:-$HOME/.config/lanclip/config.json}"
MAC_BIN="${LANCLIP_MAC_BIN:-$HOME/.local/bin/lanclipd}"
SSH_PEER="${LANCLIP_PEER:-paulislava@pc}"
WIN_BIN="${LANCLIP_WIN_BIN:-C:\\Users\\PaulIsLava\\.local\\bin\\lanclipd.exe}"
WIN_CONFIG="${LANCLIP_WIN_CONFIG:-C:\\Users\\PaulIsLava\\.config\\lanclip\\config.json}"
WIN_TMP_DIR="${LANCLIP_WIN_TMP_DIR:-C:\\Users\\PaulIsLava\\.config\\lanclip}"

PASS=0
FAIL=0

pass() { echo "PASS: $1"; PASS=$((PASS + 1)); }
fail() { echo "FAIL: $1"; FAIL=$((FAIL + 1)); }

# Мелкая находка финального ревью: `curl -H "x-clip-token: $TOKEN" ...` кладёт
# токен буквально в argv этого процесса, откуда его видит любой локальный
# пользователь/процесс через `ps`/`ps aux` на всё время жизни curl. Вместо
# этого токен идёт curl'у через стандартный ввод как строка конфига (`curl -K -`,
# формат "директива = значение") — `printf` здесь ЯВЛЯЕТСЯ builtin-командой
# bash (не `/usr/bin/printf`), поэтому сам токен ни разу не попадает в таблицу
# процессов ни в каком виде. Прочие curl-опции (`--noproxy`, `-m`, URL)
# передаются как обычно через argv — секретов среди них нет.
curl_with_token() {
    local token="$1"
    shift
    printf 'header = "x-clip-token: %s"\n' "$token" | curl -K - "$@"
}

if ! command -v jq >/dev/null 2>&1; then
    echo "Нужен jq (brew install jq) для разбора конфига Mac." >&2
    exit 2
fi

if [ ! -f "$MAC_CONFIG" ]; then
    echo "Конфиг Mac не найден: $MAC_CONFIG" >&2
    exit 2
fi

MAC_PORT="$(jq -r '.port' "$MAC_CONFIG")"
MAC_TOKEN="$(jq -r '.token' "$MAC_CONFIG")"
PEER_HOST="$(jq -r '.peers[1] // .peers[0]' "$MAC_CONFIG")"

echo "=== lanclip smoke ==="
echo "Mac: порт=$MAC_PORT, конфиг=$MAC_CONFIG"
echo "Сосед (для проверки /health c Mac): $PEER_HOST"
echo

echo "--- 1) /health: Mac -> Mac (локально) ---"
BODY="$(curl_with_token "$MAC_TOKEN" -s -m 5 --noproxy '*' "http://127.0.0.1:${MAC_PORT}/health" || true)"
echo "$BODY"
if echo "$BODY" | jq -e '.ok == true' >/dev/null 2>&1; then
    pass "Mac /health отвечает ok=true"
else
    fail "Mac /health не ответил ok=true: $BODY"
fi
echo

echo "--- 2) /health: Mac -> ПК ---"
BODY="$(curl_with_token "$MAC_TOKEN" -s -m 5 --noproxy '*' "http://${PEER_HOST}:${MAC_PORT}/health" || true)"
echo "$BODY"
if echo "$BODY" | jq -e '.ok == true' >/dev/null 2>&1; then
    pass "ПК /health отвечает ok=true (запрошено с Mac)"
else
    fail "ПК /health не ответил ok=true (запрошено с Mac): $BODY"
fi
echo

echo "--- 3) /health: ПК -> Mac (через SSH, PowerShell Invoke-WebRequest) ---"
# Инлайновый -Command здесь ненадёжен: вложенные кавычки PowerShell-скрипта
# (@{"x-clip-token"=...}) ломаются при склейке через ssh/bash. Поэтому скрипт
# сначала пишется во временный файл и копируется на ПК, а выполняется через
# -File — так же, как остальные PowerShell-проверки в этом проекте.
TMP_PS="$(mktemp -t lanclip-health-XXXXXX).ps1"
trap 'rm -f "$TMP_PS"' EXIT
cat > "$TMP_PS" <<PSEOF
\$ErrorActionPreference = "Stop"
try {
    \$cfg = Get-Content "$WIN_CONFIG" -Raw | ConvertFrom-Json
    \$peer = \$cfg.peers[1]
    if (-not \$peer) { \$peer = \$cfg.peers[0] }
    \$r = Invoke-WebRequest -Uri ("http://" + \$peer + ":" + \$cfg.port + "/health") -Headers @{"x-clip-token"=\$cfg.token} -UseBasicParsing -TimeoutSec 5
    Write-Output \$r.Content
} catch {
    Write-Output ("ERROR: " + \$_.Exception.Message)
}
PSEOF
# Кладём временный скрипт рядом с конфигом ПК — эта папка заведомо существует
# (в ней лежит config.json) и доступна на запись пользователю агента.
WIN_TMP="${WIN_TMP_DIR}\\smoke-health.ps1"
scp -q "$TMP_PS" "${SSH_PEER}:${WIN_TMP}"
BODY="$(ssh "$SSH_PEER" powershell -NoProfile -ExecutionPolicy Bypass -File "$WIN_TMP" 2>/dev/null || true)"
ssh "$SSH_PEER" "del \"$WIN_TMP\"" >/dev/null 2>&1 || true
echo "$BODY"
if echo "$BODY" | jq -e '.ok == true' >/dev/null 2>&1; then
    pass "Mac /health отвечает ok=true (запрошено с ПК по SSH)"
else
    fail "Mac /health не ответил ok=true (запрошено с ПК по SSH): $BODY"
fi
echo

echo "--- 4) lanclipd get (с Mac, манифест соседа-ПК) ---"
if OUTPUT="$("$MAC_BIN" get --config "$MAC_CONFIG" 2>&1)"; then
    echo "$OUTPUT"
    pass "lanclipd get (Mac) отработал без ошибки"
else
    echo "$OUTPUT"
    fail "lanclipd get (Mac) завершился с ошибкой"
fi
echo

echo "--- 5) lanclipd.exe get (по SSH на ПК, манифест соседа-Mac) ---"
if OUTPUT="$(ssh "$SSH_PEER" "$WIN_BIN" get --config "$WIN_CONFIG" 2>&1)"; then
    echo "$OUTPUT"
    pass "lanclipd.exe get (ПК по SSH) отработал без ошибки"
else
    echo "$OUTPUT"
    fail "lanclipd.exe get (ПК по SSH) завершился с ошибкой"
fi
echo

echo "=== Итог: $PASS пройдено, $FAIL упало ==="
[ "$FAIL" -eq 0 ]
