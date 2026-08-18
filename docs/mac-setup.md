# Mac-агент lanclip: установка

Минимальная памятка. Развёрнутая документация проекта — задача 27.

## Установка

```
./scripts/install-mac.sh
```

Идемпотентно: повторный запуск безопасен, существующий конфиг не трогает.

Что ставит:
- бинарник — `~/.local/bin/lanclipd` (стабильный путь, подпись сертификатом
  разработчика с фиксированным идентификатором `space.paulislava.lanclip` —
  ad-hoc здесь не годится, см. `ai/ERRORS.md`);
- конфиг — `~/.config/lanclip/config.json`, права `0600` (создаётся один раз,
  с пустым `peers`, если файла ещё не было — второй машине адрес пишете вручную);
- лог — `~/Library/Logs/lanclip.log`;
- LaunchAgent — `~/Library/LaunchAgents/space.paulislava.lanclip.plist`
  (`RunAtLoad`, `KeepAlive`), слушает порт 8901.

## Первый запуск — обязательно руками из Терминала

На macOS 15+ диалог разрешения **Local Network** фоновому LaunchAgent может не
показаться вовсе. Чтобы он гарантированно появился, после установки нужно один
раз запустить агент интерактивно:

```
launchctl bootout gui/$(id -u)/space.paulislava.lanclip
~/.local/bin/lanclipd serve --config ~/.config/lanclip/config.json
```

Дождитесь диалога, разрешите доступ, остановите (Ctrl+C) и верните LaunchAgent:

```
launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/space.paulislava.lanclip.plist
```

Разрешение привязано к пути и подписи бинарника, а не к тому, как именно он был
запущен — дальше LaunchAgent работает с уже выданным доступом.

## Разрешения

Точный путь бинарника везде один: **`~/.local/bin/lanclipd`**.

1. **Accessibility** (иначе синтез Ctrl+Shift+V молча не работает, диалог сам не
   появляется): Системные настройки → Конфиденциальность и безопасность →
   Accessibility → «+» → Cmd+Shift+G в диалоге выбора файла → вставить
   `~/.local/bin/lanclipd` → добавить и включить переключатель.
2. **Local Network** — см. «Первый запуск» выше.

Оба разрешения выдаются только руками — программно их получить нельзя.

## Проверка, что агент жив

```
launchctl print gui/$(id -u)/space.paulislava.lanclip   # ожидаем state = running
~/.local/bin/lanclipd status                            # порт, буфер, сосед
tail -n 50 ~/Library/Logs/lanclip.log                    # ожидаем пусто/без ошибок
```
