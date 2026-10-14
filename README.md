# VideoSync

Synchronized video playback across multiple Windows PCs on the same network. One PC is the
**host** and controls playback. Every other PC runs as a **client** and plays the same video in
lockstep: screens typically stay within one frame (~20 ms) of each other.

A single `VideoSync.exe` needs no installer or runtime. It uses .NET Framework 4.8 and WPF, which
are built into Windows 10 and 11.

## Download

Get **`VideoSync-Share.zip`** from the
[latest release](https://github.com/keuriseu/VideoSync/releases/latest). It's a ready-to-copy
folder:

```
VideoSync-Share\
    VideoSync.exe
    Start Host.cmd       <- double-click on the ONE controlling PC
    Start Client.cmd     <- double-click on every other PC
    READ ME FIRST.txt
    sync-test.mp4        <- sample clip; replace with your own video
```

1. Unzip it, put your video in the folder and delete `sync-test.mp4`.
2. Copy the folder to every PC on the same network.
3. Double-click **Start Host.cmd** on one PC and **Start Client.cmd** on the others, in any
   order. They go fullscreen and play in sync.

Windows may warn that the exe is unrecognized because it isn't code-signed: click
**More info → Run anyway**. If a downloaded file does nothing, right-click it, choose
**Properties**, tick **Unblock**, then click **OK**.

## Manual use

1. Copy `VideoSync.exe` to every PC.
2. Copy the video to every PC. Each PC plays its own local copy; only control messages go over
   the network.
3. On the controlling PC, run VideoSync, choose **Host**, then **Open video...**.
4. On every other PC, run VideoSync and choose **Client**. It finds the host automatically. If it
   doesn't, type the host's IP address and press **Connect**. On each client, set the
   **Media folder** to the folder that holds the video.
5. Press **Play** on the host. Every screen starts on the same frame at the same moment.

The first time you run it, Windows Firewall asks for network access. Allow it on **Private**
networks.

### Running unattended

- **Start with Windows:** this checkbox adds a small script to the user's Startup folder that
  relaunches VideoSync with the same options after sign-in, for example after an overnight Windows
  Update restart. The PC must sign in automatically after a restart for this to help.
- **Keep awake:** while a video plays, VideoSync stops Windows from turning off the display or
  sleeping.
- **Self-healing:** if a player freezes, its video goes black, or it can't be brought back in sync,
  VideoSync reloads the video and resyncs it within a few seconds. It also reloads after the PC
  wakes, is unlocked, or changes display settings, because these are known to blank WPF's video
  player.

### Syncing the desktop background

Click **Sync desktop background** on the host. Every connected client receives the host's current
wallpaper and its Fill/Fit/Tile setting and applies it. Clients that connect later receive it as
well.

- **Large images:** pictures over 8 MB are re-encoded as quality-92 JPEG, capped at 7680 px wide.
  Clients verify a checksum before applying.
- **Restore:** before its first change, each client saves a copy of its own background to
  `%APPDATA%\VideoSync\original-wallpaper.*`. **Restore my wallpaper** puts it back.
- **Opting out:** untick **Allow wallpaper sync** on a client, or start it with `--no-wallpaper`.
- **Feedback:** the host's screen list shows each client's result.
- **Testing:** set `VIDEOSYNC_WALLPAPER_DRYRUN=1` on a client to receive and save the picture
  without changing its desktop.
- **Limits:** a solid-color background can't be sent. Every monitor gets the same picture.

### Finding the host's address

You only need this if clients don't find the host on their own (some networks block discovery).

- **On the host's screen:** the address appears in the top-left corner, even in fullscreen,
  while no screens are connected and for 20 s after start. Press `I` to show or hide it.
- **Copy it:** press `Ctrl+C` on the host, or use the **Copy address** button next to the address
  in the toolbar.
- **The `host-address.txt` file:** the host saves its address to this file next to
  `VideoSync.exe`. Copy that file into the folder on each client PC; clients started from a folder
  that contains it connect to that address.
- **Stale addresses:** if a saved address stops working (for example, the host got a new IP),
  a client switches to any host it can discover after about 6 s.

### Controls (host)

| Action | How |
|---|---|
| Play / pause | **Play/Pause** button or `Space` |
| Seek | Click or drag the timeline; `←` / `→` jump 5 s |
| Stop (back to start) | **Stop** |
| Loop forever | **Loop** checkbox |
| Fullscreen (any PC) | `F11`, double-click the video, or the **Fullscreen** button. `Esc` exits. |

The host's side panel lists every connected screen with its live drift and status.

### Unattended / signage setup

Command-line options let PCs start straight into playback, for example from a startup shortcut:

```bash
VideoSync.exe --host --file "D:\Media\show.mp4" --loop --play --fullscreen
```

```bash
VideoSync.exe --client --folder "D:\Media" --fullscreen
```

| Option | Meaning |
|---|---|
| `--host` | Start as host |
| `--client [address]` | Start as client; without an address, connect to the first host discovered |
| `--file <path>` | (host) open this video |
| `--play` | (host) start playing immediately |
| `--loop` | (host) loop the video |
| `--folder <path>` | (client) where to look for the video files |
| `--fullscreen` | start fullscreen |
| `--mute` | start muted |
| `--no-wallpaper` | (client) never accept the host's desktop background |

Clients reconnect automatically if the host restarts or the network drops. Until then they keep
playing on the last known timeline. Settings such as role, host, folder and volume are remembered
in `%APPDATA%\VideoSync\settings.ini`.

## How it works

- **Discovery:** the host broadcasts a UDP beacon on port 47801 every second, on every network
  interface.
- **Control:** clients connect to the host over TCP port 47800.
- **Clock sync:** each client pings the host once a second and estimates the host's clock
  NTP-style, using the lowest-latency of the last 15 samples. On a wired LAN this is
  sub-millisecond.
- **Shared timeline:** the host broadcasts the state as *"video X is at position P at host time T,
  playing or paused"*. Starts, seeks and loop restarts are scheduled about 0.5 s in the future, so
  every screen begins on the same frame at the same instant.
- **Drift correction:** every 100 ms each PC (the host included) compares its player position with
  the timeline and corrects it. The method comes from measuring how WPF's MediaElement actually
  behaves:
  - **Ahead:** hold the current frame until the timeline catches up. Pause→Play resumes within a
    few ms.
  - **Behind:** seek well past the timeline, then hold to trim the excess. Seeks during playback
    take an unpredictable 70–600 ms, so they can't be aimed precisely.
  - **Playback speed is never changed.** Every `SpeedRatio` change stalls the player for
    150–250 ms.
  - **Learned latencies:** each PC learns its own start latency and seek cost and stores them in
    `settings.ini`.
- **Recovery:** if Media Foundation fails mid-playback, the video is reloaded and resynced
  automatically.

### Measured results

Results from host and client running on the same PC with the included 30 s test clip, over 100 s
of playback with 3 loop restarts, a pause/resume and a client joining mid-play:

- 99–100% of drift readings were within 20 ms.
- Steady state was −7 to +16 ms per screen.
- A client joining mid-play converges in about 2 s.

## Testing sync by eye

`samples\sync-test.mp4` is a 30 s 720p test pattern with a large running timestamp and frame
counter. Play it on all screens and photograph them together: the frame numbers should match or
differ by one.

## Notes and limits

- **Formats:** VideoSync plays what Windows Media Foundation can decode. MP4 (H.264/AAC) works
  everywhere. HEVC, VP9 and AV1 need the matching codec extensions from the Microsoft Store.
- **Same file on every PC:** if a client's copy differs in size from the host's, a warning is
  shown. Clients look for the file by name in the media folder, then at the host's full path, then
  next to the exe.
- **Network:** wired Ethernet gives the tightest clock sync. Wi-Fi works but has more jitter.
- **Firewall:** if you dismissed the firewall prompt, allow the app from an elevated prompt (adjust
  the path):

```bash
netsh advfirewall firewall add rule name="VideoSync" dir=in action=allow program="C:\VideoSync\VideoSync.exe" enable=yes profile=private,domain
```

- **Logs:** written to `%APPDATA%\VideoSync\videosync.log`. While playing, the host logs every
  screen's drift every 10 s. If that folder can't be written, the log goes to `videosync.log` next
  to the exe instead.
- **Testing switches:** these environment variables are for development:

  | Variable | Effect |
  |---|---|
  | `VIDEOSYNC_DEBUG=1` | per-tick sync trace in the log |
  | `VIDEOSYNC_PORT=<n>` | use ports n and n+1 instead of 47800/47801, so a test doesn't disturb a running setup |
  | `VIDEOSYNC_FAULT=dead@15,ahead@40` | inject a frozen player, or a 30 s jump ahead, at the given seconds to exercise self-healing |

## Building

Run `build.ps1`. It compiles with the C# compiler that ships with Windows
(`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`); no SDK or Visual Studio is needed.

```bash
powershell -ExecutionPolicy Bypass -File build.ps1
```

To build the ready-to-copy folder and zip in `dist\`, add `-Package`:

```bash
powershell -ExecutionPolicy Bypass -File build.ps1 -Package
```

Repository layout:

- `src\Program.cs`: entry point and command-line options
- `src\MainWindow.cs`: UI and the sync engine
- `src\Network.cs`: host server, discovery beacons, client connection and clock sync
- `src\Protocol.cs`: shared clock, playback state, message framing, settings and log
- `src\Wallpaper.cs`: reading, sending and applying the desktop background
- `share\`: launcher scripts and quick guide included in the zip
- `samples\sync-test.mp4`: test clip with an on-screen timer and frame counter
