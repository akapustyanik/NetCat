# NetCat — Windows Native Hybrid-VPN & DPI Bypass

![NetCat Logo](assets/logo.png)

**NetCat** — высокопроизводительное нативное приложение для Windows 10 / 11 (x64, ARM64) на базе **.NET 8.0 LTS** и **WPF** в современном стиле **Windows 11 Fluent Design (Mica)**.

Приложение объединяет L3 VPN-маршрутизацию, аппаратный DPI-байпас на уровне физического адаптера, встроенный WebSocket-прокси для Telegram и интеллектуальную систему отказоустойчивости с нулевой задержкой.

---

## 🌟 Ключевые возможности

* **Нативный стек Windows 11:** Никакого Electron, CEF или WebView2. Плавная работа интерфейса с аппаратным ускорением DirectX / DirectWrite, поддержка эффекта Mica и темной темы.
* **L3 TUN сетевой стек нулевого копирования:** Интеграция драйвера **Wintun v0.14+** (виртуальный адаптер `172.19.0.1/30`).
* **L2/L3 DPI Bypass (Zapret):** Интеграция с [Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube) через пакетный фильтр **WinDivert** (стратегия `ALT13`, разблокировка Discord, YouTube и прямого доступа на полной скорости провайдера).
* **Встроенный Telegram WS-Proxy:** Интеграция с [Flowseal/tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy) для мгновенного прямого подключения Telegram Desktop на порту `127.0.0.1:10852` без необходимости внешнего VPN.
* **Сетевые ядра:**
  * **sing-box v1.14+** (официальный `SagerNet/sing-box`) — первичный L3/L4 маршрутизатор, DNS-резолвер, поддержка протоколов VLESS (XTLS Reality), VMess, Trojan, Shadowsocks, Hysteria2, TUIC.
  * **Xray-core** (`XTLS/Xray-core`) — вспомогательный мост для gRPC fallback.
  * **OpenVPN** (`OpenVPN/openvpn`) — поддержка классических `.ovpn` профилей.
* **Dual-Path Failover (Отказоустойчивость с нулевым разрывом):**
  * **Синхронный TCP Racing (Active-Active):** параллельная отправка SYN через два независимых протокола, выбор минимального RTT.
  * **Hot Standby:** постоянный мониторинг конечных точек (`generate_204`) с автоматическим переключением на резервный узел без перезапуска TUN-адаптера.
* **Мониторинг трафика в реальном времени:** Аппаратный компонент `TrafficGraph` на базе `DrawingContext` (DirectX/WPF) с частотой обновления 1 Гц (без выделения памяти под DOM-структуры).
* **Редактор конфигураций:** Встроенный JSON-редактор на базе **AvalonEdit** с подсветкой синтаксиса и валидацией схемы.
* **Системная интеграция:**
  * Автозапуск с наивысшими правами без всплывающего окна UAC через Windows Task Scheduler (`schtasks.exe` / `ITaskService`).
  * Системный трей через Win32 API (`Shell_NotifyIcon`) со сменой иконок статуса и контекстным меню.
  * Гарантированное завершение дочерних процессов при выходе через Windows Job Objects.
  * Очистка статических маршрутов и DNS-кэша при выключении.

---

## 📁 Структура проекта

```text
NetCat/
├── assets/
│   ├── icons/                 # app.ico, tray_active.ico, tray_idle.ico, tray_error.ico
│   └── logo.png               # Фирменный кот-космонавт NetCat
├── bin/
│   ├── sing-box/              # Бинарные файлы SagerNet/sing-box
│   ├── xray/                  # Бинарные файлы XTLS/Xray-core
│   ├── openvpn/               # Бинарные файлы OpenVPN и wintun.dll
│   ├── zapret/                # Бинарные файлы Flowseal/zapret-discord-youtube (winws.exe, WinDivert)
│   ├── tg-ws-proxy/           # Бинарные файлы Flowseal/tg-ws-proxy (TgWsProxy_windows.exe)
│   └── modules_manifest.json  # Манифест версий модулей и SHA-256
├── data/
│   ├── appsettings.json       # Конфигурация приложения
│   ├── routing_rules.json     # Правила маршрутизации трафика
│   └── sing-box-template.json # Базовый шаблон конфигурации sing-box
├── src/
│   ├── NetCat.Core/           # Доменные модели, перечисления, интерфейсы
│   ├── NetCat.Engine/         # Супервизор процессов, билдеры конфигураций, парсеры протоколов
│   ├── NetCat.Network/        # Failover-движок, Wintun адаптер, таблица маршрутизации, метрики
│   ├── NetCat.Updater/        # Дифференциальный апдейтер модулей, проверка хэшей SHA-256
│   └── NetCat.UI/             # Графический интерфейс WPF, Windows 11 Fluent Design, Tray
├── tests/
│   └── NetCat.Tests/          # Unit-тесты парсеров, билдеров конфигураций и валидаторов
├── Publish-NetCatRelease.ps1   # Автоматическая сборка self-contained релиза
├── Invoke-NetCatLocalRelease.ps1 # Локальный запуск сборки и верификации
├── Test-NetCatPackage.ps1     # Валидация пакета, манифеста и контрольных сумм
├── RELEASE_GUIDE.md           # Подробная памятка по оформлению релизов
└── NetCat.sln
```

---

## 🛠️ Сборка и тестирование

### Требования
* Windows 10 / 11 (x64)
* .NET SDK 8.0 или выше

### Сборка решения
```powershell
dotnet build NetCat.sln -c Release
```

### Запуск тестов
```powershell
dotnet test NetCat.sln
```

### Сборка релизного пакета
```powershell
.\Invoke-NetCatLocalRelease.ps1
```
Готовый self-contained архив `NetCat-v<версия>.zip` и файл контрольной суммы `.sha256` будут сформированы в каталоге `artifacts/`.

---

## 📦 Оформление релизов

Подробные инструкции по оформлению, версионированию, структуре changelog и публикации релизов на GitHub находятся в файле:
👉 **[RELEASE_GUIDE.md](RELEASE_GUIDE.md)**

---

## 📜 Лицензия

Распространяется под свободной лицензией. Компоненты сторонних ядер и утилит (`sing-box`, `Xray-core`, `OpenVPN`, `Zapret`, `tg-ws-proxy`, `Wintun`) подчиняются соответствующим лицензиям их авторов.
