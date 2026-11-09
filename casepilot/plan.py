"""支持计划识别.

Azure 未提供查询订阅支持计划等级的公开 ARM API (Microsoft.Addons supportPlanTypes 已下线),
因此综合两个信号并标注来源:
  1. ticket: 该订阅最近 Case 的 supportPlanType (最可靠)
  2. offer : 订阅 quotaId 对应的产品类型 (仅能判断 MSDN/免费等无付费支持的订阅)
"""
TIERS = [("unified", "unified"), ("premier", "premier"), ("prodirect", "prodirect"), ("professional direct", "prodirect"),
         ("internal", "internal"), ("standard", "standard"), ("developer", "developer-or-basic"), ("basic", "developer-or-basic")]
HIGH_SEVERITY_TIERS = {"unified", "premier", "prodirect", "internal", "standard"}
NO_PAID_SUPPORT_OFFERS = ("MSDN", "FreeTrial", "AzurePass", "Sponsored", "Students", "DreamSpark", "AzureForStudents")


def tier_of(name):
    n = (name or "").lower()
    return next((t for k, t in TIERS if k in n), "unknown")


def infer(tickets, sub_policies):
    with_plan = sorted((t for t in tickets if t.get("supportPlanType")), key=lambda t: t.get("createdDate") or "", reverse=True)
    if with_plan:
        name = with_plan[0]["supportPlanType"]
        tier = tier_of(name)
        return {"plan": name, "tier": tier, "source": "ticket",
                "allowsCritical": tier in HIGH_SEVERITY_TIERS if tier != "unknown" else None}
    quota = (sub_policies or {}).get("quotaId", "")
    if any(quota.startswith(o) for o in NO_PAID_SUPPORT_OFFERS):
        return {"plan": "Developer / Basic (按订阅类型推断)", "tier": "developer-or-basic", "source": "offer", "allowsCritical": False}
    return {"plan": None, "tier": "unknown", "source": "none", "allowsCritical": None}
