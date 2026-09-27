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

### Версия поднимается сама

Patch-версию бампает git-хук [.githooks/pre-commit](.githooks/pre-commit): каждый коммит
поднимает `<Version>` в [AutoPrint.csproj](AutoPrint.csproj) (`1.0.3` → `1.0.4`) и кладёт
правку в тот же коммит. Правится **только** `<Version>` — `AssemblyVersion`/`FileVersion`
выводятся из неё; если прибить их гвоздями, приложение будет считать себя старым и
обновляться по кругу.

Хук живёт в репозитории, но git его сам не подхватывает. Один раз на каждой машине:

```powershell
git config core.hooksPath .githooks
```

Выпуск версии после этого — обычный коммит и пуш:

```powershell
git add -A
git commit -m "fix: ..."     # 1.0.3 -> 1.0.4
git push
```

Workflow соберёт `win-x64` self-contained, упакует `AutoPrint-1.0.4-portable.zip`,
создаст тег `v1.0.4` и GitHub Release (latest). Кассы увидят его через «Проверить
обновления» или фоновую автопроверку.

Когда бамп не нужен или нужен другой:

| Ситуация | Что делать |
|---|---|
| Коммит без релиза (правка README, эксперимент) | `git commit --no-verify` или `AUTOPRINT_NO_BUMP=1 git commit` |
| Minor/major (`1.0.4` → `1.1.0`) | Поправь `<Version>` руками и застейджи csproj — хук увидит ручную правку и не перебьёт её |
| Слияние, rebase, cherry-pick | Хук пропускает их сам — иначе каждый rebase конфликтовал бы по строке `<Version>` |
| Отключить совсем | `git config --unset core.hooksPath` |

> Если версия в csproj не изменилась — тег уже существует, и workflow пропустит публикацию.
> Ручной перезапуск: вкладка Actions → Build & Release → Run workflow (можно с `force`).
>
> Слияние двух веток, где обе бампали версию, даст конфликт в строке `<Version>` —
> разрешается выбором большего числа.

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
