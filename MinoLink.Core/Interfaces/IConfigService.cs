using MinoLink.Core.Models;

namespace MinoLink.Core.Interfaces;

public interface IConfigService
{
    MinoLinkConfig GetConfig();
    void UpdateConfig(Action<MinoLinkConfig> update);
}
