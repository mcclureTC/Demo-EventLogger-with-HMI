namespace EventLoggerDemo.Hmi;

/// <summary>Connection settings, from the "Plc" section of appsettings.json.</summary>
public sealed class PlcOptions
{
    /// <summary>AMS Net ID of the target, or "local" for the TwinCAT system on this PC.</summary>
    public string AmsNetId { get; set; } = "local";

    /// <summary>ADS port of the PLC runtime. 851 is the first PLC.</summary>
    public int Port { get; set; } = 851;

    /// <summary>Language of the event texts, as a Windows LCID. 1033 = English (US).</summary>
    public int LanguageId { get; set; } = 1033;

    /// <summary>How long to wait for the PLC to pick up a request.</summary>
    public int RequestTimeoutMs { get; set; } = 2000;

    /// <summary>How many entries the event history shows.</summary>
    public uint LoggedEventCount { get; set; } = 50;

    public bool IsLocal => string.Equals(AmsNetId, "local", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Mirror of E_HmiRequest in the PLC. The values are the contract.</summary>
public enum HmiRequest : ushort
{
    NotSet = 0,
    RaiseLowAirPressure = 1,
    ClearLowAirPressure = 2,
    RaiseDriveFault = 3,
    ClearDriveFault = 4,
    SendMachineStarted = 5,
    SendBatchCompleted = 6,
}

public sealed record RequestBody(
    HmiRequest Request,
    int IntegerArgument = 0,
    int SecondIntegerArgument = 0,
    string? TextArgument = null);

public sealed record TemperatureBody(double Value);

public sealed record RequestResult(bool Accepted, string Message);

public sealed record PlcStatus(
    bool Connected,
    string? Error,
    uint Heartbeat,
    bool IsInitialised,
    uint RequestCount,
    double OvenTemperature,
    double OvenTemperatureLimit,
    bool OverTemperatureRaised,
    bool LowAirPressureRaised,
    bool DriveFaultRaised)
{
    public static PlcStatus Disconnected(string error) =>
        new(false, error, 0, false, 0, 0, 0, false, false, false);
}

/// <summary>Identifies one active alarm: event class, event ID and source.</summary>
public sealed record AlarmKey(Guid EventClass, uint EventId, string Source);

public sealed record ActiveAlarm(
    AlarmKey Key,
    string Text,
    string Severity,
    string Source,
    DateTime? RaisedUtc,
    DateTime? ClearedUtc,
    bool IsRaised,
    string ConfirmationState);

public sealed record LoggedEvent(
    string Type,
    uint EventId,
    string Text,
    string Severity,
    string Source,
    DateTime? RaisedUtc,
    DateTime? ClearedUtc,
    DateTime? ConfirmedUtc);
