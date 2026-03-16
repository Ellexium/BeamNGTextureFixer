# BeamNG Texture Fixer 🛠️

A professional utility for BeamNG.drive modders and players to automatically repair "NO TEXTURE" (orange) mods caused by game remasters and asset path changes.

## 📋 The Problem
When BeamNG remasters a vehicle (like the Bolide, Covet, or T-Series), they often rename or move texture files. Older mods that rely on those specific paths break, resulting in the infamous orange "NO TEXTURE" look.

## 💡 The Solution
This tool scans your broken mods, identifies missing textures, and automatically "injects" them back into a new fixed version of the mod using a library of old game assets provided by the user.

### Key Features:
- **Smart Path Matching:** Handles filename suffix changes (e.g., `_nm` vs `_n`) and folder moves.
- **Conflict Prevention:** Detects filename collisions and ensures textures are placed in unique directories.
- **Safe & Rigid:** Creates a separate `_fixed.zip` without touching your original mod or game files (and provides optional in place fixes instead of creating a new zip file).

---

## 🚀 Quick Start Guide

### 1. Prerequisites
*   A folder containing assets from older versions of BeamNG (the "Fuel" for the fixer).

### 2. Getting Legacy Assets (Required)
The tool needs a source for the missing textures. One of the easiest ways to get these is via the **Steam Console**:
1. Go to [SteamDB - BeamNG Manifests](https://steamdb.info/depot/284161/manifests/) (You can see earlier versions by signing in with your Steam account).
2. Find a date where the mod used to work and copy the **Manifest ID**.
3. Press `Win + R`, type `steam://open/console`, and run:
   `download_depot 284160 284161 [MANIFEST_ID]` (without the "[ ]", for example download_depot 284160 284161 7682944342838776037)
   (note: you MUST own a copy of the game already)
5. Optionally, once downloaded, move the **`content`** folder to a new location location (e.g., `Documents/BeamNG_Legacy_Assets`) and delete the rest of the older version of the game if you wish.

### 3. Fixing a Mod
1. Open **BeamNG Texture Fixer**.
2. Select your **Old Content Folder** (the one you created in Step 2).
3. Select your **New Content Folder** (usually something like "...SteamLibrary\steamapps\common\BeamNG.drive\content"
4. Select the broken mod ZIP.
5. Click **Scan Mod(s)** to see what's missing.
6. Click **Build Fixed Mod(s)** to generate your repaired mod!

---

## ⚖️ License & Legal
This project is licensed under the **GNU GPL v3**. 

**Disclaimer:** This tool does not ship with any BeamNG.drive assets. Users are responsible for providing their own game files through official channels (Steam). BeamNG.drive is a trademark of BeamNG GmbH.
