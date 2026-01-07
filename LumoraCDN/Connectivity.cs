// tracks if we have internet or if everything is fucked

using System;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace Lumora.CDN;

public static class Connectivity
{
    private static volatile bool _isOnline = true;
    private static volatile bool _isChecking;
    private static DateTime _lastCheck = DateTime.MinValue;
    private static DateTime _lastFailure = DateTime.MinValue;
    private static int _consecutiveFailures;

    public static TimeSpan CheckInterval { get; set; } = TimeSpan.FromSeconds(5);
    public static TimeSpan BackoffDuration { get; set; } = TimeSpan.FromSeconds(30);
    public static int FailureThreshold { get; set; } = 3; // 3 strikes and youre out
    public static string PingUrl { get; set; } = "https://api.lumoravr.com/health";

    public static bool IsOnline
    {
        get
        {
            // in backoff? stay offline
            if (_consecutiveFailures >= FailureThreshold)
            {
                if (DateTime.UtcNow - _lastFailure < BackoffDuration)
                    return false;
                _consecutiveFailures = 0; // backoff done, try again
            }
            return _isOnline;
        }
    }

    public static event Action<bool>? StatusChanged;

    // call this when shit works
    public static void ReportSuccess()
    {
        _consecutiveFailures = 0;
        if (!_isOnline)
        {
            _isOnline = true;
            StatusChanged?.Invoke(true);
        }
    }

    // call this when shit breaks
    public static void ReportFailure()
    {
        _lastFailure = DateTime.UtcNow;
        var failures = Interlocked.Increment(ref _consecutiveFailures);

        if (failures >= FailureThreshold && _isOnline)
        {
            _isOnline = false;
            StatusChanged?.Invoke(false);
            _ = CheckConnectivityAsync(); // start checking in background
        }
    }

    public static async Task<bool> CheckAsync() => await CheckConnectivityAsync(force: true);

    public static bool CheckSystemNetwork()
    {
        try
        {
            return NetworkInterface.GetIsNetworkAvailable();
        }
        catch
        {
            return true; // assume online if check fails
        }
    }

    private static async Task<bool> CheckConnectivityAsync(bool force = false)
    {
        if (_isChecking && !force)
            return _isOnline;

        if (!force && DateTime.UtcNow - _lastCheck < CheckInterval)
            return _isOnline;

        _isChecking = true;
        _lastCheck = DateTime.UtcNow;

        try
        {
            if (!CheckSystemNetwork())
            {
                SetOffline();
                return false;
            }

            // ping our server
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.GetAsync(PingUrl);

            if (response.IsSuccessStatusCode)
            {
                SetOnline();
                return true;
            }

            SetOffline();
            return false;
        }
        catch
        {
            SetOffline();
            return false;
        }
        finally
        {
            _isChecking = false;
        }
    }

    private static void SetOnline()
    {
        _consecutiveFailures = 0;
        if (!_isOnline)
        {
            _isOnline = true;
            StatusChanged?.Invoke(true);
        }
    }

    private static void SetOffline()
    {
        _lastFailure = DateTime.UtcNow;
        if (_isOnline)
        {
            _isOnline = false;
            StatusChanged?.Invoke(false);
        }
    }

    // start background monitoring
    public static void StartMonitoring(TimeSpan interval)
    {
        _ = MonitorLoop(interval);
    }

    private static async Task MonitorLoop(TimeSpan interval)
    {
        while (true)
        {
            await Task.Delay(interval);
            if (!_isOnline)
                await CheckConnectivityAsync(force: true);
        }
    }
}
