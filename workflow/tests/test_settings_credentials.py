import os
import subprocess
import sys
from pathlib import Path

import pytest
from pydantic import ValidationError

from app.settings import Settings


SAFE_INTERNAL_TOKEN = "production-internal-token-for-tests"
SAFE_CHECKPOINT_KEY = "production-checkpoint-hmac-key-for-tests"
DISTINCTIVE_INTERNAL_SECRET = "sentinel-internal-secret-must-never-appear"
DISTINCTIVE_CHECKPOINT_SECRET = "sentinel-checkpoint-secret-must-never-appear"
INTERNAL_TOKEN_ERROR = (
    "Production INTERNAL_API_TOKEN must be configured with a non-development value"
)
CHECKPOINT_KEY_ERROR = (
    "Production runtime features require a non-development CHECKPOINT_HMAC_KEY"
)
WORKFLOW_ROOT = Path(__file__).resolve().parents[1]


def _startup_environment(**overrides: str) -> dict[str, str]:
    environment = os.environ.copy()
    for name in (
        "APP_ENVIRONMENT",
        "INTERNAL_API_TOKEN",
        "CHECKPOINT_HMAC_KEY",
        "AGENT_TEST_RUN_ENABLED",
        "MULTI_AGENT_DISPATCH_ENABLED",
        "AGENT_WRITE_TOOLS_ENABLED",
    ):
        environment.pop(name, None)
    environment.update(overrides)
    return environment


def test_production_defaults_reject_committed_internal_token(monkeypatch) -> None:
    monkeypatch.delenv("APP_ENVIRONMENT", raising=False)
    monkeypatch.delenv("INTERNAL_API_TOKEN", raising=False)

    with pytest.raises(ValidationError, match=INTERNAL_TOKEN_ERROR) as exc_info:
        Settings(_env_file=None)

    assert "internal-dev-token" not in str(exc_info.value)


def test_production_startup_fails_before_serving_with_committed_token() -> None:
    result = subprocess.run(
        [sys.executable, "-c", "from app.settings import settings"],
        cwd=WORKFLOW_ROOT,
        env=_startup_environment(APP_ENVIRONMENT="Production"),
        capture_output=True,
        text=True,
        check=False,
    )

    assert result.returncode != 0
    assert INTERNAL_TOKEN_ERROR in result.stderr
    assert "internal-dev-token" not in result.stderr


def test_production_startup_accepts_safe_credentials() -> None:
    result = subprocess.run(
        [sys.executable, "-c", "from app.settings import settings"],
        cwd=WORKFLOW_ROOT,
        env=_startup_environment(
            APP_ENVIRONMENT="Production",
            INTERNAL_API_TOKEN=SAFE_INTERNAL_TOKEN,
        ),
        capture_output=True,
        text=True,
        check=False,
    )

    assert result.returncode == 0, result.stderr


@pytest.mark.parametrize(
    ("settings_overrides", "environment_overrides", "secrets"),
    [
        (
            {
                "internal_api_token": "internal-dev-token",
                "checkpoint_hmac_key": DISTINCTIVE_CHECKPOINT_SECRET,
            },
            {
                "INTERNAL_API_TOKEN": "internal-dev-token",
                "CHECKPOINT_HMAC_KEY": DISTINCTIVE_CHECKPOINT_SECRET,
            },
            ("internal-dev-token", DISTINCTIVE_CHECKPOINT_SECRET),
        ),
        (
            {
                "internal_api_token": DISTINCTIVE_INTERNAL_SECRET,
                "checkpoint_hmac_key": "agent-run-checkpoint-dev-key",
                "agent_test_run_enabled": True,
            },
            {
                "INTERNAL_API_TOKEN": DISTINCTIVE_INTERNAL_SECRET,
                "CHECKPOINT_HMAC_KEY": "agent-run-checkpoint-dev-key",
                "AGENT_TEST_RUN_ENABLED": "true",
            },
            (DISTINCTIVE_INTERNAL_SECRET, "agent-run-checkpoint-dev-key"),
        ),
    ],
)
def test_credential_failures_hide_all_input_values(
    settings_overrides: dict[str, object],
    environment_overrides: dict[str, str],
    secrets: tuple[str, str],
) -> None:
    with pytest.raises(ValidationError) as exc_info:
        Settings(
            app_environment="production",
            **settings_overrides,
            _env_file=None,
        )

    validation_message = str(exc_info.value)
    for secret in secrets:
        assert secret not in validation_message

    result = subprocess.run(
        [sys.executable, "-c", "from app.settings import settings"],
        cwd=WORKFLOW_ROOT,
        env=_startup_environment(
            APP_ENVIRONMENT="Production",
            **environment_overrides,
        ),
        capture_output=True,
        text=True,
        check=False,
    )

    assert result.returncode != 0
    for secret in secrets:
        assert secret not in result.stderr


def test_production_accepts_safe_internal_token_without_runtime_features() -> None:
    configured = Settings(
        app_environment="production",
        internal_api_token=SAFE_INTERNAL_TOKEN,
        checkpoint_hmac_key="",
        _env_file=None,
    )

    assert configured.internal_api_token == SAFE_INTERNAL_TOKEN


def test_development_preserves_zero_configuration_defaults() -> None:
    configured = Settings(
        app_environment="Development",
        agent_test_run_enabled=True,
        multi_agent_dispatch_enabled=True,
        agent_write_tools_enabled=True,
        _env_file=None,
    )

    assert configured.app_environment == "development"
    assert configured.internal_api_token == "internal-dev-token"
    assert configured.checkpoint_hmac_key == "agent-run-checkpoint-dev-key"


@pytest.mark.parametrize(
    "feature_flag",
    [
        "agent_test_run_enabled",
        "multi_agent_dispatch_enabled",
        "agent_write_tools_enabled",
    ],
)
@pytest.mark.parametrize("unsafe_key", ["", "agent-run-checkpoint-dev-key"])
def test_production_runtime_features_reject_unsafe_checkpoint_key(
    feature_flag: str,
    unsafe_key: str,
) -> None:
    overrides = {
        "app_environment": "production",
        "internal_api_token": SAFE_INTERNAL_TOKEN,
        "checkpoint_hmac_key": unsafe_key,
        feature_flag: True,
    }

    with pytest.raises(ValidationError, match=CHECKPOINT_KEY_ERROR) as exc_info:
        Settings(**overrides, _env_file=None)

    assert "agent-run-checkpoint-dev-key" not in str(exc_info.value)


@pytest.mark.parametrize(
    "feature_flag",
    [
        "agent_test_run_enabled",
        "multi_agent_dispatch_enabled",
        "agent_write_tools_enabled",
    ],
)
def test_production_runtime_features_accept_safe_checkpoint_key(
    feature_flag: str,
) -> None:
    configured = Settings(
        app_environment="production",
        internal_api_token=SAFE_INTERNAL_TOKEN,
        checkpoint_hmac_key=SAFE_CHECKPOINT_KEY,
        **{feature_flag: True},
        _env_file=None,
    )

    assert configured.checkpoint_hmac_key == SAFE_CHECKPOINT_KEY


@pytest.mark.parametrize("unsafe_token", ["", "internal-dev-token"])
def test_production_internal_token_errors_are_static(unsafe_token: str) -> None:
    with pytest.raises(ValidationError, match=INTERNAL_TOKEN_ERROR) as exc_info:
        Settings(
            app_environment="production",
            internal_api_token=unsafe_token,
            _env_file=None,
        )

    message = str(exc_info.value)
    if unsafe_token:
        assert unsafe_token not in message
    else:
        assert INTERNAL_TOKEN_ERROR in message


def test_delete_retention_startup_rejects_missing_evidence_provider() -> None:
    with pytest.raises(
        ValidationError,
        match="CHECKPOINT_RETENTION_MODE=delete requires a backup/restore evidence provider",
    ):
        Settings(
            app_environment="development",
            checkpoint_retention_mode="delete",
            checkpoint_database_url="postgresql://retention.test/checkpoints",
            _env_file=None,
        )


def test_report_retention_remains_available_without_evidence_provider() -> None:
    configured = Settings(
        app_environment="development",
        checkpoint_retention_mode="report",
        checkpoint_database_url="postgresql://retention.test/checkpoints",
        _env_file=None,
    )

    assert configured.checkpoint_retention_mode == "report"
