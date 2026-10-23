# EventLoggerDemoPlc

Sample PLC project showing how a TwinCAT 3 PLC reports alarms and messages through the
TwinCAT EventLogger, and how a web HMI triggers and displays them.

## What it does

| Event | Kind | Severity | Confirmation | Raised by | Source |
|---|---|---|---|---|---|
| OverTemperature | Alarm | Error | Yes | PLC, oven temperature above 80 degC (clears below 75 degC) | Oven |
| LowAirPressure | Alarm | Warning | No | HMI request | Air supply |
| DriveFault | Alarm | Critical | Yes | HMI request | Conveyor drive |
| MachineStarted | Message | Info | - | HMI request | Operator panel |
| BatchCompleted | Message | Info | - | HMI request | Production |

The event texts and severities live in the event class `EventLoggerDemo`, which is defined in
`EventLoggerDemo.tmc` and added to the Type System. TwinCAT generates `TC_EVENTS.EventLoggerDemo.*`
from it when the PLC project is built.

## Structure

| Object | Purpose |
|---|---|
| `AlarmPoint` | One `FB_TcAlarm` with its own `FB_TcSourceInfo`. Create, NewArguments, Raise, Clear. |
| `MessagePoint` | One `FB_TcMessage` with its own `FB_TcSourceInfo`. Create, NewArguments, Send. |
| `DemoMachine` | The demo application: oven temperature supervision and HMI request handling. |
| `GVL_Hmi` | The HMI contract: `Commands` (written by the HMI) and `Status` (read by the HMI). |
| `AlarmListener` | Extends `FB_ListenerBase2`: keeps a table of the alarms raised on this system. |
| `GVL_EventLogging` | Size of that table, its text language, subscription retry interval. |
| `MqttPublisher` | Publishes `GVL_Hmi.PlcAlarms` to an MQTT broker with TF6701 (see `MQTT.md` in the repository). |
| `AlarmJsonWriter` | Builds the JSON payloads with `FB_JsonSaxWriter`. |
| `F_AlarmKey`, `F_FileTimeToIso` | An alarm's MQTT key; a FILETIME as ISO 8601 UTC. |
| `GVL_Mqtt` | Publish mode, heartbeat, text wait, enable, and the publisher's status. |
| `GVL_MqttSite` | This PC's broker connection: host, port, TLS, Thing Name, certificate files. |
| `MAIN` | Calls `DemoMachine`, then `AlarmListener`, then `MqttPublisher`. |

## AlarmListener: reading the EventLogger in the PLC

Tc3_EventLogger has no block that returns the EventLogger's list of active alarms. A listener is
the PLC's way to follow it: `AlarmListener` extends `FB_ListenerBase2`, subscribes once, calls
`Execute` every cycle, and keeps `GVL_Hmi.PlcAlarms` up to date from the callbacks.

| Callback | Effect on the table |
|---|---|
| `OnAlarmRaised` | Adds a row: event entry, source name, time raised, text |
| `OnAlarmConfirmed` | Sets `IsConfirmed` on the row |
| `OnAlarmCleared` | Removes the row |
| `OnAlarmDisposed` | Removes the row (the alarm instance was released) |

- The table holds **raised** alarms only. A cleared alarm still waiting for confirmation leaves the
  table, although the EventLogger keeps it in its active list. The web HMI shows that full list.
- It sees every alarm on the TwinCAT system, from any PLC or module, not only this project's.
- It only learns of alarms raised after it subscribed.
- Each callback receives `REFERENCE TO FB_TcEvent`, which must not be copied, so the table stores
  the event's details rather than the event.
- Texts arrive a few cycles after the raise. `OnAlarmRaised` starts an `FB_RequestEventText`
  request with the event's class, ID and arguments (while the event is still valid); every cycle
  `CollectTexts` copies finished texts into their rows and sets `TextAvailable`. Asking
  `fbEvent.RequestEventText` once in the callback is not enough: the first request for an event
  only starts, so the text appeared from the second raise onwards.
- `GVL_EventLogging.MaxPlcAlarms` (32) sets the table size. `Overflow` turns TRUE when a raised
  alarm did not fit.
- `ChangeCount` rises when an alarm is added, confirmed or removed, and `TextCount` when a text
  arrives. `MqttPublisher` watches both to know when to publish.

## MqttPublisher

Publishes the alarm table using only what AWS IoT Core supports: MQTT 3.1.1, QoS 1, TLS with a client
certificate, topics under `dt/eventloggerdemo/<thing>/`. The full description, topics and payloads are
in `MQTT.md` at the repository root.

- Settings in `GVL_MqttSite` and `GVL_Mqtt.PublishMode` are read once, at PLC start.
- On every (re)connect: `status` online, the other mode's retained topic emptied, the full table.
- A change waits up to `GVL_Mqtt.TextWaitTime` for alarm texts. A publication takes a copy of the
  table and sends one message per cycle, so per-alarm mode needs a few cycles.
- The Last Will is set to QoS 1 explicitly: TF6701's default for it is QoS 2, which AWS refuses.
- `GVL_Mqtt.Status` shows the connection state, error code, sequence and counters.

## HMI contract

The HMI writes the request arguments first and `GVL_Hmi.Commands.Request` last. The PLC executes
the request in the next cycle, writes `Status.LastRequestAccepted`, increments `Status.RequestCount`
and sets `Request` back to `NotSet`. The HMI waits for `RequestCount` to change.

| Request | Arguments |
|---|---|
| RaiseLowAirPressure (1) | IntegerArgument: pressure in mbar |
| ClearLowAirPressure (2) | - |
| RaiseDriveFault (3) | IntegerArgument: drive error code |
| ClearDriveFault (4) | - |
| SendMachineStarted (5) | TextArgument: operator name |
| SendBatchCompleted (6) | IntegerArgument: batch number, SecondIntegerArgument: part count |

`Commands.OvenTemperature` is written directly by the HMI slider.

Confirming alarms is done by the HMI through the EventLogger itself, not through the PLC.

## Things to know

- Arguments are captured when an alarm is raised or a message is sent, so `NewArguments()` is
  called immediately before `Raise()` or `Send()`.
- Clearing an alarm does not confirm it. A cleared, unconfirmed alarm stays in the active list
  until the operator confirms it. `Clear(ResetConfirmation := TRUE)` would discard the pending
  confirmation instead.
- Alarms raised when the PLC stops are cleared on restart; pending confirmations are not.
- The source of each event is set with `FB_TcSourceInfo.sName`, so the event text does not repeat it.

## Libraries

| Library | Version | Used for |
|---|---|---|
| Tc3_EventLogger | * (latest) | FB_TcAlarm, FB_TcMessage, FB_TcSourceInfo, I_TcArguments, TcEventEntry |
| Tc3_Module | * (latest) | Dependency of Tc3_EventLogger |
| Tc2_System | * (latest) | HRESULT and system types |
| Tc2_Standard | * (latest) | Standard IEC blocks, CONCAT, MID, REPLACE |
| Tc2_Utilities | * (latest) | GUID_TO_STRING, F_ToLCase, FILETIME_TO_SYSTEMTIME, SYSTEMTIME_TO_STRING |
| Tc3_IotBase | * (latest) | FB_IotMqttClient (TF6701) |
| Tc3_JsonXml | * (latest) | FB_JsonSaxWriter |

Developed against TwinCAT 3.1 build 4024 (Tc3_EventLogger 3.1.27 or later) and run on build 4026. The EventLogger and ADS
are part of the standard TwinCAT 3 runtime; confirm against your licence set if the target has a
restricted one. MQTT publishing needs a **TF6701** licence on the target (a 7-day trial is enough
for the demo).
