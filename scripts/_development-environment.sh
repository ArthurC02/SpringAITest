# Local start wrappers opt into Development without modifying infra/.env.
defaulted=""
preserved=""
dot_env="$SCRIPT_DIR/../infra/.env"

if [ -z "${ASPNETCORE_ENVIRONMENT+x}" ]; then
  if [ "${DEVELOPMENT_ENVIRONMENT_IGNORE_DOT_ENV:-false}" = true ] \
    || ! { [ -f "$dot_env" ] && grep -Eq '^[[:space:]]*(export[[:space:]]+)?ASPNETCORE_ENVIRONMENT[[:space:]]*=' "$dot_env"; }; then
    export ASPNETCORE_ENVIRONMENT=Development
    defaulted="ASPNETCORE_ENVIRONMENT"
  else
    preserved="ASPNETCORE_ENVIRONMENT"
  fi
else
  preserved="ASPNETCORE_ENVIRONMENT"
fi

if [ -z "${APP_ENVIRONMENT+x}" ]; then
  if [ "${DEVELOPMENT_ENVIRONMENT_IGNORE_DOT_ENV:-false}" = true ] \
    || ! { [ -f "$dot_env" ] && grep -Eq '^[[:space:]]*(export[[:space:]]+)?APP_ENVIRONMENT[[:space:]]*=' "$dot_env"; }; then
    export APP_ENVIRONMENT=Development
    defaulted="${defaulted:+$defaulted, }APP_ENVIRONMENT"
  else
    preserved="${preserved:+$preserved, }APP_ENVIRONMENT"
  fi
else
  preserved="${preserved:+$preserved, }APP_ENVIRONMENT"
fi

[ -z "$defaulted" ] || echo "  Local development environment defaulted: $defaulted"
[ -z "$preserved" ] || echo "  Explicit process/infra/.env environment preserved: $preserved"
