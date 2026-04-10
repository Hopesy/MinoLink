using MinoLink.Core.Models;

namespace MinoLink.Core.Interfaces;

public interface IAppUpdateService
{
    Task<AppUpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default);
}
