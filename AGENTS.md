# Unity Project Rules

## General
- Read relevant code and project structure before proposing changes.
- Prefer small, focused, reversible changes.
- Do not invent APIs, test results, or Unity scene state.
- Explain dependencies and risks before large changes.
- Never claim a test passed unless it was actually run.
- Preserve unrelated user changes.
- Do not modify scenes, prefabs, ProjectSettings, or Packages/manifest.json
  unless the task explicitly requires it.
- Do not allow multiple agents to edit the same files concurrently.

## Workflow
1. Understand the request and inspect relevant files.
2. Define scope, dependencies, risks, and acceptance criteria.
3. Review the plan before implementation.
4. Make changes only within the approved scope.
5. Inspect the diff and verify what can actually be tested.
6. Report modified files, test results, and remaining risks.

## Unity-specific checks
- Check Unity lifecycle methods and object destruction.
- Check event subscriptions and unsubscriptions.
- Check null references and missing serialized references.
- Consider prefab and scene dependencies.
- Distinguish static code review from tests performed in Unity Editor.
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

