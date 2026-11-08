using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Chameleon.Storage.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "access_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_email = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    token_hash = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_access_tokens", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "entities",
                columns: table => new
                {
                    sandbox_id = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_entities", x => new { x.sandbox_id, x.type, x.key });
                });

            migrationBuilder.CreateTable(
                name: "faults",
                columns: table => new
                {
                    sandbox_id = table.Column<string>(type: "text", nullable: false),
                    connector = table.Column<string>(type: "text", nullable: false),
                    route = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: true),
                    delay_ms = table.Column<int>(type: "integer", nullable: true),
                    remaining = table.Column<int>(type: "integer", nullable: false),
                    until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_faults", x => new { x.sandbox_id, x.connector, x.route });
                });

            migrationBuilder.CreateTable(
                name: "interactions",
                columns: table => new
                {
                    seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    kind = table.Column<string>(type: "text", nullable: false),
                    connector = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<string>(type: "text", nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    summary = table.Column<string>(type: "text", nullable: false),
                    detail = table.Column<string>(type: "text", nullable: true),
                    id = table.Column<string>(type: "text", nullable: true),
                    sandbox_id = table.Column<string>(type: "text", nullable: false),
                    correlation_id = table.Column<string>(type: "text", nullable: true),
                    method = table.Column<string>(type: "text", nullable: true),
                    path = table.Column<string>(type: "text", nullable: true),
                    status_code = table.Column<int>(type: "integer", nullable: true),
                    duration_ms = table.Column<long>(type: "bigint", nullable: true),
                    request_headers = table.Column<string>(type: "jsonb", nullable: true),
                    request_body = table.Column<string>(type: "text", nullable: true),
                    response_body = table.Column<string>(type: "text", nullable: true),
                    truncated = table.Column<bool>(type: "boolean", nullable: false),
                    fault = table.Column<string>(type: "text", nullable: true),
                    entity_type = table.Column<string>(type: "text", nullable: true),
                    from_state = table.Column<string>(type: "text", nullable: true),
                    to_state = table.Column<string>(type: "text", nullable: true),
                    by_clock = table.Column<bool>(type: "boolean", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_interactions", x => x.seq);
                });

            migrationBuilder.CreateTable(
                name: "sandboxes",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    persistent = table.Column<bool>(type: "boolean", nullable: false),
                    clock_mode = table.Column<string>(type: "text", nullable: false),
                    clock_offset_seconds = table.Column<double>(type: "double precision", nullable: false),
                    frozen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    webhook_policy = table.Column<string>(type: "jsonb", nullable: true),
                    webhook_headers = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sandboxes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "webhook_attempts",
                columns: table => new
                {
                    seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    delivery_id = table.Column<string>(type: "text", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    url = table.Column<string>(type: "text", nullable: false),
                    status_code = table.Column<int>(type: "integer", nullable: true),
                    duration_ms = table.Column<long>(type: "bigint", nullable: false),
                    response_body = table.Column<string>(type: "text", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    blocked = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_attempts", x => x.seq);
                });

            migrationBuilder.CreateTable(
                name: "webhook_deliveries",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    sandbox_id = table.Column<string>(type: "text", nullable: false),
                    connector = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<string>(type: "text", nullable: false),
                    correlation_id = table.Column<string>(type: "text", nullable: true),
                    url = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    max_attempts = table.Column<int>(type: "integer", nullable: false),
                    base_delay_ms = table.Column<int>(type: "integer", nullable: false),
                    timeout_ms = table.Column<int>(type: "integer", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_token = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_deliveries", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_access_tokens_owner_email",
                table: "access_tokens",
                column: "owner_email");

            migrationBuilder.CreateIndex(
                name: "IX_access_tokens_token_hash",
                table: "access_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_entities_sandbox_id_due_at",
                table: "entities",
                columns: new[] { "sandbox_id", "due_at" });

            migrationBuilder.CreateIndex(
                name: "IX_interactions_correlation_id",
                table: "interactions",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "IX_interactions_recorded_at",
                table: "interactions",
                column: "recorded_at");

            migrationBuilder.CreateIndex(
                name: "IX_interactions_sandbox_id_at",
                table: "interactions",
                columns: new[] { "sandbox_id", "at" });

            migrationBuilder.CreateIndex(
                name: "IX_interactions_sandbox_id_entity_id",
                table: "interactions",
                columns: new[] { "sandbox_id", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_attempts_delivery_id",
                table: "webhook_attempts",
                column: "delivery_id");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_sandbox_id_entity_id",
                table: "webhook_deliveries",
                columns: new[] { "sandbox_id", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_status_next_attempt_at",
                table: "webhook_deliveries",
                columns: new[] { "status", "next_attempt_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "access_tokens");

            migrationBuilder.DropTable(
                name: "entities");

            migrationBuilder.DropTable(
                name: "faults");

            migrationBuilder.DropTable(
                name: "interactions");

            migrationBuilder.DropTable(
                name: "sandboxes");

            migrationBuilder.DropTable(
                name: "webhook_attempts");

            migrationBuilder.DropTable(
                name: "webhook_deliveries");
        }
    }
}
