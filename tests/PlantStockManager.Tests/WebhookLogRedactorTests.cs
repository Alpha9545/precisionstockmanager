using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Daily Report webhook receiver: WebhookLogRedactor must never let an
    // API key/token/password/secret/signature reach the log, whatever the
    // payload's shape.
    public class WebhookLogRedactorTests
    {
        [Theory]
        [InlineData("apiKey")]
        [InlineData("api_key")]
        [InlineData("token")]
        [InlineData("accessToken")]
        [InlineData("secret")]
        [InlineData("client_secret")]
        [InlineData("password")]
        [InlineData("pwd")]
        [InlineData("Authorization")]
        [InlineData("auth_header")]
        [InlineData("signature")]
        [InlineData("credential")]
        public void IsSensitiveKey_RecognizesCommonSecretFieldNames(string key)
            => Assert.True(WebhookLogRedactor.IsSensitiveKey(key));

        [Theory]
        [InlineData("status")]
        [InlineData("recipient")]
        [InlineData("timestamp")]
        [InlineData("messageId")]
        public void IsSensitiveKey_DoesNotFlagOrdinaryFields(string key)
            => Assert.False(WebhookLogRedactor.IsSensitiveKey(key));

        [Fact]
        public void RedactJson_TopLevelSecret_IsReplaced()
        {
            var redacted = WebhookLogRedactor.RedactJson("""{"status":"sent","apiKey":"sk_live_abc123"}""");
            Assert.NotNull(redacted);
            Assert.DoesNotContain("sk_live_abc123", redacted);
            Assert.Contains(WebhookLogRedactor.RedactedPlaceholder, redacted);
            Assert.Contains("\"status\":\"sent\"", redacted);
        }

        [Fact]
        public void RedactJson_NestedSecret_IsReplaced()
        {
            var redacted = WebhookLogRedactor.RedactJson("""{"auth":{"token":"eyJhbGciOi.secret.value"},"status":"sent"}""");
            Assert.NotNull(redacted);
            Assert.DoesNotContain("eyJhbGciOi.secret.value", redacted);
        }

        [Fact]
        public void RedactJson_SecretInsideArray_IsReplaced()
        {
            var redacted = WebhookLogRedactor.RedactJson("""{"events":[{"password":"hunter2"},{"password":"hunter3"}]}""");
            Assert.NotNull(redacted);
            Assert.DoesNotContain("hunter2", redacted);
            Assert.DoesNotContain("hunter3", redacted);
        }

        [Fact]
        public void RedactJson_NoSensitiveFields_IsUnchangedInSubstance()
        {
            var redacted = WebhookLogRedactor.RedactJson("""{"status":"sent","count":10}""");
            Assert.NotNull(redacted);
            Assert.Contains("\"status\":\"sent\"", redacted);
            Assert.Contains("\"count\":10", redacted);
            Assert.DoesNotContain(WebhookLogRedactor.RedactedPlaceholder, redacted);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("{ not valid json ]]]")]
        [InlineData("just plain text")]
        public void RedactJson_InvalidOrEmptyInput_ReturnsNull_NeverThrows(string input)
            => Assert.Null(WebhookLogRedactor.RedactJson(input));
    }
}
