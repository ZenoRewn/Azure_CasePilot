"""AI 起草 Case: 用户随口描述 → 结构化草稿 (服务/问题类型/严重性/标题/描述) + 缺失信息追问.

两步调用, 每步都只能从 Azure 真实目录中选择, 防止模型编造:
  1. 从服务目录选服务, 同时生成标题/描述/严重性/追问
  2. 从该服务的问题类型目录选问题类型
`chat(messages, schema) -> dict` 由调用方注入 (Azure OpenAI / Foundry), 便于测试。
"""
import json

SEVERITIES = ("minimal", "moderate", "critical", "highestcriticalimpact")
MAX_QUESTIONS = 3

SERVICE_SCHEMA = {
    "type": "object",
    "properties": {
        "service": {"type": "string", "description": "服务目录中的 name 字段"},
        "severity": {"type": "string", "enum": list(SEVERITIES)},
        "title": {"type": "string"},
        "description": {"type": "string"},
        "questions": {"type": "array", "items": {"type": "string"}},
    },
    "required": ["service", "severity", "title", "description", "questions"],
    "additionalProperties": False,
}

PROBLEM_SCHEMA = {
    "type": "object",
    "properties": {"problemId": {"type": "string"}},
    "required": ["problemId"],
    "additionalProperties": False,
}

SYSTEM = """你是 Azure 技术支持工单助手。根据用户描述起草一张 Azure Support Case。
规则:
- service 必须是给定服务目录中的 name, 选最贴切的一个。
- severity: minimal=不影响业务, moderate=生产受影响但有变通, critical=生产中断, highestcriticalimpact=关键业务全面中断。拿不准时用 moderate。
- title: 一句话, 包含服务、区域/资源 (若已知) 与核心现象, 中文, 不超过 80 字。
- description 使用以下结构, 只写用户提供或可直接推断的事实, 未知项写"待补充":
  【现象】【业务影响】【首次发生时间 (含时区)】【错误信息 / Request ID】【已尝试的排查】
- questions: 仅当缺少会显著影响排查的关键信息时提问 (最多 3 个, 简短中文, 合并相关项); 信息足够或已有补充问答时返回空数组。
- 不要编造 Request ID、资源名或时间。"""


POPULAR = ("Azure OpenAI", "Microsoft Foundry", "Foundry Tools", "Virtual Machine running Linux", "Virtual Machine running Windows",
           "Kubernetes Service (AKS)", "App Service", "Storage Account Management", "Azure SQL Database",
           "Service and subscription limits (quotas)", "Billing", "Subscription management")
SYNONYMS = {"aoai": "openai", "gpt": "openai", "模型": "openai", "foundry": "foundry", "虚拟机": "virtual machine", "vm": "virtual machine",
            "ssh": "virtual machine", "rdp": "virtual machine", "k8s": "kubernetes", "aks": "kubernetes", "存储": "storage",
            "配额": "quota", "quota": "quota", "账单": "billing", "发票": "billing", "数据库": "sql", "网站": "app service", "web": "app service"}


def shortlist(text, services, limit=60):
    """控制 token: 只把关键词命中与常用服务发给模型 (完整目录 ~350 项会触发 TPM 限流)。"""
    t = text.lower()
    keys = {w for w in t.replace("/", " ").split() if len(w) > 2} | {v for k, v in SYNONYMS.items() if k in t}
    hit = [s for s in services if any(k in s["displayName"].lower() for k in keys)]
    pop = [s for s in services if s["displayName"] in POPULAR]
    seen, out = set(), []
    for s in hit + pop:
        if s["name"] not in seen:
            seen.add(s["name"])
            out.append(s)
    return out[:limit]


def _catalog(services):
    return "\n".join(f"{s['name']}\t{s['displayName']}" for s in services)


def draft_case(text, answers, services, problems_of, chat):
    qa = "\n".join(f"问: {x['q']}\n答: {x['a']}" for x in (answers or []))
    cands = shortlist(text + " " + qa, services)
    user = f"用户描述:\n{text}\n\n" + (f"补充问答:\n{qa}\n\n" if qa else "") + f"服务目录 (name\\tdisplayName):\n{_catalog(cands)}"
    r = chat([{"role": "system", "content": SYSTEM}, {"role": "user", "content": user}], SERVICE_SCHEMA)

    pick = (r.get("service") or "").strip().lower()
    svc = next((s for s in services if pick in (s["name"].lower(), s["displayName"].lower())), None)
    problem = None
    if svc:
        problems = problems_of(svc)
        p = chat([
            {"role": "system", "content": "从问题类型目录中选出最匹配的一项, 返回其 id。"},
            {"role": "user", "content": f"工单: {r.get('title')}\n{r.get('description')}\n\n问题类型目录 (id\\tdisplayName):\n"
                                        + "\n".join(f"{x['id']}\t{x['displayName']}" for x in problems)},
        ], PROBLEM_SCHEMA)
        pid = (p.get("problemId") or "").strip().lower()
        problem = next((x for x in problems if pid in (x["id"].lower(), x["displayName"].lower())), None)

    # 只追问一轮: 用户回答过后, 剩余未知项在描述中标注"待补充"即可, 不再阻塞提交
    questions = [] if answers else [q.strip() for q in (r.get("questions") or []) if q.strip()][:MAX_QUESTIONS]
    sev = r.get("severity") if r.get("severity") in SEVERITIES else "moderate"
    return {
        "service": svc, "problem": problem, "severity": sev,
        "title": (r.get("title") or "").strip()[:200], "description": (r.get("description") or "").strip(),
        "questions": questions,
        "ready": bool(svc and problem and not questions),
    }


def response_format(schema, name="casepilot"):
    return {"type": "json_schema", "json_schema": {"name": name, "strict": True, "schema": schema}}


def parse_content(resp):
    return json.loads(resp["choices"][0]["message"]["content"])
