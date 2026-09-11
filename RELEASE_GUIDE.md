# 📌 ПАМЯТКА: Как оформлять релизы NetCat

Данное руководство составлено на основе анализа существующих релизов репозитория [akapustyanik/NetCat](https://github.com/akapustyanik/NetCat) (`v0.4.0`, `v0.3.7`, `v0.3.6` и др.). Все последующие релизы должны строго следовать данному стандарту.

---

## 1. Стандарт именования и структура релиза

### 1.1. Тег Git (Tag)
* Формат: `vX.Y.Z` (обязательно строчная буква `v` в начале).
* Примеры: `v0.5.0`, `v0.5.1`, `v1.0.0`.

### 1.2. Название релиза (Release Title)
* Формат: `NetCat vX.Y.Z` или `NetCat X.Y.Z`.
* Пример: `NetCat v0.5.0`.

### 1.3. Обязательные файлы ассетов (Assets)
Каждый релиз должен содержать ровно **два** прикрепленных файла:
1. `NetCat-vX.Y.Z.zip` — полный self-contained архив приложения со всеми модулями, рантаймом, `release-manifest.json` и зависимостями.
2. `NetCat-vX.Y.Z.zip.sha256` — файл контрольной суммы SHA-256.

> ⚠️ **Важно:** Содержимое файла `.sha256` должно быть строго в формате:
> ```text
> <sha256_hash_в_нижнем_регистре>  NetCat-vX.Y.Z.zip
> ```
> (Хэш, два пробела, имя zip-файла). Скрипт `Publish-NetCatRelease.ps1` формирует его автоматически.

---

## 2. Шаблон описания релиза (Release Notes Body)

Текст каждого релиза оформляется по следующей структуре:

```markdown
NetCat X.Y.Z

Updated <Month Day, Year>.

Changes:
- [Описание изменения 1 на английском или русском]
- [Описание изменения 2]
- [Описание изменения 3]
```

### Пример реального оформления:
```markdown
NetCat 0.5.0

Updated September 11, 2026.

Changes:
- Complete transition to high-performance Windows Native .NET 8 WPF architecture with Windows 11 Fluent Design.
- Integrated Flowseal zapret-discord-youtube DPI bypass with ALT13 desync strategy.
- Integrated Flowseal tg-ws-proxy for direct Telegram desktop connection without external VPN.
- Sing-box 1.14.0 primary L3/L4 TUN router with zero-copy Wintun driver.
- Added Dual-Path Failover engine with TCP Racing and Hot-Standby monitoring.
- Built-in AvalonEdit JSON configuration editor.
- UAC-free Windows Task Scheduler autostart and native Shell_NotifyIcon tray integration.
- Restored full self-contained Windows release package format with SHA-256 checksums.
```

---

## 3. Пошаговый процесс создания релиза

### Шаг 1. Проверка версии в коде
Перед сборкой убедитесь, что номер версии обновлен в:
1. `Publish-NetCatRelease.ps1`: переменная `$version = "0.5.0"`
2. `bin/modules_manifest.json`: поле `"app_version": "0.5.0"`
3. `src/NetCat.UI/Views/MainWindow.xaml`: бейдж `"v0.5.0"`

### Шаг 2. Запуск тестов
Убедитесь, что все тесты проходят:
```powershell
dotnet test NetCat.sln
```

### Шаг 3. Автоматическая сборка релизного пакета
Запустите скрипт локальной сборки:
```powershell
.\Invoke-NetCatLocalRelease.ps1
```
Скрипт автоматически:
- Выполняет `dotnet publish` в режиме `win-x64 --self-contained true`.
- Собирает необходимые папки `bin/`, `assets/`, `data/`.
- Генерирует `release-manifest.json`.
- Создает архив `artifacts/NetCat-vX.Y.Z.zip`.
- Генерирует контрольную сумму `artifacts/NetCat-vX.Y.Z.zip.sha256`.
- Запускает валидацию `Test-NetCatPackage.ps1`.

### Шаг 4. Фиксация изменений и создание тега Git
```powershell
git add .
git commit -m "Release vX.Y.Z"
git tag -a vX.Y.Z -m "NetCat vX.Y.Z"
git push origin main --tags
```

### Шаг 5. Публикация релиза на GitHub

#### Вариант А: Через консоль с помощью GitHub CLI (`gh`) — РЕКОМЕНДУЕТСЯ:
1. Создайте текстовый файл с описанием релиза, например `release_notes.md`:
   ```markdown
   NetCat 0.5.0

   Updated September 11, 2026.

   Changes:
   - ...
   ```
2. Выполните команду публикации:
   ```powershell
   gh release create v0.5.0 `
       artifacts\NetCat-v0.5.0.zip `
       artifacts\NetCat-v0.5.0.zip.sha256 `
       --repo akapustyanik/NetCat `
       --title "NetCat v0.5.0" `
       --notes-file release_notes.md
   ```

#### Вариант Б: Через веб-интерфейс GitHub:
1. Перейдите в репозиторий: `https://github.com/akapustyanik/NetCat/releases/new`
2. Выберите или создайте тег: `vX.Y.Z`
3. Укажите Release title: `NetCat vX.Y.Z`
4. В поле описания вставьте текст по шаблону из Раздела 2.
5. Прикрепите два файла из папки `artifacts/`:
   - `NetCat-vX.Y.Z.zip`
   - `NetCat-vX.Y.Z.zip.sha256`
6. Нажмите **Publish release**.

---

## 4. Чек-лист перед публикацией

- [ ] `dotnet test NetCat.sln` завершился успехом (0 ошибок).
- [ ] `Test-NetCatPackage.ps1` успешно проверил архив и манифест.
- [ ] Размер zip-архива составляет от 100 до 220 МБ (self-contained рантайм включен).
- [ ] В архиве присутствуют `NetCat.exe`, `release-manifest.json`, папки `bin/`, `assets/`, `data/`.
- [ ] Имя файла sha256 совпадает с архивом: `NetCat-vX.Y.Z.zip.sha256`.
- [ ] Текст релиза содержит заголовок `NetCat X.Y.Z`, дату `Updated ...` и список `Changes:`.
