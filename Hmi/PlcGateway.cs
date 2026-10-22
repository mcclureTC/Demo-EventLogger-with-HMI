using Microsoft.Extensions.Options;
using TwinCAT.Ads;

namespace EventLoggerDemo.Hmi;

/// <summary>
/// Reads and writes the PLC's GVL_Hmi over ADS. One request at a time; reconnects after a failure.
/// </summary>
public sealed class PlcGateway : IDisposable
{
    private const string Commands = "GVL_Hmi.Commands.";
    private const string Status = "GVL_Hmi.Status.";
    private const int TextArgumentLength = 80; // STRING in ST_HmiCommands

    private readonly PlcOptions _options;
    private readonly ILogger<PlcGateway> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AdsClient? _client;

    public PlcGateway(IOptions<PlcOptions> options, ILogger<PlcGateway> log)
    {
        _options = options.Value;
        _log = log;
    }

    public async Task<PlcStatus> ReadStatusAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var client = Connected();
            return new PlcStatus(
                Connected: true,
                Error: null,
                Heartbeat: Read<uint>(client, Status + "Heartbeat"),
                IsInitialised: Read<bool>(client, Status + "IsInitialised"),
                RequestCount: Read<uint>(client, Status + "RequestCount"),
                OvenTemperature: Read<double>(client, Status + "OvenTemperature"),
                OvenTemperatureLimit: Read<double>(client, Status + "OvenTemperatureLimit"),
                OverTemperatureRaised: Read<bool>(client, Status + "OverTemperatureRaised"),
                LowAirPressureRaised: Read<bool>(client, Status + "LowAirPressureRaised"),
                DriveFaultRaised: Read<bool>(client, Status + "DriveFaultRaised"));
        }
        catch (Exception ex)
        {
            Drop(ex);
            return PlcStatus.Disconnected(ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Writes the arguments, then the request, and waits for the PLC to execute it.
    /// The PLC increments Status.RequestCount once per executed request.
    /// </summary>
    public async Task<RequestResult> SendRequestAsync(RequestBody body)
    {
        if (body.Request == HmiRequest.NotSet)
            return new RequestResult(false, "No request given.");

        await _gate.WaitAsync();
        try
        {
            var client = Connected();
            var countBefore = Read<uint>(client, Status + "RequestCount");

            var text = body.TextArgument ?? string.Empty;
            if (text.Length > TextArgumentLength)
                text = text[..TextArgumentLength];

            client.WriteValue(Commands + "IntegerArgument", body.IntegerArgument);
            client.WriteValue(Commands + "SecondIntegerArgument", body.SecondIntegerArgument);
            client.WriteValue(Commands + "TextArgument", text);
            client.WriteValue(Commands + "Request", (ushort)body.Request); // last: this starts the request

            var deadline = DateTime.UtcNow.AddMilliseconds(_options.RequestTimeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
                if (Read<uint>(client, Status + "RequestCount") == countBefore)
                    continue;

                var accepted = Read<bool>(client, Status + "LastRequestAccepted");
                return new RequestResult(accepted, accepted
                    ? $"{body.Request} done."
                    : $"{body.Request} was rejected by the EventLogger.");
            }

            return new RequestResult(false,
                "The PLC did not pick up the request. Is it running, and has it created its events?");
        }
        catch (Exception ex)
        {
            Drop(ex);
            return new RequestResult(false, ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RequestResult> SetOvenTemperatureAsync(double value)
    {
        await _gate.WaitAsync();
        try
        {
            Connected().WriteValue(Commands + "OvenTemperature", value);
            return new RequestResult(true, $"Oven temperature set to {value:0} degC.");
        }
        catch (Exception ex)
        {
            Drop(ex);
            return new RequestResult(false, ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private AdsClient Connected()
    {
        if (_client is { IsConnected: true })
            return _client;

        _client?.Dispose();
        var client = new AdsClient();
        var netId = _options.IsLocal ? AmsNetId.Local : AmsNetId.Parse(_options.AmsNetId);
        client.Connect(netId, _options.Port);
        _client = client;
        return client;
    }

    private static T Read<T>(AdsClient client, string symbol) =>
        (T)client.ReadValue(symbol, typeof(T));

    private void Drop(Exception ex)
    {
        _log.LogWarning("ADS access failed: {Message}", ex.Message);
        _client?.Dispose();
        _client = null;
    }

    public void Dispose()
    {
        _client?.Dispose();
        _gate.Dispose();
    }
}
