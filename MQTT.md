# MQTT publishing

On the `MQTT` branch the PLC publishes its alarm table (`GVL_Hmi.PlcAlarms`, kept by `AlarmListener`)
to an MQTT broker, and the web HMI gains a **Broker view** that shows what arrived there.

The demo uses Mosquitto on the local PC, set up to behave like **AWS IoT Core**: TLS with client
certificates, the certificate's name as the client's identity, and per-device topic rules. The code
uses only what AWS IoT Core supports, so moving to AWS is a configuration change. See
[Moving to AWS IoT Core](#moving-to-aws-iot-core).

```
PLC: AlarmListener -> GVL_Hmi.PlcAlarms -> MqttPublisher (TF6701) --TLS 8883--> Mosquitto --TLS 8883--> HMI: BrokerClient -> Broker view
```

## What you need

- Everything the main demo needs (see `README.md`).
- **TF6701 TwinCAT 3 IoT Communication.** On TwinCAT 3.1.4026, install
  `TF6701.IotCommunication.XAE` with the Package Manager on the engineering PC (it brings the
  Tc3_IotBase library); the MQTT driver is part of the runtime's Standard Workload on the target. The
  licence is needed on the target; activating the configuration generates a 7-day trial.
- **Mosquitto 2.x** for Windows, from <https://mosquitto.org/download/>, installed to the default folder.

## Contents

| Path | What it is |
|---|---|
| `Broker/mosquitto.conf` | Broker configuration: TLS on 8883 with client certificates, plus a diagnostic listener (commented out) |
| `Broker/acl.conf`, `Broker/acl-diagnostic.conf` | Topic rules per identity, like AWS IoT policies |
| `Broker/run-broker.cmd` | Starts Mosquitto with this configuration |
| `Tools/CertTool/` | Creates the demo CA and the three certificates in `Broker/certs` |
| `TwinCAT/EventLoggerDemoPlc/POUs/Mqtt/` | `MqttPublisher`, `AlarmJsonWriter`, `F_AlarmKey`, `F_FileTimeToIso` |
| `TwinCAT/EventLoggerDemoPlc/GVLs/GVL_Mqtt.TcGVL` | Publish mode, heartbeat, enable, status |
| `TwinCAT/EventLoggerDemoPlc/GVLs/GVL_MqttSite.TcGVL` | This PC's broker connection: host, port, certificate files |
| `Hmi/BrokerClient.cs` | The consumer (MQTTnet) behind the Broker view |

## Setup

The demo runs on one PC: TwinCAT runtime, Mosquitto, the HMI and CertTool together. That is what the
defaults in `GVL_MqttSite` and `appsettings.json` are for.

A PLC on a separate target also works. On the target, `localhost` and `127.0.0.1` mean the target
itself, so the PLC must be given the broker PC's name or address, the certificate files must be on the
target, and the broker PC must accept connections on port 8883. Each step below says what changes.

### 1. Certificates

From the repository folder:

```bat
dotnet run --project Tools\CertTool
```

This writes `ca.crt`, `broker.crt` and two client certificates, each with its `.key`, to
`Broker\certs`. The broker certificate covers `localhost`, the PC's name, the loopback addresses and
the PC's current IPv4 addresses, so the PLC can connect by name or by address. Add other names with
`--host <name-or-address>` (repeatable). Running it again keeps the CA and re-issues the others;
`--force` replaces the CA too. If the PC's address changes, run it again and restart the broker. The
`.key` files are private keys and are kept out of Git.

On one PC the PLC reads its three files (`ca.crt`, `eventloggerdemo-01.crt`, `eventloggerdemo-01.key`)
straight from `Broker\certs`: `GVL_MqttSite` points at
`C:\repo\Demo-EventLogger-with-HMI\Broker\certs\`. If the repository is elsewhere, change the three paths.

**Separate target:** the TwinCAT MQTT driver reads the files on the machine the PLC runs on. Copy the
three files to a folder on the target, for example `C:\EventLoggerDemo\certs\`, and point the three paths
there.

| Identity (certificate CN) | Used by |
|---|---|
| `eventloggerdemo-01` | The PLC. It is also the MQTT client ID and the AWS Thing Name. |
| `eventloggerdemo-hmi` | The HMI's Broker view |

### 2. Broker

Double-click `Broker\run-broker.cmd`, or run `.\Broker\run-broker.cmd` from the repository folder.
Mosquitto runs in that window until you close it or press Ctrl+C. It is ready when the window shows:

```
Opening ipv6 listen socket on port 8883.
Opening ipv4 listen socket on port 8883.
mosquitto version 2.x running
```

Mosquitto's Windows service, if installed, listens on 1883 and can keep running; it is not used.
Retained messages are kept in `Broker\data` and survive a restart of the broker.

The first time, Windows Firewall asks whether `mosquitto.exe` may accept connections. On one PC the
connections are local and either answer works. **Separate target:** tick **Private networks** only and
press **Allow**. Pressing Cancel adds rules that block it, and the target cannot connect: delete the
`mosquitto.exe` block rules under **Inbound Rules** in `wf.msc` and start the broker again. Every client
still needs a certificate from your demo CA, so allowing the port does not let anyone else in.

### 3. PLC

1. On one PC, `GVL_MqttSite` needs no change, unless the repository is not at
   `C:\repo\Demo-EventLogger-with-HMI` (then change the three paths). `BrokerHost` is `127.0.0.1`.
   **Separate target:** set `BrokerHost` to the broker PC's name or IPv4 address (one of those CertTool
   listed for `broker.crt`), and the three paths to where you copied the files on the target.
2. Build, activate and start the PLC as for the main demo. The project now also references
   Tc3_IotBase, Tc3_JsonXml and Tc2_Utilities. `GVL_MqttSite` is read at PLC start: after changing it,
   use Reset Cold or download again.
3. In an online view, `GVL_Mqtt.Status.Connected` turns TRUE within a few seconds, and the broker window
   logs `New client connected ... as eventloggerdemo-01`.

### 4. HMI

Start the HMI as before (`Hmi\run.cmd`). The **Broker view** panel shows the broker connection, the
PLC's online state, the publish mode, the heartbeat age and the alarms as received from the broker.
**Recent messages** lists the last 15 messages with their topics.

## Try it

| Do this | You should see in the Broker view |
|---|---|
| Raise the air-supply alarm | It appears within about a second, with its text |
| Confirm it on the HMI | Confirmed turns to Yes |
| Clear it | It leaves the list. Raised alarms only: a cleared alarm waiting for confirmation is not published. |
| Wait 30 s | The heartbeat age resets |
| Stop the PLC | PLC turns offline (the broker's Last Will) |
| Set `GVL_Mqtt.PublishMode := E_MqttPublishMode.PerAlarm` and restart the PLC | Mode shows `per-alarm`; Recent messages shows one message per alarm plus the index |

`PublishMode` is read when the PLC starts. Changing it online sets `GVL_Mqtt.Status.ModeChangePending`
until the next start. `GVL_Mqtt.Enable := FALSE` publishes `offline` and disconnects.

## Topics

| Topic | Retained | Content |
|---|---|---|
| `dt/eventloggerdemo/<thing>/status` | Yes | `online` or `offline` |
| `dt/eventloggerdemo/<thing>/heartbeat` | No | Every 30 s: alarm count |
| `dt/eventloggerdemo/<thing>/alarms` | Yes | Table mode: every alarm in one document. Emptied in per-alarm mode. |
| `dt/eventloggerdemo/<thing>/alarms/index` | Yes | Per-alarm mode: the keys of the current alarms. Emptied in table mode. |
| `dt/eventloggerdemo/<thing>/alarms/<key>` | Yes | Per-alarm mode: one alarm; an empty retained message deletes it |

`<key>` is the event class GUID, event ID and source name in lowercase with dashes, for example
`3f2a9c1e5b7d4e8a9c217d4e5f6a8b90-2-air-supply`. All messages use QoS 1. Payloads are UTF-8 JSON
without a terminating null byte.

## Payloads

Every message starts with the same fields:

| Field | Meaning |
|---|---|
| `thing` | The PLC's Thing Name |
| `session` | When the publisher started (ISO 8601). A later session means the PLC restarted and sequences start again. |
| `sequence` | Rises with every message in a session. Consumers ignore a message older than the last one on its topic. |
| `timestamp` | When the message was built, ISO 8601 UTC |
| `mode` | `table` or `per-alarm` |
| `reason` | `startup`, `reconnect`, `change`, `text` (a text arrived after the alarm was published) or `heartbeat` |

Table mode, `.../alarms`:

```json
{
  "thing": "eventloggerdemo-01", "session": "2026-10-23T12:00:01.250Z", "sequence": 42,
  "timestamp": "2026-10-23T12:05:47.351Z", "mode": "table", "reason": "change",
  "count": 1, "overflow": false,
  "alarms": [
    {
      "key": "3f2a9c1e5b7d4e8a9c217d4e5f6a8b90-2-air-supply",
      "eventClass": "3f2a9c1e-5b7d-4e8a-9c21-7d4e5f6a8b90",
      "eventId": 2, "severity": "Warning", "severityLevel": 2, "source": "Air supply",
      "raised": "2026-10-23T12:05:37.371Z", "raisedFileTime": "134372258173710000",
      "confirmed": false, "textAvailable": true, "text": "Supply air pressure is low (4200 mbar)"
    }
  ]
}
```

Per-alarm mode: each `.../alarms/<key>` carries the common fields plus one alarm's fields, and
`.../alarms/index` carries `count` and `keys`. `raisedFileTime` is a string because the value is too
large for a JavaScript number.

When an alarm changes, the PLC waits up to 1 s (`GVL_Mqtt.TextWaitTime`) for the texts of new alarms
before publishing. A text arriving later is published on its own with reason `text`. In per-alarm
mode one message is sent per PLC cycle.

## AWS IoT Core compatibility

| AWS IoT Core | How the demo matches it |
|---|---|
| TLS 1.2 on 8883 with X.509 client certificates | Mosquitto requires a client certificate; same three PEM files |
| Thing Name = certificate = client ID | Certificate CN is the identity (`use_identity_as_username`) and the PLC's client ID |
| IoT policy per device | ACL: each identity publishes only under its own name; the HMI only reads |
| QoS 0 and 1 only | Everything is QoS 1, including the Last Will (TF6701's default for the Will is QoS 2, so it is set explicitly) |
| Retained messages are not delivered to wildcard subscriptions | The consumer subscribes only to exact topics, and to each alarm topic listed in the index |
| No Last Will on an orderly disconnect | The PLC publishes `offline` itself before disconnecting deliberately |
| No guaranteed order; duplicates possible | `session` and `sequence` in every message |
| Topics: 7 slashes, 256 bytes at most | At most 6 slashes and about 220 bytes |
| Messages up to 128 KB | The full 32-row table is about 20 KB at most |

## Moving to AWS IoT Core

These steps follow AWS's documented examples but have not yet been tried against AWS with this sample.

1. In the AWS IoT console, create two Things: `eventloggerdemo-01` and `eventloggerdemo-hmi`. For each,
   create a certificate, download the certificate and private key, and attach it to the Thing.
   Download Amazon's root CA (`AmazonRootCA1.pem`).
2. Create the two policies below (replace `REGION` and `ACCOUNT`), and attach each to its Thing's
   certificate.
3. Find the account's endpoint: **Settings > Device data endpoint**, of the form
   `xxxxxxxx-ats.iot.<region>.amazonaws.com`.
4. PLC: in `GVL_MqttSite` set `BrokerHost` to the endpoint and the three file paths to Amazon's root
   CA and the PLC's certificate and key. The port stays 8883 and `ThingName` stays `eventloggerdemo-01`.
5. HMI: in `appsettings.json`, under `Broker`, set `Host` and the three files the same way, with the
   HMI's certificate and key.

PLC policy: connect with a Last Will, publish (and retain) only under its own name.

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": "iot:Connect",
      "Resource": "arn:aws:iot:REGION:ACCOUNT:client/${iot:Connection.Thing.ThingName}",
      "Condition": { "ForAllValues:StringEquals": { "iot:ConnectAttributes": ["LastWill"] } }
    },
    {
      "Effect": "Allow",
      "Action": ["iot:Publish", "iot:RetainPublish"],
      "Resource": "arn:aws:iot:REGION:ACCOUNT:topic/dt/eventloggerdemo/${iot:Connection.Thing.ThingName}/*"
    }
  ]
}
```

HMI policy: connect, subscribe and receive the demo topics only.

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": "iot:Connect",
      "Resource": "arn:aws:iot:REGION:ACCOUNT:client/${iot:Connection.Thing.ThingName}"
    },
    {
      "Effect": "Allow",
      "Action": "iot:Subscribe",
      "Resource": "arn:aws:iot:REGION:ACCOUNT:topicfilter/dt/eventloggerdemo/*"
    },
    {
      "Effect": "Allow",
      "Action": "iot:Receive",
      "Resource": "arn:aws:iot:REGION:ACCOUNT:topic/dt/eventloggerdemo/*"
    }
  ]
}
```

`iot:RetainPublish` is needed in addition to `iot:Publish` for retained messages, and a retained Last
Will needs the `LastWill` connect condition. `${iot:Connection.Thing.ThingName}` resolves to the Thing
the certificate is attached to, which is why the client ID must equal the Thing Name.

## Diagnostic listener

To find out whether a connection problem is caused by the certificates, use plain MQTT. The diagnostic
listener is bound to 127.0.0.1, so this test only works with the PLC on the same PC as the broker:

1. In `Broker\mosquitto.conf`, remove the `#` in front of the four diagnostic lines and restart the
   broker. If Mosquitto's Windows service is running, stop it first (`net stop mosquitto`): it also
   uses port 1883.
2. PLC: `GVL_MqttSite.UseTls := FALSE`, `BrokerPort := 1883`, and restart the PLC.
3. HMI: `"UseTls": false` and `"Port": 1883` under `Broker`.

If it works on 1883 but not on 8883, the certificates or their paths are the cause. Put everything
back afterwards: the diagnostic listener has no identities, so the topic rules cannot apply, and AWS
offers no equivalent.

## Troubleshooting

| Symptom | Look at | Likely cause |
|---|---|---|
| Broker window: `Only one usage of each socket address` | Diagnostic listener | Mosquitto's Windows service already uses 1883: `Stop-Service mosquitto` (administrator) |
| `GVL_Mqtt.Status.Connected` stays FALSE | `Status.ConnectionState` | See the next rows |
| `MQTT_ERR_CONN` and nothing at all in the broker window | `GVL_MqttSite.BrokerHost`, firewall | PLC on a separate target with `BrokerHost` still `localhost` or `127.0.0.1` (that is the target itself); or Windows Firewall on the broker PC blocking `mosquitto.exe` |
| `MQTT_ERR_TLS_CA_NOTFOUND`, `_CERT_NOTFOUND`, `_KEY_NOTFOUND` | `GVL_MqttSite` file paths | Files not copied to the PLC's machine, or paths wrong |
| `MQTT_ERR_TLS_CN_MISMATCH` | `GVL_MqttSite.BrokerHost` | Not one of the names or addresses CertTool listed for `broker.crt`: use one of those, or run CertTool with `--host` and restart the broker |
| `MQTT_ERR_TLS_VERIFY_FAIL`, `MQTT_ERR_TLS_HANDSHAKE_FAIL` | Certificates | CA re-created with `--force` but the broker not restarted, or files from different runs |
| `MQTT_ERR_CONN_REFUSED`, `MQTT_ERR_CONN_TIMEDOUT` | Broker window | Broker not running, or wrong port |
| PLC connects but Broker view shows nothing | Broker window | `ThingName` differs from the certificate's CN, so the ACL refuses the messages (`Denied PUBLISH` in the log) |
| Broker view: "not connected" | The message after it | HMI certificate paths, or the broker not running |
| Alarm in the HMI's active list but not in the Broker view | - | Expected for a cleared alarm waiting for confirmation: only raised alarms are published |
| `Status.PublishFailures` rising | `Status.ErrorCode` | Broker refusing messages, or a document too large |

## Licences

| Component | Licence |
|---|---|
| TF6701 TwinCAT 3 IoT Communication | Beckhoff licence on the PLC PC (7-day trial available) |
| Tc3_IotBase, Tc3_JsonXml, Tc2_Utilities | Beckhoff PLC libraries, installed with TwinCAT |
| MQTTnet 4.3.7 | MIT |
| Mosquitto | EPL 2.0 / EDL 1.0; installed separately, not part of this repository |
