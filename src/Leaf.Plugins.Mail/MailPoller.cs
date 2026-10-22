using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.Mail;

/// <summary>
/// Background sync loop, started only from <see cref="MailPlugin.OnStartupAsync"/> (which
/// the host runs only for an enabled plugin). It does nothing until an account is
/// connected, polls every two minutes, backs off exponentially on Gmail 429/5xx or
/// network failures, and stops polling once the account needs re-authorization; a
/// successful reconnect wakes it.
/// </summary>
public sealed class MailPoller(MailService service, ILogger<MailPoller>? logger = null)
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);
    private readonly SemaphoreSlim _wake = new(0, 1);

    public void Wake()
    {
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            var delay = Interval;
            try
            {
                var result = await service.SyncOnceAsync(ct);
                failures = 0;
                if (result is SyncResult.NeedsReauth or SyncResult.Idle)
                    delay = Timeout.InfiniteTimeSpan; // dormant until a connect wakes the loop
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (GmailApiException ex)
            {
                failures++;
                delay = NextDelay(failures);
                logger?.LogWarning("Mail sync failed ({Status}); retrying in {Delay}", (int)ex.Status, delay);
                await SafeRecordAsync(ex.Transient
                    ? $"Gmail is unavailable or rate limited ({(int)ex.Status}); retrying with backoff."
                    : ex.Message, ct);
            }
            catch (MailOAuthException ex)
            {
                failures++;
                delay = NextDelay(failures);
                logger?.LogWarning("Mail token refresh failed ({Code}); retrying in {Delay}", ex.Code, delay);
                await SafeRecordAsync(ex.Message, ct);
            }
            catch (Exception ex)
            {
                failures++;
                delay = NextDelay(failures);
                logger?.LogError("Mail sync failed unexpectedly ({Type}); retrying in {Delay}", ex.GetType().Name, delay);
                await SafeRecordAsync("Mail sync failed unexpectedly; retrying.", ct);
            }

            try { await _wake.WaitAsync(delay, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    internal static TimeSpan NextDelay(int failures)
    {
        var seconds = Interval.TotalSeconds * Math.Pow(2, Math.Min(failures, 10));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxBackoff.TotalSeconds));
    }

    private async Task SafeRecordAsync(string message, CancellationToken ct)
    {
        try { await service.RecordErrorAsync(message, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
    }
}
