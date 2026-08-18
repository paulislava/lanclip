# lanclip — накопленные ошибки и грабли

Формат: симптом → причина → как проверить/воспроизвести → решение. Команды —
буквально те, что были прогнаны, а не пересказ, чтобы следующий человек мог
повторить проверку и получить тот же результат.

## Windows: SSH-сессия и интерактивный рабочий стол — разные буферы обмена

**Задача:** 22 (`win/src/WinClipboard.cs`, `win/tests/WinClipboardTests.cs`).

**Симптом:** код/тесты, обращающиеся к `System.Windows.Forms.Clipboard` через
`ssh paulislava@pc ...`, либо не видят содержимое буфера, которое реально видно
на экране ПК, либо запись в буфер из SSH-сессии никак не проявляется на
интерактивном рабочем столе.

**Причина:** OpenSSH Server на Windows работает как служба (`sshd.exe`), и
каждое SSH-соединение порождает процесс в **сессии `SessionId = 0`** —
неинтерактивной служебной сессии со своей изолированной оконной станцией
(window station) и, соответственно, своим отдельным системным буфером обмена.
Интерактивный рабочий стол пользователя (тот, что виден на экране/через RDP) —
отдельная сессия (в проверенном случае `SessionId = 1`) со своей оконной
станцией `WinSta0` и своим буфером. Буферы двух сессий не связаны и не
синхронизируются друг с другом.

**Как проверить принадлежность сессии** (команды, реально прогнанные 2026-08-18):

```powershell
# Внутри SSH-сессии (ssh paulislava@pc):
[System.Diagnostics.Process]::GetCurrentProcess().SessionId
# → 0

# Там же: какая сессия сейчас интерактивна на самой машине:
query session
# →  console       PaulIsLava             1  Активно
#    (SessionId интерактивной консоли = 1)
```

Если число из первой команды не совпадает с `SessionId` интерактивной консоли
из `query session` — сессии разные, буферы разные, результату сравнения
содержимого буфера через SSH доверять нельзя.

**Независимая проверка на живом содержимом** (не только `SessionId`, а
фактические данные — прогнано на реальной машине Павла):

```powershell
# По SSH (сессия 0):
Add-Type -AssemblyName System.Windows.Forms
[System.Windows.Forms.Clipboard]::ContainsText()   # → False (буфер сессии 0 пуст)

# Тем же кодом, но через разовую задачу планировщика в интерактивной сессии
# (см. обход ниже):
[System.Windows.Forms.Clipboard]::ContainsText()   # → True
[System.Windows.Forms.Clipboard]::GetText()        # → реальный текст с рабочего стола
```

**Обход** — разовая задача планировщика с интерактивным токеном пользователя;
создаётся и удаляется вокруг каждого прогона, ничего не остаётся на машине
после:

```powershell
Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue

$action    = New-ScheduledTaskAction -Execute "powershell.exe" -Argument $Command
$trigger   = New-ScheduledTaskTrigger -Once -At (Get-Date).AddSeconds(5)
$principal = New-ScheduledTaskPrincipal -UserId "PaulIsLava" -LogonType Interactive
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
    -Principal $principal -Force | Out-Null

Start-ScheduledTask -TaskName $TaskName
# дождаться завершения по опросу:
Get-ScheduledTaskInfo -TaskName $TaskName   # LastRunTime / LastTaskResult

Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
```

`$Command` — аргументы для `powershell.exe`, обычно
`-NoProfile -ExecutionPolicy Bypass -File <путь-к-скрипту-или-exe> ...`,
перенаправляющие вывод в файл (`*> путь\output.txt`), который затем читается
обратно по SSH обычным `Get-Content`.

**Вывод:** любой код или тест, которому нужен настоящий системный буфер
интерактивного пользователя на Windows, нельзя гонять напрямую через
SSH-сессию — только через разовую `Register-ScheduledTask` с
`-LogonType Interactive` и `-UserId` этого пользователя (для резидентного
агента то же самое решено постоянной задачей с `-AtLogOn`, см.
`docs/superpowers/specs/2026-08-18-lanclip-design.md`, раздел "Известные грабли,
заложенные в дизайн" → "Сессия рабочего стола").

**Файлы:** `win/tests/WinClipboardTests.cs`, `win/src/WinClipboard.cs`.
