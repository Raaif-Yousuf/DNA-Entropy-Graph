# A check that cannot fail is not a check

A guard, test, or scanner whose "pass" outcome and its own broken path are
the same outcome is not evidence of anything: a count assertion satisfied
by zero matches, a scanner that silently matches nothing and reports a
clean tree, a test that asserts `"proxy" in calls[0]` which still passes if
the production argument that put `"proxy"` there was deleted, because a
stub's own default happens to supply the same string.

**The mandatory check:** mutation-test it. Break the production code on
purpose and confirm the test/guard goes red with a message that names the
real problem, then restore. An arm that stays green under a deliberate
break is a coverage hole, not a pass — see
`.claude/skills/fixing-a-bug/SKILL.md` step 4 for the full discipline and
`.claude/skills/tests-first/SKILL.md`'s non-negotiables for where this
applies to a new guard specifically.
