# Azure CasePilot: technical map (server.py, casepilot/*.py, tests/)

The code uses only the Python 3.9+ standard library (`certifi` is optional). All 74 tests pass on Windows with the az-bundled Python 3.14: `python -m unittest discover -s tests`, about 1.1 s. That run printed `ResourceWarning: unclosed socket` but no failures.

## Modules
| File | Role |
|---|---|
| `server.py` (214 lines) | Runs `ThreadingHTTPServer`. Holds `Config`, `Handler`, the `ROUTES` dict and the `MCP` instance. |
| `casepilot/core.py` (477) | Auth (`Auth` singleton `AUTH`), the ARM REST helpers `arm`/`arm_list`, and the Support API calls. Also sets up `SSL_CTX`, `check_ids` and a 6-hour catalog cache. |
| `casepilot/service.py` (77) | Shared business layer used by Web and MCP: `ai_config`, `_post` (429 retry), `chat`, `ai_draft`, `transcribe`, `support_plan`. |
| `casepilot/ai.py` (120) | Pure functions for drafting. The `chat()` call is passed in as a parameter. |
| `casepilot/providers.py` (94) | `AIConfig` dataclass plus helpers that build URLs, headers and multipart bodies. |
| `casepilot/plan.py` (29) | Infers the support plan. |
| `casepilot/mcp.py` (158) | JSON-RPC MCP server. Shared by stdio (`python -m casepilot.mcp`) and HTTP (`/mcp`). |

## Config and modes (`Config.from_env`)
- **`local`** (default)
  - Binds `127.0.0.1`, starting at port 8765. If a port is taken it tries the next one, up to 20 ports.
  - Opens a browser after 0.6 s unless `CASEPILOT_NO_BROWSER=1`.
  - `SESSION_TOKEN = secrets.token_urlsafe(24)` is generated per process and injected into `index.html` in place of `__SESSION_TOKEN__`.
- **`server`** (`CASEPILOT_MODE=server`)
  - Binds `0.0.0.0:8080`.
  - Requires `CASEPILOT_API_KEY` of at least 24 characters, or `CASEPILOT_TRUST_PROXY_AUTH=1`; otherwise it exits.
  - Auth comes from Workload Identity if `AZURE_FEDERATED_TOKEN_FILE` is set, otherwise from SP env vars (`AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`).
- `CASEPILOT_PORT` is used only if it is all digits, which ignores the K8s `tcp://` service-link variable.

## HTTP surface
**GET**
- `/healthz` and `/readyz` need no auth and return `{ok:true, mode, auth:AUTH.mode}`.
- Every other GET first checks the Host header. In local mode Host must be exactly `127.0.0.1:<port>` or `localhost:<port>`, otherwise 403 `{"error":"forbidden host"}`.
- `/` serves `index.html` with the token injected.
- Static files: `/js/{core,auth,create,history,detail,ai,main}.js`, `/style.css`, `/logo.png`, `/favicon.ico` (which serves `logo.png`).
- Anything else returns 404 JSON.

Every response sets `Cache-Control: no-store`, `nosniff`, and the CSP `default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; media-src 'self' blob:`.

**POST checks, in order**
1. Host check.
2. Auth:
   - local: header `X-CasePilot-Token == SESSION_TOKEN`, else 403.
   - server: `Authorization: Bearer <api_key>` (constant-time compare), or proxy mode with `X-MS-CLIENT-PRINCIPAL-NAME` present, else 401.
3. Body larger than 16 MiB returns 413. The body is parsed as JSON (empty body becomes `{}`).

**POST responses**
- `/mcp` returns the JSON-RPC response with 200, or an empty 202 for notifications.
- `/api/<action>`:
  - In server mode, actions in `SERVER_BLOCKED` return 403: `login/cli`, `login/device/*`, `login/sp`, `logout`, `quit`.
  - `check_ids(req)` runs before the route lookup. `tenantId`/`subscriptionId` must be 36-character GUID-ish strings; `service`/`name` must match `^[\w.-]+$`. Failure is 400.
  - Unknown action returns 404 `{"error":"unknown api"}`.
  - Success: `{ok:true, data}`.
  - `ApiError`: status `e.status` (forced to 500 if outside 400–599) with `{ok:false, error, detail}`.
  - Any other exception: 500 `{ok:false, error:"Type: msg"}`.

**`/api/` routes**
| action | request | data |
|---|---|---|
| `status` | – | `{mode, cloud, sp_tenant, sp_client_id}` |
| `login/cli` | – | `{user, tenant, cloud}` from `az account show` |
| `login/sp` | `{tenantId, clientId, clientSecret, cloud}` | `{tenant, cloud}`; gets a token immediately to validate |
| `login/device/start` | `{cloud}` | `{userCode, verificationUri, message}` |
| `login/device/poll` | – | `{pending:true}` or `{pending:false, user, tenant, cloud}` |
| `logout` | – | null |
| `quit` | – | calls `os._exit(0)` after 0.3 s |
| `tenants` | – | `[{tenantId, displayName, domain}]` |
| `subscriptions` | `{tenantId}` | `[{subscriptionId, displayName, state}]`, filtered to the tenant, sorted |
| `services` | `{tenantId}` | `[{id, name, displayName, resourceTypes}]` |
| `problems` | `{tenantId, service}` | `[{id, displayName}]` |
| `allResources` | `{tenantId, subscriptionId}` | `[{id, name, type(lower), location, group}]` |
| `resources` | `{tenantId, subscriptionId, resourceTypes[]}` | up to 15 types joined with `resourceType eq` and `or` |
| `create` | `{tenantId, subscriptionId?, title, description, serviceId, problemClassificationId, severity, advancedDiagnosticConsent?, require24X7Response?, problemStartTime?, resourceId?, quota?{region, sku, newLimit}, contact{firstName, lastName, email, phone?, method, timeZone, language, country, cc?}, dryRun?}` | `{name, supportTicketId, status, severity, createdDate, httpStatus, portalUrl}`; with `dryRun:true` it returns `{dryRun, request, scope}` and does not call Azure |
| `tickets` | `{tenantId, subscriptionId?, days}` (1–365) | rows `{name, supportTicketId, title, status, severity, createdDate, serviceDisplayName, supportPlanType, problemClassificationDisplayName}`, newest first |
| `ticket` | `{tenantId, subscriptionId?, name}` | `{properties, communications:[{sender, subject, body, direction, createdDate}]}` |
| `close` | same as `ticket` | `{status}`; PATCH, then read back, up to 4 attempts, else 409 |
| `reply` | `{tenantId, subscriptionId?, name, subject, body, sender?}` | `{name}`; in sp/workload mode `sender` defaults to the case's primary email |
| `ai/draft` | `{text, answers?[{q,a}], tenantId, ai?{...}}` | draft (see AI) |
| `ai/transcribe` | `{audio(b64, ≤14 MiB chars), filename?, mime?, ai?}` | `{text, provider}` |
| `ai/config` | `{ai?}` | `AIConfig.public()`, where the keys are replaced by booleans |
| `plan` | `{tenantId, subscriptionId}` | plan (see below) |

## Auth (`core.Auth`)
`mode` is one of `cli`, `sp`, `device`, `workload`. Tokens are cached per `(tenant or "_default", resource)` and refreshed 300 s before expiry. Clouds supported: AzureCloud, AzureChinaCloud, AzureUSGovernment, each with its own ARM and login hosts.

- **cli**: `az account get-access-token --resource <res>/ [--tenant T] -o json`. Reads `accessToken` and `expires_on` (falls back to now + 1800). The cloud comes from `environmentName`.
- **device code**: POST `{login}/organizations/oauth2/v2.0/devicecode`.
  - client_id `04b07795-...` (the Azure CLI public client; override with `CASEPILOT_CLIENT_ID`).
  - scope `{arm}/user_impersonation offline_access openid profile`.
  - Polling uses `grant_type=device_code`; `authorization_pending` and `slow_down` map to `{pending:true}`.
  - After login it uses refresh tokens per tenant: `{login}/{tenant}/oauth2/v2.0/token`, scope `{res}/user_impersonation offline_access`.
- **sp (App Registration)**: `client_credentials` with `scope {res}/.default`. Only the SP's own tenant is allowed; any other tenant returns 400.
- **workload**: a `jwt-bearer` client assertion read from `AZURE_FEDERATED_TOKEN_FILE`, sent to `AZURE_AUTHORITY_HOST`.
- The AI/STT resource is `https://cognitiveservices.azure.com`.

## Support / ARM calls (`SUPPORT_API = "2024-04-01"`)
The base path is `/subscriptions/{sub}/providers/Microsoft.Support` when a subscription is given, otherwise `/providers/Microsoft.Support` (tenant level).

- `GET /providers/Microsoft.Support/services` and `.../services/{svc}/problemClassifications`, cached 6 hours per cloud.
- Create:
  1. `POST {base}/checkNameAvailability` with `{name:"casepilot-<12hex>", type}`.
  2. `PUT {base}/supportTickets/{name}`.
  3. Poll the `Azure-AsyncOperation` header every 3 s for up to 120 s.
  4. `GET` the ticket.
- Quota cases send `quotaTicketDetails` with payload `{"VMFamily", "NewLimit"}`.
- List: `GET {base}/supportTickets?$top=100&$filter=createdDate ge <date>`, following `nextLink`.
- Detail: `GET` the ticket and `GET .../communications`. If the communications call fails, a placeholder row is returned instead.
- Close: `PATCH {status:"closed"}`.
- Reply: `PUT .../communications/cp-<12hex>` with `{subject, body, sender?}`.
- Other ARM calls:
  - `/tenants` and `/subscriptions` (2022-12-01)
  - `/subscriptions/{sub}/resources` (2021-04-01)
  - `GET /subscriptions/{sub}` (2022-12-01), for `subscriptionPolicies.quotaId`
- Every call sends an `x-ms-client-request-id`. Errors include `{requestId, url, details}`.

## AI / STT
- **`AIConfig` defaults**:
  - chat endpoint `https://westus3-azureai-service-507154.openai.azure.com`, deployment `gpt-6-luna`, `reasoning_effort=none`
  - STT `azure-openai`, `gpt-4o-mini-transcribe`, `zh-CN`
- **Environment overrides**: `CASEPILOT_AOAI_ENDPOINT`, `_AOAI_DEPLOYMENT`, `_REASONING`, `_STT_PROVIDER`, `_STT_ENDPOINT`, `_STT_DEPLOYMENT`, `_STT_LANGUAGE`, `_AOAI_KEY`, `_STT_KEY`, `_SPEECH_REGION`.
- **Per-request override** via `req.ai`. Only known string fields are accepted. In server mode `api_key` and `stt_key` are dropped from the request.
- **Chat**: `POST {ep}/openai/v1/chat/completions` with `{model, messages, reasoning_effort, response_format: json_schema strict}`.
  - Auth is the `api-key` header if a key is set, otherwise an Entra Bearer token.
  - On 429 it waits per `Retry-After` (at most 15 s) and retries, 3 attempts total. Final error is 502.
- **Draft (`ai.draft_case`)** makes two calls.
  1. Service call: builds a shortlist of at most 60 services (keyword/synonym hits plus `POPULAR`) and returns `{service, severity, title, description, questions}`.
  2. Problem call: ranks problem types by token overlap, keeps at most 40, and returns `{problemId}`.
  - Both picks must match a real catalog entry by `name`/`id` or `displayName`; otherwise the result is null.
  - At most 3 questions, and only in the first round.
  - Severity outside the allowed values falls back to `moderate`.
  - Title is cut to 200 characters.
  - Output: `{service, problem, severity, title, description, questions, ready}`.
- **STT (azure-openai)**: `{ep}/openai/deployments/{dep}/audio/transcriptions?api-version=2025-04-01-preview`, multipart part `file` plus `language=zh`.
- **STT (azure-speech)**: `{speech_ep}/speechtotext/transcriptions:transcribe?api-version=2024-11-15`, part `audio` plus `definition={"locales":[..]}`. Key header `Ocp-Apim-Subscription-Key`. The transcript is the concatenated `combinedPhrases[].text`.

## Plan inference (`plan.infer`)
1. If the newest ticket in the last 365 days has `supportPlanType`, the tier is matched by substring: unified, premier, prodirect, internal, standard, or developer-or-basic. `allowsCritical` is true for the high tiers. Source `ticket`.
2. Otherwise, if `quotaId` starts with MSDN, FreeTrial, AzurePass, Sponsored, Students, DreamSpark or AzureForStudents, the result is developer-or-basic with `allowsCritical:false`. Source `offer`.
3. Otherwise `{plan:null, tier:"unknown", source:"none"}`.

## MCP
- Protocol `2025-06-18`. Methods: `initialize`, `ping`, `tools/list`, `tools/call`. Notifications (no `id`) get no response. Unknown method is -32601, parse error -32700, internal error -32603.
- **Tools**:
  - `list_services(query)`
  - `list_problem_types(service)`
  - `draft_case(text, answers)`
  - `create_case(...)`: needs `confirm:true`; `DEFAULT_CONTACT` is zh-hans / CHN / China Standard Time
  - `list_cases(days)`
  - `get_case(name)`
  - `add_case_message`: needs `confirm:true`; HTML-escapes the message and converts `\n` to `<br>`
  - `close_case`: needs `confirm:true`
  - `get_support_plan`
- **Scope defaults**:
  - HTTP: `CASEPILOT_TENANT_ID` or `AZURE_TENANT_ID`, plus `CASEPILOT_SUBSCRIPTION_ID`.
  - stdio: environment variables, else `az account show`.
- Results are returned as `content[text]` plus `structuredContent` (lists are wrapped as `{items}`). `ToolError` and `ApiError` become `isError:true`.

## Tests (74, all `unittest` + `mock`)
| File | Tests | Covers |
|---|---|---|
| `test_ai` | 15 | catalog grounding, shortlists, strict schema |
| `test_core_auth` | 4 | cli/sp per-resource token, workload |
| `test_http` | 12 | a real server on port 0 in local and server mode: token, Host check, bearer, `/mcp`, blocked routes, env |
| `test_mcp` | 10 | protocol handling, the confirm gate |
| `test_plan` | 5 | plan inference |
| `test_providers` | 9 | AI config, URLs, headers, multipart |
| `test_server` | 10 | `build_ticket`, validate, sender, `check_ids` |
| `test_service` | 9 | 429 retry, key stripping in server mode |

No test runs real `az` or reaches the network.

## Windows / POSIX / macOS assumptions
1. **az output encoding (the main risk).** `_run_az` runs `subprocess.run(..., text=True)` with the locale encoding, which is `cp1252` on this machine. az writes UTF-8 JSON, so Chinese or other non-ASCII tenant or user names will be garbled or raise `UnicodeDecodeError`. The fix is `encoding="utf-8"` (optionally with `PYTHONIOENCODING=utf-8` in the env).
2. **`az` resolves to a batch file.** `shutil.which("az")` returns `C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.CMD`. CreateProcess runs `.cmd` files through cmd.exe implicitly, so it works, but cmd metacharacters in arguments would be interpreted; tenant ids are GUID-validated. Each call starts a console window when run from a GUI or no-console host; it needs `creationflags=CREATE_NO_WINDOW`. The 60 s timeout does not kill the child process tree.
3. **Port fallback never falls back on Windows.** `ThreadingHTTPServer.allow_reuse_address` is True. On Windows `SO_REUSEADDR` lets a second instance bind a port that is already in use, so the "try the next of 20 ports" loop won't move to the next port, and the two instances can steal each other's connections. Windows needs `SO_EXCLUSIVEADDRUSE` or `allow_reuse_address=False`.
4. **PATH append is macOS-specific.** `core.py` adds `/opt/homebrew/bin` and `/usr/local/bin` to PATH. On Windows this is harmless but useless; a GUI host may need `...\CLI2\wbin` added instead.
5. **SSL root certificates.** The `security find-certificate` keychain fallback only runs on darwin. On Windows `ssl.create_default_context()` loads the Windows system store, which is fine. The temp PEM in `tempfile.gettempdir()` is written without an explicit encoding.
6. **Missing explicit encodings.** `Path(AZURE_FEDERATED_TOKEN_FILE).read_text()` has no encoding (Linux/K8s only, low risk). `index.html` is read with `encoding="utf-8"`. Static files are served as bytes.
7. **No signal handling.** Shutdown is only `KeyboardInterrupt` around `serve_forever`, and `/api/quit` calls `os._exit(0)`. A Windows native host must kill the child process or call `/api/quit`; there is no SIGTERM handling and no graceful shutdown.
8. **No server-side key storage.** There is no keychain code in Python. In local mode the AI/STT keys come in per request (`req.ai`). The README says the macOS app stores keys in the keychain, but that is outside the scanned files. A Windows app would need its own store (Credential Manager or DPAPI).
9. **Launch script.** `start.bat` uses `py -3`, then `python`. This machine has neither on PATH; only the az-bundled `python.exe` exists. The script also contains Chinese text under `@echo off` without `chcp 65001`, so that text will be garbled.
10. **stdio MCP.** `sys.stdin`/`sys.stdout` use the console code page on Windows, and responses are written with `ensure_ascii=False`. Non-ASCII output can raise `UnicodeEncodeError` unless the host sets `PYTHONUTF8=1` or the code reconfigures stdout to UTF-8.
11. **Host check needs the exact port.** Local mode only accepts `127.0.0.1:<port>` and `localhost:<port>`. A WebView2 host must navigate to exactly that origin (not `[::1]` or a custom virtual host) and send `X-CasePilot-Token`, which can be scraped from the served HTML.
12. **Browser and recording.** `webbrowser.open` works on Windows. The frontend records `audio.webm`, which needs MediaRecorder; WebView2 supports it.

Files: `C:\Users\zhengyuren\ClaudeCode\Azure_CasePilot\server.py`, `C:\Users\zhengyuren\ClaudeCode\Azure_CasePilot\casepilot\core.py`, `C:\Users\zhengyuren\ClaudeCode\Azure_CasePilot\casepilot\service.py`, `C:\Users\zhengyuren\ClaudeCode\Azure_CasePilot\casepilot\providers.py`, `C:\Users\zhengyuren\ClaudeCode\Azure_CasePilot\casepilot\ai.py`, `C:\Users\zhengyuren\ClaudeCode\Azure_CasePilot\casepilot\plan.py`, `C:\Users\zhengyuren\ClaudeCode\Azure_CasePilot\casepilot\mcp.py`, `C:\Users\zhengyuren\ClaudeCode\Azure_CasePilot\start.bat`, `C:\Users\zhengyuren\ClaudeCode\Azure_CasePilot\tests\`.

---

# CasePilot macOS native app: technical map for a Windows port

Source: `C:\Users\zhengyuren\ClaudeCode\Azure_CasePilot\macos\Sources\CasePilot\*.swift` (15 files) and `macos\Tests\CasePilotTests\*.swift` (2 files), about 2,570 lines in total. Built with SwiftUI plus `@Observable` and Swift concurrency.

## Architecture
- **The app does not use Python.** It is pure Swift and calls the ARM / Microsoft.Support REST API directly with `URLSession`, using **api-version 2024-04-01**. Tenants and subscriptions use `2022-12-01`; resources use `2021-04-01`.
- The `az` CLI is used only as a token source in CLI mode, by running `az account show` and `az account get-access-token` as a subprocess.
- `AppStore` (MainActor, observable) holds identity, tenants, subscriptions, services, plan, tickets, contact and recent problems. `SupportClient` and `AIClient` are built from the shared `AuthProvider` actor.
- The UI language is Simplified Chinese. The app is light-mode only. Closing the last window quits it.

## Models (`Models.swift`)
- `AzureCloud`: global / china / usgov. Each has ARM, login and portal URLs (management.azure.com / chinacloudapi.cn / usgovcloudapi.net; login.microsoftonline.com / .chinacloudapi.cn / .us).
- Simple records: `Tenant{id,name,domain}`, `Subscription{id,name,state}`, `SupportService{id(full ARM id),name(GUID),displayName,resourceTypes}`, `ProblemClassification{id,displayName}`, `AzureResource{id,name,type(lowercased),location}`.
- `Severity`:
  - Values: minimal / moderate / critical / highestcriticalimpact, with levels C / B / A / A+.
  - Each has a Chinese title and detail text.
  - `isHigh` is true for critical and above.
  - It can be parsed case-insensitively from the API value.
- `CaseKind`: technical / quota / billing. The kind is inferred from the service: quota if the displayName contains "service and subscription limits", billing if it equals "billing".
- `Contact` (Codable):
  - Fields: first, last, email, cc (comma list), phone, preferPhone.
  - Defaults: language `zh-hans`, country `CHN`, time zone `China Standard Time`.
  - Has static option lists: 5 languages, 11 countries, 13 Windows time-zone IDs.
- `QuotaRequest{region,vmFamily,newLimit}`.
- `NewCase{service,problem,resourceId,title,description,severity=.moderate,require24x7,consent=true,startTime,quota,contact}`.
- `TicketSummary{id(ARM name),ticketNumber,title,status,severity,created,service,problem,plan}` with `isOpen` meaning status is "open".
- `Communication{id,sender,subject,body(plain text),fromMicrosoft(direction=="Outbound"),created}`.
- `TicketDetail{summary,props(raw dict),communications}`.
- `RecentProblem` (Codable; at most 6 kept in UserDefaults).

## Authentication and tokens (`Auth.swift`, actor `AuthProvider`)
There are three modes, held as `Mode {none, cli, device, sp}`:
1. **Azure CLI**:
   - Looks for `az` on PATH plus Homebrew paths. On Windows this must find `az.cmd`.
   - Runs `az account show -o json` to get the cloud (`environmentName`), user and tenant.
   - Tokens come from `az account get-access-token --resource <res>/ [--tenant t]`, reading `accessToken` and `expires_on`.
2. **Device code (browser login)**:
   - Uses the public client id `04b07795-8ddb-461a-bbee-02f9e1bf7b46` (the Azure CLI app).
   - POSTs to `{login}/organizations/oauth2/v2.0/devicecode` with scope `{arm}/user_impersonation offline_access openid profile`.
   - Polls `/token` with the device_code grant and ignores `authorization_pending` / `slow_down`.
   - Reads tid and upn from JWT claims and keeps the refresh token in memory only.
   - For other tenants or resources it uses the refresh_token grant against `{login}/{tid}/oauth2/v2.0/token`, scope `{res}/user_impersonation offline_access`, and rotates the refresh token.
3. **App Registration (service principal)**:
   - Uses the client_credentials grant with scope `{res}/.default`.
   - It is limited to its own tenant: asking for any other tenant raises an error.
   - It is validated by fetching a token at login.

Token handling:
- Cache key is `tenant|resource`. A cached token is reused while it has more than 300 s left.
- The default resource is ARM. AI uses `https://cognitiveservices.azure.com`.
- `postForm` encodes `+` as `%2B`.
- `jwtClaims` does a base64url decode.
- Errors use `AppError{message, requestId}`.

## Key storage (`Keychain.swift`)
- **SPStore**:
  - Tenant, clientId and cloud go to UserDefaults key `sp`.
  - The secret goes to the Keychain generic password with service `com.zenorewn.casepilot.sp` and account = clientId.
  - Provides save, load and clear.
- **SecretStore**:
  - Keychain service `com.zenorewn.casepilot.ai`, accounts `aoai-key` and `stt-key`.
  - Setting an empty value deletes the entry.
- Windows equivalents: Credential Manager (`CredWrite` / `PasswordVault`) or DPAPI.
- Other UserDefaults keys:
  - `aiConfig` (JSON), `contact` (JSON), `recentProblems`, `recentSvc` (max 4)
  - `tenant`, `sub.<tenantId>`, `settingsTab`

## REST surface (`SupportClient.swift`)
- **Request handling**: every call sends Bearer, `Content-Type: application/json` and `x-ms-client-request-id`. Errors are parsed from `error.code/message` and include `x-ms-request-id`. `list` follows `nextLink`.
- **Base path**: `/subscriptions/{sub}/providers/Microsoft.Support` when a subscription is chosen, otherwise tenant-level `/providers/Microsoft.Support`.
- **Scope lookups**:
  - `tenants`: `GET /tenants`.
  - `subscriptions`: filtered to the tenantId and sorted by name.
  - `quotaId`: from `GET /subscriptions/{s}` → `subscriptionPolicies.quotaId`.
- **Catalog lookups**:
  - `services`: `/providers/Microsoft.Support/services`, sorted.
  - `problems`: `services/{name}/problemClassifications`.
  - `resources`: `/subscriptions/{s}/resources` with `$filter` of up to 15 `resourceType eq` clauses OR'ed together.
- **`buildBody(NewCase)`** (static, unit-tested) produces `{properties:{...}}` containing:
  - title and description (trimmed), serviceId, problemClassificationId, severity
  - `require24X7Response = severity.isHigh && require24x7`
  - `advancedDiagnosticConsent` "Yes" or "No"
  - contactDetails: firstName, lastName, primaryEmailAddress, preferredContactMethod (email or phone), preferredTimeZone, preferredSupportLanguage, country, optional phoneNumber, and additionalEmailAddresses from the split cc
  - optional `problemStartTime` (ISO8601)
  - `technicalTicketDetails.resourceId`
  - `quotaTicketDetails{quotaChangeRequestVersion:"1.0", quotaChangeRequests:[{region, payload: JSON string with sorted keys {"NewLimit":int,"VMFamily":..}}]}`
- **create**:
  - Sends `PUT supportTickets/casepilot-<12hex>`.
  - If an `Azure-AsyncOperation` header comes back, it polls up to 40 times, 3 s apart, until succeeded, failed or canceled, then GETs the ticket.
- **tickets(days)**: `$top=100&$filter=createdDate ge <iso>`, sorted newest first.
- **detail**: GETs the ticket plus `/communications`. Bodies go through `HTMLText.plain`, which strips HTML, scripts and styles, decodes entities and collapses blank lines. The app never renders raw HTML.
- **reply**:
  - Sends `PUT communications/cp-<12hex>`.
  - Subject is "客户补充信息 / Update from customer".
  - The body is HTML-escaped and newlines become `<br>`. A sender can optionally be set.
- **close**:
  - Sends `PATCH {status:"closed"}` up to 4 times. "redundant action" and "currently Closed" errors are ignored.
  - After each PATCH it re-GETs the ticket to confirm the status is closed. If it never is, it raises an error.
- **Portal deep link**: `{portal}/#view/Microsoft_Azure_Support/SupportRequestDetails.ReactView/id/<url-encoded resource id>`.

## Support-plan inference (`AI.swift` `SupportPlan`, mirrors `casepilot/plan.py`)
There is no API for this, so the plan is inferred in order:
1. Take `supportPlanType` from the newest ticket in the last 365 days and map it to a tier: unified, premier, prodirect, internal, standard, or developer-or-basic.
2. Otherwise, if the quotaId starts with MSDN, FreeTrial, AzurePass, Sponsored, Students, DreamSpark or AzureForStudents, report Developer/Basic with no critical severity allowed.
3. Otherwise report unknown.

`allowsCritical` is true only for the high tiers. The UI shows where the inference came from.

## Screens
1. **Login** (shown when there is no identity):
   - A segmented picker switches between Azure CLI, browser login and App Registration.
   - CLI login is tried silently on launch.
   - Device-code login shows the code in a large monospaced font, copies it automatically and shows the verification link.
   - The SP form has Tenant, Client and a secure Secret field, with a hint that the SP needs the Support Request Contributor and Reader roles.
2. **Main window**: a `NavigationSplitView`.
   - **Sidebar**, "工作台" section: 新建 Case, and 我的 Case with an open-count badge.
   - **Sidebar**, "设置" section: links to AI 与语音, 联系人 and MCP.
   - **Identity card** at the bottom shows user, method and plan. Clicking it opens a menu that switches tenant, switches subscription (including "无·租户级"), switches auth method (CLI / browser / saved SP / new SP), or logs out.
   - **Toolbar** has a tenant picker and a subscription picker.
   - **Menu commands**: Ctrl+N new case, Ctrl+R refresh.
   - Errors from `store.banner` appear as an alert.
   - **Load chain**: after login, load tenants (falling back to the domain as the name), pick the saved or home tenant, load subscriptions (restoring the saved per-tenant choice), load services, then tickets and the plan.
3. **Create Case** (a Form):
   - **AIComposer** sits at the top (see the next section).
   - **Service**: a searchable popover where terms are AND'ed and Enter picks the first match. There is a "从资源选择" button, quick chips (Quota, Billing, then recent and popular services, 6 at most) and reuse of recent problems.
   - **Resource picker sheet**: searches name, type and id across up to N resources, maps resource type to services, and ranks them with `rank()` (a leaf-name match scores -100, then fewer resourceTypes ranks higher).
   - **Problem picker**: loaded after a service is chosen. Quota cases auto-pick "compute-vm (cores-vcpus)".
   - **Severity**: shows the level, title and detail. Details for VM quota requests (region, family, limit) are required when that applies.
   - **Title and description**: a TextEditor with a mic button and an "插入模板" button that inserts 【现象】【业务影响】【首次发生时间】【复现步骤】【错误信息/Request ID】【近期变更】【已尝试的排查】.
   - **Resource and options**: resource picker (technical cases only), optional start time, 24x7 toggle (enabled only for high severity), and diagnostics consent.
   - **Contact**: a summary line with a link to edit it in Settings.
   - **Checklist that gates submit**: problem type, quota details, title of at least 5 characters, description of at least 20 characters, and a valid contact.
   - **Submit**: "预览请求体" shows the pretty-printed JSON. A confirm alert summarises the case and warns if the severity exceeds what the plan allows. After a successful submit the problem is remembered, the form fields are cleared and tickets are reloaded.
4. **History**:
   - A table with columns Case#, title plus service·problem, status and severity, and created time (relative).
   - A context menu copies the Case number or opens the case in the portal.
   - Search covers number, title and service. Filters: status (全部 / 处理中 / 已关闭, with counts) and time range (7, 30, 90 or 365 days). There is a refresh button.
   - **Detail pane**:
     - Header: number (copyable), portal link, and a close-case button that asks for confirmation.
     - Tabs: 概览 (overview) and 沟通记录 (communications).
     - Overview shows service, problem, created and modified dates, SLA (expirationTime and slaMinutes), plan, engineer email, 24x7, contact, language and time zone, resource and quota.
     - Communications are listed newest first.
     - The reply box has a mic button and a send action.
5. **Settings** (a separate window with tabs):
   - **AI 与语音**:
     - Chat: Endpoint, Deployment, API Key. An empty key means Entra ID is used.
     - Speech-to-text: provider (Azure OpenAI `gpt-4o-mini-transcribe`, or Azure Speech fast transcription), region, endpoint, deployment, key, language.
     - A reset-to-defaults button.
   - **联系人**: last and first name, email, cc, preferred method, phone (required if phone is preferred), language, country, time zone. Edits save immediately.
   - **MCP**:
     - A copyable `mcpServers` JSON snippet for the local stdio server (`python -m casepilot.mcp`, reusing the az login).
     - Notes that write operations need `confirm=true`.
     - Shows the server endpoint `POST https://<domain>/mcp` with a Bearer API key.

## AI composer (`AI.swift`, `AIClient.swift`, `AIComposer.swift`)
- **`AIConfig` defaults**:
  - chat `https://westus3-azureai-service-507154.openai.azure.com` with deployment `gpt-6-luna`
  - STT on the same endpoint with deployment `gpt-4o-mini-transcribe`, language `zh-CN`
  - The custom decoder fills in defaults for missing fields so older saved settings still load.
- **Auth headers**: with a key, chat uses `api-key`; STT uses `api-key`, or `Ocp-Apim-Subscription-Key` for Speech. Without a key, it sends a Bearer token for the cognitive-services resource.
- **Chat**:
  - Calls `POST {endpoint}/openai/v1/chat/completions` with `{model, messages, reasoning_effort:"none", response_format: json_schema strict}`.
  - It parses `choices[0].message.content` as JSON.
  - On 429 it retries up to 2 times, waiting Retry-After seconds (default 3, capped at 15).
  - Errors 401/403 and 429 come with hints.
- **Speech-to-text**:
  - Azure OpenAI: `{ep}/openai/deployments/{dep}/audio/transcriptions?api-version=2025-04-01-preview`, multipart with `file` and a 2-letter `language`.
  - Azure Speech: `{ep or https://{region}.api.cognitive.microsoft.com}/speechtotext/transcriptions:transcribe?api-version=2024-11-15`, multipart with `audio` and `definition={"locales":[..]}`, result joined from `combinedPhrases[].text`.
  - Recording is m4a AAC, 16 kHz mono, into a temp file. The mic button toggles start and stop.
- **`AIDrafter.draft`** (same rules as `casepilot/ai.py`):
  1. Build the service shortlist: keyword hits from the text, Chinese and English synonyms (e.g. 虚拟机/vm/ssh→virtual machine, aks/k8s→kubernetes, 配额→quota, 账单→billing), then a set of popular services. Capped at 60.
  2. First chat call: a Chinese system prompt with the schema `{service, severity enum, title, description(【现象】【业务影响】【首次发生时间】【错误信息/Request ID】【已尝试的排查】, unknown items written as 待补充), questions[]}`.
  3. Map the returned service by name or displayName. A hallucinated value maps to nil.
  4. Second chat call: pick a problem id from a problem shortlist ranked by word overlap, capped at 40.
  5. Questions are capped at 3 and asked for one round only: once answers are given, questions come back empty.
  6. Severity defaults to moderate. The title is capped at 200 characters.
  7. `ready` means a service and problem were found and there are no questions left.
- **UI flow**:
  - The user enters a short description (with mic) and clicks "生成草稿".
  - If questions come back, each gets an answer field with a mic; the user clicks "补充并更新" or "跳过".
  - `applyDraft` then fills service, problem, severity, 24x7 (set when severity is high), title and description.
  - AI errors show a button that opens the AI settings.

## Self-test (`SelfTest.swift`, DEBUG builds only; runs 1 s after launch based on environment variables)
- `CASEPILOT_SELFTEST=1`: real end-to-end run. CLI login → creates a "[CasePilot 工具测试, 请直接关闭]" case (minimal severity, VM Linux / RDP-SSH, first resource) → replies → reads the communications → closes → verifies it is closed. **It creates a real case, so it must not be run here.**
- `CASEPILOT_SP_TEST=<file>`: reads tenant, client and secret lines; logs in as the SP; checks that SPStore round-trips; then runs create and close.
- `CASEPILOT_AI_TEST`, `CASEPILOT_MENU_TEST`, `CASEPILOT_SWITCH_TEST`: AI drafting, a synthetic click test that the identity-card menu opens, and auth switching.
- `CASEPILOT_SNAPSHOT=<dir>`: renders UI screenshots.
- Output is printed as `[selftest]` / `[sptest]` log lines, then the app exits.

## Unit tests (XCTest, offline)
- **`CasePilotTests.swift`**:
  - Contact defaults are zh-hans / CHN / China Standard Time.
  - The quota payload is exactly `{"NewLimit":100,"VMFamily":"DSv5 Series"}`.
  - 24x7 is sent only for high severity.
  - cc becomes additionalEmailAddresses, and the resource goes to technicalTicketDetails.
  - `HTMLText.plain` strips tags and scripts (expected output `"你好 Zheng\n\nA & B"`).
  - `ResourcePicker.rank` prefers "Virtual Machine running Linux" over a broader service.
  - `jwtClaims` returns `tid`.
- **`AIDraftTests.swift`** (chat is stubbed with a closure):
  - Services map by name or displayName, and the result is ready.
  - A hallucinated service maps to nil and the draft is not ready.
  - Questions are capped at 3 with only one round, and severity falls back to moderate.
  - The service shortlist keeps hits and popular services and stays at 60 or fewer.
  - The problem shortlist stays at 40 or fewer, and the ssh problem ranks first.
  - AIConfig defaults are correct.
  - Header routing works, including the Speech region URL.
  - Old saved settings decode with defaults.
  - Plan tiers map correctly, and the MSDN quotaId gives `allowsCritical=false`.

## Notes for the Windows port
- **Pure logic to port first, test-first**: `buildBody`, `HTMLText.plain`, `jwtClaims`, `rank`, `shortlist` / `problemShortlist`, `AIDrafter` (with an injectable chat function), `SupportPlan`, `AIConfig.authHeader` / `sttURL` / tolerant decoding, and `Severity` / `CaseKind`.
- **Platform swaps**:

| macOS | Windows |
|---|---|
| `az` lookup | `az.cmd` on PATH |
| Keychain | Credential Manager |
| UserDefaults | JSON in `%LOCALAPPDATA%` |
| AVAudioRecorder | NAudio/WinRT `MediaCapture` (wav/m4a) |
| NSPasteboard / NSWorkspace | Clipboard / `Process.Start` |

---

# Azure CasePilot: UI map of static/ and docs/

## Files
- **static/** (plain web, no build step, no framework): `index.html` (299 lines, SVG icon sprite `#i-*`, three `<section>` views, drawer, `#modal-root`, `#toast`), `style.css` (304 lines, design tokens), `logo.png`, `js/` (`core.js` 102, `auth.js` 78, `create.js` 316, `ai.js` 119, `detail.js` 107, `history.js` 41, `main.js` 6). The scripts are classic `"use strict"` scripts that share globals, not ES modules.
- **docs/**: `UI_DESIGN_SPEC.md` (native macOS SwiftUI spec), `UI_UX_AUDIT.md` (v2.0.0 baseline, 7 issues), `UI_OPTIMIZATION_CHANGELOG.md` (v2.1.0 to v2.3.0), `UI_VERIFICATION.md`. The screenshots are `docs/screenshots/{my-cases,new-case}.png` and `docs/ui-review/after/{create,detail,history}.png`, all 2560x1640 (1280x820 @2x), light theme, rendered off-screen. The `before/` folder the docs refer to is not in the repo.
- All screenshots show the **native macOS app**, not the web UI.

## Web client (static/)
**Transport:** `core.js` `api(action, body)` sends `POST /api/<action>` with JSON and the header `X-CasePilot-Token: TOKEN`. Every response is `{ok, data | error, detail}`. When `ok` is false it throws an `Error` that carries `.detail`.

**Actions used:** `login/cli`, `login/device/start`, `login/device/poll`, `login/sp`, `logout`, `quit`, `tenants`, `subscriptions`, `services`, `problems`, `resources`, `plan`, `create`, `tickets`, `ticket`, `all`, `reply`, `close`, `ai/config`, `ai/draft`, `ai/transcribe`.

**Other client behaviour:**
- `PORTAL` maps each cloud to its portal URL: AzureCloud `portal.azure.com`, AzureChinaCloud `.cn`, AzureUSGovernment `.us`.
- Helpers: `h()` builds DOM nodes, `icon(name)` returns `<svg><use href="#i-name">`, `toast()`, and a modal that replaces `confirm` and returns `Promise<boolean>`.
- `localStorage` keys: `cp.sp` (SP tenant, client and cloud; no secret), `cp.ai` (AI settings).
- Startup (`main.js`): restore the SP fields, run `refreshChecklist()`, then `loginCli(true)` to try a silent CLI login.

**Layout:**
- `.shell` is a CSS grid: a 248px sticky white sidebar plus the content column.
- **Sidebar:**
  - Brand mark: 32px, radius 10, blue gradient.
  - Nav: `data-view="create"` (new case) and `history`. Items are 36px tall, radius 10. The active item uses `--accent-50` with `--accent-700` text. Badge counts sit on the right.
  - Footer: identity card (avatar 32px circle, name and meta with ellipsis), logout, quit.
- **Topbar:** sticky, 85% background with `backdrop-filter: blur(10px)`. Holds the tenant and subscription selectors inline, max-width 900.
- **Pages:** max-width 1200, padding 28/32/80, `enter` animation.
  - `view-auth` offers three methods: CLI, device code (cloud, code and link), and service principal (cloud, tenant, client, secret).
  - `view-create` is a stepped card form:
    - AI 帮写 (AI assistant): model button, textarea, generate, Q&A.
    - 问题类别 (problem category): kind, service filter with autocomplete list, chips, problem, resource picker, and "by resource".
    - Quota card: region, SKU, limit.
    - 问题描述 (description): title with counter, template, description, start time, consent.
    - 影响程度 (severity): `.sev` 4-column radio cards plus a 24h toggle.
    - 联系方式 (contact): last and first name, email, method; phone is required only when the method is phone; CC, language, country, timezone.
    - Progress, checklist and summary, then Create and Preview.
  - `view-history`:
    - Header: scope, refresh, stat filters (`st-all`, `st-open`, `st-closed`, `st-crit`).
    - Filters: search, status, days.
    - Table body: `h-body`.
- **Detail drawer:** fixed right at 12px inset, width `min(780px, 100vw-24px)`, with a mask behind it.
  - Header: ID, copy, status and severity badges, Portal link, close case, close (x).
  - Tabs: overview (properties, description) and communications with a count.
  - Footer: reply box and send.

**Design language** (the CSS header says it follows "BoardUI"):
- Semantic tokens: `--neutral-0…950` and `--accent-50…700`, with accent `#2b7bf3`.
- Status pairs: success `#0a7a3d/#e8f7ee`, warning `#8a5300/#fff5de`, error `#c0262d/#fdecec`.
- Font stack: `Inter, -apple-system, Segoe UI, PingFang SC, Microsoft YaHei`. Base size is 14/20.
- Type scale: title-1 24/32 600, title-3 18/26, headline 15/22, body-2 13/18, caption 12/16.
- `.mono` uses `ui-monospace, SF Mono, Menlo`, which has **no Windows fallback** (Consolas or Cascadia).
- Cards: radius 24, 1px border, soft shadow, padding 24. Controls are 40px high. Chips are 28px pills. Badges are 22px pills, optionally with a dot.
- Motion: `--ease-out` cubic(.22,1,.36,1) and `--ease-modal` cubic(.32,.72,0,1). Entrances use fade + scale(.98) + blur(2px). The drawer slides in from 24px. There are also `row-in`, `shimmer` skeleton, `spin` and `pop-in` animations. Pressed state darkens instead of scaling.
- The focus ring is a 3px `color-mix(oklab)` accent at 30%.
- `color-scheme: light` only; dark mode is out of scope by user request. Three `@media` rules provide responsive collapse.
- **Edge/WebView2 risks:** `color-mix(in oklab)`, `:has()`, `backdrop-filter` and `-webkit-font-smoothing`. All are supported in Chromium 111+, so Edge on Windows is fine.

**Keyboard and interaction (web):**
- `Ctrl` or `Cmd` + `Enter` submits AI generate (`ai-text`) and sends a reply (`d-reply`). The hint text reads "⌘ / Ctrl + Enter 发送", so it is already cross-platform.
- `Esc` closes a modal (resolving false), the template picker, and the drawer when no modal is open. It also blurs the service autocomplete.
- In the service autocomplete, Up and Down move the selection and `Enter` picks the item (it dispatches `mousedown`).
- Voice input: each `.mic` button toggles `MediaRecorder` (`audio/webm`, falling back to `audio/mp4`) and posts to `ai/transcribe`. The provider is Azure OpenAI gpt-4o-mini-transcribe or Azure Speech. A toast appears when microphone access is denied. On Windows, mic permission is needed in the browser or WebView2.
- There are no global shortcuts in the web UI (no new, refresh or settings keys).

## Native macOS app (from docs and screenshots)
**Spec direction:** "Native macOS × Quiet Sophistication". System controls come first, with no custom cards or gradients.
- **Layout:** `NavigationSplitView` with a 210–280 sidebar, a content area and an Inspector of 360–640. Forms use `.grouped` style, max width 720, centered.
- **Sidebar groups:**
  - 工作台 (workspace): 新建 Case (new case), 我的 Case (my cases) with a count badge.
  - 设置 (settings): AI 与语音 (AI and voice), 联系人 (contacts), MCP.
  - Identity card at the bottom with a chevron menu showing the account and "Azure CLI · tenant".
- **Typography:** system font. Case numbers and IDs use `.monospaced()` with `.secondary`. Titles use `.medium`.
- **Color:**
  - Status: green = in progress (处理中), secondary grey = closed (已关闭).
  - Severity: red = A/A+, orange = B, grey = C. Each severity pill carries a label, e.g. "C 最小影响" (minimal impact) or "B 中等影响" (moderate impact).
- **Create screen:**
  - AI 帮写 header with a "model · auth method" button (e.g. "gpt-6-luna · 登录身份", i.e. signed-in identity).
  - Placeholder prompt, mic button, and a 生成草稿 (generate draft) button.
  - 服务 (service) group: a "选择服务…" (choose service) button and a resource-picker icon; up to 6 common chips; up to 3 recent items under a small heading.
  - 问题类型 (problem type) picker.
  - 严重性 (severity) segmented control C / B / A / A+, with a caption under it.
  - 描述 (description): 插入模板 (insert template) link, title field, TextEditor with no own background and a mic button.
  - Contacts, then a toolbar Submit (`Cmd+Enter`).
- **History screen:**
  - A segmented filter merges in the counts (e.g. "处理中 1", in progress 1); the separate stat cards were removed.
  - Table columns: Case (monospace, 135–140 wide, `fixedSize`), title with a grey "service · problem" subtitle, severity, status, created (relative time, sortable).
  - Zebra rows, a context menu (copy Case number, open in Portal), and double-click to open the Inspector.
- **Detail Inspector:** Case number on one line; Portal is an icon button with a tooltip.
- **Shortcuts:** `Cmd+N` new, `Cmd+R` refresh, `Cmd+Enter` submit or send, `Cmd+,` settings.
- **Platform-specific pieces:**
  - Keys are entered in a `SecureField` and stored in the Keychain.
  - Liquid Glass (v2.3.0 rule): glass belongs only to the navigation and control layer (toolbar, sidebar, Inspector, rendered by the system on macOS 26). Content stays opaque. Only the login card, which floats over an empty background, uses `glassEffect`, falling back to `regularMaterial`. The identity card uses `.fill.quaternary`.
  - The window is fixed to light appearance.
- **Audit issues and their resolutions:**
  1. Duplicated statistics: counts merged into the segmented filter.
  2. Case number truncated in the table: fixed column width.
  3. Case number wrapping in the detail header: kept on one line, Portal turned into an icon button.
  4. No context menu: added.
  5. Too many shortcut chips: capped at 6 common and 3 recent.
  6. No way to configure AI or Speech keys: added.
  7. Equal visual weight for number and title: case number made monospace and secondary.
- **Verification gaps:** the context menu, double-click, real calls with configured keys, dark mode and the glass effect were never checked. Tests were `swift test` 16/16 and Python `unittest discover -s tests`.

## What this means for a Windows native app
These are the macOS conventions translated to Windows; none of it has been built yet.
- **Structure:** WinUI 3 `NavigationView` (Left), a content `Frame`, and a details pane that does the job of the Inspector.
- **Materials:** Mica on the window backdrop; Acrylic only on flyouts. Content stays on opaque `LayerFillColor` surfaces, which is the same principle as the v2.3.0 glass rule.
- **Shortcuts:** `Ctrl+N`, `Ctrl+R` or `F5`, `Ctrl+Enter`, `Ctrl+,`, and `Esc` to close a pane.
- **Fonts:** Segoe UI Variable plus Microsoft YaHei UI; Cascadia Mono or Consolas for case numbers.
- **Key storage:** Windows Credential Manager or DPAPI in place of the Keychain.
- **Status colors:** reuse the web semantic token values.
- **Backend:** reuse the same `/api/<action>` contract with the token header.