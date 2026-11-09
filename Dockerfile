# Azure CasePilot server (Web + MCP over HTTP). 零第三方依赖。
FROM python:3.13-slim
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1 CASEPILOT_MODE=server CASEPILOT_PORT=8080
WORKDIR /app
COPY server.py ./
COPY casepilot ./casepilot
COPY static ./static
RUN useradd -r -u 10001 casepilot
USER 10001
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s CMD python -c "import urllib.request;urllib.request.urlopen('http://127.0.0.1:8080/healthz')"
CMD ["python", "server.py"]
