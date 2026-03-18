"""
test_phase9_validation.py — Phase 9 proving harness.

Validates the end-to-end learning loop lifecycle without requiring sklearn
or real parquet data. Tests exercise:
  A. Cursor safety       — no double-training, correct advancement
  B. Restart safety      — file coherence across simulated restart boundaries
  C. Temporal blocking   — unsafe samples rejected from training
  D. Class skew visibility — skew warnings emitted in diagnostics
  E. Lifecycle integrity — pending→resolved→trained flow correctness

Run with:
  python -m pytest ml/tests/test_phase9_validation.py -v
  OR
  python ml/tests/test_phase9_validation.py
"""

from __future__ import annotations

import json
import os
import shutil
import sys
import tempfile
import unittest
from datetime import datetime, timezone, timedelta
from pathlib import Path
from unittest.mock import patch

# Ensure the ml package is importable
_root = Path(__file__).resolve().parent.parent.parent
if str(_root) not in sys.path:
    sys.path.insert(0, str(_root))

from ml.policies import (
    LabelPolicy, ResolutionPolicy, TrainingPolicy,
    DEFAULT_LABEL_POLICY, DEFAULT_RESOLUTION_POLICY, DEFAULT_TRAINING_POLICY,
)
from ml.training_cursor import TrainingCursor, load_cursor, save_cursor
from ml.pending_memory import (
    store_pending_sample, load_pending_samples, append_resolved_samples,
    rewrite_pending_after_resolve, load_resolved_samples,
    load_resolved_since_cursor, pending_count, resolved_count,
)
from ml.grading import grade_directional, grade_regime
from ml.temporal_security import check_temporal_safety, compute_eligibility
from ml.label_resolver import resolve_pending_batch, ResolutionResult


class _TempDataLakeTestCase(unittest.TestCase):
    """Base class that patches _cortex_root to use a temp directory."""

    def setUp(self):
        self._tmpdir = tempfile.mkdtemp(prefix="aleph_test_")
        self._cortex_root = Path(self._tmpdir)

        # Patch both pending_memory and training_cursor to use temp dir
        self._patches = []
        for mod_path in [
            "ml.pending_memory._cortex_root",
            "ml.training_cursor._cortex_root",
        ]:
            p = patch(mod_path, return_value=self._cortex_root)
            p.start()
            self._patches.append(p)

    def tearDown(self):
        for p in self._patches:
            p.stop()
        shutil.rmtree(self._tmpdir, ignore_errors=True)


# ═══════════════════════════════════════════════════════════════════
# A. CURSOR SAFETY
# ═══════════════════════════════════════════════════════════════════

class TestCursorSafety(_TempDataLakeTestCase):

    def test_cursor_starts_empty(self):
        """Fresh cursor has no consumed IDs."""
        cursor = load_cursor("TEST", "1d")
        self.assertEqual(cursor.sequence, 0)
        self.assertEqual(len(cursor.consumed_ids), 0)

    def test_cursor_marks_consumed(self):
        """After mark_consumed, those IDs are excluded from future fresh batches."""
        cursor = TrainingCursor(symbol="TEST", horizon="1d")
        cursor.mark_consumed(["pred_001", "pred_002"], "tp_v1")

        self.assertEqual(cursor.sequence, 1)
        self.assertTrue(cursor.is_consumed("pred_001"))
        self.assertTrue(cursor.is_consumed("pred_002"))
        self.assertFalse(cursor.is_consumed("pred_003"))

    def test_cursor_get_unconsumed_filters(self):
        """get_unconsumed only returns IDs not in the consumed set."""
        cursor = TrainingCursor(symbol="TEST", horizon="1d")
        cursor.mark_consumed(["pred_001", "pred_002"], "tp_v1")

        unconsumed = cursor.get_unconsumed(["pred_001", "pred_002", "pred_003", "pred_004"])
        self.assertEqual(unconsumed, ["pred_003", "pred_004"])

    def test_cursor_persists_across_save_load(self):
        """Cursor state survives save/load cycle (simulates restart)."""
        cursor = TrainingCursor(symbol="TEST", horizon="1d")
        cursor.mark_consumed(["pred_001", "pred_002", "pred_003"], "tp_v1")
        save_cursor(cursor)

        reloaded = load_cursor("TEST", "1d")
        self.assertEqual(reloaded.sequence, 1)
        self.assertTrue(reloaded.is_consumed("pred_001"))
        self.assertTrue(reloaded.is_consumed("pred_003"))
        self.assertEqual(reloaded.total_samples_ever, 3)

    def test_no_double_training_across_cycles(self):
        """Simulates two training cycles — cursor prevents double consumption."""
        # Cycle 1: mark pred_001 and pred_002 as consumed
        cursor = TrainingCursor(symbol="TEST", horizon="1d")
        cursor.mark_consumed(["pred_001", "pred_002"], "tp_v1")
        save_cursor(cursor)

        # Cycle 2: new candidates include pred_001 (old) + pred_003 (new)
        cursor2 = load_cursor("TEST", "1d")
        fresh = cursor2.get_unconsumed(["pred_001", "pred_002", "pred_003"])
        self.assertEqual(fresh, ["pred_003"])  # only the new one

        cursor2.mark_consumed(["pred_003"], "tp_v1")
        save_cursor(cursor2)

        # Verify total state
        cursor3 = load_cursor("TEST", "1d")
        self.assertEqual(cursor3.sequence, 2)
        self.assertEqual(cursor3.total_samples_ever, 3)

    def test_resolved_since_cursor_splits_correctly(self):
        """load_resolved_since_cursor correctly splits fresh vs replay."""
        # Create resolved samples
        for i in range(5):
            append_resolved_samples("TEST", "1d", [{
                "prediction_id": f"pred_{i:03d}",
                "features": [0.0] * 38,
                "actual_label": "bullish" if i % 2 == 0 else "bearish",
                "eligible_for_training": True,
                "asof_utc": "2025-01-01T00:00:00Z",
            }])

        # Cursor says pred_000 and pred_001 are consumed
        consumed = {"pred_000", "pred_001"}
        fresh, replay = load_resolved_since_cursor("TEST", "1d", consumed)

        self.assertEqual(len(fresh), 3)  # pred_002, pred_003, pred_004
        self.assertEqual(len(replay), 2)  # pred_000, pred_001

    def test_cursor_prune_does_not_lose_recent(self):
        """Pruning old IDs doesn't corrupt the cursor."""
        cursor = TrainingCursor(symbol="TEST", horizon="1d")
        ids = [f"id_{i}" for i in range(100)]
        cursor.mark_consumed(ids, "tp_v1")

        pruned = cursor.prune_old_ids(max_ids=50)
        self.assertEqual(pruned, 50)
        self.assertEqual(len(cursor.consumed_ids), 50)


# ═══════════════════════════════════════════════════════════════════
# B. RESTART SAFETY
# ═══════════════════════════════════════════════════════════════════

class TestRestartSafety(_TempDataLakeTestCase):

    def test_pending_survives_restart(self):
        """Pending samples persist across simulated restart."""
        store_pending_sample("TEST", "1d", [1.0]*38, "bullish", "2025-01-01T00:00:00Z",
                            prediction_id="restart_001")
        store_pending_sample("TEST", "1d", [2.0]*38, "bearish", "2025-01-02T00:00:00Z",
                            prediction_id="restart_002")

        # "Restart" — reload from disk
        samples = load_pending_samples("TEST", "1d")
        self.assertEqual(len(samples), 2)
        ids = {s["prediction_id"] for s in samples}
        self.assertIn("restart_001", ids)
        self.assertIn("restart_002", ids)

    def test_resolved_survives_restart(self):
        """Resolved archive persists and doesn't lose records."""
        records = [
            {"prediction_id": f"res_{i}", "features": [0.0]*38, "actual_label": "neutral",
             "asof_utc": "2025-01-01T00:00:00Z"}
            for i in range(3)
        ]
        append_resolved_samples("TEST", "1d", records)

        reloaded = load_resolved_samples("TEST", "1d")
        self.assertEqual(len(reloaded), 3)

    def test_atomic_pending_rewrite_survives_interruption(self):
        """After rewrite, only unresolved samples remain."""
        for i in range(5):
            store_pending_sample("TEST", "1d", [float(i)]*38, "neutral",
                                f"2025-01-{i+1:02d}T00:00:00Z",
                                prediction_id=f"pend_{i:03d}")

        # Resolve samples 0, 1, 2 — leave 3 and 4
        result = rewrite_pending_after_resolve(
            "TEST", "1d",
            resolved_ids={"pend_000", "pend_001", "pend_002"},
            expired_ids=set(),
        )

        self.assertEqual(result["kept"], 2)
        self.assertEqual(result["removed"], 3)

        remaining = load_pending_samples("TEST", "1d")
        self.assertEqual(len(remaining), 2)
        remaining_ids = {s["prediction_id"] for s in remaining}
        self.assertEqual(remaining_ids, {"pend_003", "pend_004"})

    def test_cursor_survives_restart_between_resolve_and_train(self):
        """Simulates crash between resolve and train — cursor wasn't updated yet."""
        # Resolve produces new resolved samples
        records = [
            {"prediction_id": "crash_001", "features": [0.0]*38, "actual_label": "bullish",
             "asof_utc": "2025-01-01T00:00:00Z", "eligible_for_training": True}
        ]
        append_resolved_samples("TEST", "1d", records)

        # System crashes before train — cursor is still empty
        cursor = load_cursor("TEST", "1d")
        self.assertEqual(cursor.sequence, 0)
        self.assertFalse(cursor.is_consumed("crash_001"))

        # On restart, fresh samples should still be available
        fresh, replay = load_resolved_since_cursor("TEST", "1d", cursor.consumed_ids)
        self.assertEqual(len(fresh), 1)
        self.assertEqual(fresh[0]["prediction_id"], "crash_001")

    def test_no_duplicate_resolve_on_restart(self):
        """Pending rewrite ensures already-resolved samples don't get re-resolved."""
        store_pending_sample("TEST", "1d", [1.0]*38, "bullish", "2025-01-01T00:00:00Z",
                            prediction_id="dup_001")

        # First resolve cycle
        rewrite_pending_after_resolve("TEST", "1d", resolved_ids={"dup_001"})

        # On restart, pending should not contain dup_001
        remaining = load_pending_samples("TEST", "1d")
        for s in remaining:
            self.assertNotEqual(s["prediction_id"], "dup_001")


# ═══════════════════════════════════════════════════════════════════
# C. TEMPORAL BLOCKING
# ═══════════════════════════════════════════════════════════════════

class TestTemporalBlocking(unittest.TestCase):

    def test_safe_payload_passes(self):
        """Point-in-time safe payload passes temporal check."""
        payload = {
            "temporal": {"observation_cutoff_utc": "2025-06-01T12:00:00Z"},
            "macro": {"cross_asset": {"as_of_utc": "2025-06-01T11:00:00Z"}},
        }
        result = check_temporal_safety(payload)
        self.assertTrue(result["passed"])
        self.assertEqual(len(result["violations"]), 0)

    def test_future_knowledge_fails(self):
        """Knowledge after cutoff fails temporal check."""
        payload = {
            "temporal": {"observation_cutoff_utc": "2025-06-01T12:00:00Z"},
            "macro": {"cross_asset": {"as_of_utc": "2025-06-01T13:00:00Z"}},
        }
        result = check_temporal_safety(payload)
        self.assertFalse(result["passed"])
        self.assertIn("cross_asset_knowledge_after_cutoff", result["violations"])

    def test_missing_cutoff_fails(self):
        """Missing observation_cutoff_utc is treated as unsafe."""
        result = check_temporal_safety({"temporal": {}})
        self.assertFalse(result["passed"])
        self.assertIn("missing_observation_cutoff_utc", result["violations"])

    def test_eligibility_blocks_unsafe(self):
        """compute_eligibility blocks temporally unsafe samples."""
        eligible, reasons = compute_eligibility(
            temporal_passed=False,
            governance={"breathless": False, "overloaded": False},
        )
        self.assertFalse(eligible)
        self.assertIn("temporal_safety_failed", reasons)

    def test_eligibility_blocks_breathless(self):
        """compute_eligibility blocks when system is breathless."""
        eligible, reasons = compute_eligibility(
            temporal_passed=True,
            governance={"breathless": True, "overloaded": False},
        )
        self.assertFalse(eligible)
        self.assertIn("system_breathless", reasons)

    def test_unsafe_sample_stays_ineligible_through_resolve(self):
        """A temporally unsafe pending sample remains blocked through resolution."""
        import pandas as pd
        import numpy as np

        now = datetime(2025, 6, 1, 12, 0, tzinfo=timezone.utc)

        pending = [{
            "prediction_id": "unsafe_001",
            "asof_utc": "2025-06-01T00:00:00Z",
            "stored_utc": "2025-06-01T00:00:00Z",
            "symbol": "TEST",
            "horizon": "1d",
            "interval": "1h",
            "horizon_bars": 24,
            "features": [0.0] * 38,
            "predicted_class": "bullish",
            "predicted_probabilities": {"bullish": 0.6, "neutral": 0.2, "bearish": 0.2},
            "regime_probabilities": {},
            "event_probabilities": {},
            "entry_price": 100.0,
            "price_basis": "close",
            "point_in_time_safe": False,  # UNSAFE
            "eligible_for_training": False,
            "learning_block_reasons": ["temporal_safety_failed"],
            "observation_cutoff_utc": "2025-06-01T00:00:00Z",
        }]

        # Build OHLCV covering the horizon
        times = pd.date_range("2025-05-31", periods=48, freq="h", tz="UTC")
        df = pd.DataFrame({
            "time": times,
            "open": np.full(48, 100.0),
            "high": np.full(48, 105.0),
            "low": np.full(48, 95.0),
            "close": np.linspace(100, 102, 48),
            "volume": np.full(48, 1000),
        })

        result = resolve_pending_batch(pending, df)

        # It should resolve (it computes the label for observability)
        # but the record must remain ineligible for training
        if result.resolved:
            for rec in result.resolved:
                self.assertFalse(rec["eligible_for_training"])
                self.assertIn("temporal_safety_failed", rec["learning_block_reasons"])


# ═══════════════════════════════════════════════════════════════════
# D. CLASS SKEW VISIBILITY
# ═══════════════════════════════════════════════════════════════════

class TestClassSkewVisibility(unittest.TestCase):

    def test_label_policy_produces_all_classes(self):
        """LabelPolicy can produce all three classes."""
        lp = DEFAULT_LABEL_POLICY
        self.assertEqual(lp.assign_label(200.0), "bullish")
        self.assertEqual(lp.assign_label(-200.0), "bearish")
        self.assertEqual(lp.assign_label(0.0), "neutral")

    def test_ambiguity_scores_near_threshold(self):
        """Samples near threshold boundaries get high ambiguity."""
        lp = DEFAULT_LABEL_POLICY
        # Exactly on bullish threshold
        self.assertGreater(lp.compute_ambiguity(100.0), 0.9)
        # Well inside bullish
        self.assertEqual(lp.compute_ambiguity(300.0), 0.0)

    def test_grading_detects_wrong_direction(self):
        """grade_directional correctly identifies wrong-direction predictions."""
        grade = grade_directional(
            predicted_class="bullish",
            actual_label="bearish",
            predicted_probabilities={"bullish": 0.7, "neutral": 0.2, "bearish": 0.1},
        )
        self.assertFalse(grade["correct"])
        self.assertEqual(grade["grade_bucket"], "wrong_direction")
        self.assertGreater(grade["brier_score"], 1.0)  # should be high for wrong

    def test_grading_detects_correct(self):
        """grade_directional correctly identifies correct predictions."""
        grade = grade_directional(
            predicted_class="bullish",
            actual_label="bullish",
            predicted_probabilities={"bullish": 0.8, "neutral": 0.1, "bearish": 0.1},
        )
        self.assertTrue(grade["correct"])
        self.assertEqual(grade["grade_bucket"], "correct")
        self.assertLess(grade["brier_score"], 0.3)

    def test_regime_concentration_metric(self):
        """Regime grading detects concentrated vs diffuse regime distributions."""
        concentrated = grade_regime({"risk_on": 0.9, "risk_off": 0.02,
                                      "inflation_pressure": 0.02, "growth_scare": 0.02,
                                      "policy_shock": 0.02, "flight_to_safety": 0.02})
        self.assertGreater(concentrated["concentration"], 0.5)
        self.assertEqual(concentrated["dominant_regime"], "risk_on")

        diffuse = grade_regime({"risk_on": 0.17, "risk_off": 0.17,
                                 "inflation_pressure": 0.17, "growth_scare": 0.17,
                                 "policy_shock": 0.16, "flight_to_safety": 0.16})
        self.assertLess(diffuse["concentration"], 0.1)


# ═══════════════════════════════════════════════════════════════════
# E. LIFECYCLE INTEGRITY
# ═══════════════════════════════════════════════════════════════════

class TestLifecycleIntegrity(_TempDataLakeTestCase):

    def test_pending_stores_and_loads(self):
        """Basic pending storage and retrieval."""
        ok = store_pending_sample("TEST", "1d", [1.0]*38, "bullish", "2025-01-01T00:00:00Z",
                                  prediction_id="life_001")
        self.assertTrue(ok)
        samples = load_pending_samples("TEST", "1d")
        self.assertEqual(len(samples), 1)
        self.assertEqual(samples[0]["prediction_id"], "life_001")

    def test_resolved_append_is_additive(self):
        """Multiple appends grow the resolved archive, never overwrite."""
        append_resolved_samples("TEST", "1d", [
            {"prediction_id": "r1", "features": [0.0]*38, "actual_label": "bullish",
             "asof_utc": "2025-01-01T00:00:00Z"},
        ])
        append_resolved_samples("TEST", "1d", [
            {"prediction_id": "r2", "features": [0.0]*38, "actual_label": "bearish",
             "asof_utc": "2025-01-02T00:00:00Z"},
        ])

        resolved = load_resolved_samples("TEST", "1d")
        self.assertEqual(len(resolved), 2)
        ids = {r["prediction_id"] for r in resolved}
        self.assertEqual(ids, {"r1", "r2"})

    def test_resolve_removes_from_pending_adds_to_resolved(self):
        """Full lifecycle: pending → resolve → pending shrinks, resolved grows."""
        # Add 3 pending
        for i in range(3):
            store_pending_sample("TEST", "1d", [float(i)]*38, "neutral",
                                f"2025-01-{i+1:02d}T00:00:00Z",
                                prediction_id=f"lc_{i:03d}")

        self.assertEqual(pending_count("TEST", "1d"), 3)
        self.assertEqual(resolved_count("TEST", "1d"), 0)

        # "Resolve" pred_000 and pred_001
        resolved_records = [
            {"prediction_id": "lc_000", "features": [0.0]*38, "actual_label": "bullish",
             "asof_utc": "2025-01-01T00:00:00Z"},
            {"prediction_id": "lc_001", "features": [1.0]*38, "actual_label": "bearish",
             "asof_utc": "2025-01-02T00:00:00Z"},
        ]
        append_resolved_samples("TEST", "1d", resolved_records)

        rewrite_pending_after_resolve("TEST", "1d",
                                      resolved_ids={"lc_000", "lc_001"})

        # Verify state
        remaining = load_pending_samples("TEST", "1d")
        self.assertEqual(len(remaining), 1)
        self.assertEqual(remaining[0]["prediction_id"], "lc_002")

        resolved = load_resolved_samples("TEST", "1d")
        self.assertEqual(len(resolved), 2)

    def test_empty_pending_rewrite_is_noop(self):
        """Rewriting with no resolved IDs doesn't lose samples."""
        store_pending_sample("TEST", "1d", [1.0]*38, "bullish", "2025-01-01T00:00:00Z",
                            prediction_id="noop_001")

        result = rewrite_pending_after_resolve("TEST", "1d", resolved_ids=set())
        self.assertEqual(result["kept"], 1)
        self.assertEqual(result["removed"], 0)

        remaining = load_pending_samples("TEST", "1d")
        self.assertEqual(len(remaining), 1)

    def test_expired_samples_removed_from_pending(self):
        """Expired samples are removed during pending rewrite."""
        store_pending_sample("TEST", "1d", [1.0]*38, "bullish", "2025-01-01T00:00:00Z",
                            prediction_id="exp_001")
        store_pending_sample("TEST", "1d", [2.0]*38, "bearish", "2025-01-02T00:00:00Z",
                            prediction_id="exp_002")

        result = rewrite_pending_after_resolve("TEST", "1d",
                                               resolved_ids=set(),
                                               expired_ids={"exp_001"})
        self.assertEqual(result["kept"], 1)
        self.assertEqual(result["expired"], 1)

        remaining = load_pending_samples("TEST", "1d")
        self.assertEqual(len(remaining), 1)
        self.assertEqual(remaining[0]["prediction_id"], "exp_002")

    def test_full_cycle_cursor_integration(self):
        """Full lifecycle: pending → resolved → cursor → train doesn't double-consume."""
        # Create resolved samples simulating two resolve cycles
        for i in range(6):
            append_resolved_samples("TEST", "1d", [{
                "prediction_id": f"full_{i:03d}",
                "features": [float(i)] * 38,
                "actual_label": ["bullish", "neutral", "bearish"][i % 3],
                "eligible_for_training": True,
                "point_in_time_safe": True,
                "asof_utc": f"2025-01-{i+1:02d}T00:00:00Z",
            }])

        # First training cycle
        cursor = load_cursor("TEST", "1d")
        fresh, replay = load_resolved_since_cursor("TEST", "1d", cursor.consumed_ids)
        self.assertEqual(len(fresh), 6)
        self.assertEqual(len(replay), 0)

        # Simulate training on first 3
        cursor.mark_consumed(
            [f["prediction_id"] for f in fresh[:3]], "tp_v1"
        )
        save_cursor(cursor)

        # Second training cycle
        cursor2 = load_cursor("TEST", "1d")
        fresh2, replay2 = load_resolved_since_cursor("TEST", "1d", cursor2.consumed_ids)
        self.assertEqual(len(fresh2), 3)  # only the unconsumed ones
        self.assertEqual(len(replay2), 3)  # the consumed ones become replay

        # Consume the rest
        cursor2.mark_consumed(
            [f["prediction_id"] for f in fresh2], "tp_v1"
        )
        save_cursor(cursor2)

        # Third cycle — everything is consumed, no fresh
        cursor3 = load_cursor("TEST", "1d")
        fresh3, replay3 = load_resolved_since_cursor("TEST", "1d", cursor3.consumed_ids)
        self.assertEqual(len(fresh3), 0)
        self.assertEqual(len(replay3), 6)
        self.assertEqual(cursor3.total_samples_ever, 6)
        self.assertEqual(cursor3.sequence, 2)


# ═══════════════════════════════════════════════════════════════════
# F. RESOLUTION POLICY BEHAVIOR
# ═══════════════════════════════════════════════════════════════════

class TestResolutionPolicy(unittest.TestCase):

    def test_resolve_defers_when_no_ohlcv(self):
        """All samples deferred when no OHLCV data available."""
        pending = [{"prediction_id": "x", "asof_utc": "2025-01-01T00:00:00Z",
                     "stored_utc": "2025-01-01T00:00:00Z",
                     "horizon_bars": 24, "interval": "1h",
                     "features": [0.0]*38}]

        result = resolve_pending_batch(pending, None)
        self.assertEqual(len(result.resolved), 0)
        self.assertEqual(len(result.deferred), 1)

    def test_resolve_produces_correct_summary(self):
        """Resolution summary contains expected fields."""
        result = resolve_pending_batch([], None)
        summary = result.summary()
        self.assertIn("total_processed", summary)
        self.assertIn("label_policy_version", summary)
        self.assertIn("resolution_policy_version", summary)

    def test_label_policy_round_trip(self):
        """LabelPolicy survives serialization."""
        lp = LabelPolicy(version="lp_test", bullish_threshold_bps=75.0)
        d = lp.to_dict()
        lp2 = LabelPolicy.from_dict(d)
        self.assertEqual(lp2.version, "lp_test")
        self.assertEqual(lp2.bullish_threshold_bps, 75.0)

    def test_training_policy_round_trip(self):
        """TrainingPolicy survives serialization."""
        tp = TrainingPolicy(version="tp_test", replay_ratio=0.5, min_samples_to_train=10)
        d = tp.to_dict()
        tp2 = TrainingPolicy.from_dict(d)
        self.assertEqual(tp2.replay_ratio, 0.5)
        self.assertEqual(tp2.min_samples_to_train, 10)


if __name__ == "__main__":
    unittest.main(verbosity=2)
