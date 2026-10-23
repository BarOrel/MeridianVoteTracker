using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeridianVoteTracker.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ImportClassification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "import_vote_outcomes",
                columns: table => new
                {
                    import_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assembly_vote_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    vote_id = table.Column<int>(type: "integer", nullable: true),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    new_rows = table.Column<int>(type: "integer", nullable: false),
                    unchanged_rows = table.Column<int>(type: "integer", nullable: false),
                    duplicate_rows = table.Column<int>(type: "integer", nullable: false),
                    contradiction_rows = table.Column<int>(type: "integer", nullable: false),
                    conflict_rows = table.Column<int>(type: "integer", nullable: false),
                    conflicts_created = table.Column<int>(type: "integer", nullable: false),
                    notes = table.Column<List<string>>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_vote_outcomes", x => new { x.import_job_id, x.assembly_vote_id });
                    table.CheckConstraint("ck_import_vote_outcomes_counts", "new_rows >= 0 AND unchanged_rows >= 0 AND duplicate_rows >= 0 AND contradiction_rows >= 0 AND conflict_rows >= 0 AND conflicts_created >= 0");
                    table.CheckConstraint("ck_import_vote_outcomes_outcome", "outcome IN ('NewVote', 'CountriesAdded', 'ConflictsDetected', 'NoChange', 'BlockedByContradiction')");
                    table.ForeignKey(
                        name: "fk_import_vote_outcomes_import_jobs_import_job_id",
                        column: x => x.import_job_id,
                        principalTable: "import_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_import_vote_outcomes_votes_vote_id",
                        column: x => x.vote_id,
                        principalTable: "votes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_import_vote_outcomes_vote_id",
                table: "import_vote_outcomes",
                column: "vote_id");
            // Hand-written: outcomes are insert-only and only for imports that are still processing.
            migrationBuilder.Sql("""
                CREATE FUNCTION mv_guard_insert_into_open_import() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE job_status text;
                BEGIN
                    SELECT status INTO job_status FROM import_jobs WHERE id = NEW.import_job_id;
                    IF mv_import_job_is_final(job_status) THEN
                        RAISE EXCEPTION 'import % is finalized (%); no outcomes can be added', NEW.import_job_id, job_status
                            USING ERRCODE = 'MV001';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER trg_import_vote_outcomes_open_import BEFORE INSERT ON import_vote_outcomes
                    FOR EACH ROW EXECUTE FUNCTION mv_guard_insert_into_open_import();
                CREATE TRIGGER trg_import_vote_outcomes_insert_only BEFORE UPDATE OR DELETE ON import_vote_outcomes
                    FOR EACH ROW EXECUTE FUNCTION mv_reject_change('import outcomes are written once');
                CREATE TRIGGER trg_import_vote_outcomes_no_truncate BEFORE TRUNCATE ON import_vote_outcomes
                    FOR EACH STATEMENT EXECUTE FUNCTION mv_reject_change('import outcomes are kept forever (HIS-01)');

                -- Phase 2 parked staged imports (Processing / Classifying, no lease). Make them claimable now
                -- that classification exists: an expired lease is what the worker picks up.
                UPDATE import_jobs SET lease_expires_at = now()
                WHERE status = 'Processing' AND phase = 'Classifying' AND lease_owner IS NULL AND lease_expires_at IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_import_vote_outcomes_no_truncate ON import_vote_outcomes;
                DROP TRIGGER IF EXISTS trg_import_vote_outcomes_insert_only ON import_vote_outcomes;
                DROP TRIGGER IF EXISTS trg_import_vote_outcomes_open_import ON import_vote_outcomes;
                DROP FUNCTION IF EXISTS mv_guard_insert_into_open_import();
                """);
            migrationBuilder.DropTable(
                name: "import_vote_outcomes");
        }
    }
}
