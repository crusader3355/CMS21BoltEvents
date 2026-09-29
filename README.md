# BoltEvents

Случайные события на закипевших болтах в Car Mechanic Simulator 2021:
и при распылении WD-40 (правая кнопка), и при проскальзывании трещотки
(левая кнопка по закипевшему болту).

## События

### WD-40 (`ToolsManager.ShowMountObjectSpray`)

- **WD-40 не помогла** (`ChanceWd40FailedPercent`, 15%): болт закисает
  обратно (`IsStuck = true`) — нужно пшикнуть ещё раз.
- **Сорванные грани** (`ChanceStrippedThreadsPercent`, 4%): см. ниже.

### Проскальзывание трещотки (`MountObject.PlayStuckAnimation`)

- **Болт пошёл!** (`ChanceUnscrewNoWd40Percent`, 15%): болт откручивается
  без WD-40 (`IsStuck = false`).
- **Сорванные грани** (`ChanceSlipStrippedPercent`, 7%): см. ниже.

### «Сорванные грани»

Болт помечается; когда он реально открутится, деталь, которую он держал,
теряет 5% от текущего состояния (100% → −10%, 20% → −2%), игрок платит
50 CR «за нервы» (при достатке денег) и получает 5 опыта.

## Как работает

- Постфикс на `ToolsManager.ShowMountObjectSpray` (корутина распылителя
  WD-40). Целевой болт читается из захваченного локала итератора
  (`_mountObject_5__3`, присваивается на первом MoveNext — читаем на
  следующем кадре). Вотчер ждёт `IsStuck == false` и бросает кубик.
- Постфикс на `MountObject.PlayStuckAnimation` (анимация проскальзывания
  трещотки по закипевшему болту) — кубик бросается сразу, с антидребезгом
  2 сек на болт.
- Для «сорванных граней» вотчер ждёт откручивания помеченного болта
  (лимит 5 минут) и бьёт по состоянию детали через `PartScript.SetCondition`.

## Конфиг

Файл `UserData\MelonPreferences.cfg`, секция `[BoltEvents]`:

- `Enabled` (true/false)
- `ChanceWd40FailedPercent` (15)
- `ChanceStrippedThreadsPercent` (4)
- `ChanceUnscrewNoWd40Percent` (15)
- `ChanceSlipStrippedPercent` (7)
- `DebugLog` (false) — логировать пшики, проскальзывания и броски в консоль

## Проверка

1. В конфиге временно `ChanceUnscrewNoWd40Percent = 100`,
   `ChanceWd40FailedPercent = 100`, `DebugLog = true`.
2. Запустить игру, найти закипевший болт. Клик ЛКМ → болт раскисает
   («Болт пошёл!»). WD-40 → болт закисает обратно.
3. Вернуть шансы на 15/4/15/7.

В консоли при старте:

```
BoltEvents: ToolsManager.ShowMountObjectSpray patched (WD-40 events)
BoltEvents: MountObject.PlayStuckAnimation patched (ratchet slip events)
```

## Сборка

```
build.bat
```
