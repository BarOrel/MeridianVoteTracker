using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeridianVoteTracker.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "snapshots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    selection = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    created_by = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    vote_ids = table.Column<List<int>>(type: "integer[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_snapshots", x => x.id);
                    table.CheckConstraint("ck_snapshots_status", "status IN ('Pending', 'Completed', 'Failed')");
                });

            migrationBuilder.CreateTable(
                name: "snapshot_publications",
                columns: table => new
                {
                    quarter = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    published_by = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_snapshot_publications", x => x.quarter);
                    table.ForeignKey(
                        name: "fk_snapshot_publications_snapshots_snapshot_id",
                        column: x => x.snapshot_id,
                        principalTable: "snapshots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "snapshot_results",
                columns: table => new
                {
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state_code = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    counted_weight = table.Column<int>(type: "integer", nullable: false),
                    matching_weight = table.Column<int>(type: "integer", nullable: false),
                    score = table.Column<decimal>(type: "numeric(4,1)", precision: 4, scale: 1, nullable: true),
                    rank = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_snapshot_results", x => new { x.snapshot_id, x.state_code });
                    table.ForeignKey(
                        name: "fk_snapshot_results_snapshots_snapshot_id",
                        column: x => x.snapshot_id,
                        principalTable: "snapshots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "snapshot_votes",
                columns: table => new
                {
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    vote_id = table.Column<int>(type: "integer", nullable: false),
                    assembly_vote_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    vote_date = table.Column<DateOnly>(type: "date", nullable: false),
                    position = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    weight = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_snapshot_votes", x => new { x.snapshot_id, x.vote_id });
                    table.ForeignKey(
                        name: "fk_snapshot_votes_snapshots_snapshot_id",
                        column: x => x.snapshot_id,
                        principalTable: "snapshots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "snapshot_entries",
                columns: table => new
                {
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    vote_id = table.Column<int>(type: "integer", nullable: false),
                    state_code = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    ballot = table.Column<string>(type: "character(1)", fixedLength: true, maxLength: 1, nullable: false),
                    counted = table.Column<bool>(type: "boolean", nullable: false),
                    matching = table.Column<bool>(type: "boolean", nullable: false),
                    weight = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_snapshot_entries", x => new { x.snapshot_id, x.state_code, x.vote_id });
                    table.ForeignKey(
                        name: "fk_snapshot_entries_snapshot_votes_snapshot_id_vote_id",
                        columns: x => new { x.snapshot_id, x.vote_id },
                        principalTable: "snapshot_votes",
                        principalColumns: new[] { "snapshot_id", "vote_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_snapshot_entries_snapshot_id_vote_id",
                table: "snapshot_entries",
                columns: new[] { "snapshot_id", "vote_id" });

            migrationBuilder.CreateIndex(
                name: "ix_snapshot_publications_snapshot_id",
                table: "snapshot_publications",
                column: "snapshot_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_snapshots_status_created_at",
                table: "snapshots",
                columns: new[] { "status", "created_at" });
            // Hand-written: a completed snapshot and its contents can never change (SNP-04); publications are permanent.
            migrationBuilder.Sql("""
                CREATE FUNCTION mv_guard_snapshot_update() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD.status <> 'Pending' THEN
                        RAISE EXCEPTION 'snapshot % is % and immutable', OLD.id, OLD.status USING ERRCODE = 'MV001';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER trg_snapshots_guard_update BEFORE UPDATE ON snapshots
                    FOR EACH ROW EXECUTE FUNCTION mv_guard_snapshot_update();
                CREATE TRIGGER trg_snapshots_no_delete BEFORE DELETE ON snapshots
                    FOR EACH ROW EXECUTE FUNCTION mv_reject_change('snapshots are kept forever');

                CREATE FUNCTION mv_guard_snapshot_content_insert() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE snapshot_status text;
                BEGIN
                    SELECT status INTO snapshot_status FROM snapshots WHERE id = NEW.snapshot_id;
                    IF snapshot_status IS DISTINCT FROM 'Pending' THEN
                        RAISE EXCEPTION 'snapshot % is % and immutable', NEW.snapshot_id, snapshot_status USING ERRCODE = 'MV001';
                    END IF;
                    RETURN NEW;
                END $$;
                """);
            foreach (var table in new[] { "snapshot_votes", "snapshot_entries", "snapshot_results" })
            {
                migrationBuilder.Sql($"""
                    CREATE TRIGGER trg_{table}_insert_while_pending BEFORE INSERT ON {table}
                        FOR EACH ROW EXECUTE FUNCTION mv_guard_snapshot_content_insert();
                    CREATE TRIGGER trg_{table}_immutable BEFORE UPDATE OR DELETE ON {table}
                        FOR EACH ROW EXECUTE FUNCTION mv_reject_change('snapshot contents are immutable');
                    """);
            }

            foreach (var table in new[] { "snapshots", "snapshot_votes", "snapshot_entries", "snapshot_results", "snapshot_publications" })
            {
                migrationBuilder.Sql($"""
                    CREATE TRIGGER trg_{table}_no_truncate BEFORE TRUNCATE ON {table}
                        FOR EACH STATEMENT EXECUTE FUNCTION mv_reject_change('snapshots are kept forever');
                    """);
            }

            migrationBuilder.Sql("""
                CREATE TRIGGER trg_snapshot_publications_permanent BEFORE UPDATE OR DELETE ON snapshot_publications
                    FOR EACH ROW EXECUTE FUNCTION mv_reject_change('publications are permanent');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS mv_guard_snapshot_update() CASCADE;
                DROP FUNCTION IF EXISTS mv_guard_snapshot_content_insert() CASCADE;
                """);
            migrationBuilder.DropTable(
                name: "snapshot_entries");

            migrationBuilder.DropTable(
                name: "snapshot_publications");

            migrationBuilder.DropTable(
                name: "snapshot_results");

            migrationBuilder.DropTable(
                name: "snapshot_votes");

            migrationBuilder.DropTable(
                name: "snapshots");
        }
    }
}
