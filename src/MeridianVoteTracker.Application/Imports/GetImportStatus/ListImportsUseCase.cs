using MeridianVoteTracker.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace MeridianVoteTracker.Application.Imports.GetImportStatus;

/// <summary>Import history for the Imports screen (HIS-01): newest first.</summary>
public sealed class ListImportsUseCase(IApplicationDbContext db, GetImportStatusUseCase getStatus)
{
    public async Task<IReadOnlyList<ImportStatusDto>> ExecuteAsync(CancellationToken cancellationToken)
    {
        var ids = await db.ImportJobs.OrderByDescending(j => j.UploadedAt).Take(100).Select(j => j.Id).ToListAsync(cancellationToken);
        var result = new List<ImportStatusDto>(ids.Count);
        foreach (var id in ids)
        {
            result.Add(await getStatus.ExecuteAsync(id, cancellationToken));
        }

        return result;
    }
}
