namespace MeridianVoteTracker.Domain.Votes;

/// <summary>A country's choice in one vote.</summary>
public enum Ballot
{
    Yes,
    No,
    Abstain,
    Absent,
}

public static class BallotCodes
{
    /// <summary>
    /// Accepts exactly "Y", "N", "A" or "X". Deliberately no case folding or synonyms ("Yes", "y"):
    /// the corrected Q1 file shows a row published as "Yes" was really an abstention, so guessing
    /// would have created wrong approved data. Such rows are Invalid and reported (ING-13).
    /// </summary>
    public static bool TryParse(string? code, out Ballot ballot)
    {
        (var ok, ballot) = code switch
        {
            "Y" => (true, Ballot.Yes),
            "N" => (true, Ballot.No),
            "A" => (true, Ballot.Abstain),
            "X" => (true, Ballot.Absent),
            _ => (false, default),
        };
        return ok;
    }

    public static string ToCode(this Ballot ballot) => ballot switch
    {
        Ballot.Yes => "Y",
        Ballot.No => "N",
        Ballot.Abstain => "A",
        Ballot.Absent => "X",
        _ => throw new ArgumentOutOfRangeException(nameof(ballot), ballot, null),
    };
}
