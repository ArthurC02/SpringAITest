"""Deployment policy for selecting the Skill script execution boundary."""

from app.engine.script_isolation import IsolatedSubprocessRunner
from app.engine.script_runner import DisabledScriptRunner, RestrictedInProcessRunner
from app.settings import settings


def configured_script_runner():
    """Return the only runner permitted by the current deployment settings."""
    if settings.isolated_skill_scripts_enabled:
        return IsolatedSubprocessRunner()
    if settings.app_environment == "development":
        return RestrictedInProcessRunner()
    return DisabledScriptRunner()
