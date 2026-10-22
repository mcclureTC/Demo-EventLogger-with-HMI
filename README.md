# Demo-EventLogger-with-HMI
A TwinCAT demo for implementing the TwinCAT Event Logger, and using a C# HMI to read the events via an ADS connection.


# TwinCAT EventLogger Demo

A small, complete sample of the TwinCAT 3 EventLogger end to end:

- a **PLC project** that raises alarms and sends messages through `FB_TcAlarm` and `FB_TcMessage`,
  with event texts, severities and arguments defined in an **event class**;
- a **Windows web HMI** (a small .NET program serving one web page) that triggers the events over
  ADS and shows what the EventLogger reports: the active alarms, with confirmation, and the history.

```
Browser  --HTTP-->  EventLoggerDemo.Hmi  --ADS (GVL_Hmi)----------->  PLC  --FB_TcAlarm/FB_TcMessage-->  EventLogger
                                         --ADS (EventLogger client)------------------------------------->      |
                                           active alarms, history, confirm  <------------------------------------+
```

The page never reads alarm texts from the PLC. It gets them from the EventLogger, already formatted
with their arguments and in the configured language, which is the point of using the EventLogger.

## Contents

| Path | What it is |
|---|---|
| `TwinCAT/EventLoggerDemo.tmc` | The event class `EventLoggerDemo` (5 events). Goes into the Type System. |
| `TwinCAT/EventLoggerDemoPlc/` | The PLC project. Its own notes are in `Documentation/EventLoggerDemoPlc.md`. |
| `Hmi/` | The web HMI: ASP.NET Core minimal API plus `wwwroot` (HTML, CSS, JS, no external downloads). |

## Requirements

- Windows 10/11 with TwinCAT 3.1 build 4024 or later (XAE for engineering; XAR or XAE runtime to run).
- .NET 8 SDK on the PC that runs the HMI, and internet access for the first build (NuGet).
- The HMI talks to the local TwinCAT router, so run it on the PLC PC, or on a PC with TwinCAT and an
  ADS route to the PLC.

## 1. Create the TwinCAT solution

1. In XAE, create a new **TwinCAT XAE Project**.
2. **Add the event class:** in Solution Explorer, right-click **SYSTEM > Type System** and choose
   **Add Existing Item...**, then pick `TwinCAT/EventLoggerDemo.tmc`. The class appears on the
   **Event Classes** tab as `EventLoggerDemo`.
3. **Add the PLC project:** right-click **PLC** and choose **Add Existing Item...**, then pick
   `TwinCAT/EventLoggerDemoPlc/EventLoggerDemoPlc.plcproj`.
4. Check under **SYSTEM > Tasks** that `PlcTask` exists and is linked to the PLC instance
   (10 ms, calls `MAIN`). XAE normally creates it from the project's task object.
5. **Build** (Ctrl+Shift+B). The build generates `TC_EVENTS.EventLoggerDemo.*` from the event class.
   If `TC_EVENTS.EventLoggerDemo` is reported as unknown, the event class was not added in step 2.
6. **Activate configuration**, choose a run-mode licence if prompted (a 7-day trial licence is fine),
   then **Login** and **Start** the PLC.

Optional: open **View > Other Windows > TwinCAT Logged Events** in XAE to watch the same events there.

## 2. Build and run the HMI

The HMI is a C# program, so it has to be built once before it runs. Visual Studio isn't needed;
the .NET SDK's command-line tools are enough.

**Install the .NET 8 SDK** (one time). Open a command prompt and check:

```bat
dotnet --list-sdks
```

If no line starts with `8.` (or later), install the **.NET 8 SDK** (the SDK, not just the runtime)
from <https://dotnet.microsoft.com/download>, then open a **new** command prompt.

**Build and start**, either by double-clicking `Hmi\run.cmd`, or by hand:

```bat
cd Hmi
dotnet build --configuration Release
dotnet run --configuration Release --no-build
```

The first build downloads the two Beckhoff NuGet packages, so it needs internet access and can take
a minute. A successful build ends with `Build succeeded. 0 Error(s)`. If it fails, `run.cmd`
stops and leaves the errors on screen.

When the server is up, the console shows `Event Logger Demo HMI is running at http://localhost:5080`
and the page opens in your default browser. Keep the console window open: closing it, or pressing
Ctrl+C, stops the HMI. Set `"OpenBrowser": false` in `appsettings.json` to stop it opening the browser.

The top bar shows two links: **PLC** (ADS to `GVL_Hmi`, live while the heartbeat moves) and
**EventLogger**.

If the browser says *localhost refused to connect*, the HMI isn't running. Look at the console
window for build errors, or for the "running at" line.

To use a remote target, set `Plc:AmsNetId` in `Hmi/appsettings.json` to its AMS Net ID (an ADS
route must exist). `Plc:LanguageId` selects the text language (1033 = English).

## 3. Try it

| Do this | You should see |
|---|---|
| Drag the oven slider above 80 degC | **OverTemperature** (Error) appears with the actual and limit values in its text. |
| Drag it back below 75 degC | It shows *Cleared* but stays in the list: it still waits for confirmation. Press **Confirm**. |
| Air supply: **Raise**, then **Clear** | **LowAirPressure** (Warning) appears and leaves at once on clear: it needs no confirmation. |
| Conveyor drive: **Raise**, **Confirm**, **Clear** | Confirming before clearing also works; the alarm leaves when it is both cleared and confirmed. |
| Messages: **Machine started**, **Batch completed** | They go straight to the history with their arguments filled in. |

## How the pieces fit

**Event class.** Each event has a name, an ID, a severity and a text with placeholders, such as
`Oven temperature {0} degC is above the limit of {1} degC`. TwinCAT generates the constant
`TC_EVENTS.EventLoggerDemo.OverTemperature` (a `TcEventEntry`) from it. Texts can be translated in the
Type System without touching the PLC code.

**PLC.** `AlarmPoint` wraps `FB_TcAlarm` and `MessagePoint` wraps `FB_TcMessage`, each with an
`FB_TcSourceInfo` whose `sName` names the source (Oven, Air supply, ...). `DemoMachine` creates them
once, fills arguments immediately before raising or sending, and executes HMI requests.

**HMI contract.** `GVL_Hmi.Commands` is written by the HMI: arguments first, `Request` last. The PLC
executes the request, sets `Status.LastRequestAccepted`, increments `Status.RequestCount` and resets
`Request`. `Commands.OvenTemperature` is written directly by the slider.

**HMI server.** `PlcGateway` (package `Beckhoff.TwinCAT.Ads`) reads and writes `GVL_Hmi` by symbol
name. `EventLoggerGateway` (package `Beckhoff.TwinCAT.TcEventLoggerAdsProxy.Net`) reads
`ActiveAlarms` and `GetLoggedEvents`, gets each text with `GetText(languageId)`, and confirms alarms.
The page polls the server once a second.

## API

| Method | Path | Body / result |
|---|---|---|
| GET | `/api/status` | PLC link, heartbeat, temperature, which alarms are raised |
| POST | `/api/requests` | `{ "request": "RaiseDriveFault", "integerArgument": 17 }` |
| PUT | `/api/oven-temperature` | `{ "value": 85 }` |
| GET | `/api/alarms` | active alarms with text, severity, source, times, confirmation state |
| GET | `/api/events` | the last 50 logged events |
| POST | `/api/alarms/confirm` | the `key` object of an alarm from `/api/alarms` |
| POST | `/api/alarms/confirm-all` | - |

The HMI server listens on `localhost` only and has no authentication. It is a demo: do not expose it
on a plant network as it is.
