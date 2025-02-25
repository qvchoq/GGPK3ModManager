# GGPK3 Mod Manager 
![Project Image](https://github.com/qvchoq/GGPK3ModManager/blob/main/preview.png)

This project is a modding tool designed to work with the **[LibGGPK3](https://github.com/aianlinb/LibGGPK3)
 module**. It enables users to manage mods and related files in the `content/remove/bundle` folder, ensuring an automatic and efficient process for applying and backing up mods.

## Features

- **Configuration**: The tool reads configuration from a `presets.json` file, which contains paths to mod files and backup directories.
- **Automatic Backup**: Once the "Apply Mods" button is pressed, the tool automatically creates backups of selected mods to their respective directories.
- **File Replacement**: After creating backups, the tool replaces the files based on the paths specified in the configuration.
- **Mod Path Consistency**: The tool ensures that mod paths match the original structure as defined in the `ggpk/index` file.

## How It Works

1. **Setup**: Place the mods and their related files into the `content/remove/bundle` directory, ensuring the structure matches that of the original `ggpk/index` file.
2. **Configuration**: Edit the `presets.json` file to specify which mods to apply and where to store backups.
3. **Apply Mods**: Click the "Apply Mods" button to start the process of backing up and replacing mod files.

## Important

I have no knowledge of С#, was able to write this for my purposes, please don't blame me for having a junk code.... 

So any support from contributors with code and presets (include content folder), would be a really nice!
