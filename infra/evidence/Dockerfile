FROM python:3.12-slim
WORKDIR /app
COPY evidence_model.py workflow_capture_proxy.py ./
RUN useradd --create-home evidence
USER evidence
EXPOSE 8080 8081
