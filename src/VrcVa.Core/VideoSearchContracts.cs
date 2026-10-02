using System.Text;

namespace VrcVa.Core;

/// <summary>One bounded metadata query. The provider receives no command or arbitrary URL.</summary>
public sealed class VideoSearchRequest
{
    public const int MaximumCandidates = 10;
    public const int MaximumQueryUtf8Bytes = TextInputSession.MaximumTranscriptUtf8Bytes;

    public VideoSearchRequest(Guid sessionId, Guid operationId, string query)
    {
        if (sessionId == Guid.Empty || operationId == Guid.Empty)
        {
            throw new ArgumentException("Session and operation IDs must be nonempty.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (Encoding.UTF8.GetByteCount(query) > MaximumQueryUtf8Bytes)
        {
            throw new ArgumentException("The search query exceeds the input limit.", nameof(query));
        }

        SessionId = sessionId;
        OperationId = operationId;
        Query = query;
    }

    public Guid SessionId { get; }
    public Guid OperationId { get; }
    public string Query { get; }
    public int CandidateLimit => MaximumCandidates;
}

/// <summary>The entire search batch, retained in memory without additional page fetches.</summary>
public sealed class VideoSearchBatch
{
    public VideoSearchBatch(IEnumerable<VideoMetadata> videos, bool isPartial = false)
    {
        ArgumentNullException.ThrowIfNull(videos);
        // Bound enumeration as well as the retained collection, including faulty providers.
        VideoMetadata[] snapshot = videos.Take(VideoSearchRequest.MaximumCandidates + 1).ToArray();
        if (snapshot.Length > VideoSearchRequest.MaximumCandidates
            || snapshot.Any(video => video is null)
            || snapshot.Select(video => video.VideoId).Distinct(StringComparer.Ordinal).Count() != snapshot.Length)
        {
            throw new ArgumentException("A video batch must contain at most ten valid, distinct videos.", nameof(videos));
        }

        Videos = Array.AsReadOnly(snapshot);
        IsPartial = isPartial;
    }

    public IReadOnlyList<VideoMetadata> Videos { get; }
    public bool IsPartial { get; }
}

public interface IVideoSearchProvider
{
    Task<VideoSearchBatch> SearchAsync(VideoSearchRequest request, CancellationToken cancellationToken);
}

public enum VideoCandidateActionKind
{
    CopyWatchUrl = 1,
}

/// <summary>An explicit candidate selection contains identities, never an arbitrary URL or command.</summary>
public sealed record VideoCandidateAction(
    Guid SessionId,
    Guid SearchOperationId,
    Guid CandidateId,
    VideoCandidateActionKind Kind);

public sealed class VideoCandidate
{
    internal VideoCandidate(Guid sessionId, Guid operationId, VideoMetadata video)
    {
        SessionId = sessionId;
        SearchOperationId = operationId;
        CandidateId = Guid.NewGuid();
        Video = video;
    }

    public Guid SessionId { get; }
    public Guid SearchOperationId { get; }
    public Guid CandidateId { get; }
    public VideoMetadata Video { get; }
    public string VideoId => Video.VideoId;
    public string Title => Video.Title;
    public Uri WatchUrl => Video.WatchUrl;
    public Uri? ThumbnailUrl => Video.ThumbnailUrl;
    public VideoCandidateActionKind AllowedAction => VideoCandidateActionKind.CopyWatchUrl;

    public VideoCandidateAction CreateSelectionAction() =>
        new(SessionId, SearchOperationId, CandidateId, AllowedAction);
}

/// <summary>An immutable snapshot of one search. Its last page is only the end of this batch.</summary>
public sealed class VideoSearchResult
{
    public const int CandidatesPerPage = 5;

    internal VideoSearchResult(
        VideoSearchRequest request,
        VideoSearchBatch batch,
        TimeSpan searchDuration)
    {
        SessionId = request.SessionId;
        OperationId = request.OperationId;
        Query = request.Query;
        SearchDuration = searchDuration;
        IsPartial = batch.IsPartial;
        Candidates = Array.AsReadOnly(batch.Videos.Select(video =>
            new VideoCandidate(SessionId, OperationId, video)).ToArray());
    }

    public Guid SessionId { get; }
    public Guid OperationId { get; }
    public string Query { get; }
    public TimeSpan SearchDuration { get; }
    public bool IsPartial { get; }
    public IReadOnlyList<VideoCandidate> Candidates { get; }
    public int PageCount => Math.Max(1, (Candidates.Count + CandidatesPerPage - 1) / CandidatesPerPage);

    public IReadOnlyList<VideoCandidate> GetPage(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= PageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        return Array.AsReadOnly(Candidates.Skip(pageIndex * CandidatesPerPage).Take(CandidatesPerPage).ToArray());
    }
}
