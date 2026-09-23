using SemanticPolicy.Evals.Cli;

return await EvalsCli.Build(configureProviders: Providers.Register).Parse(args).InvokeAsync();
