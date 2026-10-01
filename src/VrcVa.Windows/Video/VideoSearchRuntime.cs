using System.Net.Http;
using VrcVa.Core;
using VrcVa.Infrastructure;

namespace VrcVa.Windows.Video;

/// <summary>One application-owned catalog and session; credentials are read only for a new paid interpretation.</summary>
internal sealed class VideoSearchRuntime : IDisposable
{
    private readonly HttpClient _interpretationHttp = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseDefaultCredentials = false,
        UseProxy = false,
    });

    public VideoSearchRuntime(ExecutionCoordinator execution, IAnalyzer translation,
        FeatureUsageQuotas quotas, Func<string?> readTextCredential)
    {
        Session = new(execution);
        IVideoSearchProvider provider = new YtDlpVideoSearchProvider(execution);
        Catalog = CreateCatalog(translation, provider,
            new CurrentCredentialInterpreter(_interpretationHttp, quotas.SearchInterpretation, readTextCredential), Session);
        Thumbnails = new HttpVideoThumbnailProvider(new WpfVideoThumbnailDecoder());
    }

    public VideoSearchSession Session { get; }
    public FeatureCatalog Catalog { get; }
    public HttpVideoThumbnailProvider Thumbnails { get; }

    internal static FeatureCatalog CreateCatalog(IAnalyzer translation, IVideoSearchProvider provider,
        ISearchQueryInterpreter interpreter, VideoSearchSession session) => new(
            new FeatureEntry(BuiltInFeatures.Translation, translation),
            new FeatureEntry(BuiltInFeatures.DirectVideoSearch, new DirectVideoSearchHandler(provider, session)),
            new FeatureEntry(BuiltInFeatures.InterpretedVideoSearch, new InterpretedVideoSearchHandler(interpreter, provider, session)));

    public void Dispose() { Thumbnails.Dispose(); _interpretationHttp.Dispose(); }

    internal sealed class CurrentCredentialInterpreter(HttpClient http, TextRequestQuota quota,
        Func<string?> readTextCredential) : ISearchQueryInterpreter
    {
        public Task<SearchQueryInterpretation> InterpretAsync(TextInputSession input, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? credential;
            try { credential = readTextCredential(); }
            catch (Exception)
            {
                throw new ScanException(ScanFailureCode.SearchInterpretationNotConfigured, ScanStage.SearchInterpretation,
                    "検索解釈用の保存済みOpenAIキーを読み込めませんでした。資格情報の設定を確認してください。");
            }
            return new OpenAiSearchQueryInterpreter(http, OpenAiSearchInterpretationOptions.FromEnvironment(), credential, quota)
                .InterpretAsync(input, cancellationToken);
        }
    }
}
