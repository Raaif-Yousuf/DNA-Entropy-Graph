# FluentAssertions 8+ requires a commercial licence

A generally known, externally verifiable fact (not specific to this
project): FluentAssertions moved to a paid licence model starting with its
8.x line. This project's C# test stack is **xUnit v3, Shouldly, and
NSubstitute** specifically to avoid it (and to avoid Moq, for its own
unrelated SponsorLink telemetry history) — see `CLAUDE.md`'s Stack table.

If a `dotnet add package` for a test project ever pulls in
`FluentAssertions` transitively or by habit, that is a licensing problem
worth catching in review, not just a style preference.
