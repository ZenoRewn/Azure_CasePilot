#!/usr/bin/env bash
# 一键部署 CasePilot 到 AKS (Workload Identity, 无密钥访问 Azure)
# 用法: RG=zeno-rg AKS=zeno-sg-aks ACR=zenoseaacr SUB=<订阅> AOAI_RG=zeno-rg AOAI=westus3-azureai-service ./deploy/aks.sh
set -euo pipefail
: "${RG:?}" "${AKS:?}" "${ACR:?}" "${SUB:?}"
NS=${NS:-casepilot}; NAME=${NAME:-casepilot}; TAG=${TAG:-2.6.0}
cd "$(dirname "$0")/.."

az acr build -r "$ACR" -t "casepilot:$TAG" . --no-logs -o none
LOC=$(az aks show -g "$RG" -n "$AKS" --query location -o tsv)
ISSUER=$(az aks show -g "$RG" -n "$AKS" --query oidcIssuerProfile.issuerUrl -o tsv)
az identity create -g "$RG" -n "id-$NAME" -l "$LOC" -o none
CID=$(az identity show -g "$RG" -n "id-$NAME" --query clientId -o tsv)
PID=$(az identity show -g "$RG" -n "id-$NAME" --query principalId -o tsv)
TID=$(az account show --query tenantId -o tsv)
az identity federated-credential create -g "$RG" --identity-name "id-$NAME" -n "$NAME-fic" --issuer "$ISSUER" \
  --subject "system:serviceaccount:$NS:$NAME" --audiences api://AzureADTokenExchange -o none 2>/dev/null || true

# 最小权限: 提/管 Case + 读订阅资源; 调用 AI 模型
for ROLE in "Support Request Contributor" "Reader"; do
  az role assignment create --assignee-object-id "$PID" --assignee-principal-type ServicePrincipal --role "$ROLE" --scope "/subscriptions/$SUB" -o none 2>/dev/null || true
done
if [ -n "${AOAI:-}" ]; then
  AID=$(az cognitiveservices account show -g "${AOAI_RG:-$RG}" -n "$AOAI" --query id -o tsv)
  az role assignment create --assignee-object-id "$PID" --assignee-principal-type ServicePrincipal --role "Cognitive Services OpenAI User" --scope "$AID" -o none 2>/dev/null || true
fi
az aks update -g "$RG" -n "$AKS" --attach-acr "$ACR" -o none 2>/dev/null || true

az aks get-credentials -g "$RG" -n "$AKS" --overwrite-existing -o none
kubectl create namespace "$NS" --dry-run=client -o yaml | kubectl apply -f -
if ! kubectl -n "$NS" get secret casepilot-api-key >/dev/null 2>&1; then
  kubectl -n "$NS" create secret generic casepilot-api-key --from-literal=api-key="$(openssl rand -hex 24)"
fi
helm upgrade --install "$NAME" deploy/helm/casepilot -n "$NS" \
  --set image.repository="$ACR.azurecr.io/casepilot" --set image.tag="$TAG" \
  --set workloadIdentity.clientId="$CID" --set workloadIdentity.tenantId="$TID" \
  --set casepilot.subscriptionId="$SUB" --wait --timeout 5m
echo "部署完成。API Key: kubectl -n $NS get secret casepilot-api-key -o jsonpath='{.data.api-key}' | base64 -d"
echo "本地访问: kubectl -n $NS port-forward svc/$NAME 8080:80  →  http://127.0.0.1:8080/  (MCP: POST /mcp)"
