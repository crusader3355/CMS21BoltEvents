# BoltEvents

Случайные события на прикипевших болтах в Car Mechanic Simulator 2021:
и при распылении WD-40 (правая кнопка), и при проскальзывании трещотки
(левая кнопка по закипевшему болту), и при входе в сессию демонтажа
(латентная ржавчина).

## События

### WD-40 (`ToolsManager.ShowMountObjectSpray`, патчится MoveNext итератора)

- **WD-40 не помогла** (`ChanceWd40FailedPercent`, 15%): болт закисает
  обратно (`IsStuck = true`) — нужно пшикнуть ещё раз. Работает гард
  ввода (`Wd40FailGuardSeconds`): пока игрок держит ЛКМ с попытки
  до пшика, болт удерживается в закисшем состоянии и курсор не
  блокируется — без гарда игра уходит в бесконечное вращение.
- **Сорванные грани** (`ChanceStrippedThreadsPercent`, 4%): см. ниже.

### Проскальзывание трещотки (`MountObject.PlayStuckAnimation`)

- **Болт пошёл!** (`ChanceUnscrewNoWd40Percent`, 15%): болт откручивается
  без WD-40 (`IsStuck = false`), начисляется `BoltGaveWayExpReward`
  (5) опыта.
- **Сорванные грани** (`ChanceSlipStrippedPercent`, 7%): см. ниже.

### Латентная ржавчина (`GameScript.SelectToUnMount`)

- **Болт закис на ходу** (`ChanceSeizeNormalBoltPercent`, 10%): при входе
  в сессию демонтажа один случайный исправный болт тайно помечается
  закисшим. Оранжевый контур скрывается патчами `Highlighter.On` /
  `FlashingOn` (перекрашиваются в жёлтый/зелёный), попап показывается
  на первом проскальзывании трещотки. Выход из сессии без касания
  болта (ESC) откатывает событие.

### «Сорванные грани»

Болт помечается; когда он реально открутится, деталь, которую он держал,
теряет `StrippedConditionLossPercent` от текущего состояния
(по умолчанию 5: 100% → 95%, 20% → 19%), игрок платит
`StrippedMoneyPenalty` CR «за нервы» (при достатке денег) и получает
`StrippedExpReward` опыта.

## Как работает

- Постфикс на `MoveNext` корутины распылителя WD-40
  (`_ShowMountObjectSpray_d__36._mountObject_5__3`). Целевой болт
  читается из захваченного локала итератора; вотчер ждёт
  `IsStuck == false` и бросает кубик.
- Постфикс на `MountObject.PlayStuckAnimation` (анимация
  проскальзывания трещотки) — кубик бросается сразу, с антидребезгом
  2 сек на болт.
- Постфикс на `GameScript.SelectToUnMount` — бросок латентной ржавчины
  на входе в сессию демонтажа. Список болтов из аргументов метода —
  без угадывания по курсору/ховеру.
- Префиксы на `Highlighter.On` / `FlashingOn(1,3)` — оранжевый контур
  скрытого болта перекрашивается в его «нормальный» цвет; ховер
  симулируется корутиной по близости курсора на экране.
- Для «сорванных граней» вотчер ждёт откручивания помеченного болта
  (лимит 5 минут) и бьёт по состоянию детали через
  `PartScript.SetCondition`.

## Конфиг

Файл `Mods\BoltEvents\BoltEvents.cfg` (INI, UTF-8, `#`/`;` — комментарии).
Перечитывается перед каждым броском — правки без перезапуска игры.

- `Enabled` (true/false)
- `ChanceWd40FailedPercent` (15)
- `ChanceStrippedThreadsPercent` (4)
- `ChanceUnscrewNoWd40Percent` (15)
- `ChanceSlipStrippedPercent` (7)
- `ChanceSeizeNormalBoltPercent` (10)
- `Wd40FailGuardSeconds` (1.5) — гард ввода после «WD-40 не помогла»
- `StrippedConditionLossPercent` (5)
- `StrippedMoneyPenalty` (50)
- `StrippedExpReward` (5, 0 = без опыта)
- `BoltGaveWayExpReward` (5, 0 = без опыта)
- `DebugLog` (false) — логировать пшики, проскальзывания и броски в консоль
- `TitleRU/EN`, `Wd40FailedRU/EN`, `BoltGaveWayRU/EN`, `StrippedRU/EN`,
  `BoltSeizedRU/EN` — тексты попапов на обоих языках (`{0}` = потеря
  состояния в «сорванных гранях»)

## Проверка

1. В конфиге временно `ChanceUnscrewNoWd40Percent = 100`,
   `ChanceWd40FailedPercent = 100`, `ChanceSeizeNormalBoltPercent = 100`,
   `DebugLog = true`.
2. Запустить игру, найти прикипевший болт. Клик ЛКМ → болт раскисает
   («Болт пошёл!», +XP). WD-40 → болт закисает обратно.
3. Регресс гарда: зажать ЛКМ на болту, не отпуская нажать ПКМ — после
   «WD-40 не помогла» звук трещотки должен затихнуть, курсор остаться
   свободным, повторный клик вести себя как у обычного прикипевшего болта.
4. Латентная ржавчина: войти в демонтаж (колесо/ступица) — один болт
   выглядит нормально, на первом клике закисает с попапом.
5. Вернуть шансы на значения по умолчанию.

В консоли при старте:

```
BoltEvents: MountObject.PlayStuckAnimation patched (ratchet slip events)
BoltEvents: WD-40 coroutine MoveNext patched (WD-40 events)
BoltEvents: GameScript.SelectToUnMount patched (seize on unscrew entry)
BoltEvents: Highlighter.On patched (hidden seize outline)
```

## Сборка

```
build.bat
```
