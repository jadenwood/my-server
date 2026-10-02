# Mods keys read from the game DLL

This page lists the atmosphere, weather and day-night keys of the built-in `Mods\` system, as they appear in the game's own code. It also shows how that code reads and writes the `.cfg` files. The preset in `mods/presets/grim-but-readable/` uses only the keys proven here.

Tags:

| Tag | Meaning |
|---|---|
| **[IL]** | Seen in the metadata or IL of the DLL: user-string heap, field signatures, `ldstr` operands of a method body. |
| **[DEC]** | Read from a decompiled C# view of that same IL. Logic such as "the higher weight wins" comes from here. |
| **UNVERIFIED** | Not provable from the DLL. It needs a running server. |

## 1. What was examined and how

| Item | Value |
|---|---|
| Source zip | `Oxide.ReignOfKings.zip` from release 2.0.3867, sha256 `6c35c623fa9ee412945f61a32e8196091b40d56bfe9f8376d3d8e6243b72c6c8` (the same file as in `docs/oxide-rok-api.md`) |
| File | `ROK_Data/Managed/Assembly-CSharp.dll`, 8,209,408 bytes, zip date 2024-02-02, sha256 `ffea7074f49b3533d9ca86ccea259f44226bb31a90560b4a129609a99953435e` |
| Module MVID | `7ae521be-ddca-4bb1-9869-8132cf2a08fd`. Metadata version `v2.0.50727`, assembly version 0.0.0.0 |
| Tools | Python `dnfile` 0.18.0 dumped the user-string heap, TypeDef/Field rows, and `ldstr`/`ldc.r4` operands from method bodies. `ilspycmd` 10.1.1 produced the decompiled view. Nothing in the DLL was run. |

**This is the Oxide-patched server assembly.** Oxide's patcher adds hook calls. Its patch manifest (`resources/ReignOfKings.opj` at tag 2.0.3867) names none of the types on this page (`UnityModManager`, `ModFilePackageFactory`, `GameObjectModHandler`, `AtmosphereView`, `FogVolumeController`, `Weather`, `GameClock`, `ColorModParseable`, `SimplifiedProperties.*`). Even so, the file comes from whatever server build Oxide patched in early 2024. **UNVERIFIED:** that the owner's server build (4088734) has the same settings classes, key strings and defaults. The DLL has no build ID to compare. `Apply-Preset.ps1` guards against a mismatch: it only writes a key that the owner's server lists in its own `Mods\*.defaults.cfg`.

## 2. How the Mods system loads files

| Fact | Evidence |
|---|---|
| The folder is `Mods` and the extension is `cfg`. | [IL] `CodeHatch.Engine.Modding.UnityModManager` (TypeDef `0x020007ec`). The `.ctor` at RVA `0xe017c` does `ldstr "Mods"` and `ldstr "cfg"` into the private string fields `_modPath` and `_modExtension`. Both fields are `[SerializeField]`, so a scene could override them. In practice the guide and players report `Mods\`. |
| Each mod handler `<Name>` has an override file `Mods\<Name>.cfg` and a reference file `Mods\<Name>.defaults.cfg`. | [IL] `ModFilePackageFactory.Load` (RVA `0xde92c`) builds `Path + name + "." + ext`. `ModFilePackageFactory.Save` (RVA `0xdeabc`) has the strings `".defaults."`, `"-- "`, `" --"` and `" Defaults --"`. [DEC] `Save` **deletes and rewrites** `<Name>.defaults.cfg` on every save. It also rewrites `<Name>.cfg`, keeping only the keys that have a default, sorted and padded. |
| The server writes the files. Clients get the values over the network. | [DEC] `UnityModManager.LoadModHandler` reads and saves the file only when `Player.IsLocalServer`. `OnPlayerJoin` sends every package to the joining player in a `ModPackageEvent`, and the client then applies the values itself. This matches the guide's statement that players need no files. |
| Keys are case-sensitive. | [DEC] `ModPackage.GetDefaultEntry` uses `string.Equals(a, b)`, which is ordinal. |
| An override is applied only if its key has a default. | [DEC] `UnityModManager.ApplyModHandler` logs `Could not get default entry for {0} before applying.` and skips the key otherwise. [DEC] `Save` writes back only keys that have a default. The game's property writer turns every other line into a `# ...` comment. **UNVERIFIED in practice:** that a mistyped key comes back as a comment after a restart. This is inferred from the code. |
| Values are parsed into the type of the default. | [DEC] `LoadModHandler` uses `ModUtil.ConvertToType` → `SystemUtil.ConvertToType` → `Convert.ChangeType` for `int` and `float`. It uses `IModParseable.ParseFromString` for colours. If parsing fails the value becomes null, and the cast in `ApplyMod` throws, which is logged. |

### 2.1 Line format of a `.cfg` file

The parser and writer are `SimplifiedProperties.PropertyFile`, which is inside Assembly-CSharp. [DEC]

- The writer emits `key = 'value'`, with the key padded to the longest key, and `   # comment` when there is a comment. The defaults file starts with `# -- <Name> Defaults --`.
- A line starting with `#` or `=` is a comment. A line containing `=` is an entry. `{` / `}` open and close a list, and `- 'x'` is an array item.
- **Pitfall:** the key is `line.Substring(0, indexOf('=') - 1)`, which means the character right before `=` is dropped. Always write `key = 'value'` with a space before `=`. `key='value'` would lose the last letter of the key.
- Quotes and spaces around the key and the value are trimmed.
- A key that appears twice in the same file gets renamed `key2`, `key3` and so on (`Tools.GetAlias`). In practice the duplicate is ignored.
- Escapes: `\#`, `\'`, `\n` and `\r`.

## 3. Atmosphere, weather and clock keys

These three settings classes are not mod handlers. Each one is an `IModable` component. [DEC] The only code that collects `IModable` components for scene objects like these is `CodeHatch.Engine.Modding.GameObjectModHandler` (TypeDef `0x020007e4`). It prefixes every key with the component's `ModHandlerName` and a dot ([IL] `GetModDefaults` at RVA `0xde5f4`, `ldstr "{0}.{1}"`). So **the key, as written in the file, is `<ModHandlerName>.<Key>`**.

**UNVERIFIED: the file name.** It is `GameObjectModHandler._handlerName`, a `[SerializeField]` string whose code default is `"Default"` ([IL] `.ctor` at RVA `0xde570`, `ldstr "Default"`). The real value is stored in Unity scene data, not in the DLL. The guide describes the category as "Environment / Weather & Day-Night", which suggests a file such as `Environment.cfg`, but nothing proves that name. All three components could also sit under different handlers. **Find the file on the server:** it is the `Mods\*.defaults.cfg` that contains `Atmosphere.FogDensity`:

```powershell
Select-String -Path 'G:\RealmTest\server\Mods\*.defaults.cfg' -Pattern '^\s*(Atmosphere|Weather|Clock)\.' | Select-Object Filename, Line
```

### 3.1 `AtmosphereView` (TypeDef `0x0200092a`), prefix `Atmosphere`

`get_ModHandlerName` (RVA `0x10462c`) does `ldstr "Atmosphere"`. `GetModDefaults` (RVA `0x104634`) and `ApplyMod` (RVA `0x104714`) both `ldstr` the six keys below. [IL]

| Key in file | Value type | What it does [DEC] | Code default [IL] / [DEC] |
|---|---|---|---|
| `Atmosphere.IslandLatitude` | `float` (field `_AreaLatitude`, sig `0x0c` = R4) | Sets the sky system's latitude, clamped to -90..90. This changes the sun and moon paths. | none (0). **UNVERIFIED:** scene value |
| `Atmosphere.IslandLongitude` | `float` (`_AreaLongitude`) | Sets longitude, clamped to -90..90. | none (0). **UNVERIFIED:** scene value |
| `Atmosphere.SunColor` | colour (`ColorModParseable`, field `_SunColor` of type `UnityEngine.Color`) | Sets `TOD_Sky.Sun.LightColor` and `MeshColor`. Each frame the light becomes `SunColor * (1.12 - 0.65 * cloudOcclusion)`. | `Color.white`. **UNVERIFIED:** scene value |
| `Atmosphere.MoonColor` | colour (`_MoonColor`) | Same as `SunColor`, for the moon. This is the main night-light colour and brightness lever. | `Color.white`. **UNVERIFIED:** scene value |
| `Atmosphere.FogColor` | colour | `FogVolumeController.GlobalFogColor`. It **multiplies** the per-biome fog colour (`FogVolume.FogColor = biomeColour * GlobalFogColor`). | `Color.white` (`FogVolumeController` field initializer) |
| `Atmosphere.FogDensity` | `float` | `FogVolumeController.GlobalFogDensity`. It **divides** fog visibility: `Visibility = (biome/weather visibility) / GlobalFogDensity`. A value above 1 means thicker fog. | `1f` (`ldc.r4 1.0` in the `FogVolumeController..ctor` at RVA `0x104f44`) |

- `FogColor` and `FogDensity` are offered only when `FogVolumeController.Instance != null` [DEC]. **UNVERIFIED:** that a headless dedicated server has that object. If `Mods\*.defaults.cfg` does not list these two keys, the server cannot apply them, and `Apply-Preset.ps1` skips them.
- The same goes for the whole `Atmosphere.` block: it exists only if `AtmosphereView` is in the dedicated server's scene. This is **UNVERIFIED**.

### 3.2 `Weather` (TypeDef `0x02000941`), prefix `Weather`

`get_ModHandlerName` (RVA `0x107874`) does `ldstr "Weather"`. `GetModDefaults` (RVA `0x10787c`) and `ApplyMod` (RVA `0x107910`) both `ldstr` the five keys below. [IL]

| Key in file | Type | Meaning [DEC] | Code default |
|---|---|---|---|
| `Weather.ClearWeight` | `int` (sig `0x08` = I4) | Weight of "Clear" | 0, with `[Range(0,10)]`. **UNVERIFIED:** scene value |
| `Weather.CloudyWeight` | `int` | Weight of "Cloudy" | same |
| `Weather.PrecipitateLowWeight` | `int` | Weight of light rain | same |
| `Weather.PrecipitateMediumWeight` | `int` | Weight of medium rain | same |
| `Weather.PrecipitateHeavyWeight` | `int` | Weight of heavy rain or storm | same |

How the weights are used (`Weather.ChangeTheWeather`) [DEC]: each weight is multiplied by `Random.Range(0,100)`, and the weather with the **strictly highest** product wins. A tie keeps the current weather. A negative weight is clamped to 0 on apply. Because only the highest roll counts, a small difference in weight makes a large difference in odds. For example, 5/3/2/1/1 gives about 65% clear (Monte Carlo over 300k draws). Rain also reduces fog visibility through `FogVolumeController.RainyWeatherVisibility`. **UNVERIFIED:** how often `ChangeTheWeather` runs, and so how long each weather lasts.

### 3.3 `GameClock` (TypeDef `0x02000954`), prefix `Clock`

| Key in file | Type | Meaning [DEC] | Code default |
|---|---|---|---|
| `Clock.DaySpeed` | `float` | `TimeOfDay += dt/3600 * DaySpeed * DaySpeedModifier(TimeOfDay)`, wrapped to 0..24 h. It is clamped to at least 0 on apply. `DaySpeedModifier` is a scene `AnimationCurve`, so day and night can run at different speeds. **UNVERIFIED:** the curve's shape. | `1f` (`ldc.r4 1.0` in `.ctor`). **UNVERIFIED:** scene value |

There is **no separate dusk or dawn length key**. `DaySpeed` speeds up or slows down the whole cycle.

### 3.4 Colour value format

`ColorModParseable` (TypeDef `0x020007e3`) [IL] [DEC]:

- **Write:** `MakeString` does `string.Format("rgba({0},{1},{2},{3})", r, g, b, a)` with floats from 0 to 1.
- **Read:** `ParseFromString` trims the characters `( ) r g b a` from both ends, splits on `,`, and calls `Convert.ToSingle` on **four** parts. All four parts are required.
- **UNVERIFIED and a real risk:** both directions use the current culture. On a Windows account set to a comma-decimal locale (for example de-DE or fr-FR), the defaults file would contain `rgba(0,9,...)`. The parser would then split it into the wrong parts, and `1.25` might parse as `125`. Check that the generated `.defaults.cfg` uses dots. If it does not, run the server under an account with a dot-decimal format.

## 4. Other hard-coded handler names (file names proven)

These `IModHandler` classes return a constant `ModName`, so their file names are fixed [IL]/[DEC]:

`Players`, `Blocks`, `Blueprints`, `Animals`, `Farming`, `Social`, `Resources`, `Buff Effects`, `Character Creation` and `Realm` (the tax collector).

Two handlers build their names at run time:

- `LootModHandler`: `<level name> Loot Tables`
- `SpawnerModHandler`: also uses the level name

`Test` also exists and is a developer handler. The keys inside these files were not part of this task and are not listed here.

## 5. What still needs a running server

1. The `GameObjectModHandler` file name(s) that hold the `Atmosphere.`, `Weather.` and `Clock.` keys. `Apply-Preset.ps1` finds them automatically.
2. The real scene defaults: the colours, the weather weights and `DaySpeed`. Read them from `Mods\*.defaults.cfg`.
3. Whether the dedicated server lists the `Atmosphere.*` keys at all. They need `AtmosphereView` and `FogVolumeController` in the server scene.
4. Whether values reach clients exactly as the code suggests. Join and look.
5. The decimal separator issue in section 3.4.
6. Whether the owner's server build matches this DLL.
