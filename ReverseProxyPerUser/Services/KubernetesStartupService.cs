using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using k8s;
using k8s.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;
using ReverseProxyPerUser.Hubs;

namespace ReverseProxyPerUser.Services
{
    public class KubernetesStartupService
    {
        private readonly Kubernetes _client;
        private readonly IHubContext<StartupNotifierHub> _hub;
        private readonly ILogger<KubernetesStartupService> _logger;
        private readonly IMemoryCache _cache;

        private const string Namespace = "default"; // Adjust as needed
        private const string Image = "kasmweb/firefox:1.17.0-rolling-daily"; // Or whatever image you're using

        public KubernetesStartupService(
            IHubContext<StartupNotifierHub> hub,
            ILogger<KubernetesStartupService> logger,
            IMemoryCache cache)
        {
            _hub = hub;
            _logger = logger;
            _cache = cache;

            KubernetesClientConfiguration config;
            if (KubernetesClientConfiguration.IsInCluster())
            {
                config = KubernetesClientConfiguration.InClusterConfig();
            }
            else
            {
                config = KubernetesClientConfiguration.BuildConfigFromConfigFile();
            }

            _client = new Kubernetes(config);
        }

        public async Task<bool> IsPodRunning(string username)
        {
            try
            {
                var pods = await _client.ListNamespacedPodAsync(Namespace, labelSelector: $"app={username}");

                foreach (var pod in pods.Items)
                {
                    if (pod.Status.Phase == "Running")
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking pod status");
            }

            return false;
        }

        public async Task StartUserApp(string username, string connectionId)
        {
            await StartBackendCheckAsync(username, connectionId);
        }

        public async Task<bool> IsBackendAvailable(string username)
        {
            var svcName = $"{username}-svc";
            try
            {
                var service = await _client.ReadNamespacedServiceAsync(svcName, Namespace);
                return service != null;
            }
            catch
            {
                return false;
            }
        }

        public async Task StartBackendCheckAsync(string username, string connectionId)
        {
            if (_cache.TryGetValue($"startup:{username}", out _))
            {
                return;
            }

            _cache.Set($"startup:{username}", true, TimeSpan.FromMinutes(5));

            await _hub.Clients.Client(connectionId).SendAsync("status", "Starting container...");

            string password = GeneratePassword();

            await CreateDeploymentAsync(username, password);
            await CreateServiceAsync(username);

            // Optionally notify frontend with password or status
            await _hub.Clients.Client(connectionId).SendAsync("status", $"Container ready. Password: {password}");
        }

        private string GeneratePassword()
        {
            return Guid.NewGuid().ToString("N")[..12];
        }

        private async Task CreateDeploymentAsync(string username, string password)
        {
            var deploy = new V1Deployment
            {
                Metadata = new V1ObjectMeta
                {
                    Name = $"{username}-deploy",
                    NamespaceProperty = Namespace
                },
                Spec = new V1DeploymentSpec
                {
                    Replicas = 1,
                    Selector = new V1LabelSelector
                    {
                        MatchLabels = new Dictionary<string, string> { { "app", username } }
                    },
                    Template = new V1PodTemplateSpec
                    {
                        Metadata = new V1ObjectMeta
                        {
                            Labels = new Dictionary<string, string> { { "app", username } }
                        },
                        Spec = new V1PodSpec
                        {
                            Containers = new List<V1Container>
                            {
                                new V1Container
                                {
                                    Name = "browser",
                                    Image = Image,
                                    Env = new List<V1EnvVar>
                                    {
                                        new V1EnvVar("VNC_PW", password)
                                    },
                                    Ports = new List<V1ContainerPort>
                                    {
                                        new V1ContainerPort { ContainerPort = 6901 }
                                    }
                                }
                            }
                        }
                    }
                }
            };

            await _client.CreateNamespacedDeploymentAsync(deploy, Namespace);
        }

        private async Task CreateServiceAsync(string username)
        {
            var svc = new V1Service
            {
                Metadata = new V1ObjectMeta
                {
                    Name = $"{username}-svc",
                    NamespaceProperty = Namespace
                },
                Spec = new V1ServiceSpec
                {
                    Selector = new Dictionary<string, string> { { "app", username } },
                    Ports = new List<V1ServicePort>
                    {
                        new V1ServicePort
                        {
                            Port = 443,
                            TargetPort = 6901
                        }
                    }
                }
            };

            await _client.CreateNamespacedServiceAsync(svc, Namespace);
        }
    }
}
