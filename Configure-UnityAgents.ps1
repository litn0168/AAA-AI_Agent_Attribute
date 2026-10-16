
$ErrorActionPreference = "Stop"

$root = "D:\Unity Project\My project"
$agentsDir = Join-Path $root ".opencode\agents"
$variant = "high"

if (!(Test-Path $root)) {
    throw "Khong tim thay project: $root"
}

New-Item -ItemType Directory -Path $agentsDir -Force | Out-Null

# Lay danh sach model hien tai tu OpenCode.
$catalogOutput = & opencode models 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "Khong doc duoc danh sach model. Dung lai de tranh thay doi cau hinh."
}

$catalogText = ($catalogOutput | Out-String)
$catalogText = [regex]::Replace(
    $catalogText,
    "$([char]27)\[[0-?]*[ -/]*[@-~]",
    ""
)

$available = New-Object 'System.Collections.Generic.HashSet[string]' (
    [System.StringComparer]::OrdinalIgnoreCase
)

foreach ($line in ($catalogText -split "`r?`n")) {
    $id = $line.Trim()

    if ($id -match '^[a-zA-Z0-9._-]+/[a-zA-Z0-9._-]+$') {
        [void]$available.Add($id)
    }
}

if ($available.Count -eq 0 -or
    !($available | Where-Object { $_ -like "opencode/*" })) {
    throw "Khong doc duoc ID model hop le. Khong sua file agent."
}

# Moi agent co mot chuoi model uu tien.
# Chi su dung cac model OpenCode ban dang thay trong danh sach Free.
$profiles = [ordered]@{
    orchestrator = @{
        Description = "Project Manager for the Unity agent team"
        Mode = "primary"
        Models = @(
            "opencode/nemotron-3.5-lightning-free",
            "opencode/deepseek-v4.1-flash-free",
            "opencode/nemotron-3-ultra-free"
        )
        Body = "You are the Unity Project Manager. Receive user requests, classify tasks, coordinate the other Herdr agents, track their results, and report unresolved issues. Do not edit project files. Use Herdr CLI for delegation when available. Never claim a task finished without checking the agent's output."
    }

    architect = @{
        Description = "Unity systems architect and implementation planner"
        Mode = "all"
        Models = @(
            "opencode/nemotron-3-ultra-free",
            "opencode/deepseek-v4.1-flash-free",
            "opencode/step-5-preview-free"
        )
        Body = "You are the Unity Architect. Analyze project structure, dependencies, interfaces, risks, and acceptance criteria. Plan the implementation and define clear file ownership. Do not edit project files. Return a plan for the Project Manager and Developer."
    }

    developer = @{
        Description = "Implements approved Unity code changes"
        Mode = "all"
        Models = @(
            "opencode/deepseek-v4.1-flash-free",
            "opencode/nemotron-3.5-lightning-free",
            "opencode/mimo-v2.6-flash-free"
        )
        Body = "You are the Unity Developer. Only implement an approved task with a defined scope. Avoid files owned by another task. Do not change scenes, prefabs, or project settings unless explicitly authorized. Review your diff and report files changed, tests actually run, and remaining risks."
    }

    reviewer = @{
        Description = "Read-only Unity architecture and code reviewer"
        Mode = "all"
        Models = @(
            "opencode/step-5-preview-free",
            "opencode/longcat-2.5-preview-free",
            "opencode/nemotron-3-ultra-free"
        )
        Body = "You are a read-only Unity Reviewer. Do not edit files. Inspect plans and diffs for logic errors, dependencies, lifecycle problems, event leaks, API mismatches, and prefab or scene risks. Report actionable findings with severity, evidence, and suggested fixes."
    }

    qa = @{
        Description = "Read-only Unity test planner and QA reviewer"
        Mode = "all"
        Models = @(
            "opencode/longcat-2.5-preview-free",
            "opencode/step-5-preview-free",
            "opencode/deepseek-v4.1-flash-free"
        )
        Body = "You are a read-only Unity QA agent. Do not edit files. Design test cases, edge cases, regression checks, and reproducible bug reports. Separate tests actually run from tests merely suggested. State what still needs verification in the Unity Editor or Play Mode."
    }

    "art-designer" = @{
        Description = "Unity game art direction and visual design"
        Mode = "all"
        Models = @(
            "opencode/nemotron-3.5-super-vl-free",
            "opencode/mimo-v2.6-flash-free"
        )
        Body = "You are the Unity Art Designer. Define visual direction, palettes, character and enemy concepts, environment style, UI direction, reference-image analysis, and asset requirements. Do not edit project files. Distinguish visual concepts from assets that have actually been created."
    }

    "technical-artist" = @{
        Description = "Unity asset integration and graphics pipeline specialist"
        Mode = "all"
        Models = @(
            "opencode/mimo-v2.6-flash-free",
            "opencode/nemotron-3.5-super-vl-free",
            "opencode/deepseek-v4.1-flash-free"
        )
        Body = "You are the Unity Technical Artist. Analyze model, texture, material, rig, animation, shader, import settings, and asset-pipeline issues. Implement only explicitly approved changes and avoid unrelated gameplay code. Do not modify scenes or prefabs without authorization. Report the actual checks performed."
    }
}

function Set-FrontmatterValue {
    param(
        [System.Collections.Generic.List[string]]$Lines,
        [string]$Key,
        [string]$Value
    )

    $pattern = "^\s*" + [regex]::Escape($Key) + "\s*:"

    for ($i = 0; $i -lt $Lines.Count; $i++) {
        if ($Lines[$i] -match $pattern) {
            $Lines[$i] = "${Key}: $Value"
            return
        }
    }

    $Lines.Add("${Key}: $Value")
}

foreach ($name in $profiles.Keys) {
    $profile = $profiles[$name]
    $chosen = $null

    foreach ($candidate in $profile.Models) {
        if ($available.Contains($candidate)) {
            $chosen = $candidate
            break
        }
    }

    if (!$chosen) {
        Write-Error "Khong co model du phong trong danh sach cho agent: $name"
        throw "Dung lai. Khong tu chuyen sang model ngoai danh sach duoc phe duyet."
    }

    $modelSetting = "$chosen#$variant"
    $path = Join-Path $agentsDir "$name.md"

    if (Test-Path $path) {
        $raw = Get-Content -LiteralPath $path -Raw
        $match = [regex]::Match(
            $raw,
            '(?s)\A---\r?\n(?<front>.*?)\r?\n---(?<body>.*)\z'
        )

        if (!$match.Success) {
            throw "Khong doc duoc frontmatter cua $path. Khong ghi de file nay."
        }

        $backup = "$path.pre-model-config.bak"
        if (!(Test-Path $backup)) {
            Copy-Item -LiteralPath $path -Destination $backup
        }

        $lines = New-Object 'System.Collections.Generic.List[string]'
        foreach ($line in ($match.Groups["front"].Value -split "`r?`n")) {
            $lines.Add($line)
        }

        Set-FrontmatterValue $lines "description" $profile.Description
        Set-FrontmatterValue $lines "mode" $profile.Mode
        Set-FrontmatterValue $lines "model" $modelSetting

        $newContent = "---`r`n" +
            ($lines -join "`r`n") +
            "`r`n---" +
            $match.Groups["body"].Value
    }
    else {
        $newContent = @"
---
description: $($profile.Description)
mode: $($profile.Mode)
model: $modelSetting
---
$($profile.Body)
"@
    }

    Set-Content -LiteralPath $path -Value $newContent -Encoding UTF8

    Write-Host ("{0,-18} -> {1}" -f $name, $modelSetting) -ForegroundColor Green
}

Write-Host ""
Write-Host "Da cap nhat cau hinh 7 agent." -ForegroundColor Green
Write-Host "Kiem tra model va variant truoc khi khoi dong cac pane."
