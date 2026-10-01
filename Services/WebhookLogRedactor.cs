using System.Text.Json;
using System.Text.Json.Nodes;

namespace PlantStockManager.Services
{
    // Daily Report webhook receiver: a pure, database-free helper that
    // redacts likely-sensitive fields (API keys/tokens/passwords/secrets/
    // signatures) out of an arbitrary, unknown-shape JSON payload before it
    // is ever written to the log. The payload structure is not controlled
    // by this app (it's whatever Sendvise sends), so this errs conservative
    // -- any key whose name CONTAINS one of the known fragments (case-
    // insensitive) has its value replaced, at any nesting depth, in objects
    // and arrays alike.
    public static class WebhookLogRedactor
    {
        private static readonly string[] SensitiveKeyFragments =
        {
            "key", "token", "secret", "password", "pwd", "auth", "signature", "credential"
        };

        public const string RedactedPlaceholder = "***REDACTED***";

        public static bool IsSensitiveKey(string key)
            => SensitiveKeyFragments.Any(f => key.Contains(f, StringComparison.OrdinalIgnoreCase));

        // Returns the redacted JSON text, or null if rawJson is not valid
        // JSON (the caller decides what to log in that case -- this
        // function never throws).
        public static string? RedactJson(string rawJson)
        {
            if (string.IsNullOrWhiteSpace(rawJson))
                return null;
            try
            {
                var node = JsonNode.Parse(rawJson);
                var redacted = Redact(node);
                return redacted?.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) ?? "null";
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static JsonNode? Redact(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    var newObj = new JsonObject();
                    foreach (var kvp in obj)
                    {
                        newObj[kvp.Key] = IsSensitiveKey(kvp.Key)
                            ? JsonValue.Create(RedactedPlaceholder)
                            : Redact(kvp.Value);
                    }
                    return newObj;

                case JsonArray arr:
                    var newArr = new JsonArray();
                    foreach (var item in arr)
                        newArr.Add(Redact(item));
                    return newArr;

                default:
                    return node?.DeepClone();
            }
        }
    }
}
