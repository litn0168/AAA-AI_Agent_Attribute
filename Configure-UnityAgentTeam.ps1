# Configure a 7-agent OpenCode team for the Unity project.
# Creates timestamped backups before changing existing files.
# Requires OpenCode v2 with `opencode models` and project `.opencode` support.

$ErrorActionPreference = 'Stop'
$root = 'D:\Unity Project\My project'
$agentDir = Join-Path $root '.opencode\agents'
$skillDir = Join-Path $root '.opencode\skills\unity-multi-agent-orchestration'
$configPath = Join-Path $root 'opencode.json'

if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw "Project folder not found: $root"
}

# Read the currently available model catalog. This verifies ID presence only,
# not the current price, quota, or the user's provider entitlement.
$modelOutput = & opencode models 2>&1
$modelExit = $LASTEXITCODE
if ($modelExit -ne 0) {
    throw "Could not read model catalog using 'opencode models'. No files were changed."
}
$modelText = ($modelOutput | ForEach-Object { [string]$_ }) -join "`n"
$escapeChar = [string][char]27
$modelText = [regex]::Replace($modelText, $escapeChar + '\[[0-?]*[ -/]*[@-~]', '')

$available = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
foreach ($line in ($modelText -split "`r?`n")) {
    $trimmed = $line.Trim()
    if ($trimmed -match '^(opencode/[a-zA-Z0-9._-]+)$') {
        [void]$available.Add($Matches[1])
    }
}

if ($available.Count -eq 0) {
    throw "Could not parse model IDs from 'opencode models'. No files were changed."
}

# Only models explicitly listed in this approved OpenCode-Free allow-list may
# be considered as candidates. Catalog presence is NOT proof of zero cost.
$profiles = [ordered]@{
    orchestrator = @{
        Description = 'Unity Project Manager that coordinates specialized agents'
        Mode = 'primary'
        Models = @('opencode/deepseek-v4.1-flash-free', 'opencode/nemotron-3.5-lightning-free', 'opencode/nemotron-3-ultra-free')
        Body = @'
You are the Project Manager for this Unity project. Read AGENTS.md and the unity-multi-agent-orchestration skill for every substantial request.

You are read-only: never edit, write, patch, or directly mutate the Unity project. Classify the request and delegate to the named specialist agents using OpenCode's subagent tool. For a substantial gameplay/system change, ask Architect to plan, Reviewer to independently inspect risks, and QA to define tests; then delegate approved implementation to Developer. For art-direction tasks use Art Designer; for asset import/material/shader/rig/pipeline tasks use Technical Artist. For combined tasks, coordinate both branches with explicit non-overlapping file ownership.

Run independent analysis tasks in parallel when useful. Wait for their results, resolve contradictions, and do not claim work or tests completed unless a tool result proves it. After Developer changes code, request a fresh Reviewer pass and QA pass. Report changed files, evidence, test results actually observed, unrun tests, and unresolved risks. For a trivial isolated change, use only the specialists needed.
'@
        Permissions = @'
  - action: edit
    resource: "*"
    effect: deny
  - action: shell
    resource: "*"
    effect: deny
  - action: subagent
    resource: "*"
    effect: deny
  - action: subagent
    resource: "architect"
    effect: allow
  - action: subagent
    resource: "developer"
    effect: allow
  - action: subagent
    resource: "reviewer"
    effect: allow
  - action: subagent
    resource: "qa"
    effect: allow
  - action: subagent
    resource: "art-designer"
    effect: allow
  - action: subagent
    resource: "technical-artist"
    effect: allow
'@
    }
    architect = @{
        Description = 'Unity systems architect and implementation planner'
        Mode = 'all'
        Models = @('opencode/nemotron-3-ultra-free', 'opencode/deepseek-v4.1-flash-free', 'opencode/step-5-preview-free')
        Body = @'
You are the Unity Systems Architect. Read relevant scripts and project conventions before proposing an implementation. Define ownership by file, dependencies/interfaces, risks, acceptance criteria, and a practical test plan. Prefer data-driven design consistent with the project. Do not edit files; return a plan that Developer can implement and Reviewer can assess. Distinguish inspected facts from assumptions.
'@
        Permissions = @'
  - action: edit
    resource: "*"
    effect: deny
  - action: shell
    resource: "*"
    effect: deny
'@
    }
    developer = @{
        Description = 'Implements approved Unity code changes'
        Mode = 'all'
        Models = @('opencode/deepseek-v4.1-flash-free', 'opencode/nemotron-3.5-lightning-free', 'opencode/mimo-v2.6-flash-free')
        Body = @'
You are the Unity Developer. Implement only a task authorized by Project Manager or the user, with a clear scope and acceptance criteria. Inspect existing callers and conventions first. Keep changes small and avoid overlapping files owned by another task. Ask before broad or risky edits. Do not modify scenes, prefabs, ProjectSettings, or Packages/manifest.json unless the request explicitly requires it. Inspect the diff before finishing. Report files changed, reasoning, checks actually run, checks not run, and remaining risks. Never claim Unity compilation or Play Mode passed without evidence.
'@
        Permissions = @'
  - action: edit
    resource: "*"
    effect: ask
'@
    }
    reviewer = @{
        Description = 'Read-only Unity architecture and code reviewer'
        Mode = 'all'
        Models = @('opencode/step-5-preview-free', 'opencode/longcat-2.5-preview-free', 'opencode/nemotron-3-ultra-free')
        Body = @'
You are the independent, read-only Unity Reviewer. Never edit project files. Inspect the actual plan and diff, surrounding code, serialized assets when relevant, and callers. Look for logic bugs, mismatched APIs, lifecycle issues, null handling, event leaks, incorrect assumptions/calculations, scene/prefab implications, and regressions. Rank findings by severity. Each finding must include file/line or concrete evidence, impact, and a fix. If you did not inspect or run something, say so. Independently recompute numeric claims rather than repeating another agent's estimates.
'@
        Permissions = @'
  - action: edit
    resource: "*"
    effect: deny
  - action: shell
    resource: "*"
    effect: deny
'@
    }
    qa = @{
        Description = 'Read-only Unity test planner and QA reviewer'
        Mode = 'all'
        Models = @('opencode/longcat-2.5-preview-free', 'opencode/step-5-preview-free', 'opencode/deepseek-v4.1-flash-free')
        Body = @'
You are the independent, read-only Unity QA agent. Never edit files. Derive test cases from acceptance criteria and actual code. Cover ordinary cases, boundaries, invalid states, regression, and Unity-specific behavior. Specify setup, action, expected result, and observable evidence. Distinguish tests actually executed from tests merely suggested. Check physical and numeric reasoning independently. Never propose a test procedure that makes the system under test incapable of behaving normally (for example, making a Rigidbody2D Static while trying to verify its knockback). State what still requires the Unity Editor or Play Mode.
'@
        Permissions = @'
  - action: edit
    resource: "*"
    effect: deny
  - action: shell
    resource: "*"
    effect: deny
'@
    }
    'art-designer' = @{
        Description = 'Unity game art direction, visual design, and asset briefs'
        Mode = 'all'
        Models = @('opencode/nemotron-3.5-super-vl-free', 'opencode/mimo-v2.6-flash-free')
        Body = @'
You are the Unity Art Designer. Define a consistent visual direction: silhouettes, palettes, character/enemy concepts, environment mood, UI style, VFX direction, and asset briefs. Analyze supplied images when available. Produce concrete dimensions, viewpoints, palette/material suggestions, and acceptance criteria where appropriate. Do not edit project files or claim an asset was created unless a suitable image/asset tool actually did so. Coordinate with Technical Artist on technical constraints.
'@
        Permissions = @'
  - action: edit
    resource: "*"
    effect: deny
  - action: shell
    resource: "*"
    effect: deny
'@
    }
    'technical-artist' = @{
        Description = 'Unity asset integration and graphics pipeline specialist'
        Mode = 'all'
        Models = @('opencode/mimo-v2.6-flash-free', 'opencode/nemotron-3.5-super-vl-free', 'opencode/deepseek-v4.1-flash-free')
        Body = @'
You are the Unity Technical Artist. Inspect model, texture, material, shader, rig, animation, import settings, sprite slicing, sorting, and graphics-pipeline issues. For each task, identify the actual asset paths and explain the changes needed. Do not edit scenes, prefabs, ProjectSettings, Packages/manifest.json, or gameplay scripts unless explicitly authorized. Ask for approval before asset edits. Report import settings, compatibility considerations, files changed, and the actual checks performed. Do not confuse an image concept with a production-ready Unity asset.
'@
        Permissions = @'
  - action: edit
    resource: "*"
    effect: deny
  - action: edit
    resource: "Assets/**"
    effect: ask
  - action: edit
    resource: "Assets/**/*.unity"
    effect: deny
  - action: edit
    resource: "Assets/**/*.prefab"
    effect: deny
  - action: shell
    resource: "*"
    effect: ask
'@
    }
}

# Preflight every profile before modifying any project file.
$resolvedModels = [ordered]@{}
foreach ($name in $profiles.Keys) {
    $selected = $null
    foreach ($candidate in $profiles[$name].Models) {
        if (-not $candidate.StartsWith('opencode/')) { continue }
        if (-not $candidate.EndsWith('-free')) { continue }
        if ($available.Contains($candidate)) { $selected = $candidate; break }
    }
    if (-not $selected) {
        throw "No approved model ID is currently listed for agent '$name'. No project files were changed."
    }
    $resolvedModels[$name] = "$selected#high"
}

# Prepare config before making changes. Preserve the existing MCP configuration.
$config = $null
if (Test-Path -LiteralPath $configPath -PathType Leaf) {
    try {
        $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    }
    catch {
        throw "Could not parse $configPath as JSON. No project files were changed. Back up and inspect it manually first."
    }
}
else {
    $jsoncPath = Join-Path $root 'opencode.jsonc'
    if (Test-Path -LiteralPath $jsoncPath) {
        throw "Found opencode.jsonc but no opencode.json. This script will not create a competing config. Set default_agent in opencode.jsonc manually or convert it carefully first. No project files were changed."
    }
    $config = [pscustomobject]@{ '$schema' = 'https://opencode.ai/config.json' }
}

# Back up all affected files first.
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupDir = Join-Path $root ".agent-config-backup\$stamp"
New-Item -ItemType Directory -Path $backupDir -Force | Out-Null

$backupCandidates = @(
    (Join-Path $root 'AGENTS.md'),
    $configPath
)
foreach ($name in $profiles.Keys) { $backupCandidates += (Join-Path $agentDir "$name.md") }
$backupCandidates += (Join-Path $skillDir 'SKILL.md')
foreach ($path in $backupCandidates) {
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        $relative = $path.Substring($root.Length).TrimStart('\')
        $dest = Join-Path $backupDir $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $dest) -Force | Out-Null
        Copy-Item -LiteralPath $path -Destination $dest -Force
    }
}

New-Item -ItemType Directory -Path $agentDir -Force | Out-Null
foreach ($name in $profiles.Keys) {
    $profile = $profiles[$name]
    $model = $resolvedModels[$name]
    $content = @"
---
description: $($profile.Description)
mode: $($profile.Mode)
model: $model
permissions:
$($profile.Permissions.TrimEnd())
---
$($profile.Body.Trim())
"@
    $path = Join-Path $agentDir "$name.md"
    Set-Content -LiteralPath $path -Value $content -Encoding UTF8
    Write-Host ("{0,-18} {1}" -f $name, $model) -ForegroundColor Green
}

# Add persistent workflow instructions without deleting existing Unity rules.
$agentsRulesPath = Join-Path $root 'AGENTS.md'
$currentRules = if (Test-Path -LiteralPath $agentsRulesPath) { Get-Content -LiteralPath $agentsRulesPath -Raw } else { "# Unity Project Rules`r`n" }
$section = @'

<!-- UNITY-AGENT-ORCHESTRATION:BEGIN -->
## Multi-agent workflow

- The default entry point is `orchestrator`. It must not implement changes itself.
- For non-trivial work, use the Architect to plan, then ask Reviewer and QA for independent analysis before implementation.
- Delegate code changes to Developer only after the task scope, owned files, dependencies, and acceptance criteria are clear.
- Use Art Designer for visual direction/concepts and Technical Artist for import/material/shader/rig/asset-pipeline integration.
- Run independent analysis agents in parallel where useful. Never assign the same file to multiple agents for simultaneous editing.
- After implementation, ask Reviewer to inspect the real diff and QA to evaluate tests. Treat only observed output as executed tests.
- Numerical calculations, physics assumptions, file locations, and API claims must be verified against the project or recomputed.
- Do not modify scenes, prefabs, ProjectSettings, or Packages/manifest.json unless the user request explicitly requires it.
- Final reports must include changed files, evidence, tests actually run, tests not run, and unresolved risks.
<!-- UNITY-AGENT-ORCHESTRATION:END -->
'@
$pattern = '(?s)\r?\n?<!-- UNITY-AGENT-ORCHESTRATION:BEGIN -->.*?<!-- UNITY-AGENT-ORCHESTRATION:END -->'
if ($currentRules -match $pattern) {
    $currentRules = [regex]::Replace($currentRules, $pattern, $section.TrimEnd())
}
else {
    $currentRules = $currentRules.TrimEnd() + $section + "`r`n"
}
Set-Content -LiteralPath $agentsRulesPath -Value $currentRules -Encoding UTF8

# Add a reusable orchestration skill.
New-Item -ItemType Directory -Path $skillDir -Force | Out-Null
$skill = @'
---
name: unity-multi-agent-orchestration
description: Coordinate Unity gameplay, architecture, code review, QA, and graphics tasks across the specialized agent team.
---

# Unity Multi-Agent Orchestration

1. Classify the request as gameplay/code, architecture, review/QA, art direction, technical art, or combined.
2. For a trivial isolated change, delegate only to Developer, then obtain proportionate review.
3. For a larger gameplay/system change, launch Architect, Reviewer, and QA analysis in parallel where possible.
4. For visual tasks, ask Art Designer for the visual brief. If engine integration is needed, ask Technical Artist to review import/runtime constraints.
5. Reconcile results and produce a concise approved task for Developer with owned files, dependencies, acceptance criteria, and explicit exclusions.
6. Do not let Developer and Technical Artist edit the same file simultaneously. Scene, prefab, project settings, and package manifest changes require explicit user authorization.
7. After changes, have Reviewer inspect the actual diff and QA compare the outcome to acceptance criteria. Ask for corrections when findings are supported by evidence.
8. Report exact files changed, what was actually tested, what was not tested, and remaining risk. Never describe suggested tests as executed tests.
9. If a specialist is unavailable or its model fails, say which part is blocked. Do not pretend the work was delegated or completed.
'@
Set-Content -LiteralPath (Join-Path $skillDir 'SKILL.md') -Value $skill -Encoding UTF8

# Set the default agent while retaining existing config keys, including MCP.
if ($config.PSObject.Properties.Name -contains 'default_agent') {
    $config.default_agent = 'orchestrator'
}
else {
    $config | Add-Member -MemberType NoteProperty -Name 'default_agent' -Value 'orchestrator'
}
if (-not ($config.PSObject.Properties.Name -contains '$schema')) {
    $config | Add-Member -MemberType NoteProperty -Name '$schema' -Value 'https://opencode.ai/config.json'
}
$configJson = $config | ConvertTo-Json -Depth 100
Set-Content -LiteralPath $configPath -Value $configJson -Encoding UTF8

Write-Host "`nConfiguration updated." -ForegroundColor Green
Write-Host "Backup folder: $backupDir"
Write-Host "Default agent: orchestrator"
Write-Host "Important: the model catalog confirms ID availability, not current price/quota. This script only selects from the approved opencode/*-free ID allow-list."
Write-Host "Next: start a NEW OpenCode session in this project and verify the current model shown for each specialist when called as a subagent. Existing sessions may retain their selected model/agent."
