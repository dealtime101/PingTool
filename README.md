# PingTool

A small Windows desktop tool that watches whether hosts answer, how fast, and what changed when they stop.

It started as a one-address ping window. It now watches several targets at once, with ping, TCP, web and DNS probes, tells you **where** a fault most likely is, keeps a timeline of the whole session, and can save a report you can send to someone who does not have the tool.

## Features

- **Several targets at once**, each with its own statistics, graph and alerts.
- **Four kinds of probe** (see [Targets](#targets)): ping, TCP port, web request, DNS lookup.
- **Live numbers**: last result, min / average / max, jitter, loss, and the *recent* picture (p50 / p95 / p99, jitter and loss over the last 60 pings, so an old spike does not hide an improvement).
- **Live graph** of the last 180 pings, or **all targets on one graph** to compare them on the same time scale.
- **"Where is the fault?"** compares the targets side by side: everything down (this PC or its link), local targets up but every Internet target down (router or ISP), only some targets down, and so on. It is a hint from the pattern, and says so.
- **Alerts** (sound and notification) when a target goes down, becomes degraded (too slow or losing too many pings) and recovers. The two limits are set in the window.
- **Incidents**: outages and slowdowns as periods (when they started, how long, how many pings failed, the most frequent cause, how often it came back), not as a long list of failed probes.
- **Network path at each outage**: when a target goes down, the route to it is traced and compared with the route seen while it was healthy, so a changed or silent router is named.
- **Session timeline**: the *whole* session of a target in one picture with a time axis, with outages and slowdowns shaded and a summary of when it was slowest and when it lost the most.
- **Readable failures**: a short word on the big result ("Timeout", "No host", "Refused", "HTTP 503", "TLS", "Frag"...) and the full explanation on hover, instead of one generic "Timeout".
- **Automatic log** (optional, **Save the log to disk**): every ping appended to a daily CSV per host in `%APPDATA%\PingTool\logs` (change the folder with `LogFolder` in `settings.json`), so a night of monitoring or a crash loses nothing. A file open in a spreadsheet is retried, never a reason to stop.
- **Export**: a timestamped CSV log, and a **self-contained HTML report** (one file, no script, graphs drawn inline) for an ISP or an IT team.
- **Profiles**: save a set of targets and settings under a name (a quick check, a long watch, the office network, a hotel wifi) and switch with one click.
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

## Alerts

For each target, PingTool looks at the last 10 pings:

- **Down**: 3 pings in a row failed (the **Down after** box sets that number). **Back up** at the first reply; the notification says how long the outage lasted ("back up after 2 min 14 s").
- **Degraded**: a full window of 10 pings shows at least the *loss* limit (default 30 %) or at least the *slow* limit as an average (default 150 ms). One isolated spike does not trigger it.
- **Recovered**: loss back under 10 % and the average under 80 % of the slow limit, so a target hovering at the limit does not flap.

The thresholds are the three boxes **Slow above (ms)**, **Loss at least (%)** and **Down after**.

## Settings

| Setting | Range | Default |
|---|---|---|
| Interval between probes | 100 – 60 000 ms | 1 000 ms |
| Timeout | 100 – 10 000 ms | 1 000 ms |
| Packet size (ping only) | 1 – 65 500 bytes | 32 bytes |
| Slow above | 1 – 60 000 ms | 150 ms |
| Loss at least | 1 – 100 % | 30 % |
| Down after | 1 – 20 failed pings in a row | 3 |

They are saved in `%APPDATA%\PingTool\settings.json`. A hand-edited file is checked on load: out-of-range numbers are pulled to the nearest limit, and blank or duplicate entries in the lists are dropped. A damaged file is replaced by defaults instead of stopping the program.

Up to 20 profiles and the 10 most recent addresses are kept.

## Requirements

- Windows.
- The [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (the program is framework-dependent).

## Build

```
dotnet build -c Release
dotnet publish PingTool/PingTool.csproj -c Release -r win-x64 --self-contained false
```

The published folder holds `PingTool.exe` and the few files next to it (`PingTool.dll` and the `.json` files that describe it); copy the whole folder anywhere. On Linux or macOS the project can be *built* (not run) by adding `-p:EnableWindowsTargeting=true`.

## Versions

The version number lives in one place, `<Version>` in `PingTool/PingTool.csproj`, and is shown in the window title (`PingTool v1.7.2`). A build made with `-p:SourceRevisionId=<commit>` also carries the commit it came from (`1.7.2+077830b`), which shows in the file properties of `PingTool.exe`. Releases are tagged `vX.Y.Z`.

## Things worth knowing

- **DNS probe**: it goes through the operating system's resolver, cache included, so a name looked up a moment ago answers in 0 ms. It catches a resolver that stopped answering, not a specific DNS server.
- **Web probe**: it sends a real `GET` (the body is not read) and does not follow redirects. A certificate that cannot be validated is a failure ("TLS").
- **Network path**: it uses ICMP with a growing TTL. Many routers do not answer such probes, so a silent hop does not prove a fault at that spot. For `tcp://` and `http(s)://` targets a firewall that drops ICMP leaves the trace silent. IPv6 paths have not been tried.
- **Log size**: the log behind the CSV export and the session timeline keeps the latest 100 000 measurements (about 27 hours at one ping per second) and starts again at each **Start**.
- **Packet size** applies to ping only.
- **CSV**: a field that a spreadsheet would run as a formula (it starts with `=`, `+`, `-` or `@`, even after spaces or invisible characters) is prefixed with an apostrophe. A few other programs show that apostrophe.

## Project status

Early releases (1.x). The probes, statistics, alert logic, timeline, report and settings handling are covered by automated checks that are kept outside this repository; there is no test project in the repository yet.

## License

MIT. See [LICENSE.txt](LICENSE.txt).
