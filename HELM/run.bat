
REM helm update
REM helm upgrade --install ingress-nginx ingress-nginx   --repo https://kubernetes.github.io/ingress-nginx   --namespace ingress-nginx --create-namespace

helm uninstall browser-isolation
docker build -t simon:v1 ../.
helm install browser-isolation .  --set ingress.hosts[0].host="*.example.com"   