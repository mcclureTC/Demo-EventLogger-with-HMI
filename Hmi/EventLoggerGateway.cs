using Microsoft.Extensions.Options;
using TcEventLoggerAdsProxyLib;

namespace EventLoggerDemo.Hmi;

/// <summary>
/// Talks to the TwinCAT EventLogger directly: active alarms, the logged-event history and
/// confirmation. Texts come back already formatted with their arguments, in the configured language.
/// </summary>
public sealed class EventLoggerGateway : IDisposable
{
    private readonly object _sync = new();
    private readonly PlcOptions _options;
    private readonly ILogger<EventLoggerGateway> _log;
    private TcEventLogger? _logger;

    public EventLoggerGateway(IOptions<PlcOptions> options, ILogger<EventLoggerGateway> log)
    {
        _options = options.Value;
        _log = log;
    }

    public IReadOnlyList<ActiveAlarm> GetActiveAlarms() => Use(logger =>
    {
        var result = new List<ActiveAlarm>();
        foreach (TcAlarm alarm in logger.ActiveAlarms)
        {
            result.Add(new ActiveAlarm(
                Key: new AlarmKey(alarm.EventClass, alarm.EventId, alarm.SourceName),
                Text: alarm.GetText(_options.LanguageId),
                Severity: alarm.SeverityLevel.ToString(),
                Source: alarm.SourceName,
                RaisedUtc: FromFileTime(alarm.FileTimeRaised),
                ClearedUtc: FromFileTime(alarm.FileTimeCleared),
                IsRaised: alarm.IsRaised,
                ConfirmationState: alarm.ConfirmationState.ToString()));
        }
        return result.OrderByDescending(a => a.RaisedUtc).ToList();
    });

    public IReadOnlyList<LoggedEvent> GetLoggedEvents() => Use(logger =>
    {
        var result = new List<LoggedEvent>();
        foreach (TcLoggedEvent logged in logger.GetLoggedEvents(_options.LoggedEventCount))
        {
            result.Add(new LoggedEvent(
                Type: logged.EventType.ToString(),
                EventId: logged.EventId,
                Text: logged.GetText(_options.LanguageId),
                Severity: logged.SeverityLevel.ToString(),
                Source: logged.SourceName,
                RaisedUtc: FromFileTime(logged.FileTimeRaised),
                ClearedUtc: FromFileTime(logged.FileTimeCleared),
                ConfirmedUtc: FromFileTime(logged.FileTimeConfirmed)));
        }
        return result.OrderByDescending(e => e.RaisedUtc).ToList();
    });

    /// <summary>Confirms one active alarm. Returns false if it is no longer in the list.</summary>
    public bool Confirm(AlarmKey key) => Use(logger =>
    {
        foreach (TcAlarm alarm in logger.ActiveAlarms)
        {
            if (alarm.EventClass == key.EventClass
                && alarm.EventId == key.EventId
                && alarm.SourceName == key.Source)
            {
                alarm.Confirm();
                return true;
            }
        }
        return false;
    });

    public void ConfirmAll() => Use(logger =>
    {
        logger.ConfirmAllAlarms();
        return true;
    });

    public bool IsConnected
    {
        get { lock (_sync) return _logger is { IsConnected: true }; }
    }

    private T Use<T>(Func<TcEventLogger, T> action)
    {
        lock (_sync)
        {
            try
            {
                return action(Connected());
            }
            catch (Exception ex)
            {
                _log.LogWarning("EventLogger access failed: {Message}", ex.Message);
                Drop();
                throw;
            }
        }
    }

    private TcEventLogger Connected()
    {
        if (_logger is { IsConnected: true })
            return _logger;

        Drop();
        var logger = new TcEventLogger();
        if (_options.IsLocal)
            logger.Connect();
        else
            logger.Connect(_options.AmsNetId);
        _logger = logger;
        return logger;
    }

    private void Drop()
    {
        if (_logger is null)
            return;
        try { _logger.Disconnect(); } catch { /* already gone */ }
        _logger = null;
    }

    /// <summary>The EventLogger reports times as FILETIME; zero means "not yet".</summary>
    private static DateTime? FromFileTime(long fileTime) =>
        fileTime > 0 ? DateTime.FromFileTimeUtc(fileTime) : null;

    public void Dispose()
    {
        lock (_sync) Drop();
    }
}
