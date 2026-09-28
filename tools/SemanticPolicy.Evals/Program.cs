using System.CommandLine;
using SemanticPolicy.Evals.Cli;

// After Ctrl+C the command line library cancels `run` and ends the process once this much time has passed, finished
// or not. A resume cut short still copies every row it has not written into its rewrite before that rewrite takes the
// recording's place, and the copy grows with the recording: the library's 2 s is not enough for a large one, and past
// the window the rewrite is abandoned with every call the resume paid for. `run` returns as soon as it is done, so
// the longer window is only ever waited out in full by a call that ignores its cancellation.
InvocationConfiguration configuration = new() { ProcessTerminationTimeout = TimeSpan.FromSeconds(30) };
return await EvalsCli.Build(configureProviders: Providers.Register).Parse(args).InvokeAsync(configuration);
