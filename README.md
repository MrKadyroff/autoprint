# AutoPrint — принт-сервер Mulex P80

Бесперебойный сервер печати чеков для термопринтера Mulex P80 (.NET 8 + WPF).
Принимает чеки из браузера (JSON / HTML / PNG / PDF), рендерит и печатает по USB или сети (RJ45, TCP 9100), с очередью, диагностикой и автообновлением.

## Требования
- Windows 10/11
- .NET 8 SDK (сборка) / приложение публикуется self-contained (рантайм не нужен на кассе)
- WebView2 Runtime (для HTML-чеков; в Win10/11 обычно уже есть)

## Локальная сборка и запуск
```powershell
dotnet build
dotnet run
```

## Автопубликация релизов (GitOps)

Релизы выходят **автоматически** через GitHub Actions — см. [.github/workflows/release.yml](.github/workflows/release.yml).

Как выпустить новую версию:
1. Подними версию в [AutoPrint.csproj](AutoPrint.csproj): `<Version>1.1.0</Version>` (и `AssemblyVersion`/`FileVersion`).
2. Закоммить и запушь в `main`:
   ```powershell
   git add -A
   git commit -m "release: 1.1.0"
   git push
   ```
3. Workflow сам: соберёт `win-x64` self-contained, упакует `AutoPrint-1.1.0-portable.zip`,
   создаст тег `v1.1.0` и GitHub Release (помеченный как latest).
4. Приложения у касс через «Проверить обновления» (или автопроверку) увидят релиз и обновятся.

> Если версия в csproj не изменилась — тег уже существует, и workflow пропустит публикацию.
> Ручной перезапуск: вкладка Actions → Build & Release → Run workflow (можно с `force`).

## Первичная настройка репозитория
```powershell
git init -b main
git add -A
git commit -m "initial"
git remote add origin https://github.com/<owner>/<repo>.git
git push -u origin main
```
После этого в приложении (карточка «Обновления») укажи `<owner>` и `<repo>` — они сохранятся в `%AppData%\AutoPrint\config.json`.

## Драйверы
Инсталляторы `DriverInstall.exe` и `Normal POS Driver VL2.0.1.exe` коммитятся в репозиторий —
они нужны сборке (копируются в `Drivers\`) и устанавливаются из приложения с правами администратора.
