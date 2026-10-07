using SemanticPolicy.Mcp.Gateway;

// The gateway does not serve yet: a sound configuration ends the command with success, a wrong one with its refusal.
return await GatewayCli.RunAsync(args, Console.Out, Console.Error, (_, _, _) => Task.FromResult(ExitCodes.Success));
