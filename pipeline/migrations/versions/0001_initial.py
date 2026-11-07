"""initial schema (SPEC section 8)

Revision ID: 0001
Revises:
Create Date: 2026-11-07
"""

import sqlalchemy as sa
from alembic import op

revision = "0001"
down_revision = None
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.create_table(
        "scenarios",
        sa.Column("name", sa.String(128), primary_key=True),
    )
    op.create_table(
        "machines",
        sa.Column("id_hash", sa.String(128), primary_key=True),
        sa.Column("os_build", sa.String(64), nullable=False),
        sa.Column("cpu", sa.String(256), nullable=False),
        sa.Column("logical_cpus", sa.Integer, nullable=False),
        sa.CheckConstraint("logical_cpus >= 1", name="ck_machines_logical_cpus"),
    )
    op.create_table(
        "runs",
        sa.Column("run_id", sa.Uuid, primary_key=True),
        sa.Column(
            "scenario", sa.String(128), sa.ForeignKey("scenarios.name"), nullable=False
        ),
        sa.Column(
            "machine_id_hash",
            sa.String(128),
            sa.ForeignKey("machines.id_hash"),
            nullable=False,
        ),
        sa.Column("condition", sa.String(1), nullable=False),
        sa.Column("label", sa.String(256), nullable=False),
        sa.Column("started_utc", sa.DateTime(timezone=True), nullable=False),
        sa.Column("duration_s", sa.Float, nullable=False),
        sa.Column("on_ac", sa.Boolean, nullable=False),
        sa.Column("battery_saver", sa.Boolean, nullable=False),
        sa.Column("plan_guid", sa.String(64), nullable=False),
        sa.Column("start_pct", sa.Integer, nullable=False),
        sa.Column("end_pct", sa.Integer, nullable=False),
        sa.Column("dropped_events", sa.Integer, nullable=False),
        sa.Column("sampler_gaps_s", sa.Float, nullable=False),
        sa.Column("gates_passed", sa.Boolean, nullable=False),
        sa.Column("mean_discharge_mw", sa.Float, nullable=True),
        sa.Column("total_cpu_ms", sa.Float, nullable=False),
        sa.Column("total_wakeups", sa.Integer, nullable=False),
        sa.CheckConstraint("condition IN ('A', 'B')", name="ck_runs_condition"),
        sa.CheckConstraint("duration_s > 0", name="ck_runs_duration"),
        sa.CheckConstraint("start_pct BETWEEN 0 AND 100", name="ck_runs_start_pct"),
        sa.CheckConstraint("end_pct BETWEEN 0 AND 100", name="ck_runs_end_pct"),
        sa.CheckConstraint("dropped_events >= 0", name="ck_runs_dropped"),
        sa.CheckConstraint("sampler_gaps_s >= 0", name="ck_runs_gaps"),
        sa.CheckConstraint("total_cpu_ms >= 0", name="ck_runs_total_cpu"),
        sa.CheckConstraint("total_wakeups >= 0", name="ck_runs_total_wakeups"),
    )
    op.create_index(
        "ix_runs_scenario_condition_started",
        "runs",
        ["scenario", "condition", "started_utc"],
    )
    op.create_index("ix_runs_machine_id_hash", "runs", ["machine_id_hash"])
    op.create_table(
        "process_cpu",
        sa.Column(
            "run_id",
            sa.Uuid,
            sa.ForeignKey("runs.run_id", ondelete="CASCADE"),
            primary_key=True,
        ),
        sa.Column("process", sa.String(256), primary_key=True),
        sa.Column("cpu_ms", sa.Float, nullable=False),
        sa.Column("context_switches", sa.Integer, nullable=False),
        sa.CheckConstraint("cpu_ms >= 0", name="ck_process_cpu_cpu_ms"),
        sa.CheckConstraint("context_switches >= 0", name="ck_process_cpu_cs"),
    )
    op.create_table(
        "wakeups",
        sa.Column(
            "run_id",
            sa.Uuid,
            sa.ForeignKey("runs.run_id", ondelete="CASCADE"),
            primary_key=True,
        ),
        sa.Column("process", sa.String(256), primary_key=True),
        sa.Column("wakeups", sa.Integer, nullable=False),
        sa.Column("wakeups_per_s", sa.Float, nullable=False),
        sa.CheckConstraint("wakeups >= 0", name="ck_wakeups_wakeups"),
        sa.CheckConstraint("wakeups_per_s >= 0", name="ck_wakeups_per_s"),
    )
    op.create_table(
        "battery_samples",
        sa.Column(
            "run_id",
            sa.Uuid,
            sa.ForeignKey("runs.run_id", ondelete="CASCADE"),
            primary_key=True,
        ),
        sa.Column("t_s", sa.Float, primary_key=True),
        sa.Column("discharge_mw", sa.Float, nullable=False),
        sa.Column("remaining_mwh", sa.Float, nullable=False),
        sa.CheckConstraint("t_s >= 0", name="ck_battery_t_s"),
        sa.CheckConstraint("remaining_mwh >= 0", name="ck_battery_remaining"),
    )


def downgrade() -> None:
    op.drop_table("battery_samples")
    op.drop_table("wakeups")
    op.drop_table("process_cpu")
    op.drop_index("ix_runs_machine_id_hash", table_name="runs")
    op.drop_index("ix_runs_scenario_condition_started", table_name="runs")
    op.drop_table("runs")
    op.drop_table("machines")
    op.drop_table("scenarios")
