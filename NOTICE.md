# Notice

Joust (1982) was made by Williams Electronics: designed by John Newcomer, programmed by Bill Pfutzenreuter with Cary Kolker and Ken Lantz, with the sound ROM by John Kotlarik and Tim Murphy. The game, its source code, its graphics and its sound belong to their rights holder. This repository is a preservation project. It claims no rights in the original game, and it contains no ROM files.

## Not covered by LICENSE

- `joust-main-asm/`: the original source as rewritten for Macro Assembler {AS} by SynaMax (https://github.com/synamaxmusic/joust), from the source published by historicalsource (https://github.com/historicalsource/joust). It is included unchanged.
- `src/Joust.Port/Translated/*.g.cs`: a machine translation of that source into C#, made by `joust-tools translate`.
- Everything the programs make from the source: ROM images, game images, pictures and sound.
- The icon (`docs/images/icon.png`, `src/Joust.App/Icon.ico`, and `src/Joust.Web/wwwroot/favicon.ico` and `icon.png`), which shows the game's characters.

## Covered by LICENSE (public domain)

All other code in this repository is new work: the assembler, the reference machine, the hand-written port and its runtime, the renderers and shaders, the audio chain, the front ends, the tools, the tests and the documentation. It is dedicated to the public domain under the Unlicense (`LICENSE`). No credit is asked for.
