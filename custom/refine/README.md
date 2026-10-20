# Preserved custom refine roll (1668)

`CustomRefineRoll.cs.txt` is a verbatim copy of the roll that `RefineItemHandler` (RMI 1668 ReqRefineItem)
used until the faithful rework. It is NOT retail behaviour. It is kept as a re-enableable alternative, not as
dead code: to bring it back, paste the block over the `RollRefine` call in `RefineItemHandler.HandleAsync`
(the handler's variables it expects are listed at the top of the file). The `.txt` suffix keeps it out of the
build.

## Why it existed

The 2026-09-14 design pass wanted a no-destroy, two-band "gamble" with safe checkpoints at +0 and +10, and
reused the `REFINE_RATE` columns for that. The client display (the refine window's three numbers, read from
`REFINE_RATE.ct` in `ASSET.VDK` through the display caves) was kept in sync with those reinterpreted columns.
The values were "DESIGN values, tune freely".

## How it differs from the faithful model

Both models read `data/REFINE_RATE.csv` (keyed `INDEX` = target level, rates /10000) and the catalyst rows of
`data/REFINE_ITEM.csv`. Nothing is ever destroyed in either.

| Aspect | Custom (this folder) | Faithful (current handler) |
| --- | --- | --- |
| Success | `Succ_Rate` + catalyst `Succ_Rate`, capped 10000 | the same |
| Lucky jump | catalyst `3_`/`2_GRADE_RATE`, +3 then +2 then +1 | the same |
| `Break_Rate` | "baseline protection": P(a fail does NOT reset to the band baseline) | survival: P(a fail does not break); nothing is destroyed here, so a failed survival roll resets to the band floor |
| `Down_Rate` | "P(a fail drops at all)", so "maintain protection" = 10000 - Down_Rate | P(a surviving fail still drops the level) |
| Fail zones | one roll r: stay, then -1 (clamped to the baseline), then reset to the baseline | two rolls: survival, then (if survived) Down_Rate; the drop follows `Down_Type` |
| `Down_Type` | ignored (only a display flag) | 1 = drop to the band floor (+19 -> +10, not -1) |
| Band floor | `cur < 10 ? 0 : 10` | `cur >= 11 ? 10 : 0` |
| Max level | const 20 | highest `INDEX` in the table |

The custom roll also made +1..+10 and +11..+20 identical gambles by design. In the retail table the rows
`INDEX` 1 and 11 are guaranteed (Succ 10000, Down 0), which is what makes +0 and +10 the band floors.

## Re-enabling checklist

1. Paste the roll back and make its max level consistent with the table (it used a const 20).
2. Keep `REFINE_RATE.csv` (server roll) and the client `REFINE_RATE.ct` (display) carrying the same numbers
   the custom model expects; the retail table does not describe the custom semantics.
3. Update `tests/RO2.Tests/Packets/RefineRollTests.cs`, which asserts the faithful outcomes.
