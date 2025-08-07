{{- if .Values.shutdown.enabled }}
apiVersion: batch/v1
kind: CronJob
metadata:
  name: {{ include "kasm-user-proxy.name" . }}-shutdown-idle
spec:
  schedule: "{{ .Values.shutdown.schedule }}"
  jobTemplate:
    spec:
      template:
        spec:
          containers:
            - name: cleanup
              image: bitnami/kubectl:latest
              command:
                - /bin/sh
                - -c
                - >
                  for d in $(kubectl get deployments -l app=kasm-user -o jsonpath='{.items[*].metadata.name}'); do
                    lastused=$(kubectl get pod -l app=$d -o jsonpath='{.items[0].status.startTime}' | xargs date -d);
                    now=$(date +%s);
                    idle={{ .Values.shutdown.idleMinutes }};
                    if [ $(($now - $lastused)) -gt $(($idle * 60)) ]; then
                      kubectl delete deployment $d;
                    fi;
                  done
          restartPolicy: OnFailure
{{- end }}