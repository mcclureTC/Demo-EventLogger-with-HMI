namespace EventLoggerDemo.Hmi;

/// <summary>MQTT broker connection of the Broker view, from the "Broker" section of appsettings.json.</summary>
public sealed class BrokerOptions
{
    public bool Enabled { get; set; } = true;
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 8883;

    /// <summary>FALSE only with the broker's diagnostic listener on port 1883.</summary>
    public bool UseTls { get; set; } = true;

    /// <summary>PEM files; relative paths are relative to the Hmi folder.</summary>
    public string CaFile { get; set; } = "../Broker/certs/ca.crt";
    public string CertFile { get; set; } = "../Broker/certs/eventloggerdemo-hmi.crt";
    public string KeyFile { get; set; } = "../Broker/certs/eventloggerdemo-hmi.key";

    /// <summary>This consumer's client ID; equals its certificate's common name.</summary>
    public string ClientId { get; set; } = "eventloggerdemo-hmi";

    /// <summary>The PLC whose alarms are shown: its Thing Name.</summary>
    public string Thing { get; set; } = "eventloggerdemo-01";

    public string TopicRoot { get; set; } = "dt/eventloggerdemo";
}

public sealed record BrokerAlarm(
    string Key,
    string EventClass,
    uint EventId,
    string Severity,
    int SeverityLevel,
    string Source,
    string? Raised,
    bool Confirmed,
    bool TextAvailable,
    string Text);

public sealed record BrokerMessage(
    DateTime ReceivedUtc,
    string Topic,
    int Bytes,
    bool Retained,
    string Summary);

/// <summary>What the broker currently says about the PLC.</summary>
public sealed record BrokerView(
    bool Enabled,
    bool Connected,
    string Broker,
    string? Error,
    string Thing,
    string DeviceState,
    string? Session,
    string? Mode,
    double? HeartbeatAgeSeconds,
    uint? LastSequence,
    IReadOnlyList<BrokerAlarm> Alarms,
    IReadOnlyList<BrokerMessage> RecentMessages);
