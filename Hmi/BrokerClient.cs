using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace EventLoggerDemo.Hmi;

/// <summary>
/// Subscribes to the PLC's alarm topics on the MQTT broker and keeps what they say.
///
/// Written for AWS IoT Core as much as for the demo Mosquitto:
///   - every subscription is to an exact topic, because AWS does not deliver retained messages
///     to wildcard subscriptions; per-alarm topics are subscribed one by one from the index;
///   - messages may arrive twice or out of order, so each topic keeps the newest sequence it has
///     seen and older messages are ignored; a newer session (PLC restart) starts over;
///   - subscriptions are changed from the background loop, never inside the message handler.
/// </summary>
public sealed class BrokerClient : BackgroundService
{
    private const int RecentMessageCount = 15;

    private readonly BrokerOptions _options;
    private readonly ILogger<BrokerClient> _log;
    private readonly string _contentRoot;
    private readonly string _prefix;
    private readonly object _sync = new();

    private IMqttClient? _client;
    private X509Certificate2? _ca;
    private X509Certificate2? _clientCertificate;

    // State, guarded by _sync
    private bool _connected;
    private string? _error;
    private string _deviceState = "unknown";
    private string? _session;
    private string? _mode;
    private DateTime? _lastHeartbeatUtc;
    private uint? _lastSequence;
    private List<BrokerAlarm> _tableAlarms = new();
    private readonly Dictionary<string, BrokerAlarm> _perAlarm = new();
    private List<string> _indexKeys = new();
    private readonly HashSet<string> _subscribedKeys = new();
    private readonly Dictionary<string, uint> _sequenceByTopic = new();
    private readonly LinkedList<BrokerMessage> _recent = new();

    public BrokerClient(IOptions<BrokerOptions> options, ILogger<BrokerClient> log, IHostEnvironment environment)
    {
        _options = options.Value;
        _log = log;
        _contentRoot = environment.ContentRootPath;
        _prefix = $"{_options.TopicRoot}/{_options.Thing}/";
    }

    public BrokerView GetView()
    {
        lock (_sync)
        {
            IReadOnlyList<BrokerAlarm> alarms = _mode == "per-alarm"
                ? _indexKeys.Where(_perAlarm.ContainsKey).Select(k => _perAlarm[k]).ToList()
                : _tableAlarms.ToList();

            return new BrokerView(
                Enabled: _options.Enabled,
                Connected: _connected,
                Broker: $"{_options.Host}:{_options.Port}" + (_options.UseTls ? " (TLS)" : " (no TLS)"),
                Error: _error,
                Thing: _options.Thing,
                DeviceState: _deviceState,
                Session: _session,
                Mode: _mode,
                HeartbeatAgeSeconds: _lastHeartbeatUtc is { } t ? (DateTime.UtcNow - t).TotalSeconds : null,
                LastSequence: _lastSequence,
                Alarms: alarms,
                RecentMessages: _recent.ToList());
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
            return;

        var client = new MqttFactory().CreateMqttClient();
        client.ApplicationMessageReceivedAsync += e =>
        {
            HandleMessage(e.ApplicationMessage);
            return Task.CompletedTask;
        };
        client.DisconnectedAsync += e =>
        {
            lock (_sync)
            {
                if (_connected)
                    _log.LogWarning("Broker connection lost: {Reason}", e.Exception?.Message ?? e.Reason.ToString());
                _connected = false;
                _subscribedKeys.Clear();
                if (e.Exception is not null)
                    _error = e.Exception.Message;
            }
            return Task.CompletedTask;
        };
        _client = client;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!client.IsConnected)
                    await ConnectAsync(client, stoppingToken);
                else
                    await ReconcileSubscriptionsAsync(client, stoppingToken);

                await Task.Delay(client.IsConnected ? 250 : 5000, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                lock (_sync)
                    _error = ex.Message;
                _log.LogWarning("Broker: {Message}", ex.Message);
                try { await Task.Delay(5000, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        if (client.IsConnected)
        {
            try { await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), CancellationToken.None); }
            catch { /* shutting down */ }
        }
        client.Dispose();
    }

    private async Task ConnectAsync(IMqttClient client, CancellationToken ct)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(_options.Host, _options.Port)
            .WithClientId(_options.ClientId)
            .WithProtocolVersion(MqttProtocolVersion.V311)
            .WithCleanSession(true)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(60))
            .WithTimeout(TimeSpan.FromSeconds(10));

        if (_options.UseTls)
        {
            LoadCertificates();
            builder.WithTlsOptions(tls => tls
                .UseTls()
                .WithSslProtocols(SslProtocols.Tls12)
                .WithClientCertificates(new[] { _clientCertificate! })
                .WithCertificateValidationHandler(ValidateServerCertificate));
        }

        await client.ConnectAsync(builder.Build(), ct);

        var subscribe = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(_prefix + "status", MqttQualityOfServiceLevel.AtLeastOnce)
            .WithTopicFilter(_prefix + "heartbeat", MqttQualityOfServiceLevel.AtLeastOnce)
            .WithTopicFilter(_prefix + "alarms", MqttQualityOfServiceLevel.AtLeastOnce)
            .WithTopicFilter(_prefix + "alarms/index", MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
        await client.SubscribeAsync(subscribe, ct);

        lock (_sync)
        {
            _connected = true;
            _error = null;
            _subscribedKeys.Clear();
        }
        _log.LogInformation("Broker connected: {Host}:{Port}, watching {Thing}", _options.Host, _options.Port, _options.Thing);
    }

    /// <summary>Subscribes to alarms newly listed in the index and drops those no longer listed.</summary>
    private async Task ReconcileSubscriptionsAsync(IMqttClient client, CancellationToken ct)
    {
        List<string> toAdd, toRemove;
        lock (_sync)
        {
            var wanted = _mode == "per-alarm" ? _indexKeys.ToHashSet() : new HashSet<string>();
            toAdd = wanted.Where(k => !_subscribedKeys.Contains(k)).ToList();
            toRemove = _subscribedKeys.Where(k => !wanted.Contains(k)).ToList();
        }

        if (toAdd.Count > 0)
        {
            var builder = new MqttClientSubscribeOptionsBuilder();
            foreach (var key in toAdd)
                builder.WithTopicFilter(_prefix + "alarms/" + key, MqttQualityOfServiceLevel.AtLeastOnce);
            await client.SubscribeAsync(builder.Build(), ct);
            lock (_sync)
                _subscribedKeys.UnionWith(toAdd);
        }

        if (toRemove.Count > 0)
        {
            var builder = new MqttClientUnsubscribeOptionsBuilder();
            foreach (var key in toRemove)
                builder.WithTopicFilter(_prefix + "alarms/" + key);
            await client.UnsubscribeAsync(builder.Build(), ct);
            lock (_sync)
            {
                _subscribedKeys.ExceptWith(toRemove);
                foreach (var key in toRemove)
                    _perAlarm.Remove(key);
            }
        }
    }

    private void HandleMessage(MqttApplicationMessage message)
    {
        var topic = message.Topic ?? string.Empty;
        if (!topic.StartsWith(_prefix, StringComparison.Ordinal))
            return;

        var suffix = topic[_prefix.Length..];
        var payload = message.PayloadSegment;

        lock (_sync)
        {
            if (payload.Count == 0)
            {
                HandleEmpty(suffix);
                AddRecent(topic, 0, message.Retain, "empty: deletes the retained message");
                return;
            }

            try
            {
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                var session = Str(root, "session");
                var sequence = root.TryGetProperty("sequence", out var s) && s.TryGetUInt32(out var n) ? n : 0u;

                var verdict = Accept(topic, session, sequence);
                if (verdict is not null)
                {
                    AddRecent(topic, payload.Count, message.Retain, verdict);
                    return;
                }

                var summary = Apply(suffix, root);
                if (sequence > 0)
                    _lastSequence = sequence;
                AddRecent(topic, payload.Count, message.Retain, summary);
            }
            catch (JsonException ex)
            {
                AddRecent(topic, payload.Count, message.Retain, "not JSON: " + ex.Message);
            }
        }
    }

    /// <summary>Returns null to accept the message, or why it is ignored.</summary>
    private string? Accept(string topic, string? session, uint sequence)
    {
        // Sessions are ISO 8601 timestamps of the publisher's start, so they sort in time.
        if (session is not null && _session is not null)
        {
            var order = string.CompareOrdinal(session, _session);
            if (order < 0)
                return "ignored: from an earlier PLC session";
            if (order > 0)
                StartNewSession(session);
        }
        else if (session is not null)
        {
            _session = session;
        }

        if (sequence > 0)
        {
            if (_sequenceByTopic.TryGetValue(topic, out var last) && sequence <= last)
                return $"ignored: sequence {sequence} is not newer than {last}";
            _sequenceByTopic[topic] = sequence;
        }
        return null;
    }

    private void StartNewSession(string session)
    {
        _session = session;
        _sequenceByTopic.Clear();
        _tableAlarms = new List<BrokerAlarm>();
        _perAlarm.Clear();
        _indexKeys = new List<string>();
    }

    private string Apply(string suffix, JsonElement root)
    {
        var mode = Str(root, "mode");
        if (mode is not null)
            _mode = mode;

        switch (suffix)
        {
            case "status":
                _deviceState = Str(root, "state") ?? "unknown";
                return "status: " + _deviceState;

            case "heartbeat":
                _lastHeartbeatUtc = DateTime.UtcNow;
                return $"heartbeat: {Num(root, "count")} alarm(s)";

            case "alarms":
                _tableAlarms = root.TryGetProperty("alarms", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Select(ParseAlarm).ToList()
                    : new List<BrokerAlarm>();
                return $"table: {_tableAlarms.Count} alarm(s), {Str(root, "reason")}";

            case "alarms/index":
                _indexKeys = root.TryGetProperty("keys", out var keys) && keys.ValueKind == JsonValueKind.Array
                    ? keys.EnumerateArray().Select(k => k.GetString() ?? string.Empty).Where(k => k.Length > 0).ToList()
                    : new List<string>();
                foreach (var stale in _perAlarm.Keys.Where(k => !_indexKeys.Contains(k)).ToList())
                    _perAlarm.Remove(stale);
                return $"index: {_indexKeys.Count} key(s), {Str(root, "reason")}";

            default:
                if (!suffix.StartsWith("alarms/", StringComparison.Ordinal))
                    return "unknown topic";
                var alarm = ParseAlarm(root);
                _perAlarm[suffix["alarms/".Length..]] = alarm;
                return $"alarm: {alarm.Source}, {alarm.Severity}";
        }
    }

    private void HandleEmpty(string suffix)
    {
        if (suffix == "alarms")
            _tableAlarms = new List<BrokerAlarm>();
        else if (suffix == "alarms/index")
            _indexKeys = new List<string>();
        else if (suffix.StartsWith("alarms/", StringComparison.Ordinal))
            _perAlarm.Remove(suffix["alarms/".Length..]);
    }

    private void AddRecent(string topic, int bytes, bool retained, string summary)
    {
        _recent.AddFirst(new BrokerMessage(DateTime.UtcNow, topic, bytes, retained, summary));
        while (_recent.Count > RecentMessageCount)
            _recent.RemoveLast();
    }

    private static BrokerAlarm ParseAlarm(JsonElement e) => new(
        Key: Str(e, "key") ?? string.Empty,
        EventClass: Str(e, "eventClass") ?? string.Empty,
        EventId: (uint)Num(e, "eventId"),
        Severity: Str(e, "severity") ?? "Unknown",
        SeverityLevel: (int)Num(e, "severityLevel"),
        Source: Str(e, "source") ?? string.Empty,
        Raised: Str(e, "raised"),
        Confirmed: Bool(e, "confirmed"),
        TextAvailable: Bool(e, "textAvailable"),
        Text: Str(e, "text") ?? string.Empty);

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private void LoadCertificates()
    {
        if (_clientCertificate is not null)
            return;

        _ca = LoadCertificateOnly(Resolve(_options.CaFile));

        // Windows TLS cannot use a key loaded straight from PEM, so the pair is round-tripped
        // through PKCS#12 to give it a usable private key.
        using var pem = X509Certificate2.CreateFromPemFile(Resolve(_options.CertFile), Resolve(_options.KeyFile));
        _clientCertificate = new X509Certificate2(pem.Export(X509ContentType.Pkcs12));
    }

    /// <summary>Accepts the broker only if its certificate chains to the configured CA and matches the host name.</summary>
    private bool ValidateServerCertificate(MqttClientCertificateValidationEventArgs args)
    {
        if ((args.SslPolicyErrors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
            return false; // missing certificate or wrong host name

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(_ca!);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        using var server = new X509Certificate2(args.Certificate);
        return chain.Build(server);
    }

    /// <summary>A certificate without a private key, such as the CA (or Amazon's root CA), from a PEM file.</summary>
    private static X509Certificate2 LoadCertificateOnly(string path)
    {
        var pem = File.ReadAllText(path);
        var fields = PemEncoding.Find(pem);
        return new X509Certificate2(Convert.FromBase64String(pem[fields.Base64Data]));
    }

    private string Resolve(string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(_contentRoot, path));
}
