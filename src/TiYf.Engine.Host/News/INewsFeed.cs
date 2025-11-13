using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TiYf.Engine.Core;

namespace TiYf.Engine.Host.News;

internal interface INewsFeed
{
    Task<IReadOnlyList<NewsEvent>> FetchAsync(DateTime? sinceUtc, int sinceOccurrencesAtTimestamp, CancellationToken cancellationToken);
}
