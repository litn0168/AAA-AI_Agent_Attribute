---
name: unity-code-review
description: Review Unity code changes for correctness, lifecycle bugs, dependencies, maintainability, and regressions.
---

# Unity Code Review

1. Inspect the actual diff and surrounding code.
2. Check correctness, null handling, lifecycle, event subscriptions, and object destruction.
3. Check dependencies and compatibility with existing callers.
4. Consider performance and allocations in frequently called methods.
5. Check serialized fields and potential scene or prefab dependencies.
6. Report only actionable findings, ranked by severity.
7. Include evidence and a proposed correction.
8. Never edit files or claim tests were run without evidence.
