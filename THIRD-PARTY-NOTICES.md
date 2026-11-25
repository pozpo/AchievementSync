# Third-party notices

Original AchievementSync source code is Copyright (C) 2026 Pozo and licensed under **GNU GPL-3.0-or-later** (see [LICENSE.md](LICENSE.md)). That license does not replace or relicense third-party code, data, or binary payloads included in the repository or the application, nor projects AchievementSync interoperates with such as [Roschach96/achievement-viewer](https://github.com/Roschach96/achievement-viewer).

Keep the license and attribution files beside the resources they describe. Before publishing a release binary, verify the exact upstream version and redistribution terms for each bundled payload and provide any corresponding source required by its license.

## Known bundled components

| Component or files | Notice and license information |
|---|---|
| Achievement Enabler templates under `src/AchievementSync.Core/Resources/ColdClientTemplate/` | The included `Achievement-Enabler-LICENSE.txt` contains the upstream MIT license and copyright notice for Roschach96's Achievement Enabler project. The upstream project is [Roschach96/Achievement-Enabler](https://github.com/Roschach96/Achievement-Enabler). |
| Goldberg / GBE Fork files under `src/AchievementSync.Core/Resources/RegularEmulator/` and `src/AchievementSync.Core/Resources/ColdClientTemplate/` | The included `Goldberg-CREDITS.md` preserves upstream attributions and component license texts. The current GBE Fork repository has an LGPL-3.0 license; the credits file also contains GPL-3.0 notices for some components. See [Detanup01/gbe_fork](https://github.com/Detanup01/gbe_fork) and the included credits before redistributing any individual payload. |
| ColdClient loader files | `Goldberg-README.experimental_steamclient.md` attributes the original loader to Rat431 and describes a modified version. The bundled files must retain their applicable upstream terms; do not assume the AchievementSync license covers them. See [Rat431/ColdAPI_Steam](https://github.com/Rat431/ColdAPI_Steam). |
| `src/AchievementSync.Core/Resources/schema_parse.zip` | This archive contains `generate_emu_config.exe`, associated with [alex47exe/gse_fork_tools](https://github.com/alex47exe/gse_fork_tools), and a packaged Python runtime with third-party dependencies. The upstream tool repository uses LGPL-3.0; some dependency license files are inside the archive. Preserve the archive and its internal notices, and record the exact upstream revision/hash used for release builds. |
| `src/AchievementSync.Core/Resources/ColdLoaderTemplate/coldloader.dll` | The related [denuvosanctuary/coldloader](https://github.com/denuvosanctuary/coldloader) project declares LGPL-3.0. Confirm the bundled binary corresponds to a source revision under those terms and retain the license/source information. |

The dependency list and versions can change. This notice is an inventory of the current tree, not a replacement for the original license files or a legal review.
