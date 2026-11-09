"""CasePilot 核心: 认证 (CLI / 设备码 / App Registration) 与 Azure Support REST 调用. 零第三方依赖."""
import json
import re
import os
import secrets
import shutil
import ssl
import tempfile
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import webbrowser
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

HOST = "127.0.0.1"
PORT = int(os.environ.get("CASEPILOT_PORT", "8765"))
SUPPORT_API = "2024-04-01"
SESSION_TOKEN = secrets.token_urlsafe(24)
# 从 Finder 启动 .app 时 PATH 很精简, 补上 az 常见安装位置
os.environ["PATH"] = os.pathsep.join([os.environ.get("PATH", ""), "/opt/homebrew/bin", "/usr/local/bin"])
# PyInstaller 打包后资源位于 sys._MEIPASS
STATIC = Path(getattr(sys, "_MEIPASS", Path(__file__).parent)) / "static"
# 设备码登录使用 Azure CLI 的公共 Client ID, 客户无需自建 App Registration
DEVICE_CLIENT_ID = os.environ.get("CASEPILOT_CLIENT_ID", "04b07795-8ddb-461a-bbee-02f9e1bf7b46")

CLOUDS = {
    "AzureCloud": {"arm": "https://management.azure.com", "login": "https://login.microsoftonline.com"},
    "AzureChinaCloud": {"arm": "https://management.chinacloudapi.cn", "login": "https://login.chinacloudapi.cn"},
    "AzureUSGovernment": {"arm": "https://management.usgovcloudapi.net", "login": "https://login.microsoftonline.us"},
}


def _ssl_context():
    """python.org 版 macOS Python 默认无 CA: 依次尝试 certifi / 系统默认 / macOS 根证书钥匙串。"""
    try:
        import certifi
        return ssl.create_default_context(cafile=certifi.where())
    except ImportError:
        pass
    ctx = ssl.create_default_context()
    if ctx.cert_store_stats().get("x509_ca") or sys.platform != "darwin":
        return ctx
    p = subprocess.run(["security", "find-certificate", "-a", "-p",
                        "/System/Library/Keychains/SystemRootCertificates.keychain"], capture_output=True, text=True)
    if p.returncode == 0 and p.stdout:
        f = Path(tempfile.gettempdir()) / "casepilot-roots.pem"
        f.write_text(p.stdout)
        return ssl.create_default_context(cafile=str(f))
    return ctx


SSL_CTX = _ssl_context()


class ApiError(Exception):
    def __init__(self, status, message, detail=None):
        super().__init__(message)
        self.status, self.message, self.detail = status, message, detail


# ---------------------------------------------------------------- 认证

class Auth:
    def __init__(self):
        self.lock = threading.Lock()
        self.mode = None          # "cli" | "sp" | "device"
        self.device = None        # {flow, refresh_token, user}
        self.cloud = "AzureCloud"
        self.sp = None            # {tenant, client_id, secret}
        self.cache = {}           # tenant -> (token, expires_epoch)

    @property
    def arm(self):
        return CLOUDS[self.cloud]["arm"]

    def status(self):
        return {"mode": self.mode, "cloud": self.cloud,
                "sp_tenant": self.sp["tenant"] if self.sp else None,
                "sp_client_id": self.sp["client_id"] if self.sp else None}

    def use_cli(self):
        if not shutil.which("az"):
            raise ApiError(400, "未找到 Azure CLI (az), 请先安装或改用 App Registration")
        acct = _run_az(["account", "show"])
        cloud = acct.get("environmentName") or "AzureCloud"
        with self.lock:
            self.mode, self.sp, self.cache = "cli", None, {}
            self.cloud = cloud if cloud in CLOUDS else "AzureCloud"
        return {"user": acct.get("user", {}).get("name"), "tenant": acct.get("tenantId"), "cloud": self.cloud}

    def use_sp(self, tenant, client_id, secret, cloud):
        if cloud not in CLOUDS:
            raise ApiError(400, f"不支持的云环境: {cloud}")
        with self.lock:
            self.mode, self.cloud, self.cache = "sp", cloud, {}
            self.sp = {"tenant": tenant.strip(), "client_id": client_id.strip(), "secret": secret}
        self.token(self.sp["tenant"])  # 立即验证凭据
        return {"tenant": self.sp["tenant"], "cloud": cloud}

    def logout(self):
        with self.lock:
            self.mode, self.sp, self.device, self.cache = None, None, None, {}

    # ---- 设备码登录 (浏览器登录, 无需 Azure CLI)
    def device_start(self, cloud):
        if cloud not in CLOUDS:
            raise ApiError(400, f"不支持的云环境: {cloud}")
        url = f"{CLOUDS[cloud]['login']}/organizations/oauth2/v2.0/devicecode"
        flow = _post_form(url, {"client_id": DEVICE_CLIENT_ID,
                                "scope": CLOUDS[cloud]["arm"] + "/user_impersonation offline_access openid profile"})
        with self.lock:
            self.device = {"flow": flow, "cloud": cloud, "expires": time.time() + int(flow.get("expires_in", 900))}
        return {"userCode": flow["user_code"], "verificationUri": flow["verification_uri"], "message": flow.get("message")}

    def device_poll(self):
        d = self.device
        if not d or "flow" not in d:
            raise ApiError(400, "请先发起设备码登录")
        if time.time() > d["expires"]:
            raise ApiError(400, "设备码已过期, 请重新发起")
        url = f"{CLOUDS[d['cloud']]['login']}/organizations/oauth2/v2.0/token"
        try:
            data = _post_form(url, {"grant_type": "urn:ietf:params:oauth:grant-type:device_code",
                                    "client_id": DEVICE_CLIENT_ID, "device_code": d["flow"]["device_code"]})
        except ApiError as e:
            if "authorization_pending" in e.message or "slow_down" in e.message:
                return {"pending": True}
            raise
        claims = _jwt_claims(data.get("id_token") or data["access_token"])
        with self.lock:
            self.mode, self.cloud, self.sp, self.cache = "device", d["cloud"], None, {}
            self.device = {"refresh_token": data["refresh_token"], "home": claims.get("tid")}
            self.cache[(claims.get("tid") or "_default", self.arm)] = (data["access_token"], time.time() + int(data.get("expires_in", 3600)))
        return {"pending": False, "user": claims.get("preferred_username") or claims.get("upn"),
                "tenant": claims.get("tid"), "cloud": self.cloud}

    def token(self, tenant=None, resource=None):
        """resource 默认 ARM; 亦可为 https://cognitiveservices.azure.com (AI / 语音)。"""
        if not self.mode:
            raise ApiError(401, "尚未认证")
        resource = (resource or self.arm).rstrip("/")
        if self.mode == "sp":
            if tenant and tenant != self.sp["tenant"]:
                raise ApiError(400, "App Registration 仅能访问其所在租户 (除非为多租户应用并已在目标租户授权)")
            tenant = self.sp["tenant"]
        key = (tenant or "_default", resource)
        cached = self.cache.get(key)
        if cached and cached[1] - 300 > time.time():
            return cached[0]
        if self.mode == "cli":
            tok, exp = self._cli_token(tenant, resource)
        elif self.mode == "device":
            tok, exp = self._refresh_token(tenant or self.device["home"], resource)
        elif self.mode == "workload":
            tok, exp = self._workload_token(resource)
        else:
            tok, exp = self._sp_token(resource)
        self.cache[key] = (tok, exp)
        return tok

    def _cli_token(self, tenant, resource):
        args = ["account", "get-access-token", "--resource", resource + "/"]
        if tenant:
            args += ["--tenant", tenant]
        data = _run_az(args, hint_tenant=tenant)
        exp = data.get("expires_on") or time.time() + 1800
        return data["accessToken"], float(exp)

    def _sp_token(self, resource):
        sp = self.sp
        url = f"{CLOUDS[self.cloud]['login']}/{sp['tenant']}/oauth2/v2.0/token"
        try:
            data = _post_form(url, {"grant_type": "client_credentials", "client_id": sp["client_id"],
                                    "client_secret": sp["secret"], "scope": resource + "/.default"})
        except ApiError as e:
            raise ApiError(401, "App Registration 获取令牌失败: " + e.message)
        return data["access_token"], time.time() + int(data.get("expires_in", 3600))

    def _workload_token(self, resource):
        """AKS Workload Identity: 用投射的联邦令牌换取 Entra 令牌。"""
        assertion = Path(os.environ["AZURE_FEDERATED_TOKEN_FILE"]).read_text().strip()
        url = f"{os.environ.get('AZURE_AUTHORITY_HOST', CLOUDS[self.cloud]['login'] + '/').rstrip('/')}/{os.environ['AZURE_TENANT_ID']}/oauth2/v2.0/token"
        data = _post_form(url, {"grant_type": "client_credentials", "client_id": os.environ["AZURE_CLIENT_ID"],
                                "client_assertion_type": "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                                "client_assertion": assertion, "scope": resource + "/.default"})
        return data["access_token"], time.time() + int(data.get("expires_in", 3600))

    def use_workload(self):
        for k in ("AZURE_FEDERATED_TOKEN_FILE", "AZURE_CLIENT_ID", "AZURE_TENANT_ID"):
            if not os.environ.get(k):
                raise ApiError(400, f"未检测到 Workload Identity 环境变量 {k}")
        with self.lock:
            self.mode, self.sp, self.cache = "workload", None, {}
        self.token()
        return {"user": "workload:" + os.environ["AZURE_CLIENT_ID"][:8], "tenant": os.environ["AZURE_TENANT_ID"], "cloud": self.cloud}

    def _refresh_token(self, tenant, resource):
        url = f"{CLOUDS[self.cloud]['login']}/{tenant}/oauth2/v2.0/token"
        try:
            data = _post_form(url, {"grant_type": "refresh_token", "client_id": DEVICE_CLIENT_ID,
                                    "refresh_token": self.device["refresh_token"],
                                    "scope": resource + "/user_impersonation offline_access"})
        except ApiError as e:
            raise ApiError(401, f"无法获取租户 {tenant} 的令牌 (可能需要 MFA 或未加入该租户)", e.message)
        if data.get("refresh_token"):
            self.device["refresh_token"] = data["refresh_token"]
        return data["access_token"], time.time() + int(data.get("expires_in", 3600))


def _post_form(url, fields):
    req = urllib.request.Request(url, urllib.parse.urlencode(fields).encode())
    try:
        with urllib.request.urlopen(req, timeout=30, context=SSL_CTX) as r:
            return json.load(r)
    except urllib.error.HTTPError as e:
        err = _safe_json(e.read())
        raise ApiError(401, f"{err.get('error', e.code)}: {str(err.get('error_description', ''))[:400]}")


def _jwt_claims(tok):
    import base64
    seg = tok.split(".")[1]
    return json.loads(base64.urlsafe_b64decode(seg + "=" * (-len(seg) % 4)))


def _run_az(args, hint_tenant=None):
    p = subprocess.run([shutil.which("az") or "az", *args, "-o", "json"], capture_output=True, text=True, timeout=60)
    if p.returncode != 0:
        msg = p.stderr.strip() or "az 命令失败"
        hint = f"az login --tenant {hint_tenant}" if hint_tenant else "az login"
        raise ApiError(401, f"Azure CLI 错误。可尝试在终端执行 `{hint}` 后重试。", msg[-800:])
    return json.loads(p.stdout)


def _safe_json(raw):
    try:
        return json.loads(raw)
    except Exception:
        return {"raw": raw.decode(errors="replace")[:800] if isinstance(raw, bytes) else str(raw)}


AUTH = Auth()


# ---------------------------------------------------------------- ARM 调用

def arm(method, path, tenant=None, body=None, raw=False):
    url = path if path.startswith("http") else AUTH.arm + path
    headers = {"Authorization": "Bearer " + AUTH.token(tenant), "Content-Type": "application/json",
               "x-ms-client-request-id": str(uuid.uuid4())}
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data, headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=60, context=SSL_CTX) as r:
            payload = _safe_json(r.read() or b"{}")
            return (r.status, dict(r.headers), payload) if raw else payload
    except urllib.error.HTTPError as e:
        err = _safe_json(e.read()).get("error", {})
        rid = e.headers.get("x-ms-request-id") or e.headers.get("x-ms-correlation-request-id")
        raise ApiError(e.code, f"{err.get('code', e.code)}: {err.get('message', e.reason)}",
                       {"requestId": rid, "url": url.split("?")[0], "details": err.get("details")})


def arm_list(path, tenant=None):
    out, url = [], path
    while url:
        page = arm("GET", url, tenant)
        out += page.get("value", [])
        url = page.get("nextLink")
    return out


GUID = re.compile(r"^[0-9a-fA-F-]{36}$")


def check_ids(r):
    for k in ("tenantId", "subscriptionId"):
        v = r.get(k)
        if v and not GUID.match(v):
            raise ApiError(400, f"{k} 格式非法")
    for k in ("service", "name"):
        v = r.get(k)
        if v and not re.match(r"^[\w.-]+$", v):
            raise ApiError(400, f"{k} 格式非法")


def support_base(sub):
    return f"/subscriptions/{sub}/providers/Microsoft.Support" if sub else "/providers/Microsoft.Support"


_svc_cache = {}
CACHE_TTL = 6 * 3600


def _cached(key, loader):
    hit = _svc_cache.get(key)
    if not hit or hit[0] < time.time():
        hit = _svc_cache[key] = (time.time() + CACHE_TTL, loader())
    return hit[1]


def list_services(tenant):
    def load():
        items = arm_list(f"/providers/Microsoft.Support/services?api-version={SUPPORT_API}", tenant)
        return sorted(({"id": s["id"], "name": s["name"], "displayName": s["properties"]["displayName"],
                                   "resourceTypes": s["properties"].get("resourceTypes", [])} for s in items),
                      key=lambda s: s["displayName"].lower())
    return _cached((AUTH.cloud, "services"), load)


def list_problems(tenant, service):
    def load():
        items = arm_list(f"/providers/Microsoft.Support/services/{service}/problemClassifications"
                         f"?api-version={SUPPORT_API}", tenant)
        return sorted(({"id": p["id"], "displayName": p["properties"]["displayName"]} for p in items),
                      key=lambda p: p["displayName"].lower())
    return _cached((AUTH.cloud, "pc", service), load)


def list_resources(tenant, sub, types):
    if not types:
        return []
    filt = " or ".join(f"resourceType eq '{t}'" for t in types[:15])
    q = urllib.parse.quote(filt)
    items = arm_list(f"/subscriptions/{sub}/resources?api-version=2021-04-01&$filter={q}", tenant)
    return sorted(({"id": r["id"], "name": r["name"], "type": r["type"], "location": r.get("location")}
                   for r in items), key=lambda r: r["name"].lower())


def list_all_resources(tenant, sub):
    items = arm_list(f"/subscriptions/{sub}/resources?api-version=2021-04-01", tenant)
    return sorted(({"id": r["id"], "name": r["name"], "type": r["type"].lower(), "location": r.get("location"),
                    "group": r["id"].split("/")[4]} for r in items), key=lambda r: r["name"].lower())


def build_ticket(f):
    contact = f["contact"]
    props = {
        "title": f["title"].strip(),
        "description": f["description"].strip(),
        "serviceId": f["serviceId"],
        "problemClassificationId": f["problemClassificationId"],
        "severity": f["severity"],
        "advancedDiagnosticConsent": "Yes" if f.get("advancedDiagnosticConsent") else "No",
        "require24X7Response": bool(f.get("require24X7Response")),
        "contactDetails": {
            "firstName": contact["firstName"], "lastName": contact["lastName"],
            "primaryEmailAddress": contact["email"],
            "preferredContactMethod": contact.get("method", "email"),
            "preferredTimeZone": contact["timeZone"],
            "preferredSupportLanguage": contact["language"],
            "country": contact["country"],
        },
    }
    if contact.get("phone"):
        props["contactDetails"]["phoneNumber"] = contact["phone"]
    if contact.get("cc"):
        props["contactDetails"]["additionalEmailAddresses"] = [e.strip() for e in contact["cc"].split(",") if e.strip()]
    if f.get("problemStartTime"):
        props["problemStartTime"] = f["problemStartTime"]
    q = f.get("quota")
    if q:
        props["quotaTicketDetails"] = {
            "quotaChangeRequestVersion": "1.0",
            "quotaChangeRequests": [{"region": q["region"],
                                     "payload": json.dumps({"VMFamily": q["sku"], "NewLimit": int(q["newLimit"])})}],
        }
    if f.get("resourceId"):
        props["technicalTicketDetails"] = {"resourceId": f["resourceId"]}
    return {"properties": props}


def validate(f):
    missing = [k for k in ("title", "description", "serviceId", "problemClassificationId", "severity")
               if not str(f.get(k, "")).strip()]
    c = f.get("contact") or {}
    missing += ["contact." + k for k in ("firstName", "lastName", "email", "timeZone", "language", "country")
                if not str(c.get(k, "")).strip()]
    if c.get("method") == "phone" and not c.get("phone"):
        missing.append("contact.phone")
    if missing:
        raise ApiError(400, "缺少必填字段: " + ", ".join(missing))
    if f["severity"] not in ("minimal", "moderate", "critical", "highestcriticalimpact"):
        raise ApiError(400, "severity 非法")
    if len(f["title"]) > 200:
        raise ApiError(400, "标题过长")


def create_ticket(f):
    validate(f)
    tenant, sub = f.get("tenantId"), f.get("subscriptionId")
    body = build_ticket(f)
    if f.get("dryRun"):
        return {"dryRun": True, "request": body, "scope": support_base(sub)}
    base = support_base(sub)
    name = "casepilot-" + uuid.uuid4().hex[:12]
    avail = arm("POST", f"{base}/checkNameAvailability?api-version={SUPPORT_API}", tenant,
                {"name": name, "type": "Microsoft.Support/supportTickets"})
    if avail.get("nameAvailable") is False:
        name = "casepilot-" + uuid.uuid4().hex
    url = f"{base}/supportTickets/{name}?api-version={SUPPORT_API}"
    status, headers, payload = arm("PUT", url, tenant, body, raw=True)
    op = headers.get("Azure-AsyncOperation") or headers.get("azure-asyncoperation")
    deadline = time.time() + 120
    while op and time.time() < deadline:
        st = arm("GET", op, tenant)
        state = (st.get("status") or "").lower()
        if state in ("succeeded", "failed", "canceled"):
            if state != "succeeded":
                raise ApiError(500, f"创建失败: {st.get('error', st)}")
            break
        time.sleep(3)
    ticket = arm("GET", url, tenant)
    p = ticket.get("properties", {})
    return {"name": name, "supportTicketId": p.get("supportTicketId"), "status": p.get("status"),
            "severity": p.get("severity"), "createdDate": p.get("createdDate"), "httpStatus": status,
            "portalUrl": "https://portal.azure.com/#view/Microsoft_Azure_Support/HelpAndSupportBlade/~/overview"}


def list_tickets(tenant, sub, days=30):
    days = max(1, min(int(days or 30), 365))
    since = time.strftime('%Y-%m-%dT00:00:00Z', time.gmtime(time.time() - days * 86400))
    q = urllib.parse.quote(f"createdDate ge {since}")
    items = arm_list(f"{support_base(sub)}/supportTickets?api-version={SUPPORT_API}&$top=100&$filter={q}", tenant)
    rows = [{"name": t["name"], **{k: t["properties"].get(k) for k in
             ("supportTicketId", "title", "status", "severity", "createdDate", "serviceDisplayName",
               "supportPlanType", "problemClassificationDisplayName")}}
            for t in items]
    return sorted(rows, key=lambda t: t.get("createdDate") or "", reverse=True)


def ticket_detail(tenant, sub, name):
    if not name or "/" in name:
        raise ApiError(400, "非法 Case 名")
    base = f"{support_base(sub)}/supportTickets/{urllib.parse.quote(name)}"
    p = arm("GET", f"{base}?api-version={SUPPORT_API}", tenant).get("properties", {})
    try:
        comms = arm_list(f"{base}/communications?api-version={SUPPORT_API}", tenant)
        comms = sorted(({"sender": c["properties"].get("sender"), "subject": c["properties"].get("subject"),
                         "body": c["properties"].get("body"), "direction": c["properties"].get("communicationDirection"),
                         "createdDate": c["properties"].get("createdDate")} for c in comms),
                       key=lambda c: c.get("createdDate") or "")
    except ApiError as e:
        comms = [{"sender": "CasePilot", "subject": "无法加载沟通记录", "body": e.message, "direction": "", "createdDate": ""}]
    return {"properties": p, "communications": comms}


def close_ticket(tenant, sub, name):
    if not name or "/" in name:
        raise ApiError(400, "非法 Case 名")
    url = f"{support_base(sub)}/supportTickets/{urllib.parse.quote(name)}?api-version={SUPPORT_API}"
    # PATCH 的返回值不可靠 (未处理完的补充信息可能随后重新打开 Case), 关闭后回读确认
    for _ in range(4):
        arm("PATCH", url, tenant, {"status": "closed"})
        time.sleep(3)
        status = arm("GET", url, tenant).get("properties", {}).get("status")
        if (status or "").lower() == "closed":
            return {"status": status}
        time.sleep(5)
    raise ApiError(409, f"关闭请求已发送, 但 Case 当前状态仍为 {status}, 请稍后重试")


def add_communication(tenant, sub, name, subject, body):
    if not (body or "").strip():
        raise ApiError(400, "内容不能为空")
    cname = "cp-" + uuid.uuid4().hex[:12]
    url = f"{support_base(sub)}/supportTickets/{urllib.parse.quote(name)}/communications/{cname}?api-version={SUPPORT_API}"
    arm("PUT", url, tenant, {"properties": {"subject": (subject or "Update")[:200], "body": body}})
    return {"name": cname}


