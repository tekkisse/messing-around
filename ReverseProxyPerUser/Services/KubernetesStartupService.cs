using k8s;
using k8s.Models;
using Microsoft.AspNetCore.SignalR;
using ReverseProxyPerUser.Hubs;

namespace ReverseProxyPerUser.Services;

public class KubernetesStartupService
{
    private readonly IKubernetes _k8s;
    private readonly IHubContext<StartupNotifierHub> _hub;

    public KubernetesStartupService(IHubContext<StartupNotifierHub> hub)
    {
        var config = KubernetesClientConfiguration.InClusterConfig();
        _k8s = new Kubernetes(config);
        _hub = hub;
    }

    public async Task<bool> IsPodRunning(string user)
    {
        var pods = await _k8s.CoreV1.ListNamespacedPodAsync("default", labelSelector: $"app=user-{user}");
        return pods.Items.Any(p => p.Status.Phase == "Running");
    }

    public async Task StartUserApp(string user, string connectionId)
    {
        var deployName = $"user-{user}";
        var serviceName = $"svc-{user}";
        var label = new Dictionary<string, string> { { "app", $"user-{user}" } };

        try { await _k8s.AppsV1.ReadNamespacedDeploymentAsync(deployName, "default"); }
        catch
        {
            await _k8s.AppsV1.CreateNamespacedDeploymentAsync(new V1Deployment
            {
                Metadata = new V1ObjectMeta { Name = deployName },
                Spec = new V1DeploymentSpec
                {
                    Replicas = 1,
                    Selector = new V1LabelSelector { MatchLabels = label },
                    Template = new V1PodTemplateSpec
                    {
                        Metadata = new V1ObjectMeta { Labels = label },
                        Spec = new V1PodSpec
                        {
                            Containers = new List<V1Container>
                            {
                                new V1Container
                                {
                                    Name = "browser",
                                    Image = "kasmweb/firefox:dev",
                                    Ports = new List<V1ContainerPort> { new V1ContainerPort(6901) }
                                }
                            }
                        }
                    }
                }
            }, "default");

            await _k8s.CoreV1.CreateNamespacedServiceAsync(new V1Service
            {
                Metadata = new V1ObjectMeta { Name = serviceName },
                Spec = new V1ServiceSpec
                {
                    Selector = label,
                    Ports = new List<V1ServicePort> { new V1ServicePort { Port = 80, TargetPort = 6901 } }
                }
            }, "default");
        }

        for (int i = 0; i < 30; i++)
        {
            if (await IsPodRunning(user))
            {
                await _hub.Clients.Client(connectionId).SendAsync("BackendReady", $"https://{user}.example.com");
                return;
            }

            await Task.Delay(2000);
        }

        await _hub.Clients.Client(connectionId).SendAsync("BackendFailed");
    }
}
