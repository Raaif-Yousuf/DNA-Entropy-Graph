# "Wired to nothing" is the house bug

Code that compiles, runs, passes its tests, is reviewed as correct, and does
**nothing** at runtime. Inherited as a named bug class from the prototype
team's own experience (an optional cloud parameter no caller ever set, a
hook that ran on a render one frame too late), and this project has its own
new shapes on top: a WinUI binding to a property that does not exist, a
Google Cloud resource created with no label so the Cloud page can never find
it again.

The reason tests rarely catch it: a unit test mounts the thing and calls it
directly, which is exactly the step missing in production.

**The one rule:** before saying a change is done, name the single
observable that would differ between "this works" and "this is wired to
nothing," and go check it. Full per-shape checklist:
`.claude/skills/wired-to-nothing/SKILL.md`.
