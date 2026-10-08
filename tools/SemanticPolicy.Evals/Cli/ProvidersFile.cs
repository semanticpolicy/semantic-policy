using SemanticPolicy.Providers;

namespace SemanticPolicy.Evals.Cli;

/// <summary>
/// The providers file <c>run --providers</c> reads. Each entry maps a registration name to an adapter's kind and to
/// that adapter's own options class, and is registered the way an application registers the adapter. With a file,
/// its entries are every provider <c>run</c> may call.
/// </summary>
public static class ProvidersFile
{
    // Each adapter's timer is set past any `run --timeout`, so a timer in the file would only be overridden.
    private const string _timeoutRefusal = "run --timeout is the only limit on a call";

    /// <summary>
    /// Reads and checks the whole file, and returns the hook that registers each of its entries. Nothing is registered
    /// and no provider is built here; the hook registers every entry, bound or not, when <see cref="Providers.Resolve"/>
    /// applies it.
    /// </summary>
    /// <param name="path">The providers file.</param>
    /// <returns>A hook for <see cref="Providers.Resolve"/>, in place of <see cref="Providers.Register"/>.</returns>
    /// <exception cref="EvalsException">
    /// The file cannot be read, is not JSON, has a shape or an option the tool does not know, sets what the tool owns,
    /// gives a name twice, puts in a key variable something that is not a variable's name, or has a Jev route that
    /// names no key variable. The message names the file, the entry and the property, never a value. The hook throws
    /// it too, with the adapter's own message, when an adapter refuses its options.
    /// </exception>
    public static Action<ISemanticPolicyBuilder> Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        Action<ISemanticPolicyBuilder> register;
        try
        {
            register = ProvidersFileReader.Read(path, Providers.PastAnyRunTimeout, _timeoutRefusal).Register;
        }
        catch (ProvidersFileException refusal)
        {
            throw new EvalsException(refusal.Message);
        }

        return builder =>
        {
            try
            {
                register(builder);
            }
            catch (ProvidersFileException refusal)
            {
                throw new EvalsException(refusal.Message);
            }
        };
    }
}
