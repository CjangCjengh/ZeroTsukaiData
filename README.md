# ZeroTsukaiData

Data extraction tools for Zero no Tsukaima PS2 games.

## Supported games

| Code | Title | Product |
|------|-------|---------|
| concerto | ゼロの使い魔 小悪魔と春風の協奏曲 | SLPS-257.09 |
| fantasia | ゼロの使い魔 夢魔が紡ぐ夜風の幻想曲 | - |
| symphony | ゼロの使い魔 迷子の終止符と幾千の交響曲 | - |

### Game differences

| Feature | concerto | fantasia | symphony |
|---------|----------|----------|----------|
| Scripts | Squirrel **source code** | Squirrel **bytecode** (RIQS) | Squirrel **bytecode** (RIQS) |
| FST (File System Table) | ❌ None | ✅ In NORMAL.BIN | ✅ In NORMAL.BIN |
| SYSTEM.BIN | ❌ None | ✅ Present | ✅ Present |
| SOUND_ID banks | 31 large + 10 small | 50 large + 49 small | 50+ large + 51 small |

## Tools

### BinExtractor

Extracts and decrypts `*.BIN` + `*.HD` archive pairs (scene data, textures, scripts, etc.)

- **C#**: Drag `*.BIN` onto `BinExtractor.exe` (make sure `*.HD` is in the same directory)
- **Python**: `python bin_extractor.py <file1.BIN> [file2.BIN] ...`

#### Enhanced format detection

| Extension | Format | Detection method |
|-----------|--------|-----------------|
| `.tm2` | TIM2 texture | Magic `TIM2` at offset 0 |
| `.bmp` | BMP image | Magic `BM` at offset 0 |
| `.nut` | Squirrel bytecode | Magic `RIQS` at offset 2 |
| `.nut` | Squirrel source code | Keyword search (`function`, `local`, `reset()`, etc.) via Latin-1 decode (handles Shift-JIS comments) |
| `.fst` | File System Table | Starts with `[SECTION_NAME]` (e.g., `[SYSTEM]`, `[SCENE_ID]`) |
| `.fontmap` | Shift-JIS font mapping | Starts with `0x81 0x40` (fullwidth space "　") |
| `.mdl` | 3D model data | Magic `0x00010000` + float 1.0 (`0x3F800000`) |
| `.dat` | Data table | Null-padded Shift-JIS strings or null-separated ASCII strings |

#### FST (File System Table) support (Python version)

Games fantasia/symphony contain FST files inside NORMAL.BIN that map numeric indices to original filenames for all BIN sections. When extracting multiple BINs from the same game directory, the Python extractor automatically:

1. Extracts all BIN files normally (numbered `0001.ext`, `0002.ext`, ...)
2. Scans the NORMAL directory for FST files
3. Renames extracted files using original filenames from FST

FST format:
```
[SECTION_NAME]
SECTION\FILENAME.EXT
...
[END]
```

### BinPacker
- Re-packs extracted files back into `*.BIN` + `*.HD` format
- Drag the extracted folder onto `BinPacker.exe`

### NutParser
- Parses `*.nut` script files extracted from BIN archives
- Drag `*.nut` onto `NutParser.exe`
- **Source code** (concerto): copies directly (already readable Squirrel text with Shift-JIS comments)
- **Bytecode** (fantasia/symphony): disassembles RIQS bytecode to human-readable instructions
- Gracefully handles files with fewer than 9 TRAP boundaries

### AudioExtractor
- Extracts all audio from `VOICE_ID.BIN` and `SOUND_ID.BIN`, decodes PS-ADPCM to WAV
- Drag `VOICE_ID.BIN` or `SOUND_ID.BIN` onto `AudioExtractor.exe`
- The corresponding `*.HD` file should be in the same directory
- Auto-detects file type by filename prefix

#### VOICE_ID (mono voice lines)
- Output: **22050Hz mono** 16-bit WAV files, one per voice line
- Voice boundaries are detected by **flag=1 scanning**: scans the entire BIN for flag=1 ADPCM frames (the only reliable split method)
- **VOICE_ID.HD** is informational only: its slot sizes are SPU2 DMA transfer scheduling sizes, NOT voice boundaries (a voice can span multiple HD slots)

##### VOICE_ID.BIN format details
- The entire BIN is a continuous raw PS-ADPCM (Sony VAG without header) audio stream
- Each voice ends at a **flag=1** ADPCM frame (hardware end-of-sample marker)
- Pre-voice padding structure: `[flag=7] [0x0C silence ×N] [0xFF padding]` → all skipped during decode
- The `0xFF` frames serve as the boundary — everything before FF is SPU2 warm-up (skipped), everything after FF is valid audio (decoded)
- VOICE_ID.HD: each uint32 is a **SPU2 DMA transfer slot size** (used by the game for DVD→SPU2 streaming), NOT a precise voice boundary. HD slots and flag=1 positions drift apart by ~2% cumulatively
- Voice IDs in scene scripts (`0x04000000 + N`) directly map to voice index N

#### SOUND_ID (stereo BGM + mono SE)
- Output: **44050Hz stereo** WAV for BGM banks, **44050Hz mono** WAV for SE (sample rate read from BIN header)
- Requires **SOUND_ID.HD** (mandatory for bank size index)
- **Universal HD parser**: automatically handles all three games' different HD structures

##### SOUND_ID.HD structure (varies per game)
The HD parser uses a universal approach: skip header (`vals[0]=0, vals[1]=20`), then collect all values > 20 as bank sizes. Delimiter values (0 or 20) are automatically filtered out.

| Game | HD structure | Banks |
|------|-------------|-------|
| concerto | `[0][20][31 large][20,20,20][10 small][20][meta...]` | 41 |
| fantasia | `[0][20][50 large][0,20,20,20][49 small][0,20,20,20,0][meta...]` | 99 |
| symphony | `[0][20][50 large][0][bank][0][bank]...[0×6][51 small][meta...]` | 101+ |

##### SOUND_ID.BIN format details
- 16-byte header: `[reserved] [sample_rate=44050] [mode] [interleave=4096]`
- BIN data starts at offset 16, all banks laid out sequentially
- Each bank uses **dual-channel PS-ADPCM** with **4096-byte interleaving** (L block / R block / L block / R block...)
- After all banks: **SE region** containing short mono sound effects, delimited by flag=1 frames

### Tm2Converter
- Converts PS2 TIM2 (`.tm2`) texture images to PNG format
- Drag `.tm2` file(s) or a folder onto `Tm2Converter.exe`
- Single file: outputs PNG alongside the input file
- Folder: outputs all PNGs to `<folder>_png/` directory

#### Supported TIM2 pixel formats
| Depth | Format | CLUT | Description |
|-------|--------|------|-------------|
| 5 | 8-bit indexed | 32-bit RGBA | Most common |
| 4 | 4-bit indexed | 32-bit RGBA | |
| 2 | 24bpp direct | None | RGB, no alpha |
| 3 | 32bpp direct | None | RGBA |
| 5 | 8-bit indexed | 24-bit RGB | No alpha |

#### TIM2 format details
- File header: 16 bytes — magic `TIM2`, version, image count
- Image header: 48 bytes — sizes, dimensions, pixel/CLUT format, GS registers
- Data layout: `[FileHeader][ImageHeader][PixelData][CLUT]`
- PS2 8-bit CLUT unswizzle: for every 32-entry block, indices 8-15 and 16-23 are swapped (GS hardware optimization)
- PS2 alpha range: 0-128 (0x80 = fully opaque), scaled to 0-255
