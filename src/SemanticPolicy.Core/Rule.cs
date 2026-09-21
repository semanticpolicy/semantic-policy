using System.Text.Json.Serialization;
using SemanticPolicy.Protocol;

namespace SemanticPolicy;

/// <summary>
/// One question a policy asks, with what each answer means. A rule names the question, the decision
/// type and the verdict each answer maps to; it carries no numbers. Which provider answers it and at
/// what threshold is the binding's operating point, so one rule can be measured on one provider and run
/// on another. A rule is a definition: it holds what it is given and is checked by
/// <see cref="Policy.Validate"/>, not by its constructor.
/// </summary>
/// <param name="Id">The id a binding's operating point and an evaluation result refer to the rule by.</param>
/// <param name="Question">The question, as the provider reads it.</param>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(BooleanRule), "boolean")]
[JsonDerivedType(typeof(ChoiceRule), "choice")]
[JsonDerivedType(typeof(ScoreRule), "score")]
public abstract record Rule(string Id, string Question)
{
    /// <summary>The id a binding's operating point and an evaluation result refer to the rule by.</summary>
    public string Id { get; init; } = Id ?? throw new ArgumentNullException(nameof(Id));

    /// <summary>The question, as the provider reads it.</summary>
    public string Question { get; init; } = Question ?? throw new ArgumentNullException(nameof(Question));

    /// <summary>The shape of the answer the rule asks for; on the wire it is the <c>type</c> discriminator.</summary>
    // Each override carries [JsonIgnore], not this declaration: the serializer reads the attribute off the
    // derived record's property, and an un-ignored "type" property clashes with the discriminator at the
    // first serialization.
    public abstract DecisionType Type { get; }

    /// <summary>
    /// The protocol request that asks this rule's question about a context. The context goes on the wire
    /// as <see cref="SemanticContext.ToJson"/> renders it; the correlation id never does. The request is
    /// built from the rule as it is, so a rule that would fail <see cref="Policy.Validate"/> may produce a
    /// request that fails <see cref="DecisionRequest.EnsureValid"/>.
    /// </summary>
    /// <param name="context">The thing the question is about.</param>
    public abstract DecisionRequest CreateRequest(SemanticContext context);
}
