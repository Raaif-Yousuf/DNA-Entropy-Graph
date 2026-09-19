# Closing on the route, not the observable

Deciding an issue is done because "the diff looks right," or because a code
path was traced link by link and every link was present, is not the same
claim as checking the one observable that would differ if the change were
wired to nothing. A link-by-link trace can find every hop present and still
miss a runtime precondition that keeps the whole chain from ever firing.

Before closing any issue: state the commit sha(s), the acceptance criteria,
and the one observable you actually went and checked — see
`.claude/skills/working-an-issue/SKILL.md` step 3.
