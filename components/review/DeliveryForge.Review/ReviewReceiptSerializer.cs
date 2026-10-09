using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeliveryForge.Contracts.Models;
using DeliveryForge.Contracts.Serialization;
using DeliveryForge.Contracts.Validation;

namespace DeliveryForge.Review;

public static class ReviewReceiptSerializer
{
    private static readonly JsonSerializerOptions ContractJson = CreateOptions();

    public static ReviewReceiptContract Create(NormalizedReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        var receiptOutcome = review.Outcome == ReviewOutcome.Pass && !review.ProductionAccepted
            ? ReviewOutcome.Incomplete
            : review.Outcome;
        var provisional = new ReviewReceiptContract(
            "1.0.0",
            "review-receipt",
            $"sha256:{new string('0', 64)}",
            review.Identity.BaseCommit,
            review.Identity.HeadCommit,
            review.Identity.HeadTree,
            review.Identity.PatchSha256,
            review.ReviewerFamily,
            OutcomeText(receiptOutcome),
            review.Findings.Count,
            review.ArtifactHash,
            review.CompletedAt.ToUniversalTime());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(provisional, ContractJson);
        var receipt = provisional with { Identity = CanonicalJson.ComputeIdentity(bytes) };
        ContractValidator.ParseAndValidate(JsonSerializer.SerializeToUtf8Bytes(receipt, ContractJson));
        return receipt;
    }

    public static byte[] Serialize(ReviewReceiptContract receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, ContractJson);
        ContractValidator.ParseAndValidate(bytes);
        return bytes;
    }

    private static string OutcomeText(ReviewOutcome outcome) => outcome switch
    {
        ReviewOutcome.Pass => "PASS",
        ReviewOutcome.Fail => "FAIL",
        ReviewOutcome.Incomplete => "INCOMPLETE",
        ReviewOutcome.Error => "ERROR",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome))
    };

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new UtcDateTimeOffsetConverter());
        return options;
    }

    private sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            var value = reader.GetString();
            if (!DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var parsed))
                throw new JsonException("Expected an RFC 3339 timestamp.");
            return parsed.ToUniversalTime();
        }

        public override void Write(
            Utf8JsonWriter writer,
            DateTimeOffset value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToUniversalTime().ToString(
                "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture));
    }
}
