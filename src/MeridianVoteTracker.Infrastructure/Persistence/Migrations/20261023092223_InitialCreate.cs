using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MeridianVoteTracker.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "import_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_file_name = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    storage_key = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    format = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    uploaded_by = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    phase = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    total_rows = table.Column<int>(type: "integer", nullable: true),
                    staged_rows = table.Column<int>(type: "integer", nullable: false),
                    classified_rows = table.Column<int>(type: "integer", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    lease_owner = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_jobs", x => x.id);
                    table.CheckConstraint("ck_import_jobs_counters", "staged_rows >= 0 AND classified_rows >= 0 AND attempts >= 0 AND (total_rows IS NULL OR total_rows >= 0)");
                    table.CheckConstraint("ck_import_jobs_phase", "phase IN ('Pending', 'Staging', 'Classifying', 'Finalized')");
                    table.CheckConstraint("ck_import_jobs_sha256", "sha256 ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_import_jobs_status", "status IN ('Queued', 'Processing', 'Completed', 'CompletedWithIssues', 'Failed')");
                });

            migrationBuilder.CreateTable(
                name: "member_states",
                columns: table => new
                {
                    code = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_member_states", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    display_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                    table.CheckConstraint("ck_users_role", "role IN ('Analyst', 'Admin')");
                });

            migrationBuilder.CreateTable(
                name: "votes",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    assembly_vote_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    session = table.Column<int>(type: "integer", nullable: false),
                    vote_date = table.Column<DateOnly>(type: "date", nullable: false),
                    title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    metadata_revision = table.Column<int>(type: "integer", nullable: false),
                    metadata_origin = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    metadata_source_import_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    metadata_source_row_number = table.Column<int>(type: "integer", nullable: true),
                    metadata_conflict_id = table.Column<Guid>(type: "uuid", nullable: true),
                    metadata_approved_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    metadata_approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    position = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    weight = table.Column<short>(type: "smallint", nullable: true),
                    position_revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_votes", x => x.id);
                    table.CheckConstraint("ck_votes_metadata_origin", "metadata_origin IN ('InitialImport', 'ConflictResolution')");
                    table.CheckConstraint("ck_votes_position", "position IS NULL OR position IN ('For', 'Against')");
                    table.CheckConstraint("ck_votes_position_weight_together", "(position IS NULL) = (weight IS NULL)");
                    table.CheckConstraint("ck_votes_revisions", "metadata_revision >= 1 AND position_revision >= 0");
                    table.CheckConstraint("ck_votes_session", "session > 0");
                    table.CheckConstraint("ck_votes_weight", "weight IS NULL OR weight BETWEEN 1 AND 5");
                    table.ForeignKey(
                        name: "fk_votes_import_jobs_metadata_source_import_job_id",
                        column: x => x.metadata_source_import_job_id,
                        principalTable: "import_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "membership_periods",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    state_code = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    member_from = table.Column<DateOnly>(type: "date", nullable: false),
                    member_to = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_membership_periods", x => x.id);
                    table.CheckConstraint("ck_membership_periods_range", "member_to IS NULL OR member_to >= member_from");
                    table.ForeignKey(
                        name: "fk_membership_periods_member_states_state_code",
                        column: x => x.state_code,
                        principalTable: "member_states",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "conflicts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    vote_id = table.Column<int>(type: "integer", nullable: false),
                    state_code = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: true),
                    field = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    approved_value_at_detection = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    expected_revision = table.Column<int>(type: "integer", nullable: false),
                    proposed_value = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    source_import_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_row_number = table.Column<int>(type: "integer", nullable: true),
                    detected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    resolved_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolution_comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    superseded_by_conflict_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_conflicts", x => x.id);
                    table.CheckConstraint("ck_conflicts_has_difference", "approved_value_at_detection <> proposed_value");
                    table.CheckConstraint("ck_conflicts_resolution", "(status = 'Pending') = (resolved_at IS NULL AND resolved_by IS NULL)");
                    table.CheckConstraint("ck_conflicts_shape", "(kind = 'CountryBallot' AND state_code IS NOT NULL AND field = 'Ballot') OR (kind = 'VoteMetadata' AND state_code IS NULL AND field IN ('Session', 'VoteDate', 'Title'))");
                    table.CheckConstraint("ck_conflicts_status", "status IN ('Pending', 'Accepted', 'Rejected', 'Superseded')");
                    table.ForeignKey(
                        name: "fk_conflicts_conflicts_superseded_by_conflict_id",
                        column: x => x.superseded_by_conflict_id,
                        principalTable: "conflicts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_conflicts_import_jobs_source_import_job_id",
                        column: x => x.source_import_job_id,
                        principalTable: "import_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_conflicts_member_states_state_code",
                        column: x => x.state_code,
                        principalTable: "member_states",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_conflicts_votes_vote_id",
                        column: x => x.vote_id,
                        principalTable: "votes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "country_votes",
                columns: table => new
                {
                    vote_id = table.Column<int>(type: "integer", nullable: false),
                    state_code = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    ballot = table.Column<string>(type: "character(1)", fixedLength: true, maxLength: 1, nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    approval_origin = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    approval_source_import_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    approval_source_row_number = table.Column<int>(type: "integer", nullable: true),
                    approval_conflict_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approval_approved_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    approval_approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_country_votes", x => new { x.vote_id, x.state_code });
                    table.CheckConstraint("ck_country_votes_ballot", "ballot IN ('Y', 'N', 'A', 'X')");
                    table.CheckConstraint("ck_country_votes_origin", "approval_origin IN ('InitialImport', 'ConflictResolution')");
                    table.CheckConstraint("ck_country_votes_revision", "revision >= 1");
                    table.ForeignKey(
                        name: "fk_country_votes_import_jobs_approval_source_import_job_id",
                        column: x => x.approval_source_import_job_id,
                        principalTable: "import_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_country_votes_member_states_state_code",
                        column: x => x.state_code,
                        principalTable: "member_states",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_country_votes_votes_vote_id",
                        column: x => x.vote_id,
                        principalTable: "votes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "import_rows",
                columns: table => new
                {
                    import_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    row_number = table.Column<int>(type: "integer", nullable: false),
                    original_text = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    raw_assembly_vote_id = table.Column<string>(type: "text", nullable: true),
                    raw_session = table.Column<string>(type: "text", nullable: true),
                    raw_vote_date = table.Column<string>(type: "text", nullable: true),
                    raw_resolution_title = table.Column<string>(type: "text", nullable: true),
                    raw_country_code = table.Column<string>(type: "text", nullable: true),
                    raw_country_name = table.Column<string>(type: "text", nullable: true),
                    raw_ballot = table.Column<string>(type: "text", nullable: true),
                    raw_published_at = table.Column<string>(type: "text", nullable: true),
                    assembly_vote_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    session = table.Column<int>(type: "integer", nullable: true),
                    vote_date = table.Column<DateOnly>(type: "date", nullable: true),
                    title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    country_code = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: true),
                    country_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ballot = table.Column<string>(type: "character(1)", fixedLength: true, maxLength: 1, nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    classification = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    reason_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    duplicate_of_row_number = table.Column<int>(type: "integer", nullable: true),
                    duplicate_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    conflict_id = table.Column<Guid>(type: "uuid", nullable: true),
                    normalization_notes = table.Column<List<string>>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_rows", x => new { x.import_job_id, x.row_number });
                    table.CheckConstraint("ck_import_rows_classification", "classification IS NULL OR classification IN ('New', 'Unchanged', 'Duplicate', 'Invalid', 'Contradiction', 'Conflict')");
                    table.CheckConstraint("ck_import_rows_conflict", "(classification = 'Conflict') = (conflict_id IS NOT NULL)");
                    table.CheckConstraint("ck_import_rows_duplicate", "(classification = 'Duplicate') = (duplicate_kind IS NOT NULL AND duplicate_of_row_number IS NOT NULL)");
                    table.CheckConstraint("ck_import_rows_normalized", "(assembly_vote_id IS NULL) = (classification = 'Invalid')");
                    table.ForeignKey(
                        name: "fk_import_rows_conflicts_conflict_id",
                        column: x => x.conflict_id,
                        principalTable: "conflicts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_import_rows_import_jobs_import_job_id",
                        column: x => x.import_job_id,
                        principalTable: "import_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "vote_history_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    vote_id = table.Column<int>(type: "integer", nullable: false),
                    state_code = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: true),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    field = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    old_value = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    new_value = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    old_source_import_job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    old_source_row_number = table.Column<int>(type: "integer", nullable: true),
                    new_source_import_job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    new_source_row_number = table.Column<int>(type: "integer", nullable: true),
                    conflict_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vote_history_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_vote_history_events_conflicts_conflict_id",
                        column: x => x.conflict_id,
                        principalTable: "conflicts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_vote_history_events_import_jobs_new_source_import_job_id",
                        column: x => x.new_source_import_job_id,
                        principalTable: "import_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_vote_history_events_import_jobs_old_source_import_job_id",
                        column: x => x.old_source_import_job_id,
                        principalTable: "import_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_vote_history_events_votes_vote_id",
                        column: x => x.vote_id,
                        principalTable: "votes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_conflicts_source_import_job_id",
                table: "conflicts",
                column: "source_import_job_id");

            migrationBuilder.CreateIndex(
                name: "ix_conflicts_state_code",
                table: "conflicts",
                column: "state_code");

            migrationBuilder.CreateIndex(
                name: "ix_conflicts_status_vote_id",
                table: "conflicts",
                columns: new[] { "status", "vote_id" });

            migrationBuilder.CreateIndex(
                name: "ix_conflicts_superseded_by_conflict_id",
                table: "conflicts",
                column: "superseded_by_conflict_id");

            migrationBuilder.CreateIndex(
                name: "ux_conflicts_pending_ballot",
                table: "conflicts",
                columns: new[] { "vote_id", "state_code", "proposed_value" },
                unique: true,
                filter: "status = 'Pending' AND kind = 'CountryBallot'");

            migrationBuilder.CreateIndex(
                name: "ux_conflicts_pending_metadata",
                table: "conflicts",
                columns: new[] { "vote_id", "field", "proposed_value" },
                unique: true,
                filter: "status = 'Pending' AND kind = 'VoteMetadata'");

            migrationBuilder.CreateIndex(
                name: "ix_country_votes_approval_source_import_job_id",
                table: "country_votes",
                column: "approval_source_import_job_id");

            migrationBuilder.CreateIndex(
                name: "ix_country_votes_state_code",
                table: "country_votes",
                column: "state_code");

            migrationBuilder.CreateIndex(
                name: "ix_import_jobs_sha256",
                table: "import_jobs",
                column: "sha256");

            migrationBuilder.CreateIndex(
                name: "ix_import_jobs_status_uploaded_at",
                table: "import_jobs",
                columns: new[] { "status", "uploaded_at" });

            migrationBuilder.CreateIndex(
                name: "ix_import_rows_conflict_id",
                table: "import_rows",
                column: "conflict_id");

            migrationBuilder.CreateIndex(
                name: "ix_import_rows_import_job_id_assembly_vote_id",
                table: "import_rows",
                columns: new[] { "import_job_id", "assembly_vote_id" });

            migrationBuilder.CreateIndex(
                name: "ix_import_rows_import_job_id_classification",
                table: "import_rows",
                columns: new[] { "import_job_id", "classification" });

            migrationBuilder.CreateIndex(
                name: "ix_membership_periods_state_code_member_from",
                table: "membership_periods",
                columns: new[] { "state_code", "member_from" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_vote_history_events_conflict_id",
                table: "vote_history_events",
                column: "conflict_id");

            migrationBuilder.CreateIndex(
                name: "ix_vote_history_events_new_source_import_job_id",
                table: "vote_history_events",
                column: "new_source_import_job_id");

            migrationBuilder.CreateIndex(
                name: "ix_vote_history_events_old_source_import_job_id",
                table: "vote_history_events",
                column: "old_source_import_job_id");

            migrationBuilder.CreateIndex(
                name: "ix_vote_history_events_vote_id_occurred_at",
                table: "vote_history_events",
                columns: new[] { "vote_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_votes_assembly_vote_id",
                table: "votes",
                column: "assembly_vote_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_votes_metadata_source_import_job_id",
                table: "votes",
                column: "metadata_source_import_job_id");

            migrationBuilder.CreateIndex(
                name: "ix_votes_vote_date",
                table: "votes",
                column: "vote_date");

            // Hand-written: immutability/audit triggers and the membership exclusion constraint.
            migrationBuilder.Sql(AuditGuardsSql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(AuditGuardsSql.Down);

            migrationBuilder.DropTable(
                name: "country_votes");

            migrationBuilder.DropTable(
                name: "import_rows");

            migrationBuilder.DropTable(
                name: "membership_periods");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "vote_history_events");

            migrationBuilder.DropTable(
                name: "conflicts");

            migrationBuilder.DropTable(
                name: "member_states");

            migrationBuilder.DropTable(
                name: "votes");

            migrationBuilder.DropTable(
                name: "import_jobs");
        }
    }
}
