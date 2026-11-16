namespace SemanticPolicy.Evals.Format;

/// <summary>
/// A dataset or a recording that cannot be read or written as it is: a file that cannot be opened, a malformed
/// line, a label outside a rule's vocabulary, or a row a writer refuses. The message names a path, a line, a row
/// id, a rule id or a field, and never a row's input, a metadata value or a parser's message, so it can be shown
/// or logged as it is.
/// </summary>
/// <param name="message">What went wrong and where, without any content from a row.</param>
public sealed class EvalsFormatException(string message) : Exception(message);
