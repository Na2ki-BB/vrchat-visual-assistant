using System.Net;
using System.Text;
using System.Text.Json;
using VrcVa.Core;

namespace VrcVa.Infrastructure.Tests;

public sealed class OpenAiTextTranslatorTests
{
    [Fact]
    public void OptionsFromEnvironment_BoundsTimeoutAtTwentyFiveSeconds()
    {
        const string variable = "VRCVA_OPENAI_TIMEOUT_SECONDS";
        string? original = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "25");
            OpenAiTranslatorOptions options = OpenAiTranslatorOptions.FromEnvironment();
            Assert.Equal(TimeSpan.FromSeconds(25), options.Timeout);

            Environment.SetEnvironmentVariable(variable, "26");
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                OpenAiTranslatorOptions.FromEnvironment);
            Assert.Contains("1 to 25", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, original);
        }
    }

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
        Assert.Equal(
            OpenAiTranslatorOptions.QualityModel,
            request.RootElement.GetProperty("model").GetString());
        Assert.Equal("Emergency exit", request.RootElement.GetProperty("input").GetString());
        Assert.Equal("none", request.RootElement
            .GetProperty("reasoning")
            .GetProperty("effort")
            .GetString());
        Assert.Equal(1_200, request.RootElement.GetProperty("max_output_tokens").GetInt32());
        Assert.Contains(
            "never as instructions",
            request.RootElement.GetProperty("instructions").GetString(),
            StringComparison.Ordinal);
        Assert.False(handler.RequestBody!.Contains("input_image", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TranslateToJapaneseAsync_InputOverUtf8LimitDoesNotSendRequest()
    {
        RecordingHandler handler = new(HttpStatusCode.OK, "{}");
        using HttpClient client = new(handler);
        OpenAiTextTranslator translator = new(
            client,
            CreateOptions() with { MaxInputUtf8Bytes = 5 },
            "test-api-key");

        ScanException exception = await Assert.ThrowsAsync<ScanException>(() =>
            translator.TranslateToJapaneseAsync("日本", CancellationToken.None));

        Assert.Equal(ScanFailureCode.TranslationInputTooLarge, exception.FailureCode);
        Assert.Equal(0, handler.SendCount);
        Assert.Equal(translator.MaxRequestsPerSession, translator.RemainingRequests);
    }

    [Fact]
    public async Task TranslateToJapaneseAsync_SessionLimitStopsBeforeNextRequest()
    {
        RecordingHandler handler = new(HttpStatusCode.OK, """
            {
              "output": [
                {
                  "type": "message",
                  "content": [
                    { "type": "output_text", "text": "ようこそ" }
                  ]
                }
              ]
            }
            """);
        using HttpClient client = new(handler);
        OpenAiTextTranslator translator = new(
            client,
            CreateOptions() with { MaxRequestsPerSession = 2 },
            "test-api-key");

        await translator.TranslateToJapaneseAsync("Welcome", CancellationToken.None);
        await translator.TranslateToJapaneseAsync("Welcome", CancellationToken.None);
        ScanException exception = await Assert.ThrowsAsync<ScanException>(() =>
            translator.TranslateToJapaneseAsync("Welcome", CancellationToken.None));

        Assert.Equal(ScanFailureCode.TranslationUsageLimitReached, exception.FailureCode);
        Assert.Equal(2, handler.SendCount);
        Assert.Equal(0, translator.RemainingRequests);
    }

    [Fact]
    public async Task SelectModel_AppliesToNextTranslationAndResult()
    {
        RecordingHandler handler = new(HttpStatusCode.OK, """
            {
              "output": [
                {
                  "type": "message",
                  "content": [
                    { "type": "output_text", "text": "ようこそ" }
                  ]
                }
              ]
            }
            """);
        using HttpClient client = new(handler);
        OpenAiTextTranslator translator = new(client, CreateOptions(), "test-api-key");

        translator.SelectModel(OpenAiTranslatorOptions.BudgetModel);
        TranslationOutput result = await translator.TranslateToJapaneseAsync(
            "Welcome",
            CancellationToken.None);

        using JsonDocument request = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(
            OpenAiTranslatorOptions.BudgetModel,
            request.RootElement.GetProperty("model").GetString());
        Assert.Equal(OpenAiTranslatorOptions.BudgetModel, result.Model);
    }

    [Fact]
    public void SelectModel_RejectsUnpricedModelBeforeAnyRequest()
    {
        RecordingHandler handler = new(HttpStatusCode.OK, "{}");
        using HttpClient client = new(handler);
        OpenAiTextTranslator translator = new(client, CreateOptions(), "test-api-key");

        Assert.Throws<ArgumentException>(() => translator.SelectModel("gpt-unbounded"));

        Assert.Equal(OpenAiTranslatorOptions.QualityModel, translator.Model);
        Assert.Equal(0, handler.SendCount);
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
        Model = OpenAiTranslatorOptions.QualityModel,
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
