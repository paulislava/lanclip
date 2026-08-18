#!/usr/bin/env bash
# Установка lanclipd как LaunchAgent на Mac.
#
# Стабильный путь бинарника (~/.local/bin/lanclipd) и ad-hoc подпись важны по одной
# и той же причине: разрешение Accessibility в macOS привязано к пути и подписи
# исполняемого файла. Если ставить каждый раз во временную сборочную папку или
# оставлять бинарник без подписи, любая пересборка сбрасывает выданное разрешение
# и Ctrl+Shift+V перестаёт синтезировать нажатие.
#
# Идемпотентность: скрипт безопасно перезапускать. Существующий конфиг НЕ трогается
# (токен внутри может быть уже согласован со второй машиной), LaunchAgent
# перезагружается через bootout+bootstrap, а не дублируется.
set -euo pipefail

HERE="$(cd "$(dirname "$0")/.." && pwd)"
MAC_DIR="$HERE/mac"

BIN_DEST="$HOME/.local/bin/lanclipd"
CONFIG_DIR="$HOME/.config/lanclip"
CONFIG_FILE="$CONFIG_DIR/config.json"
LOG_FILE="$HOME/Library/Logs/lanclip.log"

PLIST_LABEL="space.paulislava.lanclip"
PLIST_FILE="$HOME/Library/LaunchAgents/$PLIST_LABEL.plist"
USER_ID="$(id -u)"

echo "==> Сборка lanclipd (release)"
(cd "$MAC_DIR" && swift build -c release)

BUILT_BIN="$MAC_DIR/.build/release/lanclipd"
if [ ! -x "$BUILT_BIN" ]; then
    echo "Ошибка: бинарник не найден после сборки: $BUILT_BIN" >&2
    exit 1
fi

# Остановка агента — ДО того, как трогаем бинарник по $BIN_DEST. На повторной
# установке там уже может жить старый процесс под KeepAlive=true: если сначала
# усечь файл через cp -f и тут же принудительно сменить подпись под ним, у живого
# процесса есть неконтролируемое окно, в котором система может его прибить в
# произвольный момент — в том числе посреди pull() и записи файлов в стейджинг.
# Агента может не быть вовсе (первая установка) — это ожидаемо и не повод падать.
echo "==> Остановка прежнего агента (если был)"
launchctl bootout "gui/$USER_ID/$PLIST_LABEL" 2>/dev/null || true

echo "==> Установка бинарника: $BIN_DEST"
mkdir -p "$HOME/.local/bin"
cp -f "$BUILT_BIN" "$BIN_DEST"
chmod +x "$BIN_DEST"

# Подпись устойчивой личностью, а НЕ ad-hoc.
#
# Ad-hoc (`codesign -s -`) здесь не годится, хотя раньше стояла именно она:
# у ad-hoc подписи нет устойчивого идентификатора — он выводится из хеша
# содержимого (`lanclipd-555549446a8d…`), поэтому каждая пересборка выглядит
# для macOS новой программой, и выданные ей разрешения TCC (Local Network,
# Accessibility) к новому бинарнику не относятся. Это ломало агент при
# каждой переустановке: `pull` начинал отвечать `noPeer`, потому что
# исходящие соединения в локальную сеть блокировались, а синтез вставки
# молча перестал работать.
#
# Подпись сертификатом разработчика плюс фиксированный идентификатор дают
# устойчивое designated requirement, которое пересборка не меняет, поэтому
# разрешения выдаются один раз.
SIGN_IDENTITY="${LANCLIP_SIGN_IDENTITY:-Apple Development: Pavel Kondratov (9CBTJK8ALK)}"
SIGN_ID="space.paulislava.lanclip"

echo "==> Подпись: $SIGN_IDENTITY (идентификатор $SIGN_ID)"
if ! codesign -s "$SIGN_IDENTITY" -i "$SIGN_ID" --force "$BIN_DEST" 2>/dev/null; then
    echo "ОШИБКА: не удалось подписать сертификатом «$SIGN_IDENTITY»." >&2
    echo "Доступные личности для подписи кода:" >&2
    security find-identity -v -p codesigning >&2
    echo "Задай нужную через LANCLIP_SIGN_IDENTITY=… и повтори." >&2
    echo "Ad-hoc подпись здесь не подходит: она сбрасывает разрешения при каждой пересборке." >&2
    exit 1
fi

# Проверяем, что личность действительно устойчивая: идентификатор наш,
# а подпись — не ad-hoc. Иначе разрешения снова будут слетать, и узнаем мы
# об этом только когда агент перестанет видеть соседа.
SIGN_INFO="$(codesign -dv "$BIN_DEST" 2>&1)"
if echo "$SIGN_INFO" | grep -q "Signature=adhoc"; then
    echo "ОШИБКА: подпись оказалась ad-hoc — разрешения будут слетать при пересборке." >&2
    exit 1
fi
if ! echo "$SIGN_INFO" | grep -q "Identifier=$SIGN_ID"; then
    echo "ОШИБКА: идентификатор подписи не $SIGN_ID:" >&2
    echo "$SIGN_INFO" | grep Identifier >&2
    exit 1
fi
echo "    подпись устойчивая, идентификатор $SIGN_ID"

echo "==> Конфиг: $CONFIG_FILE"
mkdir -p "$CONFIG_DIR"
if [ -f "$CONFIG_FILE" ]; then
    echo "    уже существует, не трогаю (токен внутри может быть согласован со второй машиной)"
else
    TOKEN="$("$HERE/scripts/gen-token.sh")"
    # umask на время создания файла — права 0600 выставляются самим актом создания,
    # без отдельного окна между write и chmod, где содержимое читалось бы кем угодно.
    (
        umask 177
        cat > "$CONFIG_FILE" <<JSON
{
  "port": 8901,
  "token": "$TOKEN",
  "peers": [],
  "maxBytes": 536870912,
  "autoPaste": true
}
JSON
    )
    echo "    создан новый конфиг: токен сгенерирован, peers пуст"
fi
chmod 600 "$CONFIG_FILE"

echo "==> LaunchAgent: $PLIST_FILE"
mkdir -p "$HOME/Library/LaunchAgents"
mkdir -p "$HOME/Library/Logs"
cat > "$PLIST_FILE" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>$PLIST_LABEL</string>
    <key>ProgramArguments</key>
    <array>
        <string>$BIN_DEST</string>
        <string>serve</string>
        <string>--config</string>
        <string>$CONFIG_FILE</string>
    </array>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <true/>
    <key>StandardOutPath</key>
    <string>$LOG_FILE</string>
    <key>StandardErrorPath</key>
    <string>$LOG_FILE</string>
</dict>
</plist>
PLIST

echo "==> Загрузка LaunchAgent"
launchctl bootstrap "gui/$USER_ID" "$PLIST_FILE"

# Код возврата bootstrap подтверждает только то, что launchd принял задание, а не
# то, что процесс держится, а не ушёл в перезапуск. Даём агенту секунду устояться
# и явно перепроверяем "state = running" из launchctl print — именно эта проверка
# поймала бы краш-луп (например, из-за конфига, который serve отвергает на старте),
# если он когда-нибудь вернётся в другом виде.
echo "==> Проверка, что агент поднялся и держится"
STATE=""
for attempt in 1 2 3; do
    sleep 1
    STATE="$(launchctl print "gui/$USER_ID/$PLIST_LABEL" 2>/dev/null | grep -m1 $'^\tstate = ' | awk '{print $3}')"
    [ "$STATE" = "running" ] && break
done

if [ "$STATE" != "running" ]; then
    echo "Ошибка: после bootstrap агент не в состоянии running (state=${STATE:-нет данных})." >&2
    echo "Смотрите лог: $LOG_FILE" >&2
    echo "И вывод: launchctl print gui/$USER_ID/$PLIST_LABEL" >&2
    exit 1
fi

cat <<MSG

Готово. lanclipd поставлен в $BIN_DEST и загружен как LaunchAgent (state=running).

Проверить:
  launchctl print gui/$USER_ID/$PLIST_LABEL
  $BIN_DEST status
  tail -n 50 $LOG_FILE

ВАЖНО — разрешения выдаются вручную, программно их не получить:

1. Accessibility (нужно для синтеза Ctrl+Shift+V):
   Системные настройки → Конфиденциальность и безопасность → Accessibility →
   добавить "+" → перейти по точному пути $BIN_DEST (Cmd+Shift+G в диалоге
   выбора файла) → включить переключатель.

2. Local Network (нужно, чтобы видеть соседа по локальной сети):
   На macOS 15+ диалог запроса разрешения фоновому LaunchAgent может не
   показаться вовсе. Чтобы диалог гарантированно появился, сделайте первый
   запуск руками из Терминала:
     launchctl bootout gui/$USER_ID/$PLIST_LABEL
     $BIN_DEST serve --config $CONFIG_FILE
   Дождитесь диалога, разрешите доступ, остановите (Ctrl+C) и верните
   LaunchAgent обратно:
     launchctl bootstrap gui/$USER_ID $PLIST_FILE
   Разрешение привязано к пути и подписи бинарника, а не к тому, как именно
   он был запущен — дальше LaunchAgent будет работать с уже выданным доступом.

Подробнее: docs/mac-setup.md
MSG
