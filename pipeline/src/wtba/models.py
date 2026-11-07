"""SQLAlchemy 2 models for SPEC section 8."""

from __future__ import annotations

import datetime as dt
import uuid

from sqlalchemy import (
    Boolean,
    CheckConstraint,
    DateTime,
    Float,
    ForeignKey,
    Index,
    Integer,
    String,
    Uuid,
)
from sqlalchemy.orm import DeclarativeBase, Mapped, mapped_column


class Base(DeclarativeBase):
    pass


class Scenario(Base):
    __tablename__ = "scenarios"
    name: Mapped[str] = mapped_column(String(128), primary_key=True)


class Machine(Base):
    __tablename__ = "machines"
    __table_args__ = (
        CheckConstraint("logical_cpus >= 1", name="ck_machines_logical_cpus"),
    )
    id_hash: Mapped[str] = mapped_column(String(128), primary_key=True)
    os_build: Mapped[str] = mapped_column(String(64))
    cpu: Mapped[str] = mapped_column(String(256))
    logical_cpus: Mapped[int] = mapped_column(Integer)


class Run(Base):
    __tablename__ = "runs"
    __table_args__ = (
        CheckConstraint("condition IN ('A', 'B')", name="ck_runs_condition"),
        CheckConstraint("duration_s > 0", name="ck_runs_duration"),
        CheckConstraint("start_pct BETWEEN 0 AND 100", name="ck_runs_start_pct"),
        CheckConstraint("end_pct BETWEEN 0 AND 100", name="ck_runs_end_pct"),
        CheckConstraint("dropped_events >= 0", name="ck_runs_dropped"),
        CheckConstraint("sampler_gaps_s >= 0", name="ck_runs_gaps"),
        CheckConstraint("total_cpu_ms >= 0", name="ck_runs_total_cpu"),
        CheckConstraint("total_wakeups >= 0", name="ck_runs_total_wakeups"),
        Index(
            "ix_runs_scenario_condition_started", "scenario", "condition", "started_utc"
        ),
    )
    run_id: Mapped[uuid.UUID] = mapped_column(Uuid, primary_key=True)
    scenario: Mapped[str] = mapped_column(ForeignKey("scenarios.name"))
    machine_id_hash: Mapped[str] = mapped_column(
        ForeignKey("machines.id_hash"), index=True
    )
    condition: Mapped[str] = mapped_column(String(1))
    label: Mapped[str] = mapped_column(String(256))
    started_utc: Mapped[dt.datetime] = mapped_column(DateTime(timezone=True))
    duration_s: Mapped[float] = mapped_column(Float)
    on_ac: Mapped[bool] = mapped_column(Boolean)
    battery_saver: Mapped[bool] = mapped_column(Boolean)
    plan_guid: Mapped[str] = mapped_column(String(64))
    start_pct: Mapped[int] = mapped_column(Integer)
    end_pct: Mapped[int] = mapped_column(Integer)
    dropped_events: Mapped[int] = mapped_column(Integer)
    sampler_gaps_s: Mapped[float] = mapped_column(Float)
    gates_passed: Mapped[bool] = mapped_column(Boolean)
    mean_discharge_mw: Mapped[float | None] = mapped_column(Float, nullable=True)
    total_cpu_ms: Mapped[float] = mapped_column(Float)
    total_wakeups: Mapped[int] = mapped_column(Integer)


class ProcessCpu(Base):
    __tablename__ = "process_cpu"
    __table_args__ = (
        CheckConstraint("cpu_ms >= 0", name="ck_process_cpu_cpu_ms"),
        CheckConstraint("context_switches >= 0", name="ck_process_cpu_cs"),
    )
    run_id: Mapped[uuid.UUID] = mapped_column(
        ForeignKey("runs.run_id", ondelete="CASCADE"), primary_key=True
    )
    process: Mapped[str] = mapped_column(String(256), primary_key=True)
    cpu_ms: Mapped[float] = mapped_column(Float)
    context_switches: Mapped[int] = mapped_column(Integer)


class Wakeup(Base):
    __tablename__ = "wakeups"
    __table_args__ = (
        CheckConstraint("wakeups >= 0", name="ck_wakeups_wakeups"),
        CheckConstraint("wakeups_per_s >= 0", name="ck_wakeups_per_s"),
    )
    run_id: Mapped[uuid.UUID] = mapped_column(
        ForeignKey("runs.run_id", ondelete="CASCADE"), primary_key=True
    )
    process: Mapped[str] = mapped_column(String(256), primary_key=True)
    wakeups: Mapped[int] = mapped_column(Integer)
    wakeups_per_s: Mapped[float] = mapped_column(Float)


class BatterySample(Base):
    __tablename__ = "battery_samples"
    __table_args__ = (
        CheckConstraint("t_s >= 0", name="ck_battery_t_s"),
        CheckConstraint("remaining_mwh >= 0", name="ck_battery_remaining"),
    )
    run_id: Mapped[uuid.UUID] = mapped_column(
        ForeignKey("runs.run_id", ondelete="CASCADE"), primary_key=True
    )
    t_s: Mapped[float] = mapped_column(Float, primary_key=True)
    discharge_mw: Mapped[float] = mapped_column(Float)
    remaining_mwh: Mapped[float] = mapped_column(Float)
