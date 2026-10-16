- feat(app): a guard now ties every run error code to the one action its message asks for (#176). RunErrorActions (Core) declares the action per code; ErrorMessageActionGuardTests fails when a RunErrorCodes code has no declared action, or when its RunError_* message does not say that action (or contains a dash). All 46 existing messages already passed, so no copy changed.

