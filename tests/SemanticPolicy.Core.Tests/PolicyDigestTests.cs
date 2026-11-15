using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SemanticPolicy.Protocol;

namespace SemanticPolicy.Core.Tests;

public sealed class PolicyDigestTests
{
    // Pinned() in RFC 8785 canonical form, and the SHA-256 of its UTF-8 bytes. Both were produced outside
    // this library, from the JSON SemanticPolicyJson.Options writes for Pinned(): Node.js parsed it, wrote every
    // member sorted by UTF-16 code units and every value with JSON.stringify, and hashed the result. A stored
    // verdict names its policy by this value, so a failure here means every existing policy's digest moved. A
    // change that has to alter a policy's canonical JSON is a new scheme under a new prefix, never an update of
    // these two values; a member added to a policy is omitted at its default and leaves them as they are.
    private const string _pinnedCanonical = """
        {"bindings":[{"operatingPoints":[{"gate":{"below":0.05,"kind":"score"},"ruleId":"injection",
        "thresholds":[{"atOrAbove":0,"kind":"score","verdict":"warn"},{"atOrAbove":0.25,"kind":"score",
        "verdict":"escalate"},{"atOrAbove":0.75,"kind":"score","verdict":"deny"}]},{"gate":{"below":0.5,
        "kind":"logit"},"ruleId":"leak","thresholds":[{"atOrAbove":-1.5,"kind":"logit","verdict":"warn"},
        {"atOrAbove":2,"kind":"logit","verdict":"deny"}]},{"gate":{"below":0.1,"kind":"probability"},
        "ruleId":"route","thresholds":[]},{"ruleId":"harm","thresholds":[]}],"providerId":"local"},
        {"operatingPoints":[{"calibration":{"intercept":0,"method":"platt",
        "provenance":{"datasetDigest":"sha256:dataset-marker","flaggedRows":120,"model":"model-j",
        "otherRows":380,"split":"calibration"},"slope":1.25,"sourceKind":"probability","transform":"logOdds"},
        "gate":{"below":0.02,"kind":"probability"},"ruleId":"injection","thresholds":[{"atOrAbove":0.00001,
        "kind":"probability","verdict":"warn"},{"atOrAbove":0.5,"kind":"probability","verdict":"escalate"},
        {"atOrAbove":0.95,"kind":"probability","verdict":"deny"}]},{"calibration":{"intercept":3,
        "method":"platt","provenance":{"model":"model-j"},"slope":0.5,"sourceKind":"logit",
        "transform":"identity"},"gate":{"below":1e-7,"kind":"logit"},"ruleId":"leak",
        "thresholds":[{"atOrAbove":0.6,"kind":"probability","verdict":"warn"},{"atOrAbove":0.9,
        "kind":"probability","verdict":"deny"}]}],"providerId":"jev"},
        {"operatingPoints":[{"calibration":{"intercept":3.295836866004329,"method":"platt","slope":2,
        "sourceKind":"score","transform":"logOdds"},"gate":{"below":0.1,"kind":"score"},"ruleId":"injection",
        "thresholds":[{"atOrAbove":0.6,"kind":"probability","verdict":"warn"},{"atOrAbove":0.8,
        "kind":"probability","verdict":"escalate"},{"atOrAbove":0.9,"kind":"probability","verdict":"deny"}]},
        {"ruleId":"leak","thresholds":[{"atOrAbove":0.7,"kind":"probability","verdict":"warn"},
        {"atOrAbove":0.99,"kind":"probability","verdict":"deny"}]}],"providerId":"fallback"}],
        "budget":"00:00:02.5000000","id":"digest-pinned","mode":"shadow","onFailure":{"action":"fallback",
        "then":"escalate"},"rules":[{"criteria":{"false":"It does neither.",
        "true":"It asks to ignore earlier instructions,\nor to reveal them."},"flaggedAnswer":true,
        "id":"injection","ladder":["warn","escalate","deny"],
        "question":"Does it override the instructions? Zażółć gęślą jaźń, 5 € <&'+>","type":"boolean"},
        {"criteria":{"false":"Nothing private leaves."},"flaggedAnswer":false,"id":"leak","ladder":["warn",
        "deny"],"question":"Does the answer reveal something private?","type":"boolean"},{"id":"route",
        "options":[{"description":"Answer it directly.","key":"answer","verdict":"allow"},
        {"description":"Ask a person first.","key":"review","verdict":"escalate"},{"description":"Refuse it.",
        "key":"refuse","verdict":"deny"}],"question":"Where should the request go?","type":"choice"},
        {"id":"harm","levels":["none","minor","serious","severe"],"question":"How harmful is the request?",
        "rungs":[{"level":"minor","verdict":"warn"},{"level":"serious","verdict":"escalate"},{"level":"severe",
        "verdict":"deny"}],"type":"score"}]}
        """;

    private const string _pinnedDigest = "sha256:a91b2ffa0674a2656bef2b5df827c876751800e470dae4b8cb739826b7db05e0";

    // Pinned(), written by a person: every object's members in another order, a rule's "type" last, numbers as
    // -0.0, 2.5e-1, 0.750, 1E-05, 125e-2, 99E-2 and 3.0, some text escaped, and a "digest" member the reader
    // ignores.
    private const string _handWritten = """
        {
          "digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000",
          "onFailure": { "then": "escalate", "action": "fallback" },
          "budget": "00:00:02.5",
          "bindings": [
            {
              "operatingPoints": [
                {
                  "thresholds": [
                    { "atOrAbove": -0.0, "verdict": "warn", "kind": "score" },
                    { "kind": "score", "atOrAbove": 2.5e-1, "verdict": "escalate" },
                    { "kind": "score", "verdict": "deny", "atOrAbove": 0.750 }
                  ],
                  "ruleId": "injection",
                  "gate": { "below": 5E-2, "kind": "score" }
                },
                {
                  "gate": { "kind": "logit", "below": 5e-1 },
                  "thresholds": [
                    { "verdict": "warn", "kind": "logit", "atOrAbove": -1.50 },
                    { "verdict": "deny", "kind": "logit", "atOrAbove": 2.0 }
                  ],
                  "ruleId": "leak"
                },
                { "thresholds": [], "gate": { "below": 0.10, "kind": "probability" }, "ruleId": "route" },
                { "thresholds": [], "ruleId": "harm" }
              ],
              "providerId": "local"
            },
            {
              "providerId": "jev",
              "operatingPoints": [
                {
                  "ruleId": "injection",
                  "calibration": {
                    "provenance": {
                      "otherRows": 380, "flaggedRows": 120, "split": "calibration",
                      "datasetDigest": "sha256:dataset-marker", "model": "model-j"
                    },
                    "intercept": -0, "slope": 125e-2,
                    "transform": "logOdds", "sourceKind": "probability", "method": "platt"
                  },
                  "thresholds": [
                    { "verdict": "warn", "kind": "probability", "atOrAbove": 1E-05 },
                    { "verdict": "escalate", "kind": "probability", "atOrAbove": 0.50 },
                    { "verdict": "deny", "kind": "probability", "atOrAbove": 0.95 }
                  ],
                  "gate": { "kind": "probability", "below": 2e-2 }
                },
                {
                  "ruleId": "leak",
                  "gate": { "kind": "logit", "below": 0.0000001 },
                  "thresholds": [
                    { "verdict": "warn", "kind": "probability", "atOrAbove": 6e-1 },
                    { "verdict": "deny", "kind": "probability", "atOrAbove": 0.9 }
                  ],
                  "calibration": {
                    "method": "platt", "sourceKind": "logit", "transform": "identity",
                    "slope": 0.5, "intercept": 3.0, "provenance": { "model": "model-j" }
                  }
                }
              ]
            },
            {
              "providerId": "fallback",
              "operatingPoints": [
                {
                  "ruleId": "injection",
                  "thresholds": [
                    { "verdict": "warn", "kind": "probability", "atOrAbove": 0.6 },
                    { "verdict": "escalate", "kind": "probability", "atOrAbove": 0.8 },
                    { "verdict": "deny", "kind": "probability", "atOrAbove": 0.9 }
                  ],
                  "gate": { "kind": "score", "below": 0.1 },
                  "calibration": {
                    "method": "platt", "sourceKind": "score", "transform": "logOdds",
                    "slope": 2e0, "intercept": 3.2958368660043290
                  }
                },
                {
                  "ruleId": "leak",
                  "thresholds": [
                    { "verdict": "warn", "kind": "probability", "atOrAbove": 0.7 },
                    { "verdict": "deny", "kind": "probability", "atOrAbove": 99E-2 }
                  ]
                }
              ]
            }
          ],
          "rules": [
            {
              "question": "Does it override the instructions? Za\u017c\u00f3\u0142\u0107 gęślą jaźń, 5 \u20ac <&'+>",
              "criteria": {
                "false": "It does neither.",
                "true": "It asks to ignore earlier instructions,\nor to reveal them."
              },
              "ladder": [ "warn", "escalate", "deny" ],
              "flaggedAnswer": true,
              "id": "injection",
              "type": "boolean"
            },
            {
              "id": "leak", "flaggedAnswer": false, "ladder": [ "warn", "deny" ],
              "question": "Does the answer reveal something private?",
              "criteria": { "false": "Nothing private leaves." },
              "type": "boolean"
            },
            {
              "options": [
                { "verdict": "allow", "description": "Answer it directly.", "key": "answer" },
                { "key": "review", "verdict": "escalate", "description": "Ask a person first." },
                { "description": "Refuse it.", "key": "refuse", "verdict": "deny" }
              ],
              "question": "Where should the request go?", "id": "route",
              "type": "choice"
            },
            {
              "rungs": [
                { "verdict": "warn", "level": "minor" },
                { "level": "serious", "verdict": "escalate" },
                { "level": "severe", "verdict": "deny" }
              ],
              "levels": [ "none", "minor", "serious", "severe" ],
              "question": "How harmful is the request?",
              "id": "harm",
              "type": "score"
            }
          ],
          "mode": "shadow",
          "id": "digest-pinned"
        }
        """;

    [Fact]
    public void Pinned_Policy_Has_The_Pinned_Canonical_Json_And_Digest()
    {
        Policy policy = Pinned();

        JsonNode? written = JsonSerializer.SerializeToNode(policy, SemanticPolicyJson.Options);

        byte[] canonical = CanonicalJson.ToUtf8Bytes(written);

        Encoding.UTF8.GetString(canonical).Should().Be(_pinnedCanonical.ReplaceLineEndings(string.Empty));
        policy.Digest.Should().Be(_pinnedDigest);
    }

    [Fact]
    public void Hand_Written_Policy_With_Another_Order_And_Number_Forms_Has_The_Built_Policys_Digest()
    {
        Policy? read = JsonSerializer.Deserialize<Policy>(_handWritten, SemanticPolicyJson.Options);

        read.Should().NotBeNull();
        read!.Digest.Should().Be(Pinned().Digest);
    }

    // Each row replaces one member of Pinned()'s JSON with another value and reads the policy back.
    public static TheoryData<string, string> Parts => new()
    {
        { "id", "\"digest-other\"" },
        { "mode", "\"enforce\"" },
        { "rules/0/question", "\"Does it override the instructions?\"" },
        { "rules/0/criteria/true", "\"It asks to ignore earlier instructions.\"" },
        { "bindings/0/operatingPoints/0/thresholds/1/atOrAbove", "0.3" },
        { "bindings/0/operatingPoints/0/gate/below", "0.06" },
        { "rules/2/options/1/key", "\"escalate\"" },
        { "rules/2/options/1/description", "\"Ask someone first.\"" },
        { "rules/2/options/1/verdict", "\"warn\"" },
        { "bindings/1/operatingPoints/0/calibration/slope", "1.5" },
        { "bindings/1/operatingPoints/0/calibration/provenance/flaggedRows", "121" },
        { "onFailure/then", "\"deny\"" },
        { "budget", "\"00:00:03\"" },
    };

    [Theory]
    [MemberData(nameof(Parts))]
    public void Changing_One_Part_Of_The_Policy_Changes_Its_Digest(string path, string value)
    {
        JsonNode document = JsonSerializer.SerializeToNode(Pinned(), SemanticPolicyJson.Options)!;
        string[] segments = path.Split('/');
        JsonNode owner = document;
        foreach (string segment in segments[..^1])
        {
            owner = int.TryParse(segment, out int index) ? owner[index]! : owner[segment]!;
        }

        owner[segments[^1]] = JsonNode.Parse(value);
        Policy changed = document.Deserialize<Policy>(SemanticPolicyJson.Options)!;

        changed.Digest.Should().NotBe(Pinned().Digest, path);
    }

    // A copy shares nothing kept for the instance it was made from: neither the digest, which would name the
    // original's content, nor anything record equality would compare.
    [Fact]
    public void A_Copy_Has_The_Digest_Of_Its_Own_Content()
    {
        Policy original = Pinned();
        Policy copiedBeforeTheRead = original with { };
        string digest = original.Digest;
        Policy enforced = original with { Mode = PolicyMode.Enforce };
        Policy copiedAfterTheRead = original with { };

        copiedBeforeTheRead.Should().Be(original);
        enforced.Digest.Should().NotBe(digest);
        copiedAfterTheRead.Digest.Should().Be(digest);
        copiedBeforeTheRead.Digest.Should().Be(digest);
        original.Digest.Should().BeSameAs(digest);
    }

    [Theory]
    [InlineData(double.NaN, 1.0)]
    [InlineData(0.5, double.PositiveInfinity)]
    public void Digest_Of_A_Policy_The_Serializer_Cannot_Write_Throws_Policy_Configuration_Exception(
        double threshold,
        double slope)
    {
        Policy policy = new(
            "digest-unwritable",
            PolicyMode.Enforce,
            [new BooleanRule("rule", "question-marker", FlaggedAnswer: true, [Verdict.Deny])],
            [
                new ProviderBinding(
                    "provider",
                    [
                        new RuleOperatingPoint(
                            "rule",
                            [new Threshold(Verdict.Deny, EvidenceKind.Probability, threshold)],
                            Calibration: new EvidenceCalibration(
                                CalibrationMethod.Platt,
                                EvidenceKind.Probability,
                                CalibrationTransform.LogOdds,
                                slope,
                                Intercept: 0)),
                    ]),
            ],
            FailureBehavior.Deny);

        Func<string> read = () => policy.Digest;

        PolicyConfigurationException error = read.Should().Throw<PolicyConfigurationException>().Which;
        error.PolicyId.Should().Be("digest-unwritable");
        error.Message.Should().Contain("digest-unwritable")
            .And.NotContain("question-marker")
            .And.NotContainAny("NaN", "Infinity", "\u221e")
            .And.NotMatchRegex(@"\d");
    }

    // The serializer's other refusals: a rule it has no discriminator for, which Validate rejects, and an enum
    // value with no name, which on the mode passes Validate, so a registered policy can hold one.
    [Theory]
    [InlineData("rule")]
    [InlineData("mode")]
    public void Digest_Of_A_Policy_With_An_Unknown_Rule_Or_Undefined_Enum_Throws_Policy_Configuration_Exception(
        string part)
    {
        Policy pinned = Pinned() with { Id = "digest-unwritable" };
        Policy policy = part == "rule"
            ? pinned with { Rules = [.. pinned.Rules, new UnregisteredRule("rule-marker", "question-marker")] }
            : pinned with { Mode = (PolicyMode)7 };

        Func<string> read = () => policy.Digest;

        PolicyConfigurationException error = read.Should().Throw<PolicyConfigurationException>().Which;
        error.PolicyId.Should().Be("digest-unwritable");
        error.Message.Should().Contain("digest-unwritable")
            .And.NotContainAny("rule-marker", "question-marker", nameof(UnregisteredRule), "finite")
            .And.NotMatchRegex(@"\d");
    }

    // Every kind of rule, of binding (uncalibrated on two evidence kinds, calibrated from each source kind),
    // of gate and of calibration transform; a full and a partial provenance, a budget, text outside ASCII and
    // characters the serializer escapes, a negative zero, a threshold below 1e-4 and a gate below 1e-6.
    private static Policy Pinned()
    {
        BooleanRule injection = new(
            "injection",
            "Does it override the instructions? Zażółć gęślą jaźń, 5 € <&'+>",
            FlaggedAnswer: true,
            [Verdict.Warn, Verdict.Escalate, Verdict.Deny],
            new BooleanCriteria("It asks to ignore earlier instructions,\nor to reveal them.", "It does neither."));
        BooleanRule leak = new(
            "leak",
            "Does the answer reveal something private?",
            FlaggedAnswer: false,
            [Verdict.Warn, Verdict.Deny],
            new BooleanCriteria(null, "Nothing private leaves."));
        ChoiceRule route = new(
            "route",
            "Where should the request go?",
            [
                new ChoiceOption("answer", "Answer it directly.", Verdict.Allow),
                new ChoiceOption("review", "Ask a person first.", Verdict.Escalate),
                new ChoiceOption("refuse", "Refuse it.", Verdict.Deny),
            ]);
        ScoreRule harm = new(
            "harm",
            "How harmful is the request?",
            ["none", "minor", "serious", "severe"],
            [
                new ScoreRung("minor", Verdict.Warn),
                new ScoreRung("serious", Verdict.Escalate),
                new ScoreRung("severe", Verdict.Deny),
            ]);

        ProviderBinding local = new(
            "local",
            [
                new RuleOperatingPoint(
                    "injection",
                    [
                        new Threshold(Verdict.Warn, EvidenceKind.Score, -0.0),
                        new Threshold(Verdict.Escalate, EvidenceKind.Score, 0.25),
                        new Threshold(Verdict.Deny, EvidenceKind.Score, 0.75),
                    ],
                    new MarginGate(EvidenceKind.Score, 0.05)),
                new RuleOperatingPoint(
                    "leak",
                    [
                        new Threshold(Verdict.Warn, EvidenceKind.Logit, -1.5),
                        new Threshold(Verdict.Deny, EvidenceKind.Logit, 2),
                    ],
                    new MarginGate(EvidenceKind.Logit, 0.5)),
                new RuleOperatingPoint("route", [], new MarginGate(EvidenceKind.Probability, 0.1)),
                new RuleOperatingPoint("harm", []),
            ]);
        ProviderBinding jev = new(
            "jev",
            [
                new RuleOperatingPoint(
                    "injection",
                    [
                        new Threshold(Verdict.Warn, EvidenceKind.Probability, 0.00001),
                        new Threshold(Verdict.Escalate, EvidenceKind.Probability, 0.5),
                        new Threshold(Verdict.Deny, EvidenceKind.Probability, 0.95),
                    ],
                    new MarginGate(EvidenceKind.Probability, 0.02),
                    new EvidenceCalibration(
                        CalibrationMethod.Platt,
                        EvidenceKind.Probability,
                        CalibrationTransform.LogOdds,
                        Slope: 1.25,
                        Intercept: -0.0,
                        new CalibrationProvenance("model-j", "sha256:dataset-marker", "calibration", 120, 380))),
                new RuleOperatingPoint(
                    "leak",
                    [
                        new Threshold(Verdict.Warn, EvidenceKind.Probability, 0.6),
                        new Threshold(Verdict.Deny, EvidenceKind.Probability, 0.9),
                    ],
                    new MarginGate(EvidenceKind.Logit, 1e-7),
                    new EvidenceCalibration(
                        CalibrationMethod.Platt,
                        EvidenceKind.Logit,
                        CalibrationTransform.Identity,
                        Slope: 0.5,
                        Intercept: 3,
                        new CalibrationProvenance(Model: "model-j"))),
            ]);
        ProviderBinding fallback = new(
            "fallback",
            [
                new RuleOperatingPoint(
                    "injection",
                    [
                        new Threshold(Verdict.Warn, EvidenceKind.Probability, 0.6),
                        new Threshold(Verdict.Escalate, EvidenceKind.Probability, 0.8),
                        new Threshold(Verdict.Deny, EvidenceKind.Probability, 0.9),
                    ],
                    new MarginGate(EvidenceKind.Score, 0.1),
                    new EvidenceCalibration(
                        CalibrationMethod.Platt,
                        EvidenceKind.Score,
                        CalibrationTransform.LogOdds,
                        Slope: 2,
                        Intercept: 3.295836866004329)),
                new RuleOperatingPoint(
                    "leak",
                    [
                        new Threshold(Verdict.Warn, EvidenceKind.Probability, 0.7),
                        new Threshold(Verdict.Deny, EvidenceKind.Probability, 0.99),
                    ]),
            ]);

        return new Policy(
            "digest-pinned",
            PolicyMode.Shadow,
            [injection, leak, route, harm],
            [local, jev, fallback],
            FailureBehavior.Fallback(Verdict.Escalate),
            TimeSpan.FromMilliseconds(2500));
    }

    // A kind of rule the library does not know. Rule is public and abstract, so code outside it can write one.
    private sealed record UnregisteredRule(string Id, string Question) : Rule(Id, Question)
    {
        [JsonIgnore]
        public override DecisionType Type => DecisionType.Boolean;

        public override DecisionRequest CreateRequest(SemanticContext context) => throw new UnreachableException();
    }
}
