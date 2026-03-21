using System.Text.Json;
using System.Text.Json.Nodes;
using MinoLink.Core.Interfaces;
using MinoLink.Core.Models;

namespace MinoLink.Services;

public sealed class ConfigService : IConfigService
{
    private readonly string _configPath;
    private readonly object _lock = new();
    private MinoLinkConfig _cached;

    public ConfigService(string configPath, MinoLinkConfig initial)
    {
        _configPath = configPath;
        _cached = initial;
    }

    public MinoLinkConfig GetConfig()
    {
        lock (_lock)
            return _cached;
    }

    public void UpdateConfig(Action<MinoLinkConfig> update)
    {
        lock (_lock)
        {
            update(_cached);
            SaveToFile();
        }
    }

    private void SaveToFile()
    {
        var json = File.Exists(_configPath)
            ? JsonNode.Parse(File.ReadAllText(_configPath)) as JsonObject ?? new JsonObject()
            : new JsonObject();

        var minoLink = new JsonObject
        {
            ["ProjectName"] = _cached.ProjectName,
            ["Agent"] = new JsonObject
            {
                ["Type"] = _cached.Agent.Type,
                ["WorkDir"] = _cached.Agent.WorkDir,
                ["Model"] = _cached.Agent.Model,
                ["Mode"] = _cached.Agent.Mode,
            },
        };

        if (_cached.Feishu is { } feishu)
        {
            minoLink["Feishu"] = new JsonObject
            {
                ["AppId"] = feishu.AppId,
                ["AppSecret"] = feishu.AppSecret,
                ["VerificationToken"] = feishu.VerificationToken,
                ["AllowFrom"] = feishu.AllowFrom,
            };
        }

        json["MinoLink"] = minoLink;

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(_configPath, json.ToJsonString(options));
    }
}
