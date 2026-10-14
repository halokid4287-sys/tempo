# Tempo

A free PC gaming optimizer for Windows 10/11. It scans whatever PC it's running on,
explains what's actually worth changing, and lets you pick exactly what to apply —
then keeps a record so you can undo it.

**No account, no subscription, no ads, no "pro" version.** Everything runs locally.
Nothing is uploaded anywhere.

## Download

Grab `Tempo.exe` from the [latest release](../../releases/latest). One file, no installer.
It updates itself from then on — you won't need to download it again.

> Windows will show a blue **"Windows protected your PC"** warning the first time.
> That appears for any program without a paid code-signing certificate.
> Click **More info → Run anyway**. The source is in this repo if you want to check it first.

Tempo asks for administrator rights because changing power plans and uninstalling
programs requires them. Without admin it can look, but not fix.

## What it does

| Tab | What's in it |
|---|---|
| **Dashboard** | Live CPU/GPU/RAM usage, full hardware spec, and notes specific to your PC |
| **Optimize** | ~67 options in 13 categories — settings you can undo, one-time actions, and read-only checks |
| **Startup** | Stop programs launching at boot, without uninstalling them |
| **Bloatware** | Installed apps with their real RAM/CPU/disk cost, grouped by how safe they are to remove |
| **History** | Every change made, with its original value, and an undo button |
| **Report** | Export your specs and status to share when asking for help |

## Design principles

- **Scanning never changes anything.** Changes only happen when you tick a box and confirm.
- **Originals are saved first.** Settings can be rolled back from the History tab.
- **Honest ratings.** Each option says what it does, what you gain, *and* the downsides.
  Tweaks with no real evidence behind them are labelled as such instead of being sold as
  free FPS. No tool can magically double your frame rate.
- **Protected by design.** Windows components, GPU/audio drivers, security software and
  anti-cheat are excluded from the bloatware list so they can't be removed by accident.
- **Irreversible things are labelled.** Uninstalls and file deletions say so before you confirm.

## Building from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```
git clone https://github.com/halokid4287-sys/tempo.git
cd tempo
dotnet build Tempo.sln
```

To produce the shareable single-file build:

```
dotnet publish src/Tempo.App/Tempo.App.csproj -c Release -o share
```

## Updates

Tempo checks `update/latest.json` in this repo on startup and offers to update itself.
Downloads go over HTTPS and are verified against a SHA-256 checksum before anything is
replaced. The checksum protects against corrupted downloads; it is not a substitute for
code signing, which this project doesn't have.

## Licence

Free to use, copy and modify. No warranty — it changes system settings, so read what
each option does before applying it.
