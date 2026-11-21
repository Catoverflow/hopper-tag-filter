# Hopper Tag Filter

A mod for Probably Stolen. The intake hopper's filter list can hold tags, so a hopper moves items by type (Food, Medical, Weapon ...) instead of one item at a time.

料斗标签过滤：进料斗的筛选列表可以添加标签，按物品类型搬运。

## Use

1. Open an intake hopper and set its filter to the allow list or the blacklist.
2. Press **Add tags**. Tick a category to add everything in it, or unfold it and tick single types.
3. **Pick from an item** lists the tags of an item you click, including the game's internal ones.

An item counts as listed when it is on the list itself or carries any listed tag. When several hoppers in the same place match one item, the hopper with the lower run order takes it.

## Install

Copy the `HopperTagFilter` folder into the game's `Mods` folder, activate the mod in the game's mod list and restart.

## Build

Needs Mono's C# compiler (`csc` or `mcs`) and the game installed.

```sh
./build.sh            # builds into dist/HopperTagFilter
./build.sh install    # also copies it into the game's Mods folder
```

Set `GAME_DIR` if the game is not in the default Steam library.

## Notes

- Built for game version 1.0.0.16.
- Not compatible with other mods that replace the intake hopper's window or filter.
- Hoppers triggered by "run whenever an item is added" or "run when this hopper is moved" follow the run order too. The game itself only does that for the nightly run.
- Tags are saved in the hopper's own filter list as `tag:<TYPE>`. Without the mod they match nothing and the save still loads.
- Type and category names come from the game, in whatever language it runs in. The mod's own six strings are translated into the eight languages the game has text for: English, Chinese, French, German, Spanish, Polish, Russian and Japanese. Corrections are welcome.

## License

MIT
