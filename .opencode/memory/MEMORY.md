# Project Memory

## Project Overview
- Unity game project using OpenCode for multi-agent development.
- Primary agent: orchestrator.
- Specialist agents: architect, developer, reviewer, qa, art-designer, technical-artist.
- Agent configurations are stored in .opencode/agents/.
- Project-wide instructions are stored in AGENTS.md.

## Safety Rules
- Verify the current project state before making changes.
- Do not overwrite important scenes, prefabs, or assets without authorization.
- Verify code changes with appropriate compilation checks and tests.
- Do not treat an agent's success report as proof that the change works.
- Preserve the existing agent permission boundaries.
- Never use destructive Git commands without checking for uncommitted work.

## Known Risks
- SceneBuilder.cs may overwrite DarkRealms_Proto_01.unity.
- WorldTransitionPlayTests.cs previously had a CS1503 error near line 180.
- HazardZone damage, maximum health, and respawn behavior need coordinated testing.
- These are historical notes. Verify whether the issues still exist before acting.

## Workflow
- Check git status before important Git operations.
- Record verified architectural decisions and confirmed fixes.
- Keep temporary debugging output out of persistent memory.
- Update this file only when durable project knowledge changes.
