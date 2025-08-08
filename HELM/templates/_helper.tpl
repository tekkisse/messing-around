{{- define "browser.app" -}}
{{ .Chart.Name }}
{{- end }}

{{- define "browser.isolation" -}}
{{ .Release.Name }}-{{ .Chart.Name }}
{{- end }}

{{- define "browser-proxy.serviceAccountName" -}}
{{ include "browser.isolation" . }}-sa
{{- end }}
