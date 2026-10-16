[CmdletBinding()]
param(
    [string]$ProjectRoot = 'D:\Unity Project\My project',
    [switch]$Preview
)

$ErrorActionPreference = 'Stop'
$utf8NoBom = New-Object System.Text.UTF8Encoding -ArgumentList $false

function Read-AgentFrontMatter {
    param([Parameter(Mandatory)][string]$Path)

    $raw = [System.IO.File]::ReadAllText($Path)
    $match = [regex]::Match($raw, '(?s)\A---\r?\n(?<front>.*?)\r?\n---(?<body>.*)\z')
    if (-not $match.Success) {
        throw "Frontmatter YAML khong hop le hoac khong tim thay: $Path"
    }

    $newline = "`n"
    if ($raw.Contains("`r`n")) { $newline = "`r`n" }
    return @{
        Raw = $raw
        Front = $match.Groups['front'].Value
        Body = $match.Groups['body'].Value
        Newline = $newline
    }
}

function Add-ActionRulesToAgent {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][array]$Rules
    )

    $parts = Read-AgentFrontMatter -Path $Path
    $nl = $parts.Newline
    $lines = New-Object 'System.Collections.Generic.List[string]'
    foreach ($line in ($parts.Front -split "\r?\n")) { [void]$lines.Add($line) }

    $permIndex = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^permissions:\s*$') { $permIndex = $i; break }
    }

    if ($permIndex -lt 0) {
        # Existing agents in this project use the V2 list schema. Do not silently
        # convert a different schema; ask for manual review instead.
        throw "Khong tim thay permissions: list trong $Path. Dung lai de tranh doi schema tu dong."
    }

    $permEnd = $lines.Count
    for ($i = $permIndex + 1; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ([string]::IsNullOrWhiteSpace($line) -or $line -match '^\s*#') { continue }
        if ($line -notmatch '^\s') { $permEnd = $i; break }
    }

    # Verify that the permissions block is the project's action/resource/effect list.
    $blockText = ($lines.GetRange($permIndex, $permEnd - $permIndex) -join $nl)
    if ($blockText -notmatch '(?m)^\s*-\s*action:') {
        throw "permissions: trong $Path khong co danh sach action/resource/effect ma script mong doi. Khong sua file."
    }

    $toAdd = New-Object 'System.Collections.Generic.List[string]'
    foreach ($rule in $Rules) {
        $ruleResource = '*'
        if ($rule.ContainsKey('Resource') -and -not [string]::IsNullOrWhiteSpace([string]$rule.Resource)) {
            $ruleResource = [string]$rule.Resource
        }

        $exists = $false
        for ($i = $permIndex + 1; $i -lt $permEnd; $i++) {
            if ($lines[$i] -match '^\s*-\s*action:\s*(?<a>[^\s#]+)\s*$') {
                $existingAction = $Matches['a'].Trim([char]39).Trim([char]34)
                if ($existingAction -eq $rule.Action -and $i + 2 -lt $permEnd) {
                    $resourceLine = $lines[$i + 1]
                    $effectLine = $lines[$i + 2]
                    $existingResource = $null
                    if ($resourceLine -match '^\s*resource:\s*(?<r>.*?)\s*$') {
                        $existingResource = $Matches['r'].Trim().Trim([char]39).Trim([char]34)
                    }
                    if ($existingResource -eq $ruleResource -and
                        $effectLine -match ('^\s*effect:\s*' + [regex]::Escape($rule.Effect) + '\s*$')) {
                        $exists = $true
                        break
                    }
                }
            }
        }

        if (-not $exists -and $rule.Effect -eq 'ask' -and $ruleResource -eq '*') {
            # Never replace a broad explicit deny with broad ask automatically.
            # Narrower resource-scoped ask rules are allowed to follow a broad deny;
            # OpenCode v2 evaluates ordered resource rules with the last match winning.
            for ($i = $permIndex + 1; $i -lt $permEnd; $i++) {
                if ($lines[$i] -match '^\s*-\s*action:\s*(?<a>[^\s#]+)\s*$') {
                    $existingAction = $Matches['a'].Trim([char]39).Trim([char]34)
                    if ($existingAction -eq $rule.Action -and $i + 2 -lt $permEnd) {
                        $existingResource = $null
                        if ($lines[$i + 1] -match '^\s*resource:\s*(?<r>.*?)\s*$') {
                            $existingResource = $Matches['r'].Trim().Trim([char]39).Trim([char]34)
                        }
                        if ($existingResource -eq '*' -and $lines[$i + 2] -match '^\s*effect:\s*deny\s*$') {
                            throw "Da ton tai rule deny rong cho '$($rule.Action)' resource '*', trong $Path. Script khong tu dong thay deny rong bang ask rong. Khong file nao duoc ghi truoc khi preflight hoan tat."
                        }
                    }
                }
            }
        }

        if (-not $exists) {
            [void]$toAdd.Add("  - action: $($rule.Action)")
            [void]$toAdd.Add(("    resource: `"{0}`"" -f $ruleResource))
            [void]$toAdd.Add("    effect: $($rule.Effect)")
        }
    }

    if ($toAdd.Count -eq 0) { return $parts.Raw }

    $lines.InsertRange($permEnd, $toAdd)
    $newFront = $lines -join $nl
    return "---$nl$newFront$nl---$($parts.Body)"
}

function Set-ManagedBlock {
    param(
        [Parameter(Mandatory)][string]$Body,
        [Parameter(Mandatory)][string]$StartMarker,
        [Parameter(Mandatory)][string]$EndMarker,
        [Parameter(Mandatory)][string]$Block
    )

    $start = $Body.IndexOf($StartMarker, [StringComparison]::Ordinal)
    $end = $Body.IndexOf($EndMarker, [StringComparison]::Ordinal)
    if ($start -ge 0 -and $end -ge $start) {
        $endAfter = $end + $EndMarker.Length
        return $Body.Substring(0, $start) + $Block + $Body.Substring($endAfter)
    }
    if ($start -ge 0 -or $end -ge 0) {
        throw 'Tim thay mot phan marker quan ly nhung khong du cap. Dung lai de tranh ghi de noi dung thu cong.'
    }
    return $Body.TrimEnd() + "`r`n`r`n" + $Block + "`r`n"
}

$root = [System.IO.Path]::GetFullPath($ProjectRoot)
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw "Khong tim thay Unity project: $root"
}

$agentsDir = Join-Path $root '.opencode\agents'
$skillsDir = Join-Path $root '.opencode\skills\unity-multi-agent-orchestration'
$skillPath = Join-Path $skillsDir 'SKILL.md'
$orchestratorPath = Join-Path $agentsDir 'orchestrator.md'
$artDesignerPath = Join-Path $agentsDir 'art-designer.md'
$technicalArtistPath = Join-Path $agentsDir 'technical-artist.md'

foreach ($required in @($orchestratorPath, $artDesignerPath, $technicalArtistPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Can file agent bat buoc, khong sua gi: $required"
    }
}

$startMarker = '<!-- UNITY-TEAM-ORCHESTRATION-START -->'
$endMarker = '<!-- UNITY-TEAM-ORCHESTRATION-END -->'

$skillBlock = @'
<!-- UNITY-TEAM-ORCHESTRATION-START -->
## Persistent Unity multi-agent workflow

### Mission and authority
The Project Manager is the single entry point for substantial user requests. Read the project's `AGENTS.md` and use this skill at the beginning of each substantial request. The Project Manager coordinates; it does not edit project files, invoke Unity mutation tools, or claim work is complete without reading the assigned agents' results.

Use the configured OpenCode subagent tool to call the named specialists. Do not assume that a Herdr pane and a subagent session with the same name share conversation history. Use subagents for scoped tasks and independent analysis; use separate Herdr panes when a persistent, directly observable session is specifically needed.

### Specialist routing
- **orchestrator**: classify the request, delegate, reconcile evidence, and report.
- **architect**: inspect architecture, dependencies, interfaces, file ownership, and implementation plan.
- **developer**: implement only the approved scope; report exact files changed and checks actually run.
- **reviewer**: independently review plans and actual diffs; never implement.
- **qa**: derive test cases, inspect available logs/results, and clearly mark tests not run.
- **art-designer**: develop visual direction, concepts, palette, silhouette, UI/FX guidance, and asset specifications. Use an image-generation capability only if one is actually available. A concept/prompt is not a finished asset.
- **technical-artist**: prepare and integrate approved sprites/models/textures/materials/shaders/rigs/animations/import settings and asset references. Do not silently take ownership of gameplay code.

### Routing rules
1. **Gameplay or system change:** ask architect for a plan, reviewer for an independent risk review, and QA for acceptance criteria/test design. Resolve important disagreements from file evidence. Then delegate implementation to developer. After implementation, reviewer inspects the actual diff and QA checks the results.
2. **Art-direction request:** use art-designer. Add technical-artist when a Unity-ready asset, import configuration, material, prefab attachment, or pipeline work is required.
3. **Asset integration request:** use technical-artist; use art-designer if visual choices or missing artwork must be designed. Involve developer only for code/API/gameplay integration.
4. **Combined feature and art task:** run independent planning branches in parallel only when they do not depend on each other's outputs. Agree on asset names, paths, formats, attachment points, APIs, and file owners before integration.
5. **Audit or bug investigation:** remain read-only until the root cause and permitted fix scope are clear.
6. Do not invoke all seven agents for every request. Use the smallest set that covers the work.

### File ownership and change control
- One owner per file at a time. Never allow two agents to edit the same script, scene, prefab, asset, or configuration concurrently.
- Keep gameplay code, art direction, and asset-pipeline responsibilities distinct.
- The developer owns gameplay code. The technical artist owns only explicitly assigned asset/import/material/pipeline files. The art-designer normally delivers concepts/specifications; it may request asset generation only through an available tool and required approval.
- Do not edit or overwrite scenes, prefabs, `ProjectSettings`, `Packages/manifest.json`, shared materials, or existing assets unless they are explicitly in scope and the user has authorized the operation.
- Before potentially destructive operations, inspect the target, create a non-overwriting backup outside `Assets/`, and use a unique output path where practical. Do not run a scene builder just to inspect it.
- Permission prompts are boundaries, not obstacles to bypass. Do not retry through another tool or route after a relevant action is denied.

### Evidence and verification
Every report separates:
1. Facts read directly from files/tools.
2. Inferences or calculations not verified at runtime.
3. Commands/tests actually executed and their observed results.
4. Tests blocked or not run, with reasons.

Never claim compile, Play Mode, Editor, MCP mutation, image creation, asset import, or test success without observed evidence. If the project has a compile error, report it as a blocker before proposing runtime verification. When using file/line evidence, cite exact paths and lines where available. Resolve conflicting agent claims by checking primary evidence; do not average contradictory opinions.

### Approval gates
- If the user explicitly asks to implement a scoped change, treat that as permission for the described scope, not for unrelated files or destructive actions.
- Ask before expanding into production scenes/prefabs, broad asset deletion/replacement, project settings/package changes, builders that overwrite existing paths, or changes in gameplay design not specified by the user.
- If requested behavior has a meaningful ambiguity, ask one focused question before implementation. Otherwise choose a conservative default and state it.
- If tool access, permission, model availability, or test infrastructure blocks work, report the real blocker; never silently bypass permissions or switch to a potentially paid model.

### Asset-to-code handoff checklist
For a new asset, record its intended name/type, approved output path, dimensions/scale, pivot or attachment point, sorting/layer requirements, material/shader/rig/animation dependencies, import settings, and how code or prefabs will reference it. Keep these values consistent between art-designer, technical-artist, and developer. Distinguish a proposed asset from an asset file that really exists and has been inspected in Unity.

### Final report format
1. Status and summary.
2. Specialists invoked and their concrete outputs.
3. Files/assets changed (or explicitly “none”).
4. Checks actually run and observed results.
5. Checks not run or blocked.
6. Remaining risks, assumptions, and recommended next step.
<!-- UNITY-TEAM-ORCHESTRATION-END -->
'@

$managerBlock = @'
<!-- UNITY-TEAM-SKILL-START -->
For every substantial request, read `AGENTS.md` and load the `unity-multi-agent-orchestration` skill before delegating. Follow its routing, file-ownership, approval, evidence, and final-report rules. You are the coordinator only; do not perform project mutations yourself.
<!-- UNITY-TEAM-SKILL-END -->
'@

# Prepare all changes in memory first. No file is written until every target validates.
$changes = [ordered]@{}
$originals = [ordered]@{}

# Skill: create if absent; if present, preserve all existing text and manage only our marked block.
if (Test-Path -LiteralPath $skillPath -PathType Leaf) {
    $skillRaw = [System.IO.File]::ReadAllText($skillPath)
    $skillMatch = [regex]::Match($skillRaw, '(?s)\A---\r?\n(?<front>.*?)\r?\n---(?<body>.*)\z')
    if (-not $skillMatch.Success) {
        throw "Skill da ton tai nhung frontmatter khong doc duoc: $skillPath. Khong sua file nao."
    }
    $skillBody = Set-ManagedBlock -Body $skillMatch.Groups['body'].Value -StartMarker $startMarker -EndMarker $endMarker -Block $skillBlock
    $skillNew = "---`r`n$($skillMatch.Groups['front'].Value)`r`n---$skillBody"
    if ($skillNew -cne $skillRaw) { $changes[$skillPath] = $skillNew; $originals[$skillPath] = $skillRaw }
}
else {
    $skillNew = @"
---
name: unity-multi-agent-orchestration
description: Route Unity project requests to specialist agents with scoped implementation, asset handoff, and independent review.
compatibility: opencode
---
$skillBlock
"@
    $changes[$skillPath] = $skillNew
    $originals[$skillPath] = $null
}

# Ensure the primary agent actually references the persistent skill.
$managerParts = Read-AgentFrontMatter -Path $orchestratorPath
$managerBody = $managerParts.Body
if ($managerBody -notmatch [regex]::Escape($startMarker)) {
    if ($managerBody -match '(?i)unity-multi-agent-orchestration') {
        # Existing instruction references the skill; add the managed block only if it does not already clearly require loading it.
        if ($managerBody -notmatch '(?is)read\s+`?AGENTS\.md`?.{0,100}unity-multi-agent-orchestration\s+skill') {
            $managerBody = $managerBody.TrimEnd() + "`r`n`r`n" + $managerBlock + "`r`n"
        }
    }
    else {
        $managerBody = $managerBody.TrimEnd() + "`r`n`r`n" + $managerBlock + "`r`n"
    }
    $managerNew = "---`r`n$($managerParts.Front)`r`n---$managerBody"
    if ($managerNew -cne $managerParts.Raw) { $changes[$orchestratorPath] = $managerNew; $originals[$orchestratorPath] = $managerParts.Raw }
}

# Preserve MCP read access for graphics agents, deny arbitrary Unity C# execution,
# and require approval for the known asset-generation mutation tool. Do not disable
# execute wholesale here because this could remove access to otherwise-read-only MCP tools.
$graphicsRules = @(
    @{ Action = 'unity_mcp_Unity_RunCommand'; Effect = 'deny' },
    @{ Action = 'unity-mcp_Unity_RunCommand'; Effect = 'deny' },
    @{ Action = 'unity_mcp_Unity_AssetGeneration_GenerateAsset'; Effect = 'ask' },
    @{ Action = 'unity-mcp_Unity_AssetGeneration_GenerateAsset'; Effect = 'ask' }
)

foreach ($agentPath in @($artDesignerPath, $technicalArtistPath)) {
    $existingRaw = [System.IO.File]::ReadAllText($agentPath)
    $rulesForAgent = @($graphicsRules)
    if ($agentPath -eq $technicalArtistPath) {
        # Preserve the existing broad edit deny. Ask only for edits beneath Assets,
        # then explicitly keep code and scenes blocked. Prefab/material/meta edits
        # remain approval-gated under Assets/**.
        $rulesForAgent = $rulesForAgent + @(
            @{ Action = 'edit'; Resource = 'Assets/**'; Effect = 'ask' },
            @{ Action = 'edit'; Resource = 'Assets/**/*.cs*'; Effect = 'deny' },
            @{ Action = 'edit'; Resource = 'Assets/**/*.unity*'; Effect = 'deny' },
            @{ Action = 'edit'; Resource = 'Assets/**/*.asmdef*'; Effect = 'deny' },
            @{ Action = 'edit'; Resource = 'Assets/**/*.asmref*'; Effect = 'deny' }
        )
    }
    $patched = Add-ActionRulesToAgent -Path $agentPath -Rules $rulesForAgent
    if ($patched -cne $existingRaw) {
        $changes[$agentPath] = $patched
        $originals[$agentPath] = $existingRaw
    }
}

if ($Preview) {
    Write-Host 'PREVIEW ONLY — no files were changed.' -ForegroundColor Cyan
    foreach ($path in $changes.Keys) { Write-Host "Would update: $path" }
    Write-Host 'Planned behavior:'
    Write-Host ' - Create/update .opencode/skills/unity-multi-agent-orchestration/SKILL.md'
    Write-Host ' - Ensure orchestrator references the skill'
    Write-Host ' - Deny Unity_RunCommand for Art Designer and Technical Artist'
    Write-Host ' - Require approval for Unity AssetGeneration_GenerateAsset for both graphics agents'
    Write-Host ' - Keep Technical Artist broad edit deny, ask before edits under Assets/**, and deny code/scenes/assembly definition files'
    Write-Host ' - Preserve all model IDs, variants, modes, descriptions, role bodies, and existing unrelated permissions'
    exit 0
}

if ($changes.Count -eq 0) {
    Write-Host 'No changes needed. The managed skill and permissions already appear configured.' -ForegroundColor Green
    exit 0
}

$backupRoot = Join-Path $root '.agent-config-backup'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$backupDir = Join-Path $backupRoot "orchestration-setup-$stamp"
New-Item -ItemType Directory -Path $backupDir -Force | Out-Null

# Back up every pre-existing target before writing anything; preserve relative paths.
foreach ($path in $changes.Keys) {
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        $relative = $path.Substring($root.Length).TrimStart('\')
        $backupPath = Join-Path $backupDir $relative
        $parent = Split-Path -Parent $backupPath
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
        Copy-Item -LiteralPath $path -Destination $backupPath
    }
}

# Apply prepared updates only after all originals have been backed up.
foreach ($path in $changes.Keys) {
    $parent = Split-Path -Parent $path
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    [System.IO.File]::WriteAllText($path, [string]$changes[$path], $utf8NoBom)
    Write-Host "Updated: $($path.Substring($root.Length).TrimStart('\'))" -ForegroundColor Green
}

# Verify markers/rules from disk. These checks validate file content only, not runtime permission enforcement.
$skillCheck = [System.IO.File]::ReadAllText($skillPath)
if ($skillCheck -notmatch [regex]::Escape($startMarker) -or $skillCheck -notmatch [regex]::Escape($endMarker)) {
    throw "Skill write verification failed. Originals are backed up in $backupDir"
}
foreach ($agentPath in @($artDesignerPath, $technicalArtistPath)) {
    $disk = [System.IO.File]::ReadAllText($agentPath)
    if ($disk -notmatch 'action:\s*unity_mcp_Unity_RunCommand[\s\S]{0,100}effect:\s*deny' -and
        $disk -notmatch 'action:\s*unity-mcp_Unity_RunCommand[\s\S]{0,100}effect:\s*deny') {
        throw "RunCommand deny verification failed for $agentPath. Originals are backed up in $backupDir"
    }
    if ($disk -notmatch 'action:\s*unity_mcp_Unity_AssetGeneration_GenerateAsset[\s\S]{0,100}effect:\s*ask' -and
        $disk -notmatch 'action:\s*unity-mcp_Unity_AssetGeneration_GenerateAsset[\s\S]{0,100}effect:\s*ask') {
        throw "AssetGeneration ask verification failed for $agentPath. Originals are backed up in $backupDir"
    }
}
$taDisk = [System.IO.File]::ReadAllText($technicalArtistPath)
if ($taDisk -notmatch 'action:\s*edit[\s\S]{0,100}resource:\s*["'']?Assets/\*\*["'']?[\s\S]{0,100}effect:\s*ask') {
    throw "Technical Artist scoped edit ask verification failed. Originals are backed up in $backupDir"
}
if ($taDisk -notmatch 'action:\s*edit[\s\S]{0,100}resource:\s*["'']?Assets/\*\*/\*\.cs\*["'']?[\s\S]{0,100}effect:\s*deny' -or
    $taDisk -notmatch 'action:\s*edit[\s\S]{0,100}resource:\s*["'']?Assets/\*\*/\*\.unity\*["'']?[\s\S]{0,100}effect:\s*deny') {
    throw "Technical Artist code/scene deny verification failed. Originals are backed up in $backupDir"
}

Write-Host ''
Write-Host 'Configuration files updated and content checks passed.' -ForegroundColor Green
Write-Host "Backup folder: $backupDir"
Write-Host 'Important: these are static file checks. Restart/reload OpenCode and test permissions at runtime before trusting the restrictions.' -ForegroundColor Yellow
Write-Host 'This script did NOT change code, scenes, prefabs, assets, ProjectSettings, Packages, model IDs, or variants.'
Write-Host 'Technical Artist file edits are approval-gated under Assets/** except code, scene, and assembly definition files. This is not a complete MCP sandbox; audit other mutating tools before trusting it.' -ForegroundColor Yellow
