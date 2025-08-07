using Microsoft.Extensions.Caching.Memory;

namespace ReverseProxyPerUser.Services;

public class StartupCheckService
{
    private readonly KubernetesStartupService _k8s;
    private readonly IMemoryCache _cache;

    public StartupCheckService(KubernetesStartupService k8s, IMemoryCache cache)
    {
        _k8s = k8s;
        _cache = cache;
    }

    public async Task<bool> IsBackendAvailable(string user) => await _k8s.IsPodRunning(user);

    public async Task StartBackendCheckAsync(string user, string connectionId)
    {
        if (!_cache.TryGetValue($"start:{user}", out _))
        {
            _ = _k8s.StartUserApp(user, connectionId);
            _cache.Set($"start:{user}", true, TimeSpan.FromMinutes(5));
        }
    }
}
