Интерфейс, который совмещаяет в себе zapret и VPN. Программу я навайбкодил, за основу взят v2rayN и zaptret. Для подключения протоколов используется Xray-core и Sound-box. Все модули обновляются с офф репозиториев, в том числе zapret от Flowseal.

Релизный процесс:
- локальная тестовая сборка: `pwsh -File .\Publish-NetCatPreRelease.ps1 -Label smoke`
- финальная сборка релиза: `pwsh -File .\Publish-NetCatRelease.ps1`
- итоговый архив всегда создаётся как `artifacts\NetCat-v<version>.zip`
- внутри релиза одна корневая папка `NetCat` и файл `release-manifest.json`

Структура данных:
- системные файлы приложения остаются в корне установки
- пользовательские данные хранятся в `userdata\`
- логи: `userdata\guiLogs`
- конфиг: `userdata\guiNConfig.json`
- временные файлы: `userdata\guiTemps`
