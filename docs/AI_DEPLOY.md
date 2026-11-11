# 一句话交给 AI 部署 CasePilot

把下面这句话 (替换尖括号) 发给 Claude Code / GitHub Copilot / Codex 等能执行命令的 AI 助手即可:

> 按 https://github.com/ZenoRewn/Azure_CasePilot/blob/main/docs/AI_DEPLOY.md 的步骤, 把 CasePilot 部署到我的 AKS: 资源组 `<rg>`, 集群 `<aks>`, ACR `<acr>`, 订阅 `<订阅 ID>`, AI 资源 `<AI 资源名>`; 每步执行前先说明, 涉及创建资源或授权时先让我确认。

只想在本机用? 不需要部署, 到 [Releases](https://github.com/ZenoRewn/Azure_CasePilot/releases/latest) 下载对应平台的包即可。

---

## 给 AI 助手的执行步骤

目标: 在用户的 AKS 上以 Workload Identity 运行 CasePilot Web/MCP 服务, 不在集群中保存任何 Azure 密钥。

### 0. 前置检查 (只读)
```bash
az account show -o table                      # 已登录且为目标租户
az aks show -g <rg> -n <aks> --query "{oidc:oidcIssuerProfile.enabled, wi:securityProfile.workloadIdentity.enabled}"
az acr show -n <acr> -o none && helm version && kubectl version --client
```
- 若 OIDC / Workload Identity 未开启, 先告诉用户并征得同意后执行:
  `az aks update -g <rg> -n <aks> --enable-oidc-issuer --enable-workload-identity`
- `kubectl` 需指向该集群: `az aks get-credentials -g <rg> -n <aks>`

### 1. (可选) 部署语音模型
在 AI 资源上确认存在部署 `gpt-6-luna` (或用户已有的对话模型)、`gpt-live-transcribe` (流式转写)、`gpt-4o-mini-transcribe` (整段兜底):
```bash
az cognitiveservices account deployment list -g <ai-rg> -n <AI 资源名> --query "[].name" -o tsv
```
缺失时征得同意后创建, 例如:
```bash
az cognitiveservices account deployment create -g <ai-rg> -n <AI 资源名> --deployment-name gpt-live-transcribe \
  --model-name gpt-live-transcribe --model-version 2026-07-28 --model-format OpenAI --sku-name GlobalStandard --sku-capacity 10
```
对话模型不同于默认值时, 部署后设置 Helm 值 `casepilot.ai.deployment` / `endpoint` / `sttEndpoint` (`helm upgrade ... --reuse-values --set ...`)。

### 2. 一键部署 (会创建托管标识与角色分配, 先征得同意)
在仓库根目录执行:
```bash
RG=<rg> AKS=<aks> ACR=<acr> SUB=<订阅 ID> AOAI=<AI 资源名> AOAI_RG=<ai-rg> ./deploy/aks.sh
```
脚本: ACR 构建镜像 → 创建托管标识 + 联邦凭据 → 授予 Support Request Contributor / Reader (订阅) 与 Cognitive Services OpenAI User (AI 资源) → 生成 API Key 存入 K8s Secret `casepilot-api-key` → helm 安装。

### 3. 验证
```bash
kubectl -n casepilot rollout status deploy/casepilot
kubectl -n casepilot port-forward svc/casepilot 8080:80 &
curl -s localhost:8080/healthz                 # {"ok": true, "mode": "server", ...}
```
API Key 在 Secret 中: `kubectl -n casepilot get secret casepilot-api-key -o jsonpath='{.data.api-key}' | base64 -d`。
告诉用户: Web 访问地址、API Key 获取方式、MCP 地址 `POST https://<host>/mcp` (Bearer API Key)。
如需公网访问, 建议放在 Entra 认证网关后 (Helm `casepilot.trustProxyAuth=true`), 或开启 `ingress.enabled`, 不要直接暴露。

### 4. 回滚
```bash
helm -n casepilot uninstall casepilot && kubectl delete ns casepilot
az identity delete -g <rg> -n id-casepilot     # 先征得同意
```

### 注意
- 不要把任何 Key 写入仓库或命令历史; AI/Speech Key (如必须用) 放进 K8s Secret, 见 Helm `casepilot.ai.keysSecret` (键 `aoai-key` / `stt-key`)。
- 不要为测试创建真实 Support Case; 如必须验证创建流程, 标题注明测试并在验证后立即关闭。
