using FluentValidation;
using SemanticPolicy;

namespace SupportTicketForm;

public sealed record SupportTicket(string Category, string Description);

public sealed class SupportTicketValidator : AbstractValidator<SupportTicket>
{
    private static readonly string[] _categories = ["billing", "technical", "account", "sales"];

    public SupportTicketValidator(IPolicyEvaluator evaluator)
    {
        // The first rule that fails ends the validation. A ticket that fails a cheap rule therefore never
        // reaches a model, and one whose description is flagged is not asked about its category as well.
        ClassLevelCascadeMode = CascadeMode.Stop;
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(ticket => ticket.Category)
            .Must(category => _categories.Contains(category, StringComparer.Ordinal))
            .WithMessage("'{PropertyName}' must be one of billing, technical, account or sales.");
        RuleFor(ticket => ticket.Description)
            .NotEmpty()
            .MinimumLength(20);

        // The description alone, sent as the policy's one text part.
        RuleFor(ticket => ticket.Description)
            .Semantic(evaluator, "ticket-description");

        // Both fields, category first. tools/SemanticPolicy.Evals/datasets/examples/support-ticket.jsonl names
        // the same two parts in the same order, so renaming or reordering them here leaves that dataset
        // measuring a different input.
        RuleFor(ticket => ticket.Category)
            .Semantic(evaluator, "ticket-category", ticket => new SemanticContext(
            [
                ContextPart.Text("category", ticket.Category),
                ContextPart.Text("description", ticket.Description),
            ]));
    }
}
