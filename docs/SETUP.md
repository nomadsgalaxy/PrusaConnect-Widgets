# Setting up Prusa Connect Widget

Start to finish: install the app, get your printers in, pin tiles, and tell each
one what to show. There's a troubleshooting section at the end for the couple of
things that tend to go sideways - read that first if something's already stuck.

## 1. Before you start

- **Windows 11.** The Widgets Board (`Win+W`) is a Windows 11 feature.
- **PrusaSlicer, signed into Prusa Connect.** This app reuses the Connect login
  PrusaSlicer stores for you - it does not have its own sign-in. Open
  PrusaSlicer and make sure you are logged into your Prusa account
  (the account menu shows your name, not "Log in"). If you are not signed in,
  the tiles have nothing to show.

## 2. Install the app

The app is sideloaded and signed with a self-signed development certificate, so
the first install has two parts: trust the certificate, then install the
package. The included script does both.

1. Open a PowerShell window in the package folder:

   ```powershell
   cd artifacts\msix\PrusaConnect.Widget_<version>_x64_Debug_Test
   ```

2. Run the installer (it will prompt for administrator rights to add the
   certificate to the machine's trusted store):

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\Install.ps1
   ```

If you prefer to do it by hand, import the `.cer` in that folder into
**Local Machine → Trusted People**, then `Add-AppxPackage` the `.msix`.

When it finishes, **Prusa Connect Widget** appears in the Start menu - that
shortcut opens the settings app.

## 3. First launch - confirm sign-in and import printers

1. Launch **Prusa Connect Widget** from the Start menu.
2. The settings window checks for your PrusaSlicer Connect login. If it finds a
   valid token, your Connect printers are listed.
   - If it reports that you need to sign in, sign into Prusa Connect from
     PrusaSlicer, then click **Refresh**.
3. Import the printers you want to monitor. Importing pulls each printer's
   details (including its LAN address and API key where Connect exposes them)
   into the app's local store, so a Printer status tile can fall back to the
   printer directly on your network.

You can also add printers by hand, including **Klipper printers via Moonraker**
(such as the Prusa HT90). Click **Add new**, set **Connection** to
*Klipper (Moonraker)*, enter the printer's host (port is usually 80 for a
Mainsail/Fluidd box, or 7125 for Moonraker direct), and leave **API key** blank
unless your Moonraker is locked down. For the camera, paste the snapshot URL,
e.g. `http://<printer>/webcam/snapshot`. **Test connection** checks it, then **Add**.

## 4. Pin your tiles

1. Press `Win+W` to open the Widgets Board.
2. Click **+** (Add widgets) at the top.
3. Find **Prusa Connect - 1** through **4** and pin the ones you want.
4. Newly pinned tiles show a brief loading state, then "Tile not set" until you
   configure them in the next step.

Pin them one at a time and give each a moment to settle on the board before
pinning the next.

## 5. Configure each tile

1. Go back to the settings app (Start menu) and open the **Pinned tiles**
   section. Each tile you pinned shows up as a row. If a tile you just pinned is
   missing, click **Refresh**.
2. For each row, pick a **Kind**:
   - **Printer status** → choose a printer.
   - **Team status** → choose a Prusa Connect team.
   - **Farm status** / **Farm orders** → choose a Connect Farm team.
3. The matching picker (printer, team, or farm team) appears next to the kind.
   Make your selection.

Changes are saved as you make them and picked up by the tile on its next poll -
usually within about 30 seconds. Printing tiles poll faster than idle ones.

## 6. Tile sizes

On the board, each tile's **⋯** menu lets you switch between small, medium, and
large. Each kind is laid out to use the space it is given - a large Printer
status tile shows more detail; a large summary tile shows more of the fleet.

## 7. Working within four tiles

The Windows Widgets host only reliably keeps about four tiles per app pinned, so
the app ships exactly four slots. To keep an eye on more printers than that:

- Use one **Team status** or **Farm status** tile to watch a whole team at a
  glance (counts of printing / idle / needs-attention / offline).
- Use **Farm orders** to track the queue.
- Spend the remaining slots on the specific printers you care about most.

A typical layout is one summary tile, one orders tile, and two printer tiles.

## 8. Troubleshooting

**Tiles show only the grey loading skeleton and never fill in.**
The host can get stuck holding an old copy of the provider - usually after an
update or reinstall. Restart the board host so it reloads the app:

```powershell
Stop-Process -Name Widgets -Force -ErrorAction SilentlyContinue
```

Then reopen the board with `Win+W`. If it still does not render, restart
Explorer (`Stop-Process -Name explorer -Force; Start-Process explorer`) and open
the board again.

**Tiles say to sign in, or stop updating.**
The Connect token comes from PrusaSlicer and expires periodically. Open
PrusaSlicer, confirm you are signed into Prusa Connect, and the tiles recover on
the next poll.

**A pinned tile does not appear in the settings list.**
Open the board (`Win+W`) so the tile starts, wait a few seconds, then click
**Refresh** in the settings app's Pinned tiles section.

**The board (`Win+W`) won't open at all.**
Restart Explorer:

```powershell
Stop-Process -Name explorer -Force; Start-Process explorer
```

**Where are the logs?**
`%TEMP%\prusa-widget.log`. The most useful lines are the provider startup
(`-RegisterProcessAsComServer`), `Activate id=…`, and any `failed`/`Exception`
entries.

## 9. Updating

Build or obtain a newer package and run its `Install.ps1`. Your imported
printers and credentials live in the app's per-user storage and are kept across
a normal reinstall. If tiles look stale after updating, follow the skeleton
recovery in Troubleshooting - the board host often needs a restart to pick up
the new build.

## 10. Uninstall

Right-click **Prusa Connect Widget** in the Start menu and choose **Uninstall**,
or:

```powershell
Get-AppxPackage -Name PrusaConnectWidget | Remove-AppxPackage
```

This removes the app and its local storage (imported printers and saved
credentials). Pinned tiles disappear from the board.
