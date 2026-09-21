namespace SemanticPolicy.Protocol;

/// <summary>
/// What a <c>true</c> and a <c>false</c> answer look like for a Boolean question. Either side may be
/// absent; the provider then reads the question on its own.
/// </summary>
/// <param name="True">What an input that should be answered <c>true</c> looks like.</param>
/// <param name="False">What an input that should be answered <c>false</c> looks like.</param>
public sealed record BooleanCriteria(string? True, string? False);
