# Участие в разработке (Contributing)

Спасибо за интерес к развитию **AutoZamena**! Любые улучшения алгоритма ранжирования, словаря исключений, совместимости с приложениями и UI приветствуются.

---

## Структура репозитория

- `src/AutoZamena.Core/` — чистое кроссплатформенное ядро без зависимостей от Win32 (геометрия ЙЦУКЕН/QWERTY, бинарный словарь `LexiconDatabase`, физический скорер `NoisyChannelScorer`, движок `AutoCorrectEngine`, конвертер выделения `SmartTextConverter`).
- `src/AutoZamena.App/` — нативное Windows-приложение (`WinForms` + `Win32 SendInput` / `WH_KEYBOARD_LL` / `WH_MOUSE_LL`, жесты Caramba Switcher, системный трей, фильтр окон и паролей).
- `tests/AutoZamena.Tests/` — модульные и регрессионные тесты `xUnit` + бенчмарки задержки (`< 1 мс`).
- `installer/AutoZamena.iss` — скрипт сборки официального Windows-установщика **Inno Setup 6**.
- `tools/` — генератор встроенного морфологического словаря (`build_dictionary.py`) и мультиразмерной иконки (`generate_icon.py`).

---

## Локальная сборка и тесты

Требуется **.NET 8 SDK** на Windows 10 / 11:

```powershell
dotnet restore AutoZamena.sln
dotnet build AutoZamena.sln -c Release
dotnet test AutoZamena.sln -c Release
```

### Главные правила при изменении движка (`AutoZamena.Core`)
1. **Принцип «Не навреди»:** любое изменение порогов `NoisyChannelScorer` или правил раскладки обязано проходить все тесты в `GuardAndSafetyTests`, `RealWorldTyposTests`, `MechanicsAndLayoutTests` и `CarambaAndSmartFeaturesTests`.
2. **Задержка `< 1 мс` в хуке:** поток `WH_KEYBOARD_LL` никогда не должен выполнять блокирующий I/O, обращаться к `Clipboard` или вызывать `UI Automation` синхронно.
3. **Новые ложные срабатывания:** если вы нашли реальное слово/сленг/термин, которое ложно исправляется, добавьте тест-кейс в `GuardAndSafetyTests.cs` вместе с фиксом.

---

## Сборка установщика Inno Setup

После публикации бинарника выполните компиляцию `installer/AutoZamena.iss` через **Inno Setup 6 (`ISCC.exe`)**:

```powershell
dotnet publish src/AutoZamena.App/AutoZamena.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish/standalone
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /DMyAppVersion=1.0.0 /DPublishDir="..\publish\standalone" /DOutputBaseFilename="AutoZamena-Setup-v1.0.0-win-x64" installer\AutoZamena.iss
```
