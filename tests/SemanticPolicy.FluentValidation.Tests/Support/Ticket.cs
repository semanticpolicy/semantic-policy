namespace SemanticPolicy.FluentValidation.Tests.Support;

/// <summary>
/// What the tests validate. <see cref="Description"/> is a <c>string</c> and <see cref="Notes"/> a
/// <c>string?</c>, so the rules written on them compile the one-field overloads against both
/// nullabilities of their receiver; <see cref="Priority"/> is a property that is not text at all.
/// </summary>
internal sealed record Ticket(string Category, string Description, string? Notes = null, int Priority = 0);
