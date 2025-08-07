{{- define "kasm-user-proxy.name" -}}
{{ .Chart.Name }}
{{- end }}

{{- define "kasm-user-proxy.fullname" -}}
{{ .Release.Name }}-{{ .Chart.Name }}
{{- end }}

{{- define "kasm-user-proxy.serviceAccountName" -}}
{{ include "kasm-user-proxy.fullname" . }}-sa
{{- end }}
