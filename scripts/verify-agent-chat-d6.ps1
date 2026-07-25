#!/usr/bin/env pwsh
# D6 two-round verifier.  Deterministic evidence is intentionally separate
# from the RealModel E-04/E-05 gates: mock-gpt/evidence-model cannot prove
# semantic mem0 recall or model-selected skill routing.
[CmdletBinding()]
param(
    [switch]$Build,
    [switch]$KeepEvidenceDb,
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$infra = Join-Path $repo 'infra'
$run = "d6evidence_$([guid]::NewGuid().ToString('N'))"
$script:checks = 0
$evidenceEnvNames = @(
    'EVIDENCE_HMAC_KEY','EVIDENCE_CHECKPOINT_HMAC_KEY','EVIDENCE_DB_NAME',
    'EVIDENCE_AGENT_TEST_RUN_ENABLED','WORKFLOW_DESIGNER_ENABLED',
    'MULTI_AGENT_DISPATCH_ENABLED','AGENT_CHAT_ENABLED',
    'AGENT_CHAT_TENANT_ALLOWLIST','EVIDENCE_PLATFORM_BACKEND_BASE_URL',
    'EVIDENCE_WORKFLOW_UPSTREAM_URL','EVIDENCE_PLATFORM_RABBITMQ_URL',
    'EVIDENCE_BACKEND_EMBEDDINGS_PROVIDER','EVIDENCE_WORKFLOW_LLM_BASE_URL',
    'EVIDENCE_WORKFLOW_LLM_API_KEY','EVIDENCE_REAL_WORKFLOW_MODEL'
)
$priorEvidenceEnv = @{}
foreach ($name in $evidenceEnvNames) {
    $item = Get-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
    $priorEvidenceEnv[$name] = @{ Exists=($null -ne $item); Value=if ($null -eq $item) { $null } else { $item.Value } }
}
$evidenceServices = @('backend-evidence','workflow-evidence','rabbitmq-evidence','evidence-model','workflow-capture-proxy','platform-evidence','evidence-nginx')
$evidenceStarted = $false

function Pass([string]$Name) { $script:checks++; Write-Host "PASS $Name" }
function Require([bool]$Value, [string]$Message) { if (-not $Value) { throw $Message } }
function Wait-Http([string]$Url, [string]$Name) {
    $until = (Get-Date).AddSeconds($TimeoutSeconds)
    do { try { if ((Invoke-WebRequest -UseBasicParsing -Uri $Url -TimeoutSec 4).StatusCode -eq 200) { return } } catch {}; Start-Sleep -Milliseconds 500 } while ((Get-Date) -lt $until)
    throw "$Name was not healthy before timeout"
}
function Invoke-Status([string]$Method, [string]$Url, [object]$Body = $null, [hashtable]$Headers = @{}) {
    $p = @{ Method=$Method; Uri=$Url; Headers=$Headers; UseBasicParsing=$true }
    if ($null -ne $Body) { $p.ContentType='application/json'; $p.Body=($Body | ConvertTo-Json -Depth 20 -Compress) }
    try { Invoke-WebRequest @p } catch { if ($_.Exception.Response) { $_.Exception.Response } else { throw } }
}
function Invoke-Dotnet([string]$Name, [string]$Directory, [string[]]$CommandArgs) {
    Push-Location $Directory
    try { & dotnet @CommandArgs; if ($LASTEXITCODE -ne 0) { throw "$Name failed" } } finally { Pop-Location }
    Pass $Name
}
function Invoke-Pytest([string]$Name, [string[]]$CommandArgs) {
    Push-Location (Join-Path $repo 'workflow')
    try { & uv run pytest @CommandArgs; if ($LASTEXITCODE -ne 0) { throw "$Name failed" } } finally { Pop-Location }
    Pass $Name
}
function Get-Sha256([string]$Text) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text))).Replace('-', '')).ToLowerInvariant() }
    finally { $sha.Dispose() }
}
function Invoke-AppDbSql([string]$Sql) {
    $result = & docker compose -f docker-compose.yml -f docker-compose.evidence.yml exec -T appdb psql -U postgres -d $env:EVIDENCE_DB_NAME -v ON_ERROR_STOP=1 -At -c $Sql
    if ($LASTEXITCODE -ne 0) { throw 'D6 evidence fixture SQL failed' }
    return $result
}
function Read-RootRun([string]$Conversation) {
    $literal = $Conversation.Replace("'", "''")
    $value = Invoke-AppDbSql "SELECT json_build_object('id',id,'status',status,'orchestrator_id',orchestrator_id,'orchestrator_revision',orchestrator_revision,'workflow_id',workflow_id,'workflow_revision',workflow_revision,'state_version',state_version,'checkpoint_ref',checkpoint_ref)::text FROM orchestrator_run WHERE tenant_id='demo-a' AND user_id='user-a' AND conversation_id='$literal' ORDER BY created_at DESC LIMIT 1"
    if ([string]::IsNullOrWhiteSpace($value)) { throw "No durable D6 root run exists for conversation $Conversation" }
    return ($value | ConvertFrom-Json)
}

# The profile has no model/provider dependency.  Its random key is process-only
# and is never printed or saved with evidence.
$bytes = New-Object byte[] 48
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
$env:EVIDENCE_HMAC_KEY = [Convert]::ToBase64String($bytes)
$env:EVIDENCE_CHECKPOINT_HMAC_KEY = $env:EVIDENCE_HMAC_KEY
$env:EVIDENCE_DB_NAME = $run
$env:EVIDENCE_AGENT_TEST_RUN_ENABLED = 'true'
$env:WORKFLOW_DESIGNER_ENABLED = 'true'
$env:MULTI_AGENT_DISPATCH_ENABLED = 'true'
$env:AGENT_CHAT_ENABLED = 'true'
$env:AGENT_CHAT_TENANT_ALLOWLIST = 'demo-a'
# The evidence compose profile owns its own backend/workflow/RabbitMQ chain.
# Route every deterministic D6 request there, never to the developer's normal
# appdb-backed services.
$env:EVIDENCE_PLATFORM_BACKEND_BASE_URL = 'http://backend-evidence:8080'
$env:EVIDENCE_WORKFLOW_UPSTREAM_URL = 'http://workflow-evidence:8000'
$env:EVIDENCE_PLATFORM_RABBITMQ_URL = 'amqp://app:app-dev-password@rabbitmq-evidence:5672'
$env:EVIDENCE_BACKEND_EMBEDDINGS_PROVIDER = 'fake'
$env:EVIDENCE_WORKFLOW_LLM_BASE_URL = 'http://evidence-model:8080/v1'
$env:EVIDENCE_WORKFLOW_LLM_API_KEY = 'evidence-not-a-secret'
$env:EVIDENCE_REAL_WORKFLOW_MODEL = 'evidence-model'
$createdDb = $false
$primaryFailure = $null

Push-Location $infra
try {
    # Never replace a caller's evidence profile: it has per-run HMAC/database
    # state and force-recreation would silently invalidate their run.
    $alreadyRunning = @()
    foreach ($service in $evidenceServices) {
        [string]$container = (& docker compose -f docker-compose.yml -f docker-compose.evidence.yml --profile evidence --profile evidence-real ps --all -q $service)
        $container = $container.Trim()
        if ($container) {
            $alreadyRunning += $service
        }
    }
    if ($alreadyRunning.Count -gt 0) { throw "D6 evidence profile is already active ($($alreadyRunning -join ', ')); refusing to replace caller state" }

    docker compose -f docker-compose.yml up -d appdb | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'unable to start D6 evidence dependencies' }
    $until = (Get-Date).AddSeconds($TimeoutSeconds)
    do { docker compose -f docker-compose.yml exec -T appdb pg_isready -U postgres | Out-Null; if ($LASTEXITCODE -eq 0) { break }; Start-Sleep -Milliseconds 500 } while ((Get-Date) -lt $until)
    if ($LASTEXITCODE -ne 0) { throw 'appdb was not ready' }
    Require ($env:EVIDENCE_DB_NAME -match '^d6evidence_[0-9a-f]{32}$') 'unsafe evidence database name'
    docker compose -f docker-compose.yml exec -T appdb psql -U postgres -d postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE `"$($env:EVIDENCE_DB_NAME)`"" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'unable to create isolated D6 database' }
    $createdDb = $true

    # The DB name and per-run HMACs change every invocation. Compose does not
    # reliably treat interpolated environment changes as a recreate trigger on
    # all host versions, so force the three stateful app clients to consume the
    # exact isolated profile rather than an earlier evidence database.
    $compose = @('-f','docker-compose.yml','-f','docker-compose.evidence.yml','--profile','evidence','--profile','evidence-real','up','-d','--force-recreate')
    if ($Build) { $compose += '--build' }
    $compose += @('backend-evidence','workflow-evidence','rabbitmq-evidence','evidence-model','workflow-capture-proxy','platform-evidence','evidence-nginx')
    $evidenceStarted = $true
    docker compose @compose | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'unable to start D6 evidence profile' }
    Wait-Http 'http://127.0.0.1:8180/actuator/health' 'platform-evidence'
    Wait-Http 'http://127.0.0.1:8011/health' 'workflow evidence proxy'
    Pass 'isolated current images and deterministic evidence model'

    # D6 service/integration suites cover A-CHAT-01..13's durable authority,
    # active lookup, explicit/default/legacy selection, resume, switch, public
    # redaction, restart/recovery and mem0 best-effort contracts without model
    # semantics. The following HTTP probes prove the deployed profile gates.
    Invoke-Dotnet 'Backend D6 active/owner/ambiguity/replay/recovery/redaction' (Join-Path $repo 'backend') @('test','tests/Backend.Api.Tests/Backend.Api.Tests.csproj','--no-restore','--filter','FullyQualifiedName~OrchestratorRunRepository')
    Invoke-Dotnet 'Platform D6 chat selection/resume/switch/no-fallback' (Join-Path $repo 'platform') @('test','tests/Platform.Service.Tests/Platform.Service.Tests.csproj','--no-restore','--filter','FullyQualifiedName~AgentChatRuntimeTests')
    Invoke-Pytest 'Workflow D6 checkpoint/resume/restart/no-child-rerun' @('-q','tests/test_root_orchestrator_supervisor.py','tests/test_root_orchestrator_production.py')

    $login = Invoke-Status POST 'http://127.0.0.1:8180/api/auth/login' @{ username='user-a'; password='password123' }
    Require ($login.StatusCode -eq 200) 'canary user login failed'
    $token = (($login.Content | ConvertFrom-Json).token)
    $auth = @{ Authorization="Bearer $token" }
    $anonAgui = Invoke-Status POST 'http://127.0.0.1:8180/api/copilot/agui' @{ threadId='d6-anon'; messages=@() }
    Require ($anonAgui.StatusCode -eq 401) 'AG-UI must reject anonymous callers'
    Pass 'A-CHAT-01 authenticated AG-UI gate'

    # Create the root Graph through the supported management API.  The two
    # minimal Agent pins and their immutable Orchestrator revision below are an
    # isolated SQL fixture: it deliberately avoids a second authoring UI test
    # while preserving the exact published revision/sha rows consumed at run
    # allocation.  No fixture touches the developer database.
    $rootGraph = @'
{"schemaVersion":1,"kind":"orchestrator","nodes":[{"id":"start","type":"start","typeVersion":"1.0","config":{}},{"id":"context","type":"acquire_context_and_analyze_problem","typeVersion":"1.0","config":{}},{"id":"sufficiency","type":"sufficiency_gate","typeVersion":"1.0","config":{}},{"id":"decompose","type":"decompose_work","typeVersion":"1.0","config":{}},{"id":"dispatch","type":"dispatch_agents","typeVersion":"1.0","config":{}},{"id":"join","type":"join_worker_results","typeVersion":"1.0","config":{}},{"id":"verify","type":"invoke_verifier","typeVersion":"1.0","config":{}},{"id":"repair","type":"bounded_repair","typeVersion":"1.0","config":{"maxIterations":1}},{"id":"aggregate","type":"aggregate_results","typeVersion":"1.0","config":{}},{"id":"respond","type":"respond","typeVersion":"1.0","config":{}},{"id":"audit","type":"audit","typeVersion":"1.0","config":{}},{"id":"end","type":"end","typeVersion":"1.0","config":{}}],"edges":[{"id":"e0","source":{"nodeId":"start","port":"out"},"target":{"nodeId":"context","port":"in"}},{"id":"e1","source":{"nodeId":"context","port":"out"},"target":{"nodeId":"sufficiency","port":"in"}},{"id":"e2","source":{"nodeId":"sufficiency","port":"out"},"target":{"nodeId":"decompose","port":"in"}},{"id":"e3","source":{"nodeId":"decompose","port":"out"},"target":{"nodeId":"dispatch","port":"in"}},{"id":"e4","source":{"nodeId":"dispatch","port":"out"},"target":{"nodeId":"join","port":"in"}},{"id":"e5","source":{"nodeId":"join","port":"out"},"target":{"nodeId":"verify","port":"in"}},{"id":"e6","source":{"nodeId":"verify","port":"out"},"target":{"nodeId":"repair","port":"in"}},{"id":"e7","source":{"nodeId":"repair","port":"out"},"target":{"nodeId":"aggregate","port":"in"}},{"id":"e8","source":{"nodeId":"aggregate","port":"out"},"target":{"nodeId":"respond","port":"in"}},{"id":"e9","source":{"nodeId":"respond","port":"out"},"target":{"nodeId":"audit","port":"in"}},{"id":"e10","source":{"nodeId":"audit","port":"out"},"target":{"nodeId":"end","port":"in"}},{"id":"tasks","source":{"nodeId":"decompose","port":"tasks"},"target":{"nodeId":"dispatch","port":"tasks"}},{"id":"results","source":{"nodeId":"dispatch","port":"results"},"target":{"nodeId":"join","port":"results"}}],"governance":{"maxSteps":40,"maxConcurrency":1}}
'@ | ConvertFrom-Json
    # Validate with Workflow's real compiler, then canonicalize with that exact
    # runtime implementation.  We use a safe isolated SQL fixture for this
    # published revision because the evidence profile does not grant the
    # seeded ADMIN an authoring capability; USER chat is the subject under test.
    $rootGraphBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($rootGraph | ConvertTo-Json -Depth 20 -Compress)))
    Push-Location (Join-Path $repo 'workflow')
    try { $rootGraphCanonical = (& uv run python -c "import base64,json; from app.orchestration.validator import validate; from app.orchestration.canonical import canonical_definition; value=json.loads(base64.b64decode('$rootGraphBase64')); checked=validate(value,{}); assert checked.valid, checked.errors; print(json.dumps(canonical_definition(value),ensure_ascii=False,separators=(',',':'),sort_keys=True))") -join "`n"; if ($LASTEXITCODE -ne 0) { throw 'D6 root Graph compiler validation/canonicalization failed' } }
    finally { Pop-Location }
    $workflowId = [guid]::NewGuid(); $rootGraph64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($rootGraphCanonical)); $rootGraphSha = Get-Sha256 $rootGraphCanonical

    $workerId = [guid]::NewGuid(); $verifierId = [guid]::NewGuid(); $orchestratorId = [guid]::NewGuid(); $agentRuntimeWorkflowId = [guid]::NewGuid()
    # The normal system runtime seed may intentionally advance revisions. This
    # fixture owns a private immutable revision so evidence never assumes a
    # particular seed history, and it is never reached before the context gate.
    $workerDefinition = (@{ allowed_tools=@(); audience=@('role:USER'); business_rules=@{version=1;rules=@()}; capabilities=@(); execution_roles=@('worker'); knowledge_sources=@(); output_contract=@{}; runtime_limits=@{token_budget=1024}; runtime_workflow=@{id=$agentRuntimeWorkflowId.ToString('D');revision=1}; skill_bindings=@(); system_prompt='D6 evidence worker' } | ConvertTo-Json -Depth 10 -Compress)
    $verifierDefinition = (@{ allowed_tools=@(); audience=@('role:USER'); business_rules=@{version=1;rules=@()}; capabilities=@(); execution_roles=@('verifier'); knowledge_sources=@(); output_contract=@{type='verification-report'}; runtime_limits=@{token_budget=1024}; runtime_workflow=@{id=$agentRuntimeWorkflowId.ToString('D');revision=1}; skill_bindings=@(); system_prompt='D6 evidence verifier' } | ConvertTo-Json -Depth 10 -Compress)
    $orchestratorDefinition = (@{ instructions='D6 canary evidence root'; policy=@{dispatchMode='bounded-parallel';joinPolicy='repair';repairPolicy='redispatch';aggregationPolicy='verified-only';denialPolicy='fail-closed'}; workflow=@{id=$workflowId.ToString('D');revision=1}; verifier=@{agentId=$verifierId.ToString('D');revision=1;variant='read-only';independent=$true;outputContract=@{type='verification-report'}}; workerPool=@(@{agentId=$workerId.ToString('D');revision=1}); workerPolicy=@{requiredAudience=@();requiredCapabilities=@();selection='pinned-only'}; context=@{readOnly=$true;allowedTools=@();knowledgeSources=@()}; audience=@('role:USER'); capabilities=@(); budgets=@{maxContextRounds=2;maxTasks=1;maxChildRuns=2;maxConcurrency=1;maxRepairRounds=1;tokenBudget=1024;timeoutSeconds=90} } | ConvertTo-Json -Depth 12 -Compress)
    $worker64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($workerDefinition)); $verifier64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($verifierDefinition)); $orchestrator64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($orchestratorDefinition))
    $fixtureSql = @"
WITH d AS (SELECT convert_from(decode('$rootGraph64','base64'),'UTF8') v)
INSERT INTO workflow(id,tenant_id,name,kind,enabled,draft_version,draft_definition,draft_ui_metadata,draft_definition_canonical,draft_ui_metadata_canonical,draft_definition_sha256,draft_ui_metadata_sha256,published_revision,system_owned,created_by) SELECT '$workflowId'::uuid,'demo-a','D6 evidence root','orchestrator',true,1,v::jsonb,'{}'::jsonb,convert_to(v,'UTF8'),convert_to('{}','UTF8'),'$rootGraphSha','$(Get-Sha256 '{}')',1,false,'d6-evidence' FROM d;
WITH d AS (SELECT convert_from(decode('$rootGraph64','base64'),'UTF8') v)
INSERT INTO workflow_revision(workflow_id,revision,status,schema_version,definition,ui_metadata,definition_canonical,ui_metadata_canonical,definition_sha256,ui_metadata_sha256,compiler_contract_version,created_by) SELECT '$workflowId'::uuid,1,'published',1,v::jsonb,'{}'::jsonb,convert_to(v,'UTF8'),convert_to('{}','UTF8'),'$rootGraphSha','$(Get-Sha256 '{}')','d4-graph-ir-1','d6-evidence' FROM d;
INSERT INTO workflow(id,tenant_id,name,kind,enabled,draft_version,draft_definition,draft_ui_metadata,draft_definition_canonical,draft_ui_metadata_canonical,draft_definition_sha256,draft_ui_metadata_sha256,published_revision,system_owned,created_by) VALUES('$agentRuntimeWorkflowId'::uuid,'demo-a','D6 evidence agent runtime','agent-runtime',true,1,'{}'::jsonb,'{}'::jsonb,convert_to('{}','UTF8'),convert_to('{}','UTF8'),'$(Get-Sha256 '{}')','$(Get-Sha256 '{}')',1,false,'d6-evidence');
INSERT INTO workflow_revision(workflow_id,revision,status,schema_version,definition,ui_metadata,definition_canonical,ui_metadata_canonical,definition_sha256,ui_metadata_sha256,compiler_contract_version,created_by) VALUES('$agentRuntimeWorkflowId'::uuid,1,'published',1,'{}'::jsonb,'{}'::jsonb,convert_to('{}','UTF8'),convert_to('{}','UTF8'),'$(Get-Sha256 '{}')','$(Get-Sha256 '{}')','d4-graph-ir-1','d6-evidence');
WITH d AS (SELECT convert_from(decode('$worker64','base64'),'UTF8') v)
INSERT INTO agent(id,tenant_id,slug,name,description,draft_definition,draft_definition_canonical,draft_definition_sha256,published_revision,created_by) SELECT '$workerId'::uuid,'demo-a','d6-evidence-worker','D6 evidence worker','',v::jsonb,convert_to(v,'UTF8'),'$(Get-Sha256 $workerDefinition)',1,'d6-evidence' FROM d;
WITH d AS (SELECT convert_from(decode('$worker64','base64'),'UTF8') v)
INSERT INTO agent_revision(agent_id,revision,status,system_prompt,execution_roles,capabilities,output_contract,audience,business_rules,allowed_tools,knowledge_sources,runtime_limits,runtime_workflow_id,runtime_workflow_revision,definition_sha256,canonical_definition,created_by) SELECT '$workerId'::uuid,1,'published',v::jsonb->>'system_prompt',v::jsonb->'execution_roles',v::jsonb->'capabilities',v::jsonb->'output_contract',v::jsonb->'audience',v::jsonb->'business_rules',v::jsonb->'allowed_tools',v::jsonb->'knowledge_sources',v::jsonb->'runtime_limits','$agentRuntimeWorkflowId'::uuid,1,'$(Get-Sha256 $workerDefinition)',convert_to(v,'UTF8'),'d6-evidence' FROM d;
WITH d AS (SELECT convert_from(decode('$verifier64','base64'),'UTF8') v)
INSERT INTO agent(id,tenant_id,slug,name,description,draft_definition,draft_definition_canonical,draft_definition_sha256,published_revision,created_by) SELECT '$verifierId'::uuid,'demo-a','d6-evidence-verifier','D6 evidence verifier','',v::jsonb,convert_to(v,'UTF8'),'$(Get-Sha256 $verifierDefinition)',1,'d6-evidence' FROM d;
WITH d AS (SELECT convert_from(decode('$verifier64','base64'),'UTF8') v)
INSERT INTO agent_revision(agent_id,revision,status,system_prompt,execution_roles,capabilities,output_contract,audience,business_rules,allowed_tools,knowledge_sources,runtime_limits,runtime_workflow_id,runtime_workflow_revision,definition_sha256,canonical_definition,created_by) SELECT '$verifierId'::uuid,1,'published',v::jsonb->>'system_prompt',v::jsonb->'execution_roles',v::jsonb->'capabilities',v::jsonb->'output_contract',v::jsonb->'audience',v::jsonb->'business_rules',v::jsonb->'allowed_tools',v::jsonb->'knowledge_sources',v::jsonb->'runtime_limits','$agentRuntimeWorkflowId'::uuid,1,'$(Get-Sha256 $verifierDefinition)',convert_to(v,'UTF8'),'d6-evidence' FROM d;
WITH d AS (SELECT convert_from(decode('$orchestrator64','base64'),'UTF8') v)
INSERT INTO orchestrator(id,tenant_id,name,description,draft_definition,draft_definition_canonical,draft_definition_sha256,published_revision,draft_validated_version,created_by) SELECT '$orchestratorId'::uuid,'demo-a','D6 evidence orchestrator','Canary-only durable root',v::jsonb,convert_to(v,'UTF8'),'$(Get-Sha256 $orchestratorDefinition)',1,1,'d6-evidence' FROM d;
WITH d AS (SELECT convert_from(decode('$orchestrator64','base64'),'UTF8') v)
INSERT INTO orchestrator_revision(orchestrator_id,revision,status,definition,canonical_definition,definition_sha256,workflow_id,workflow_revision,verifier_agent_id,verifier_agent_revision,created_by) SELECT '$orchestratorId'::uuid,1,'published',v::jsonb,convert_to(v,'UTF8'),'$(Get-Sha256 $orchestratorDefinition)','$workflowId'::uuid,1,'$verifierId'::uuid,1,'d6-evidence' FROM d;
INSERT INTO tenant_runtime_binding(tenant_id,enabled,default_orchestrator_id,default_orchestrator_revision,canary_user_ids) VALUES('demo-a',true,'$orchestratorId'::uuid,1,jsonb_build_array('user-a'));
"@
    Invoke-AppDbSql $fixtureSql | Out-Null
    Pass 'published root/orchestrator revision and canary binding fixture'

    $catalog = Invoke-Status GET 'http://127.0.0.1:8180/api/chat/orchestrators' $null $auth
    Require ($catalog.StatusCode -eq 200 -and (($catalog.Content | ConvertFrom-Json).orchestrators | Where-Object { $_.id -eq $orchestratorId.ToString('D') -and $_.revision -eq 1 })) 'canary USER cannot discover its published orchestrator'
    Pass 'A-CHAT-01/03 USER canary resolves exact published default'

    # Round one is intentionally context-insufficient.  A real root must be
    # allocated and checkpointed; a legacy reply here would make the DB probe
    # fail rather than silently passing evidence.
    $chatConversation = 'd6-canary-chat'
    $chatFirst = Invoke-Status POST 'http://127.0.0.1:8180/api/chat' @{ message='D6 start root'; conversationId=$chatConversation } $auth
    Require ($chatFirst.StatusCode -eq 200) 'canary chat failed to enter Root Orchestrator'
    $firstRoot = Read-RootRun "demo-a:user-a:$chatConversation"
    Require ($firstRoot.status -eq 'waiting_input' -and $firstRoot.orchestrator_id -eq $orchestratorId.ToString('D') -and $firstRoot.orchestrator_revision -eq 1 -and $firstRoot.workflow_id -eq $workflowId.ToString('D') -and $firstRoot.workflow_revision -eq 1 -and -not [string]::IsNullOrWhiteSpace($firstRoot.checkpoint_ref)) 'canary chat did not persist the pinned waiting Root run'
    Pass 'A-CHAT-01/07 durable Root allocation has exact pinned revision metadata'

    # The next turn must resume the same durable root, never make a fresh plan.
    $chatSecond = Invoke-Status POST 'http://127.0.0.1:8180/api/chat' @{ message='D6 trusted clarification: alpha=42'; conversationId=$chatConversation } $auth
    Require ($chatSecond.StatusCode -eq 200) 'canary clarification turn failed'
    $secondRoot = Read-RootRun "demo-a:user-a:$chatConversation"
    Require ($secondRoot.id -eq $firstRoot.id -and $secondRoot.status -eq 'waiting_input' -and [int64]$secondRoot.state_version -gt [int64]$firstRoot.state_version) 'clarification did not resume the same durable Root run'
    Pass 'A-CHAT-09/10 same conversation resumes durable root without fallback'

    $beforeUnknown = Invoke-AppDbSql "SELECT count(*) FROM orchestrator_run WHERE tenant_id='demo-a' AND user_id='user-a'"
    $explicit = Invoke-Status POST 'http://127.0.0.1:8180/api/chat' @{ message='D6 explicit unavailable'; conversationId='d6-explicit'; orchestratorId=([guid]::NewGuid().ToString()) } $auth
    Require ($explicit.StatusCode -in @(404,409)) 'explicit unavailable Orchestrator must not fall back'
    Require ((Invoke-AppDbSql "SELECT count(*) FROM orchestrator_run WHERE tenant_id='demo-a' AND user_id='user-a'") -eq $beforeUnknown) 'explicit unavailable Orchestrator allocated a fallback root'
    Pass 'A-CHAT-02 explicit unavailable orchestrator has no fallback'

    # AG-UI uses the same routing agent, while preserving its standard wire
    # framing.  It has no caller conversation body key, so its durable root
    # key is the authenticated identity-derived default conversation.
    $sse = Invoke-Status POST 'http://127.0.0.1:8180/api/chat/stream' @{ message='d6 raw sse'; conversationId='d6-sse' } $auth
    Require ($sse.StatusCode -eq 200 -and $sse.Content -match '(?m)^data:') 'raw chat SSE did not use data: framing'
    $aguiHeaders = @{ Authorization=$auth.Authorization; 'X-Orchestrator-Id'=$orchestratorId.ToString('D') }
    $agui = Invoke-Status POST 'http://127.0.0.1:8180/api/copilot/agui' @{ threadId='d6-agui'; runId=([guid]::NewGuid().ToString()); state=@{}; messages=@(@{ id=([guid]::NewGuid().ToString('N')); role='user'; content='D6 AGUI root' }); tools=@(); context=@(); forwardedProps=@{} } $aguiHeaders
    Require ($agui.StatusCode -eq 200 -and $agui.Content -match '(?m)^data: ') 'AG-UI did not use Root Orchestrator standard events'
    $aguiRoot = Read-RootRun 'demo-a:user-a'
    Require ($aguiRoot.status -eq 'waiting_input' -and $aguiRoot.orchestrator_id -eq $orchestratorId.ToString('D') -and $aguiRoot.workflow_id -eq $workflowId.ToString('D')) 'AG-UI did not allocate the same published Root runtime'
    Pass 'A-CHAT-04/05/08 Chat and AG-UI share published Root runtime'

    # This is an in-container Linux execution, avoiding the Windows async
    # psycopg pool ambiguity. It writes one opaque checkpoint then deletes it.
    $checkpoint = @'
import asyncio
from app.runtime.checkpoints import PostgresCheckpointStore
from app.settings import settings

async def main():
    store = PostgresCheckpointStore(settings.checkpoint_database_url)
    await store.open()
    payload = {"root_run_id":"d6","snapshot_hash":"a"*64,"tenant_id":"t","user_id":"u","stage":"context","audit":[]}
    reference, _ = await store.put_root_context_checkpoint(payload)
    try:
        assert await store.get_root_context_checkpoint(reference) == payload
    finally:
        async with store._pool.connection() as connection:
            await connection.execute("DELETE FROM workflow_root_context_checkpoint WHERE id=%s", (reference.split(":")[1],))
        await store.close()

asyncio.run(main())
'@
    $checkpointBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($checkpoint))
    $checkpointRunner = "import base64;exec(base64.b64decode('$checkpointBase64'))"
    docker compose -f docker-compose.yml -f docker-compose.evidence.yml exec -T workflow-evidence python -c $checkpointRunner | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Linux workflow PostgreSQL checkpoint roundtrip failed' }
    Pass 'Linux workflow real PostgreSQL checkpoint roundtrip'

    # Round 2 is the rollback contract. Recreate only the isolated gateway with
    # both gates closed; a new turn must be legacy and must not allocate a new
    # durable root despite the still-present tenant binding.
    $env:AGENT_CHAT_ENABLED = 'false'; $env:AGENT_CHAT_TENANT_ALLOWLIST = ''
    docker compose -f docker-compose.yml -f docker-compose.evidence.yml --profile evidence --profile evidence-real up -d --force-recreate platform-evidence | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'rollback gateway recreate failed' }
    Wait-Http 'http://127.0.0.1:8180/actuator/health' 'rollback platform-evidence'
    $beforeRollback = Invoke-AppDbSql "SELECT count(*) FROM orchestrator_run WHERE tenant_id='demo-a' AND user_id='user-a'"
    $rollback = Invoke-Status POST 'http://127.0.0.1:8180/api/chat' @{ message='d6 rollback'; conversationId='d6-rollback' } $auth
    Require ($rollback.StatusCode -eq 200) 'rollback new turn did not remain legacy'
    Require ((Invoke-AppDbSql "SELECT count(*) FROM orchestrator_run WHERE tenant_id='demo-a' AND user_id='user-a'") -eq $beforeRollback) 'rollback allocated a Root run despite disabled gateway flag'
    Pass 'rollback flag/allowlist produces legacy turn without Root allocation'

    Write-Host "BLOCKED E-04 real mem0 recall: deterministic evidence model is not semantic mem0 evidence."
    Write-Host "BLOCKED E-05 real skill routing: deterministic evidence model cannot prove model-selected routing."
    Write-Host "PASS D6 deterministic verifier checks=$script:checks"
} catch {
    $primaryFailure = $_
} finally {
    $cleanupFailures = @()
    if ($evidenceStarted) {
        # Remove only the isolated evidence chain, before its ephemeral DB is
        # dropped.  Normal appdb and the developer service profile stay intact.
        $cleanup = @('-f','docker-compose.yml','-f','docker-compose.evidence.yml','--profile','evidence','--profile','evidence-real','rm','-s','-f') + $evidenceServices
        & docker compose @cleanup | Out-Null
        if ($LASTEXITCODE -ne 0) { $cleanupFailures += "isolated evidence service cleanup exited $LASTEXITCODE" }
    }
    if ($createdDb -and -not $KeepEvidenceDb -and $env:EVIDENCE_DB_NAME -match '^d6evidence_[0-9a-f]{32}$') {
        & docker compose -f docker-compose.yml exec -T appdb psql -U postgres -d postgres -v ON_ERROR_STOP=1 -c "DROP DATABASE IF EXISTS `"$($env:EVIDENCE_DB_NAME)`" WITH (FORCE)" | Out-Null
        if ($LASTEXITCODE -ne 0) { $cleanupFailures += "isolated evidence database cleanup exited $LASTEXITCODE" }
    }
    elseif ($createdDb -and $KeepEvidenceDb) { Write-Warning "Retained isolated evidence database $($env:EVIDENCE_DB_NAME) for diagnosis" }
    try { Pop-Location } catch { $cleanupFailures += 'working directory restore failed' }
    foreach ($name in $evidenceEnvNames) {
        $prior = $priorEvidenceEnv[$name]
        try {
            if ($prior.Exists) { Set-Item -LiteralPath "Env:$name" -Value $prior.Value }
            else { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
        } catch { $cleanupFailures += "environment restore failed for $name" }
    }
    if ($null -ne $primaryFailure) {
        if ($cleanupFailures.Count -gt 0) {
            throw "D6 verifier failed: $($primaryFailure.Exception.Message); cleanup also failed: $($cleanupFailures -join '; ')"
        }
        throw $primaryFailure
    }
    if ($cleanupFailures.Count -gt 0) { throw "D6 verifier cleanup failed: $($cleanupFailures -join '; ')" }
}
