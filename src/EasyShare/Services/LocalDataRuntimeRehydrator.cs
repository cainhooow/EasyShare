namespace EasyShare.Services;

/// <summary>
/// Rehydrates only the empty runtime infrastructure required to keep the
/// current process usable after a successful full local-data reset.
/// User data, encryption keys, payloads, caches, and browser profiles remain
/// absent until a later, explicit feature operation needs them.
/// </summary>
public sealed class LocalDataRuntimeRehydrator
{
    private readonly LocalDataResetService _resetService;
    private readonly ContentIndexService _contentIndex;

    public LocalDataRuntimeRehydrator(
        LocalDataResetService resetService,
        ContentIndexService contentIndex)
    {
        _resetService = resetService ?? throw new ArgumentNullException(nameof(resetService));
        _contentIndex = contentIndex ?? throw new ArgumentNullException(nameof(contentIndex));
    }

    public Task RehydrateAsync(CancellationToken cancellationToken = default) =>
        RehydrateAsync(additionalServiceRehydration: null, cancellationToken);

    public async Task RehydrateAsync(
        Func<CancellationToken, Task>? additionalServiceRehydration,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _contentIndex
                .RehydrateAfterLocalDataResetAsync(cancellationToken)
                .ConfigureAwait(false);

            if (additionalServiceRehydration is not null)
            {
                await additionalServiceRehydration(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception rehydrationFailure)
        {
            try
            {
                _resetService.MarkPendingReset();
            }
            catch (Exception markerFailure)
            {
                throw new AggregateException(
                    "Local runtime rehydration failed and the pending-reset marker could not be restored.",
                    rehydrationFailure,
                    markerFailure);
            }

            throw;
        }
    }
}
