# In-game screenshots: `acr-autoshot` (Linux development tool)

`tools/acr-autoshot/acr-autoshot.py` starts a built stage in Assetto Corsa Rally on its own, takes screenshots on the
start line and closes the game again, so the result of a change can be looked at (by a person or an agent) without
driving the menus by hand. About 50 s from launch to the stage, under 2 minutes per stage in all.

```bash
tools/acr-autoshot/acr-autoshot.py run --stage AcrXsNz01r5 --shots 2
tools/acr-autoshot/acr-autoshot.py run --stage AcrXsNz01r5,AcrXsSc03r2 --time 09:00 --car VWPoloGTIR5
```

Screenshots go to `work/autoshot/shots/<id>-<time>-<n>.png` (full screen resolution). Leave the PC alone while it
runs: keys are only sent while the game is the active window, and the run stops if it is not.

## What it does

1. Writes the stage (`<id>FullForward`), and optionally the car and time of day, into the game's current selection in
   `PlayerDataSaveSlot.sav` (game closed; backup in `work/autoshot/save-backups/`).
2. Starts the game through Steam (`steam://rungameid/3917090`).
3. Takes a screenshot about every 0.7 s (spectacle) and recognises the menu screen from where the red highlight is;
   a key is pressed only on a screen seen twice in a row, and only on the screen it is meant for:

   | Screen | Key |
   |---|---|
   | title, "PRESS ANY BUTTON" (not while it says "Loading") | Enter |
   | HOME | E (Racing tab) |
   | RACING, RALLY or TIME ATTACK tile lit | Down |
   | RACING, FREE PRACTICE tile lit | Enter |
   | Free Practice set-up, START SESSION lit | Enter |
   | SERVICE PARK, START SESSION lit | Enter |
   | intro in the car ("Enter SKIP") | Enter until the stage HUD (timer box) shows |

4. On the stage: waits `--settle` seconds, hides MangoHud (presses its `toggle_hud` key when MangoHud is configured),
   presses any `--pre-keys`, takes `--shots` screenshots `--interval` s apart.
5. Closes the game and puts the save back as it was (`--keep-open`, `--keep-save` to skip).

When it stops short it saves the last screen as `work/autoshot/shots/<id>-failed.jpg`. "The game is not the active
window" names the window that had the focus instead (typing in another window during a run moves the focus away;
no key is sent then).

## Other commands

- `set-stage --stage <id> [--car] [--time]` / `restore`: only write the selection, or put back the last backup.
- `probe [--save file.png]`: capture the screen and print which screen it is.
- `key Enter [Down ...]`: press keys in the game (only while it is in front).
- `record [--launch]`: record your own key presses in the game with the screen just before each one, to learn a
  new menu path (`work/autoshot/record-<time>/`). Keys pressed in other windows are not recorded.
- `close`: close the game.

## Requirements and limits

- KDE Plasma (Wayland) with the game under Proton/XWayland (the default), `spectacle`, `xprop`, Python 3 with Pillow
  and python-evdev, write access to `/dev/uinput` (logind gives it to the logged-in user on Bazzite). Steam running.
- Screen recognition is measured on 21:9 (3440 x 1440) captures of game v0.6 in English; any screen at least as
  wide as 16:9 should work. A game update that moves the menus needs new regions (`record`, then the `SCREENS` table).
- The game's default keyboard menu keys (Enter, E, arrows). The shots are taken on the start line with the game's
  HUD and the default (chase) camera; the car does not move.
- The save-slot handling and the menu-recognition approach are adapted from
  [acr-daily](https://github.com/khanhonthetrack/acr-daily) (MIT); see THIRD_PARTY_NOTICES.md.
