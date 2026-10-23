namespace MeridianVoteTracker.Infrastructure.Persistence;

/// <summary>
/// Database-level guards that hold even if application code is wrong or someone uses psql.
/// Applied by the InitialCreate migration. Every guard raises SQLSTATE 'MV001' with a readable message.
/// <list type="table">
/// <item><term>vote_history_events</term><description>append-only (no UPDATE/DELETE/TRUNCATE)</description></item>
/// <item><term>votes, country_votes</term><description>never deleted (ING-09); identity columns immutable;
///   an approved-value change must bump its revision</description></item>
/// <item><term>conflicts</term><description>never deleted; immutable once resolved</description></item>
/// <item><term>import_jobs</term><description>never deleted; immutable once finalized</description></item>
/// <item><term>import_rows</term><description>writable only while their job is not finalized (staging/classification/crash recovery)</description></item>
/// <item><term>membership_periods</term><description>no overlapping periods per state (exclusion constraint)</description></item>
/// </list>
/// Production hardening (not done here): run the app with a role that has no TRUNCATE/DDL rights and
/// only INSERT/SELECT on the audit tables.
/// </summary>
internal static class AuditGuardsSql
{
    public const string Up = """
        CREATE EXTENSION IF NOT EXISTS btree_gist;

        ALTER TABLE membership_periods
            ADD CONSTRAINT ex_membership_periods_no_overlap
            EXCLUDE USING gist (state_code WITH =, daterange(member_from, member_to, '[]') WITH &&);

        CREATE FUNCTION mv_reject_change() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION '% on % is not allowed: %', TG_OP, TG_TABLE_NAME, TG_ARGV[0]
                USING ERRCODE = 'MV001';
        END $$;

        -- Audit trail: append-only.
        CREATE TRIGGER trg_vote_history_events_append_only BEFORE UPDATE OR DELETE ON vote_history_events
            FOR EACH ROW EXECUTE FUNCTION mv_reject_change('the vote history is append-only');
        CREATE TRIGGER trg_vote_history_events_no_truncate BEFORE TRUNCATE ON vote_history_events
            FOR EACH STATEMENT EXECUTE FUNCTION mv_reject_change('the vote history is append-only');

        -- Approved data is never deleted; absence from a file is not a correction (ING-09).
        CREATE TRIGGER trg_votes_no_delete BEFORE DELETE ON votes
            FOR EACH ROW EXECUTE FUNCTION mv_reject_change('approved votes are never deleted');
        CREATE TRIGGER trg_votes_no_truncate BEFORE TRUNCATE ON votes
            FOR EACH STATEMENT EXECUTE FUNCTION mv_reject_change('approved votes are never deleted');
        CREATE TRIGGER trg_country_votes_no_delete BEFORE DELETE ON country_votes
            FOR EACH ROW EXECUTE FUNCTION mv_reject_change('approved country votes are never deleted');
        CREATE TRIGGER trg_country_votes_no_truncate BEFORE TRUNCATE ON country_votes
            FOR EACH STATEMENT EXECUTE FUNCTION mv_reject_change('approved country votes are never deleted');

        CREATE FUNCTION mv_guard_country_vote_update() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF NEW.vote_id <> OLD.vote_id OR NEW.state_code <> OLD.state_code THEN
                RAISE EXCEPTION 'country_votes identity is immutable' USING ERRCODE = 'MV001';
            END IF;
            IF NEW.revision <> OLD.revision + 1 THEN
                RAISE EXCEPTION 'every approved country vote change must increment revision (old %, new %)', OLD.revision, NEW.revision
                    USING ERRCODE = 'MV001';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER trg_country_votes_guard_update BEFORE UPDATE ON country_votes
            FOR EACH ROW EXECUTE FUNCTION mv_guard_country_vote_update();

        CREATE FUNCTION mv_guard_vote_update() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF NEW.assembly_vote_id <> OLD.assembly_vote_id THEN
                RAISE EXCEPTION 'votes.assembly_vote_id is immutable' USING ERRCODE = 'MV001';
            END IF;
            IF (NEW.session, NEW.vote_date, NEW.title) IS DISTINCT FROM (OLD.session, OLD.vote_date, OLD.title)
               AND NEW.metadata_revision <> OLD.metadata_revision + 1 THEN
                RAISE EXCEPTION 'every approved vote metadata change must increment metadata_revision' USING ERRCODE = 'MV001';
            END IF;
            IF (NEW.position, NEW.weight) IS DISTINCT FROM (OLD.position, OLD.weight)
               AND NEW.position_revision <> OLD.position_revision + 1 THEN
                RAISE EXCEPTION 'every position change must increment position_revision' USING ERRCODE = 'MV001';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER trg_votes_guard_update BEFORE UPDATE ON votes
            FOR EACH ROW EXECUTE FUNCTION mv_guard_vote_update();

        -- Conflicts: decisions are permanent (CON-06).
        CREATE FUNCTION mv_guard_conflict_update() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF OLD.status <> 'Pending' THEN
                RAISE EXCEPTION 'conflict % is already %; resolved conflicts are immutable', OLD.id, OLD.status
                    USING ERRCODE = 'MV001';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER trg_conflicts_guard_update BEFORE UPDATE ON conflicts
            FOR EACH ROW EXECUTE FUNCTION mv_guard_conflict_update();
        CREATE TRIGGER trg_conflicts_no_delete BEFORE DELETE ON conflicts
            FOR EACH ROW EXECUTE FUNCTION mv_reject_change('conflicts are never deleted');
        CREATE TRIGGER trg_conflicts_no_truncate BEFORE TRUNCATE ON conflicts
            FOR EACH STATEMENT EXECUTE FUNCTION mv_reject_change('conflicts are never deleted');

        -- Imports: mutable while processing, frozen once finalized (HIS-01, ING-15).
        CREATE FUNCTION mv_import_job_is_final(job_status text) RETURNS boolean LANGUAGE sql IMMUTABLE AS $$
            SELECT job_status IN ('Completed', 'CompletedWithIssues', 'Failed')
        $$;

        CREATE FUNCTION mv_guard_import_job_update() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF mv_import_job_is_final(OLD.status) THEN
                RAISE EXCEPTION 'import % is finalized (%) and immutable', OLD.id, OLD.status USING ERRCODE = 'MV001';
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER trg_import_jobs_guard_update BEFORE UPDATE ON import_jobs
            FOR EACH ROW EXECUTE FUNCTION mv_guard_import_job_update();
        CREATE TRIGGER trg_import_jobs_no_delete BEFORE DELETE ON import_jobs
            FOR EACH ROW EXECUTE FUNCTION mv_reject_change('imports are kept forever (HIS-01)');
        CREATE TRIGGER trg_import_jobs_no_truncate BEFORE TRUNCATE ON import_jobs
            FOR EACH STATEMENT EXECUTE FUNCTION mv_reject_change('imports are kept forever (HIS-01)');

        CREATE FUNCTION mv_guard_import_row_change() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE
            job_id uuid := CASE WHEN TG_OP = 'DELETE' THEN OLD.import_job_id ELSE NEW.import_job_id END;
            job_status text;
        BEGIN
            SELECT status INTO job_status FROM import_jobs WHERE id = job_id;
            IF mv_import_job_is_final(job_status) THEN
                RAISE EXCEPTION 'rows of import % cannot change: the import is finalized (%)', job_id, job_status
                    USING ERRCODE = 'MV001';
            END IF;
            IF TG_OP = 'UPDATE' AND (NEW.import_job_id, NEW.row_number) IS DISTINCT FROM (OLD.import_job_id, OLD.row_number) THEN
                RAISE EXCEPTION 'import_rows identity is immutable' USING ERRCODE = 'MV001';
            END IF;
            RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
        END $$;
        CREATE TRIGGER trg_import_rows_guard BEFORE INSERT OR UPDATE OR DELETE ON import_rows
            FOR EACH ROW EXECUTE FUNCTION mv_guard_import_row_change();
        CREATE TRIGGER trg_import_rows_no_truncate BEFORE TRUNCATE ON import_rows
            FOR EACH STATEMENT EXECUTE FUNCTION mv_reject_change('import results are kept forever (HIS-01)');
        """;

    public const string Down = """
        DROP TRIGGER IF EXISTS trg_import_rows_no_truncate ON import_rows;
        DROP TRIGGER IF EXISTS trg_import_rows_guard ON import_rows;
        DROP FUNCTION IF EXISTS mv_guard_import_row_change();
        DROP TRIGGER IF EXISTS trg_import_jobs_no_truncate ON import_jobs;
        DROP TRIGGER IF EXISTS trg_import_jobs_no_delete ON import_jobs;
        DROP TRIGGER IF EXISTS trg_import_jobs_guard_update ON import_jobs;
        DROP FUNCTION IF EXISTS mv_guard_import_job_update();
        DROP FUNCTION IF EXISTS mv_import_job_is_final(text);
        DROP TRIGGER IF EXISTS trg_conflicts_no_truncate ON conflicts;
        DROP TRIGGER IF EXISTS trg_conflicts_no_delete ON conflicts;
        DROP TRIGGER IF EXISTS trg_conflicts_guard_update ON conflicts;
        DROP FUNCTION IF EXISTS mv_guard_conflict_update();
        DROP TRIGGER IF EXISTS trg_votes_guard_update ON votes;
        DROP FUNCTION IF EXISTS mv_guard_vote_update();
        DROP TRIGGER IF EXISTS trg_country_votes_guard_update ON country_votes;
        DROP FUNCTION IF EXISTS mv_guard_country_vote_update();
        DROP TRIGGER IF EXISTS trg_country_votes_no_truncate ON country_votes;
        DROP TRIGGER IF EXISTS trg_country_votes_no_delete ON country_votes;
        DROP TRIGGER IF EXISTS trg_votes_no_truncate ON votes;
        DROP TRIGGER IF EXISTS trg_votes_no_delete ON votes;
        DROP TRIGGER IF EXISTS trg_vote_history_events_no_truncate ON vote_history_events;
        DROP TRIGGER IF EXISTS trg_vote_history_events_append_only ON vote_history_events;
        DROP FUNCTION IF EXISTS mv_reject_change();
        ALTER TABLE membership_periods DROP CONSTRAINT IF EXISTS ex_membership_periods_no_overlap;
        """;
}
