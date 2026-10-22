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
| `MAIN` | Calls `DemoMachine.CyclicLogic` with the HMI contract. |

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
| Tc2_Standard | * (latest) | Standard IEC blocks |

Developed against TwinCAT 3.1 build 4024 (Tc3_EventLogger 3.1.27 or later). The EventLogger and ADS
are part of the standard TwinCAT 3 runtime; confirm against your licence set if the target has a
restricted one.
