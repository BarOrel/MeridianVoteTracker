using MeridianVoteTracker.Application.Abstractions;
using MeridianVoteTracker.Application.Common;
using MeridianVoteTracker.Application.Votes.Ports;
using MeridianVoteTracker.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace MeridianVoteTracker.Application.Conflicts;

public enum ConflictDecision
{
    Accept,
    Reject,
}

public sealed record ResolveConflictCommand(ConflictDecision Decision, string? Comment);

/// <summary>
/// CON-04/05: accept (the proposed value becomes approved) or reject (approved value kept), with who/when/comment.
/// Transaction: one, under the vote's lock (see <see cref="IApprovedVoteStore.ResolveConflictAsync"/>).
/// If someone else decided first, or the approved value changed since detection, nothing is written and the
/// caller gets a Conflict error (HTTP 409) carrying the conflict's current state.
/// </summary>
public sealed class ResolveConflictUseCase(
    IApprovedVoteStore approvedVotes,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider time,
    GetConflictUseCase getConflict)
{
    private const int MaxCommentLength = 2000;

    public async Task<ConflictDto> ExecuteAsync(Guid conflictId, ResolveConflictCommand command, CancellationToken cancellationToken)
    {
        if (command.Comment is { Length: > MaxCommentLength })
        {
            throw UseCaseException.Validation("comment", "comment.too_long", $"The comment can have at most {MaxCommentLength} characters.");
        }

        bool found;
        try
        {
            found = await unitOfWork.ExecuteInTransactionAsync(ct => approvedVotes.ResolveConflictAsync(
                conflictId, command.Decision == ConflictDecision.Accept, currentUser.Id, command.Comment, time.GetUtcNow(), ct), cancellationToken);
        }
        catch (DomainException ex) when (ex.Code is DomainErrors.ConflictAlreadyResolved or DomainErrors.ApprovedValueChanged)
        {
            throw await ConflictAsync(conflictId, ex.Code, ex.Message, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw await ConflictAsync(conflictId, "conflict.concurrent_update", "Someone else changed this data at the same time; nothing was applied.", cancellationToken);
        }

        if (!found)
        {
            throw UseCaseException.NotFound("conflict.not_found", $"Conflict {conflictId} does not exist.");
        }

        return await getConflict.ExecuteAsync(conflictId, cancellationToken);
    }

    private async Task<UseCaseException> ConflictAsync(Guid conflictId, string code, string message, CancellationToken cancellationToken) =>
        new(UseCaseErrorKind.Conflict, code, message) { CurrentState = await getConflict.ExecuteAsync(conflictId, cancellationToken) };
}
