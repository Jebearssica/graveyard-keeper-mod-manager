# Changelog

## 1.1.1

- **Fixed two close buttons in the title bar.** The window is drawn wider than the save screen it is cloned from, and the close button's decorative frame was being moved with it, so the frame drifted away from the cross and read as a second button. The frame now stays attached to the cross, matching the game's own windows.
- **Mouse wheel now scrolls the mod list.** The game's save list only supports click-and-drag, so the wheel did nothing on a list this long. Both wheel directions now scroll, and dragging works across the full list area instead of only its middle.

## 1.1.0

- **New native window.** The mod list now uses the game's own window frame, fonts, scroll view and dialogs, and scales with the game's resolution and UI scale setting.
- The whole window is larger than the original save screen, so mod names, versions and descriptions are readable.
- **Variable row height.** Rows wrap long names and descriptions and grow to fit, instead of clipping the text.
- **Check box per row.** A green tick means enabled, a red cross means disabled, and a padlock marks the manager's own row. Status text is no longer needed, so the space is given to the mod name.
- **Restart prompt.** Closing the window after changing something offers to restart the game. The restart waits for the current process to exit and then relaunches it - through Steam when the game is a Steam install with the client running, and directly otherwise.
- Toggling a mod twice (back to its original state) does not trigger the restart prompt.
- The gamepad **Back** button and `Esc` close the window.
- The save-slot specific actions (select, delete, gamepad focus) are removed from the cloned rows, so clicking a row can only toggle a mod.
- If the native window cannot be created, the manager falls back to the simpler window and logs why.

## 1.0.0

- First release.
- Main-menu **MODS** entry opens a list of every installed BepInEx plugin.
- Shows name, version, description, GUID and current state for each mod.
- Enable or disable a mod by renaming its DLL to `.dll.disabled`; the change applies on the next launch.
- Warns before disabling a mod that another enabled mod hard-depends on.
- English and Simplified Chinese descriptions, with a fallback to the assembly description for unknown mods.
