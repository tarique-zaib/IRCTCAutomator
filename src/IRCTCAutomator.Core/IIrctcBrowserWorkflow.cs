using IRCTCAutomator.Models;

namespace IRCTCAutomator.Core;

public interface IIrctcBrowserWorkflow
{
    Task OpenTrainSearchAsync(CancellationToken cancellationToken = default);
    Task PrepareJourneyAsync(BookingConfiguration configuration, CancellationToken cancellationToken = default);
    Task PreparePassengersAsync(BookingConfiguration configuration, CancellationToken cancellationToken = default);
    Task StopAtProtectedStepAsync(CancellationToken cancellationToken = default);
    Task CloseAsync();
}
