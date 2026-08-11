using System.Net;
using System.Text;
using System.Text.Json;
using VrcVa.Core;

namespace VrcVa.Infrastructure.Tests;

public sealed class OpenAiTextTranslatorTests
{
    [Fact]
    public async Task TranslateToJapaneseAsync_SendsTextOnlyPrivacyRequestAndParsesOutput()
    {
        RecordingHandler handler = new(HttpStatusCode.OK, """
            {
              "output": [
                {
                  "type": "message",
                  "content": [
                    { "type": "output_text", "text": "非常口です。" }
                  ]
                }
              ]
            }
            """);
        using HttpClient client = new(handler);
        OpenAiTextTranslator translator = new(
            client,
            CreateOptions(),
            "test-api-key");

        TranslationOutput result = await translator.TranslateToJapaneseAsync(
            "Emergency exit",
            CancellationToken.None);

        Assert.Equal("非常口です。", result.Text);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("test-api-key", handler.AuthorizationParameter);
        using JsonDocument request = JsonDocument.Parse(handler.RequestBody!);
        Assert.False(request.RootElement.GetProperty("store").GetBoolean());
        Assert.Equal("Emergency exit", request.RootElement.GetProperty("input").GetString());
        Assert.False(handler.RequestBody!.Contains("input_image", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TranslateToJapaneseAsync_MissingKeyDoesNotSendRequest()
    {
        RecordingHandler handler = new(HttpStatusCode.OK, "{}");
        using HttpClient client = new(handler);
        OpenAiTextTranslator translator = new(client, CreateOptions(), apiKey: null);

        ScanException exception = await Assert.ThrowsAsync<ScanException>(() =>
            translator.TranslateToJapaneseAsync("Welcome", CancellationToken.None));

        Assert.Equal(ScanFailureCode.TranslationNotConfigured, exception.FailureCode);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task TranslateToJapaneseAsync_ClassifiesAuthenticationFailure()
    {
        RecordingHandler handler = new(
            HttpStatusCode.Unauthorized,
            "secret provider body",
            requestId: "req_test_123");
        using HttpClient client = new(handler);
        OpenAiTextTranslator translator = new(client, CreateOptions(), "bad-key");

        ScanException exception = await Assert.ThrowsAsync<ScanException>(() =>
            translator.TranslateToJapaneseAsync("Welcome", CancellationToken.None));

        Assert.Equal(ScanFailureCode.TranslationAuthenticationFailed, exception.FailureCode);
        Assert.DoesNotContain("secret provider body", exception.Message, StringComparison.Ordinal);
        Assert.Contains("req_test_123", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PrivacySafeFileLogger_DoesNotWriteExceptionMessage()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"vrcva-test-{Guid.NewGuid():N}");
        try
        {
            PrivacySafeFileLogger logger = new(directory);
            logger.Error(
                "test.error",
                Guid.Parse("a10893b8-4b88-46ab-aa79-580dfd49d3a3"),
                ScanStage.Translation,
                ScanFailureCode.TranslationFailed,
                new InvalidOperationException("OCR_CONTENT_AND_SECRET"));

            string log = File.ReadAllText(Directory.GetFiles(directory).Single());
            Assert.DoesNotContain("OCR_CONTENT_AND_SECRET", log, StringComparison.Ordinal);
            Assert.Contains("exceptionType=InvalidOperationException", log, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static OpenAiTranslatorOptions CreateOptions() => new()
    {
        Endpoint = new Uri("https://example.test/v1/responses"),
        Model = "test-model",
        Timeout = TimeSpan.FromSeconds(2),
    };

    private sealed class RecordingHandler(
        HttpStatusCode statusCode,
        string responseBody,
        string? requestId = null)
        : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        public string? RequestBody { get; private set; }

        public string? AuthorizationScheme { get; private set; }

        public string? AuthorizationParameter { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCount++;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            HttpResponseMessage response = new(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
            if (requestId is not null)
            {
                response.Headers.Add("x-request-id", requestId);
            }

            return response;
        }
    }
}
