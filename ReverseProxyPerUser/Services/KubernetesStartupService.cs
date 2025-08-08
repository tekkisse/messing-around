using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using ReverseProxyPerUser.Hubs;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ReverseProxyPerUser.Services
{
    public class KubernetesStartupService
    {
        private readonly Kubernetes _client;
        private readonly IHubContext<StartupNotifierHub> _hub;
        private readonly ILogger<KubernetesStartupService> _logger;
        private readonly IMemoryCache _cache;

        // could auto detect : string ns = await File.ReadAllTextAsync("/var/run/secrets/kubernetes.io/serviceaccount/namespace");
        private const string Namespace = "default";  
        private const string Image = "kasmweb/chrome:1.17.0-rolling-daily";  

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
                // docker desktop odd-ness
                config.Host = "https://192.168.65.3:6443";
            }
            else
            {
                config = KubernetesClientConfiguration.BuildConfigFromConfigFile();
            }

            _client = new Kubernetes(config);
        }

        public async Task<bool> IsPodRunning(string username)
        {
            Console.WriteLine("check user: " + username);

            try
            {
                var pods = await _client.ListNamespacedPodAsync(Namespace, labelSelector: $"app={username}");

                foreach (var pod in pods.Items)
                {
                    Console.WriteLine(pod.Name() + " is " + pod.Status.Phase);
                    if (pod.Status.Phase == "Running")
                    {
                        Console.WriteLine("running");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking pod status");
                Console.WriteLine("error " + ex);
            }

            Console.WriteLine("no pod for user: "+username);
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
                Console.WriteLine("not trying as request already made (in last 5 min): "+ username);
                return;
            }

            _cache.Set($"startup:{username}", true, TimeSpan.FromMinutes(5));

            await _hub.Clients.Client(connectionId).SendAsync("status", "Starting container...");

            //string password = GeneratePassword();
            string password = "password";
            
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
            Console.WriteLine("New deploymnet for: " + username);
            
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

            try
            {
                await _client.CreateNamespacedDeploymentAsync(deploy, Namespace);
            }
            catch (k8s.Autorest.HttpOperationException ex)
            {
                Console.WriteLine("Kubernetes API error: " + ex.Response.Content);
                throw;
            }
            ;
        }

        private async Task CreateServiceAsync(string username)
        {
            Console.WriteLine("New service for: "+username);
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
                            Port = 8088,
                            TargetPort = 6901
                        }
                    }
                }
            };

            await _client.CreateNamespacedServiceAsync(svc, Namespace);
        }
    }
}
