FROM python:3.12-slim

ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1 AIL_HOME=/app
WORKDIR /app

COPY pyproject.toml README.md LICENSE ./
COPY packages ./packages
COPY services ./services
COPY apps ./apps
COPY docs ./docs
RUN pip install --no-cache-dir .

RUN useradd --create-home ail && mkdir -p /app/data && chown -R ail /app/data
USER ail

EXPOSE 8000
VOLUME ["/app/data"]

# Generates the flagship demo database on first start, then serves the portal on all interfaces.
CMD ["sh", "-c", "[ -f data/demo.db ] || ail demo --db data/demo.db; exec ail serve --db data/demo.db --host 0.0.0.0 --port 8000"]
