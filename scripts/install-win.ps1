# Установка lanclipd как резидентной службы на Windows: сборка, бинарник,
# конфиг, urlacl, правило файрвола и задача планировщика с автозапуском при
# входе пользователя.
#
# Идемпотентность: скрипт безопасно перезапускать. Существующий конфиг НЕ
# трогается (токен внутри может быть уже согласован со второй машиной, см.
# задачу 26), собственная задача планировщика снимается и создаётся заново
# (иначе Register-ScheduledTask падает на дубликате), urlacl и правило
# файрвола пересоздаются только если принадлежат нам самим.
#
# Обязательно интерактивная сессия: агенту нужен настоящий системный буфер
# обмена (System.Windows.Forms.Clipboard) и трей-иконка, а они существуют
# только в оконной станции WinSta0 интерактивного рабочего стола пользователя
# (см. ai/ERRORS.md, "Windows: SSH-сессия и интерактивный рабочий стол — разные
# буферы обмена"). SSH-сессия администратора живёт в SessionId=0 и для этого
# не годится — отсюда триггер AtLogOn с -LogonType Interactive и явный
# пользователь из WindowsIdentity, а не из $env:USERDOMAIN (последний
# возвращает имя рабочей группы/домена, а не имя машины, и ломает netsh).
#
# Порядок шагов важен: сборка не трогает уже развёрнутый бинарник (пишет в
# win\out\), поэтому её можно делать в любой момент. А вот старую задачу
# планировщика (и, если она почему-то пережила Stop-ScheduledTask, сам процесс
# lanclipd.exe) обязательно останавливаем ДО того, как перезаписываем
# развёрнутый бинарник — иначе есть окно, где старый процесс ещё жив, а файл
# под ним уже усечён (тот же класс бага, что ревью нашло на Mac-стороне).

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

function Write-Step {
    param([string]$Message)
    Write-Host "==> $Message"
}

# IPv4-адрес + длина префикса -> адрес сети в CIDR-нотации ("192.168.1.184",
# 24 -> "192.168.1.0/24"). Нужна для правила файрвола (Шаг 6): вместо
# встроенного ключевого слова "LocalSubnet" (оно объединяет подсети ВСЕХ
# активных адаптеров машины, включая виртуальные — см. комментарий у Шага 6)
# правило получает точную подсеть одного конкретного физического адаптера,
# вычисленную на месте.
function Get-Ipv4NetworkCidr {
    param([string]$IPAddress, [int]$PrefixLength)
    $ipBytes = [System.Net.IPAddress]::Parse($IPAddress).GetAddressBytes()
    $maskBytes = New-Object byte[] 4
    for ($i = 0; $i -lt 4; $i++) {
        $bitsInByte = [Math]::Min(8, [Math]::Max(0, $PrefixLength - ($i * 8)))
        if ($bitsInByte -gt 0) {
            $maskBytes[$i] = [byte](256 - [Math]::Pow(2, 8 - $bitsInByte))
        }
    }
    $networkBytes = New-Object byte[] 4
    for ($i = 0; $i -lt 4; $i++) {
        $networkBytes[$i] = $ipBytes[$i] -band $maskBytes[$i]
    }
    return ($networkBytes -join ".") + "/" + $PrefixLength
}

# --- Права администратора: urlacl и правило файрвола без них не поставить. ---
$principal = New-Object Security.Principal.WindowsPrincipal(
    [Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
    Write-Error "Скрипт требует прав администратора (нужны netsh http add urlacl и New-NetFirewallRule)."
    exit 1
}

# --- Пути и константы. ---
$Port = 8901
$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Split-Path -Parent $ScriptRoot
$WinDir = Join-Path $RepoRoot "win"
$BuildScript = Join-Path $WinDir "build.ps1"
$BuiltExe = Join-Path $WinDir "out\lanclipd.exe"

$BinDir = Join-Path $env:USERPROFILE ".local\bin"
$BinDest = Join-Path $BinDir "lanclipd.exe"

$ConfigDir = Join-Path $env:USERPROFILE ".config\lanclip"
$ConfigFile = Join-Path $ConfigDir "config.json"

$TaskName = "lanclip"
$FirewallDisplayName = "lanclip"

# WindowsIdentity, а НЕ $env:USERDOMAIN\$env:USERNAME: на этой машине
# $env:USERDOMAIN возвращает "WORKGROUP" (имя рабочей группы), а не имя
# компьютера — netsh http add urlacl с таким user= не совпал бы с реальным
# владельцем сессии, и неэлевированный процесс не смог бы слушать "+".
$CurrentUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name

Write-Host "Пользователь для urlacl/задачи планировщика: $CurrentUser"

# --- Шаг 1: сборка. build.ps1 сам гоняет тесты и падает не-нулевым кодом,
# если они красные; здесь просто прокидываем этот код дальше. Сборка пишет
# только во временную win\out\ и не трогает уже развёрнутый $BinDest, поэтому
# её безопасно делать до остановки старого процесса. ---
Write-Step "Сборка lanclipd (build.ps1, включая тесты)"
& $BuildScript
if ($LASTEXITCODE -ne 0) {
    Write-Error "Сборка или тесты упали с кодом $LASTEXITCODE — установка прервана, ничего на машине не менялось."
    exit 1
}
if (-not (Test-Path $BuiltExe)) {
    Write-Error "После успешной сборки не найден $BuiltExe — установка прервана."
    exit 1
}

# --- Шаг 2: остановка прежней версии — ДО замены бинарника. ---
Write-Step "Остановка прежней задачи планировщика (если была)"
$existingTask = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($existingTask) {
    Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    Write-Host "    прежняя задача снята"
} else {
    Write-Host "    прежней задачи не было"
}

# Подстраховка: Stop-ScheduledTask останавливает отслеживаемый экземпляр
# задачи, но если процесс каким-то образом отвязался от него, он остался бы
# жив под уже неактуальным бинарником. Трогаем только процессы с именем
# lanclipd — ничего постороннего.
$staleProcesses = Get-Process -Name "lanclipd" -ErrorAction SilentlyContinue
if ($staleProcesses) {
    Write-Host "    обнаружен незавершённый процесс lanclipd — останавливаю принудительно"
    $staleProcesses | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
}

# --- Шаг 3: установка бинарника. Ретрай на случай, если файл на секунду
# дольше держится системой после завершения процесса. ---
Write-Step "Установка бинарника: $BinDest"
New-Item -ItemType Directory -Force -Path $BinDir | Out-Null

$copied = $false
for ($attempt = 1; $attempt -le 5; $attempt++) {
    try {
        Copy-Item -Path $BuiltExe -Destination $BinDest -Force
        $copied = $true
        break
    } catch {
        Start-Sleep -Milliseconds 500
    }
}
if (-not $copied) {
    Write-Error "Не удалось скопировать $BuiltExe в $BinDest — файл занят? Установка прервана."
    exit 1
}

# --- Шаг 4: конфиг — создаём только если его ещё нет. Существующий конфиг не
# трогаем: токен внутри может быть уже согласован с Mac-агентом (задача 26). ---
Write-Step "Конфиг: $ConfigFile"
if (Test-Path $ConfigFile) {
    Write-Host "    уже существует, не трогаю (токен внутри может быть согласован со второй машиной)"
} else {
    New-Item -ItemType Directory -Force -Path $ConfigDir | Out-Null

    # 16 случайных байт -> 32 шестнадцатеричных символа, тем же способом, что
    # и scripts/gen-token.sh на Mac-стороне (xxd -l 16 -p /dev/urandom) и
    # Config.GenerateToken() в самом агенте (RNGCryptoServiceProvider, 16 байт).
    $tokenBytes = New-Object byte[] 16
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $rng.GetBytes($tokenBytes)
    } finally {
        $rng.Dispose()
    }
    $token = -join ($tokenBytes | ForEach-Object { $_.ToString("x2") })

    $configJson = @"
{
  "port": $Port,
  "token": "$token",
  "peers": [],
  "maxBytes": 536870912,
  "autoPaste": true
}
"@

    # UTF-8 БЕЗ BOM: Set-Content -Encoding utf8 в PowerShell пишет BOM и ломает
    # разбор конфига (Config.Load читает файл как обычный UTF-8 текст).
    [IO.File]::WriteAllText($ConfigFile, $configJson, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "    создан новый конфиг: токен сгенерирован, peers пуст"
}

# --- Шаг 5: urlacl. Без записи неэлевированная задача не сможет слушать
# "http://+:$Port/". Идемпотентно: если резервация уже наша — пересоздаём
# (не оставляем возможное расхождение параметров), если чужая — не трогаем и
# падаем с понятным сообщением. ---
Write-Step "urlacl для http://+:$Port/"
$urlPrefix = "http://+:$Port/"
$urlAclShow = (netsh http show urlacl url=$urlPrefix | Out-String)
$reservationExists = $urlAclShow -match [regex]::Escape($urlPrefix)

if ($reservationExists) {
    if ($urlAclShow -match [regex]::Escape($CurrentUser)) {
        Write-Host "    уже зарегистрирован для $CurrentUser — пересоздаю"
        netsh http delete urlacl url=$urlPrefix | Out-Null
    } else {
        Write-Error "urlacl для $urlPrefix уже занят кем-то другим — не трогаю чужую резервацию.`nВывод netsh:`n$urlAclShow"
        exit 1
    }
}
netsh http add urlacl url=$urlPrefix "user=$CurrentUser" | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Error "netsh http add urlacl упал с кодом $LASTEXITCODE"
    exit 1
}
Write-Host "    готово"

# --- Шаг 6: правило файрвола. ---
#
# ОТКЛОНЕНИЕ ОТ БРИФА: бриф просит -Profile Private. Проверка на живой машине
# показала, что активный сетевой адаптер (беспроводная сеть, тот самый, на
# котором у ПК LAN-адрес 192.168.1.184) классифицирован Windows как Public
# (NetworkCategory=0), а не Private — при этом все три профиля файрвола
# (Domain/Private/Public) включены с DefaultInboundAction=Block. Правило,
# ограниченное Private, никогда не сработало бы для реального трафика с Mac и
# автозапуск выглядел бы рабочим (Get-ScheduledTask показывает задачу), но
# /health с Mac не отвечал бы — то есть буквальное следование брифу не
# прошло бы собственную же проверку из Шага 3 брифа. Меняю профиль на Any.
#
# Но -Profile Any без ограничений открыл бы порт для входящих с ЛЮБОЙ сети,
# в которую машина когда-либо попадёт (кафе, чужой Wi-Fi) — профиль тут не
# помощник, раз реальная сеть уже сегодня классифицирована как Public. Чинить
# это через Set-NetConnectionProfile (принудительно пометить адаптер как
# Private) не вариант: это системная настройка доверия, которая влияет на
# общий доступ к файлам, обнаружение в сети и все остальные правила профиля
# Private — а адаптер к тому же может снова переклассифицироваться сам при
# следующем переподключении, и придётся гоняться за этим бесконечно.
#
# Вместо этого сужаю правило по адресу источника — НЕ встроенным ключевым
# словом "LocalSubnet". Проверка на этой же машине показала, что LocalSubnet
# вычисляется как ОБЪЕДИНЕНИЕ подсетей ВСЕХ активных адаптеров сразу — на
# этом ПК их несколько одновременно (реальный Wi-Fi, vEthernet(WSL),
# happ-tun, при активном подключении ещё и OpenVPN TAP), и часть из них уже
# сегодня сидит в приватных диапазонах (10/8, 172.16/12), которые
# HttpServer.IsPrivateAddress тоже сочтёт "приватными". LocalSubnet впустил
# бы трафик из подсети VPN/туннеля наравне с настоящей LAN, а при роуминге
# машины в чужую сеть — из ЭТОЙ чужой сети тоже (та же угроза, из-за которой
# первая правка вообще понадобилась).
#
# Поэтому вычисляю точную подсеть одного конкретного адаптера — физического
# (Get-NetAdapter -> Virtual = $false, у виртуальных коммутаторов/VPN/TAP
# всегда $true) и поднятого (Status = Up). Если такой адаптер не находится
# однозначно (ни одного или больше одного) — останавливаюсь с понятной
# ошибкой вместо того, чтобы разрешить лишнее по догадке. Подсеть
# пересчитывается заново при каждой установке, так что смена адресации
# роутером не требует правки скрипта.
Write-Step "Определение локальной подсети для правила файрвола"
$lanAdapters = @(Get-NetAdapter | Where-Object { -not $_.Virtual -and $_.Status -eq "Up" })
if ($lanAdapters.Count -eq 0) {
    Write-Error "Не нашлось ни одного физического (невиртуального) сетевого адаптера в состоянии Up — не могу определить локальную подсеть для правила файрвола. Впишите её вручную и адаптируйте скрипт."
    exit 1
}
if ($lanAdapters.Count -gt 1) {
    $names = ($lanAdapters | ForEach-Object { "$($_.Name) ($($_.InterfaceDescription))" }) -join "; "
    Write-Error "Найдено больше одного физического активного адаптера — не могу однозначно выбрать LAN-подсеть: $names. Впишите нужную подсеть в правило файрвола вручную."
    exit 1
}

$lanAdapter = $lanAdapters[0]
$lanIP = Get-NetIPAddress -InterfaceIndex $lanAdapter.InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -notlike "169.254.*" } | Select-Object -First 1
if (-not $lanIP) {
    Write-Error "У адаптера '$($lanAdapter.Name)' нет обычного IPv4-адреса (только APIPA 169.254.x.x или его нет вовсе) — не могу вычислить подсеть для правила файрвола."
    exit 1
}

$lanSubnet = Get-Ipv4NetworkCidr -IPAddress $lanIP.IPAddress -PrefixLength $lanIP.PrefixLength
Write-Host "    адаптер: $($lanAdapter.Name) ($($lanAdapter.InterfaceDescription)), подсеть: $lanSubnet"

Write-Step "Правило файрвола: TCP $Port, вход, любой профиль, только из $lanSubnet"
$existingRule = Get-NetFirewallRule -DisplayName $FirewallDisplayName -ErrorAction SilentlyContinue
if ($existingRule) {
    Write-Host "    уже существует — пересоздаю"
    $existingRule | Remove-NetFirewallRule
}
New-NetFirewallRule -DisplayName $FirewallDisplayName -Direction Inbound -LocalPort $Port `
    -Protocol TCP -Action Allow -Profile Any -RemoteAddress $lanSubnet | Out-Null
Write-Host "    готово"

# --- Шаг 7: задача планировщика. ---
#
# lanclipd.exe собран как консольное приложение (build.ps1: "/target:exe",
# см. комментарий там же — окно резидентного serve прячется настройкой задачи
# планировщика, а не /target:winexe). Поэтому Action запускает не сам .exe
# напрямую, а скрытый powershell.exe-обёртку (-WindowStyle Hidden): дочерний
# консольный процесс наследует уже скрытую консоль родителя вместо того,
# чтобы открыть свою видимую. "Run whether user is logged on or not" сюда не
# годится: такой режим работает в служебной сессии (SessionId=0, без
# оконной станции WinSta0) — ровно та поломка из ai/ERRORS.md.
Write-Step "Задача планировщика: $TaskName"

# "; exit $LASTEXITCODE" в конце — не украшение. RestartCount/RestartInterval
# ниже срабатывают у Task Scheduler по коду возврата ДЕЙСТВИЯ задачи, то есть
# этого powershell.exe-обёртки, а не lanclipd.exe напрямую. Без явного exit
# код возврата wrapper'а определяется тем, чем завершится сам powershell.exe
# -Command, а не гарантированно кодом упавшей нативной команды — при падении
# lanclipd.exe планировщик рисковал бы увидеть "успешное" завершение обёртки
# и не перезапустить агента вовсе, то есть спроектированная страховка была бы
# мертворождённой. $LASTEXITCODE после "&" на нативный .exe — это как раз код
# возврата именно lanclipd.exe.
$innerCommand = "& '{0}' serve --config '{1}'; exit `$LASTEXITCODE" -f $BinDest, $ConfigFile
$wrapperArgs = "-NoProfile -WindowStyle Hidden -Command `"$innerCommand`""

$action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument $wrapperArgs
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $CurrentUser
$principalTask = New-ScheduledTaskPrincipal -UserId $CurrentUser -LogonType Interactive -RunLevel Limited

# ExecutionTimeLimit по умолчанию у Task Scheduler — 72 часа: по истечении
# этого времени задача, задуманная как постоянно работающая служба, была бы
# молча убита планировщиком без единой ошибки в самом lanclipd. Zero снимает
# лимит — это подтверждено проверкой (см. отчёт задачи) и обязательно.
#
# RestartCount/RestartInterval — задуманы как страховка на случай падения
# процесса: если сработают, планировщик поднимет его заново сам, не дожидаясь
# следующего входа в систему. Оставляю настройку (вреда от неё нет), но
# честно: живой проверкой на этой машине автоподъём подтвердить НЕ удалось —
# ни принудительное завершение lanclipd.exe, ни изолированный тестовый Task
# с чистым "exit 1" не привели к повторному запуску за несколько минут при
# сконфигурированном RestartInterval в 1 минуту (подробности и дословный
# вывод — в отчёте задачи 25). Это не обязательное свойство для задачи —
# обязателен автозапуск при входе в систему, который подтверждён отдельно
# (Get-ScheduledTask + Start-ScheduledTask). Если это когда-нибудь всплывёт
# как реальная проблема (агент упал и не поднялся сам), разбираться заново
# отсюда — этот комментарий и есть та зацепка.
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) `
    -MultipleInstances IgnoreNew

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
    -Principal $principalTask -Settings $settings -Force | Out-Null
Write-Host "    зарегистрирована"

# --- Шаг 8: запуск и проверка, что процесс реально держится. ---
#
# Код возврата Register-ScheduledTask/Start-ScheduledTask подтверждает только
# то, что планировщик принял задание — не то, что процесс поднялся, слушает
# порт и отвечает (например, из-за конфига, который serve отверг бы на
# старте, из-за неудавшейся регистрации хоткея, или просто потому что
# HttpListener ещё не успел завершить привязку сокета). Поэтому здесь
# опрашиваем все три признака — состояние задачи, наличие процесса и
# локальный /health — В ОДНОМ цикле повторов, а не полагаемся на код
# возврата и не проверяем /health однократно уже после того, как цикл для
# задачи/процесса завершился: холодный старт сразу после копирования
# свежего бинарника (антивирус может ещё его проверять) или просто чуть более
# долгая привязка сокета — не повод ронять корректную установку из-за
# секундного зазора в таймингах.
Write-Step "Запуск задачи и проверка, что агент поднялся (задача, процесс, /health)"
Start-ScheduledTask -TaskName $TaskName

$taskRunning = $false
$processAlive = $false
$healthOk = $false
$healthDetail = "проверка ещё не выполнялась"
for ($attempt = 1; $attempt -le 15; $attempt++) {
    Start-Sleep -Seconds 1
    $state = (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue).State
    $taskRunning = ($state -eq "Running")
    $processAlive = [bool](Get-Process -Name "lanclipd" -ErrorAction SilentlyContinue)

    if ($taskRunning -and $processAlive) {
        try {
            $configText = [IO.File]::ReadAllText($ConfigFile)
            if ($configText -match '"token"\s*:\s*"([0-9a-f]+)"') {
                $localToken = $Matches[1]
                $response = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/health" `
                    -Headers @{ "X-Clip-Token" = $localToken } -UseBasicParsing -TimeoutSec 5
                $healthOk = ($response.StatusCode -eq 200)
                $healthDetail = "статус ответа: $($response.StatusCode)"
            } else {
                $healthDetail = "конфиг прочитан, но в нём не нашёлся токен"
            }
        } catch {
            $healthOk = $false
            $healthDetail = "исключение: $($_.Exception.Message)"
        }
    }

    if ($taskRunning -and $processAlive -and $healthOk) {
        break
    }
}

if (-not $taskRunning) {
    Write-Error "Задача $TaskName не в состоянии Running после запуска (state=$state). Установка не завершена."
    exit 1
}
if (-not $processAlive) {
    Write-Error "Задача Running, но процесс lanclipd не найден — похоже, ушёл в перезапуск сразу после старта. Установка не завершена."
    exit 1
}
$attemptsUsed = [Math]::Min($attempt, 15)
if (-not $healthOk) {
    Write-Error "Локальный /health так и не ответил 200 за $attemptsUsed попыток ($healthDetail) — задача Running и процесс жив, но сервер не обслуживает запросы. Установка не завершена."
    exit 1
}
Write-Host "    /health локально отвечает 200 (попытка $attemptsUsed из 15)"

Write-Host ""
Write-Host "Готово. lanclipd поставлен в $BinDest, задача '$TaskName' зарегистрирована и запущена (state=Running)."
Write-Host "Проверить:"
Write-Host "  Get-ScheduledTask $TaskName"
Write-Host "  Get-Process lanclipd"
Write-Host "  netsh http show urlacl url=$urlPrefix"
Write-Host "  Get-NetFirewallRule -DisplayName $FirewallDisplayName"
