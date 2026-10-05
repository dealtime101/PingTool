# PingTool

A small Windows desktop tool that watches whether hosts answer, how fast, and what changed when they stop.

It started as a one-address ping window. It now watches several targets at once, with ping, TCP, web and DNS probes, tells you **where** a fault most likely is, keeps a timeline of the whole session, and can save a report you can send to someone who does not have the tool.

## Features

- **Several targets at once**, each with its own statistics, graph and alerts.
- **Four kinds of probe** (see [Targets](#targets)): ping, TCP port, web request, DNS lookup.
- **Live numbers**: last result, min / average / max, jitter (the mean difference between two replies that follow each other: a lost ping breaks the chain), loss, and the *recent* picture (p50 / p95 / p99, jitter and loss over the last 60 pings, so an old spike does not hide an improvement).
- **Live graph** of the last 180 pings, or **all targets on one graph** to compare them on the same time scale.
- **"Where is the fault?"** compares the targets side by side: everything down (this PC or its link), local targets up but every Internet target down (router or ISP), only some targets down, and so on. It is a hint from the pattern, and says so.
- **Alerts** (sound and notification) when a target goes down, becomes degraded (too slow or losing too many pings) and recovers. The two limits are set in the window.
- **Incidents**: outages and slowdowns as periods (when they started, how long, how many pings failed, the most frequent cause, how often it came back), not as a long list of failed probes. In the incident window, a click on a column header sorts by that column (again to reverse; dates, durations and counts are compared as values), and the window can be enlarged, with a bar between the list and the route text.
- **Network path at each outage**: when a target goes down, the route to it is traced and compared with the route seen while it was healthy, so a changed or silent router is named.
- **Diagnose my connection**: the first entry of the profile list (and `PingTool.exe --diagnose`) fills the list with the default gateway and DNS servers read from the active network card, plus 1.1.1.1, 8.8.8.8, a name lookup and a public web address, so that the report can say whether the fault is on the PC's side, the router, the provider or the Internet, without typing any address. Without a gateway (not connected) only the Internet references are listed, and it says so.
- **Network changes of this PC**: while monitoring, a change of the PC's own network (a Wi-Fi access point, a VPN connecting, a cable unplugged, a wake from sleep: new address, new gateway, a card appearing or going away) is noted with its time, drawn as a cyan dotted line on the session timeline and listed in the HTML report, so a cut that starts at that moment is not blamed on the provider. The Wi-Fi name is not read; the card name and addresses are. Virtual adapters (Docker, Hyper-V, WSL) count too.
- **Availability summary in the report**: at the top of the HTML report, per target, the availability (the share of the period not spent in an outage, never rounded up), the number of outages, the total, average and longest, and the ping loss; further down, a **loss by hour** grid (a row per day, a square per clock hour, from green to red) that shows when the cuts happen ("mostly between 7 and 11 pm").
- **A name and limits for each target**: right-click a target, then **Name and limits of this host...** gives it a readable name ("Box", "Office VPN", shown in the list, the alerts, the graph legend and the report, with the address beside it in the report) and, if you want, its own *slow above*, *loss at least* and *down after* values in place of the global ones (a router at 2 ms and a server overseas at 180 ms do not share the same normal). The name applies at once, the limits from the next Start. They are kept per address in `settings.json` (`TargetOptions`) and go with a profile when you save it; the report has a **Limits** column.
- **Trace route on demand**: right-click a target in the list, then **Trace route to this host** (the address must have been resolved once, so start pinging first).
- **Session timeline**: the *whole* session of a target in one picture with a time axis, with outages (red stripes) and slowdowns (plain orange tint) shaded, so that the two are told apart without relying on colour, and a summary of when it was slowest and when it lost the most. The vertical scale follows the 98th percentile of the columns, not the single worst reply: one 3-second spike does not flatten the rest of a long night; the columns above the scale are cut at the top, marked with a small orange triangle, and the axis and the summary give the true peak. The chart can be reached with Tab and describes itself to a screen reader: the summary, then the incidents of that target in order (kind, start, length) and the network changes.
- **Readable failures**: a short word on the big result ("Timeout", "No host", "Refused", "HTTP 503", "TLS", "Frag"...) and the full explanation on hover, instead of one generic "Timeout".
- **Automatic log** (optional, **Save the log to disk**): every ping appended to a daily CSV per host in `%APPDATA%\PingTool\logs` (change the folder with `LogFolder` in `settings.json`), so a night of monitoring or a crash loses nothing. A file open in a spreadsheet is retried, never a reason to stop. If the folder stays unwritable for hours (the queue keeps the latest 20 000 pings), the pings that had to be thrown away are counted and PingTool says how many and from when to when, instead of leaving a log that looks complete.
- **Open a log**: **Open a log...** reads back one or several log files (the CSV of the export, or the daily files of **Save the log to disk**) and shows the whole night again: session timeline, incidents, and the HTML report, for a session that is over (application closed or crashed). The incidents are detected again from the pings with the default limits, since a log does not record the ones used at the time.
- **Export**: a timestamped CSV log, and a **self-contained HTML report** (one file, no script, graphs drawn inline) for an ISP or an IT team.
- **Profiles**: save a set of targets and settings under a name (a quick check, a long watch, the office network, a hotel wifi) and switch with one click. **Export profiles** writes them all to a `.json` file; **Import profiles** reads one (yours from another PC, or a colleague's). The file is checked on import: numbers are pulled into range, targets the address box would refuse are left out, and you are told which of your profiles would be replaced before anything changes (profiles with the same name, case ignored).
- **Compact mode**: a small always-on-top window that minimizes to the notification area.
- Settings, recent addresses and profiles are remembered between launches.

## Targets

Type an address in the box and press **Enter** (or **Start**). The text before `://` chooses the probe:

| You type | What is checked | What the time means |
|---|---|---|
| `google.ca`, `8.8.8.8` | Ping (ICMP echo) | Round-trip time |
| `tcp://example.com:443` | Can a TCP connection be opened to that port? | Time to connect |
| `http://host/path`, `https://host/path` | Does the web server answer? `4xx` / `5xx` count as failures; a redirect counts as an answer | Time until the response headers arrive, on a new connection each time |
| `dns://example.com` | Does the name resolve? | Time of the lookup |

Use **Add address** to build a list of several targets, then **Start** to probe them all in parallel.

### Web address options

A web address can end with options after a `#` (never sent to the server):

| Option | Effect |
|---|---|
| `https://example.com/#contains=Welcome` | the page must contain that text (case ignored, first 64 KB read). A maintenance page, an error page that answers `200`, or the login page of a captive portal (hotel Wi-Fi: a redirect) is then a **failure** (`Content` or `Redirect`) instead of a success. Write spaces as `%20` |
| `https://example.com/#cert=30` | a **notice** (balloon, webhook, report) when the TLS certificate expires in less than 30 days; default 14, `0` = never. A reminder a day while it lasts. An expired or untrusted certificate is a `TLS` failure as before |
| `https://example.com/#contains=Welcome&cert=7` | both |

A mistyped option is refused with a message rather than silently ignored.

## Alerts

For each target, PingTool looks at the last 10 pings:

- **Down**: 3 pings in a row failed (the **Down after** box sets that number). **Back up** at the first reply; the notification says how long the outage lasted ("back up after 2 min 14 s").
- **Degraded**: a full window of 10 pings shows at least the *loss* limit (default 30 %) or at least the *slow* limit as an average (default 150 ms). One isolated spike does not trigger it.
- **Recovered**: loss back to 10 % or less (one lost ping in ten still counts) and the average at 80 % of the slow limit or less, so a target hovering at the limit does not flap.

The thresholds are the three boxes **Slow above (ms)**, **Loss at least (%)** and **Down after**.

## Webhooks

Besides the sound and the balloon, each alert (down, degraded, recovered, back up) can be posted to up to 5 webhooks, so that you hear about an outage on your phone or in your team's channel when nobody is at the PC. Put the addresses in `settings.json` (close PingTool first):

```json
"Webhooks": [ "https://hooks.slack.com/services/…", "https://ntfy.sh/my-private-topic" ]
```

The format follows the address: **Slack** (`{"text": …}`), **Discord** (`{"content": …}`), **ntfy** (plain text, with a priority), anything else a small JSON that carries `text`, `host`, `event` (`down`, `up`, `degraded`, `recovered`), `outageSeconds`, `time`, `source` and `version` (this also suits Teams workflows and Mattermost). The alert text is the one of the balloon, with the length of the outage once the host is back.

- **It never holds up the monitoring**: alerts are queued and sent in the background, with a 5 s time limit and 2 retries (after 1 s, then 4 s) on network errors, timeouts, `429` and `5xx`. A `4xx` means a wrong address and is not retried. If a receiver stays down, at most 100 alerts are kept per webhook, the oldest going first.
- **Only `http://` and `https://` addresses**, distinct, at most 5: others are ignored and a message at start-up says how many (never which, see below). Use `https://` unless the receiver is on your own network.
- **A webhook address is a secret** (whoever has it can post to your channel). PingTool never shows it: messages name the host only. It is stored as plain text in `settings.json`, like the rest of the settings.
- When an alert cannot be delivered after the retries, one balloon says so for that webhook, once per run.
- It follows the **Alert on outage / slowdown / recovery** box: unticked, nothing is sent either.
- **`PingTool.exe --test-webhooks`** sends a test message to every webhook, shows the result of each and exits (up to about 20 s if a receiver does not answer).

## Settings

| Setting | Range | Default |
|---|---|---|
| Interval between probes (start to start, whatever the answer took) | 100 – 60 000 ms | 1 000 ms |
| Timeout | 100 – 10 000 ms | 1 000 ms |
| Packet size (ping only) | 1 – 65 500 bytes | 32 bytes |
| Slow above | 1 – 60 000 ms | 150 ms |
| Loss at least | 1 – 100 % | 30 % |
| Down after | 1 – 20 failed pings in a row | 3 |

They are saved in `%APPDATA%\PingTool\settings.json`. A hand-edited file is checked on load: out-of-range numbers are pulled to the nearest limit, and blank or duplicate entries in the lists are dropped. Trailing commas, `//` and `/* */` comments and property names in any case (`intervalMs`) are accepted; comments are not kept when PingTool saves the file again. A damaged file does not stop the program: it is kept aside (`settings.json.bad-<date-time>`, never overwritten), PingTool starts with defaults and says so, so the file can be fixed instead of lost. A file that cannot be opened at all (locked, no permission) is left alone and reported.

Up to 20 profiles and the 10 most recent addresses are kept.

## Command line

```
PingTool.exe --start --minimized 8.8.8.8 router.lan tcp://example.com:443
```

| Option | Effect |
|---|---|
| `--start` | start monitoring as soon as the window is up |
| `--minimized` | start hidden in the notification area (double-click the icon to open the window; alerts still show as balloons) |
| `--interval <ms>` | time between probes, 100 to 60 000 |
| `--headless` | **no window**: probe for `--duration`, write the report, exit (see below) |
| `--duration <time>` | with `--headless`: how long to probe, `5s` to `30d` (`30s`, `10m`, `8h`, `2d`) |
| `--report <file>` | with `--headless`: the HTML report to write (the folder is created if needed) |
| `--diagnose` | monitor the default gateway, the DNS servers and a few Internet references |
| `--test-webhooks` | send a test alert to the webhooks of `settings.json`, show the result and exit |
| `--help` | show the options |

Targets are written as in the address box and replace the saved list **for that launch only**; what the command line imposes is not saved, so a shortcut never overwrites the list built in the window. A mistyped option or target is reported in a box and nothing starts. To monitor from logon, put a shortcut with these arguments in the Startup folder (`shell:startup`).

### Without a window (Task Scheduler, scripts)

```
PingTool.exe --headless --duration 8h --report C:\reports\night.html 8.8.8.8 router.lan tcp://example.com:443
```

It probes with the same probes, monitor, incident detection and report as the window (the interval, timeout, limits, names and webhooks come from your `settings.json`; `--interval` and the targets win for this run), then writes the report and exits. The targets are the ones given, or `--diagnose`, or else the list saved in the window. The report is also **refreshed every 10 minutes** while it runs, so a process killed in the night still leaves one.

| Exit code | Meaning |
|---|---|
| `0` | no incident: no outage and no slowdown |
| `1` | at least one outage or slowdown (a target went down, or was slow or lossy beyond its limits) |
| `2` | it could not run: a wrong option or target, no probe made, or the report could not be written |

The summary goes to the standard output and any problem to the error output (redirect them with `>` and `2>` in a script); nothing is ever shown in a box, so a scheduled run never waits for a click. A `down` alert still goes to the webhooks while it runs.

## Requirements

- Windows.
- The [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (the program is framework-dependent).

## Build

```
dotnet build -c Release
dotnet publish PingTool/PingTool.csproj -c Release -r win-x64 --self-contained false
```

The published folder holds `PingTool.exe` and the few files next to it (`PingTool.dll` and the `.json` files that describe it); copy the whole folder anywhere. On Linux or macOS the project can be *built* (not run) by adding `-p:EnableWindowsTargeting=true`.

## Versions

The version number lives in one place, `<Version>` in `PingTool/PingTool.csproj`, and is shown in the window title (`PingTool v1.7.2`). A build made with `-p:SourceRevisionId=<commit>` also carries the commit it came from (`1.7.2+077830b`), which shows in the file properties of `PingTool.exe`. Releases are tagged `vX.Y.Z`, and each tag gets a [GitHub Release](https://github.com/dealtime101/PingTool/releases) with the zip (`PingTool-X.Y.Z-win-x64.zip`) and its SHA-256, built by a GitHub Actions workflow (`.github/workflows/release.yml`) from the tagged commit.

## Things worth knowing

- **DNS probe**: it goes through the operating system's resolver, cache included, so a name looked up a moment ago answers in 0 ms. It catches a resolver that stopped answering, not a specific DNS server.
- **Web probe**: it sends a real `GET` (the body is not read) and does not follow redirects. A certificate that cannot be validated is a failure ("TLS").
- **Network path**: it uses ICMP with a growing TTL. Many routers do not answer such probes, so a silent hop does not prove a fault at that spot; routers also rate-limit these replies, so each hop gets 3 probes and is called silent only if all 3 went unanswered (a path that is really dead costs up to 9 seconds to trace). On a load-balanced path a distance can be answered by several routers: all those seen are listed ("10.0.0.1 (also 10.0.0.2)"), and a hop is reported as changed only when the routers seen now and when healthy have none in common. If the probe itself cannot be sent (no permission, ICMP blocked on this PC), the trace says so instead of blaming the network. For `tcp://` and `http(s)://` targets a firewall that drops ICMP leaves the trace silent. IPv6 paths have not been tried.
- **Log size**: the log behind the CSV export and the session timeline keeps the latest 100 000 measurements (about 27 hours at one ping per second) and starts again at each **Start**. Once older measurements have been let go, an export warns that the file is incomplete (and from when it starts), and the timeline says how many are missing; tick **Save the log to disk** before Start to keep everything.
- **Packet size** applies to ping only.
- **CSV**: a field that a spreadsheet would run as a formula (it starts with `=`, `+`, `-` or `@`, even after spaces or invisible characters) is prefixed with an apostrophe. A few other programs show that apostrophe.

## Project status

Early releases (1.x). The probes, statistics, alert logic, timeline, report and settings handling are covered by automated checks that are kept outside this repository; there is no test project in the repository yet.

## License

MIT. See [LICENSE.txt](LICENSE.txt).
