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

echo "==> Сборка lanclipd (release)"
(cd "$MAC_DIR" && swift build -c release)

BUILT_BIN="$MAC_DIR/.build/release/lanclipd"
if [ ! -x "$BUILT_BIN" ]; then
    echo "Ошибка: бинарник не найден после сборки: $BUILT_BIN" >&2
    exit 1
fi

echo "==> Установка бинарника: $BIN_DEST"
mkdir -p "$HOME/.local/bin"
cp -f "$BUILT_BIN" "$BIN_DEST"
chmod +x "$BIN_DEST"

echo "==> Ad-hoc подпись (чтобы пересборка не сбрасывала разрешение Accessibility)"
codesign -s - --force "$BIN_DEST"

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

USER_ID="$(id -u)"

echo "==> Перезагрузка LaunchAgent"
# Уже загруженного агента может не быть (первая установка) — это ожидаемо и не повод падать.
launchctl bootout "gui/$USER_ID/$PLIST_LABEL" 2>/dev/null || true
launchctl bootstrap "gui/$USER_ID" "$PLIST_FILE"

cat <<MSG

Готово. lanclipd поставлен в $BIN_DEST и загружен как LaunchAgent.

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
MSG
