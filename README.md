<p align="center"> <img alt="Triad Sector" width="880" height="400" src="Resources/Textures/_Triad/Logo/logo.png" /></p>


Triad is a fork of [Monolith](https://github.com/Monolith-Station/Monolith) that runs on the [Robust Toolbox](https://github.com/space-wizards/RobustToolbox) engine written in C#.

This is the primary repo for Triad.

If you want to host or create content for Triad, this is the repo you need. It contains both RobustToolbox and the content pack for development of new content packs.

## Links

[Discord](https://discord.gg/J9Rn76nKbz) | [Steam](https://store.steampowered.com/app/1255460/Space_Station_14/)

## Contributing

We are happy to accept contributions from anybody. Get in Discord if you want to help. Don't be afraid to ask for help either!

Read [Attributing Your Changes](https://github.com/Triad-Sector/Triad_Sector/wiki/Attributing-Your-Changes) before opening a PR; it covers how to license and mark your work.

We are not currently accepting translations of the game on our main repository. If you would like to translate the game into another language consider creating a fork or contributing to a fork.

## Building

Refer to [the Space Wizards' guide](https://docs.spacestation14.com/en/general-development/setup/setting-up-a-development-environment.html) on setting up a development environment for general information, but keep in mind that Triad is not vanilla Space Station 14 and many things may not apply.
We provide some scripts shown below to make the job easier.

### Build dependencies

> - Git
> - .NET SDK 10.0


### Windows

> 1. Clone this repository
> 2. Run `Scripts/bat/updateEngine.bat` in a terminal or in file explorer to download the engine
> 3. Run `Scripts/bat/buildAllDebug.bat` after making any changes to the source
> 4. Run `Scripts/bat/runQuickAll.bat` to launch the client and the server
> 5. Connect to localhost in the client and play

### Linux

> 1. Clone this repository
> 2. Run `Scripts/sh/updateEngine.sh` in a terminal to download the engine
> 3. Run `Scripts/sh/buildAllDebug.sh` after making any changes to the source
> 4. Run `Scripts/sh/runQuickAll.sh` to launch the client and the server
> 5. Connect to localhost in the client and play

### MacOS

> 1. Clone this repository
> 2. Run `Scripts/sh/updateEngine.sh` in a terminal to download the engine
> 3. Run `Scripts/sh/buildAllDebug.sh` after making any changes to the source
> 4. Run `Scripts/sh/runQuickAll.sh` to launch the client and the server
> 5. Connect to localhost in the client and play

## License

The work as a whole is licensed under the GNU Affero General Public License version 3.0. See `LEGAL.md` for the full picture; license texts live in `LICENSES/`.

Content contributed to this repository after commit 2fca06eaba205ae6fe3aceb8ae2a0594f0effee0 is licensed under the GNU Affero General Public License version 3.0, unless otherwise stated. See `LICENSES/AGPL-3.0-or-later.txt`.

Content contributed to this repository before commit 2fca06eaba205ae6fe3aceb8ae2a0594f0effee0 is licensed under the MIT license, unless otherwise stated. See `LICENSES/MIT.txt`.

Some files inherited from Monolith are licensed under Mozilla Public License version 2.0 with Exhibit B removed and keep that license. See `LICENSES/MPL-2.0.txt`. A file that declares its own license in a header keeps it.


[2fca06eaba205ae6fe3aceb8ae2a0594f0effee0](https://github.com/new-frontiers-14/frontier-station-14/commit/2fca06eaba205ae6fe3aceb8ae2a0594f0effee0) was pushed on July 1, 2024 at 16:04 UTC

Most assets are licensed under [CC-BY-SA 3.0](https://creativecommons.org/licenses/by-sa/3.0/) unless stated otherwise. Assets have their license and the copyright in the metadata file. [Example](https://github.com/space-wizards/space-station-14/blob/master/Resources/Textures/Objects/Tools/crowbar.rsi/meta.json).

Note that some assets are licensed under the non-commercial [CC-BY-NC-SA 3.0](https://creativecommons.org/licenses/by-nc-sa/3.0/) or similar non-commercial licenses and will need to be removed if you wish to use this project commercially.
