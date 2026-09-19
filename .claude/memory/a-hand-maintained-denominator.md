# A hand-maintained denominator

A guard that checks "every X is covered" cannot see the X nobody added to
its list, and a duplicated roster is the same defect written twice: two
lists that "compute the same thing" (a label set, a `CloudError` class
enum, a `JobPhase` state table, a viewer-support matrix) routinely drift by
exactly one entry the moment one is edited and its sibling is not.

**Fix by widening the predicate, never by narrowing the scope.** Narrowing
a guard to the sites it already sees moves the blind spot rather than
closing it, while turning the report green — which is exactly why this
shape recurs across projects. Prefer a whole-tree census over a
hand-maintained roster wherever the guard lives in `Guards.Tests` or
`scripts/check_*.py`. Full elaboration:
`.claude/skills/fixing-a-bug/bug-shapes.md`.
