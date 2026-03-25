"""Tests for Phase 10.6 feature synapse wiring."""

from __future__ import annotations

import unittest

from ml.feature_adapter import FEATURE_NAMES, extract_features, feature_count


class TestFeatureAdapterSynapses(unittest.TestCase):
    def _base_payload(self) -> dict:
        return {
            "meta": {"asof_utc": "2026-03-24T12:00:00Z"},
            "technical": {
                "rsi_14": 51.2,
                "macd_line": 0.4,
                "macd_signal": 0.2,
                "macd_histogram": 0.2,
            },
            "macro": {},
            "events": {},
        }

    def _idx(self, feature_name: str) -> int:
        return FEATURE_NAMES.index(feature_name)

    def test_incumbent_vector_contract_stays_stable(self):
        self.assertEqual(feature_count(), len(FEATURE_NAMES))
        self.assertEqual(feature_count(), 38)

    def test_perception_proxies_and_calendar_wire_into_expected_slots(self):
        payload = self._base_payload()
        payload["macro"] = {
            "proxies": {
                "_status": "fresh",
                "data": {
                    "fetchedAtUtc": "2026-03-24T12:00:00Z",
                    "proxies": {
                        "vix": {"available": True, "latestClose": 30.0},
                        "dxy": {"available": True, "latestClose": 105.0},
                        "btc": {"available": True, "latestClose": 60000.0},
                    },
                },
            },
            "calendar": {
                "_status": "fresh",
                "data": {
                    "fetchedAtUtc": "2026-03-24T12:00:00Z",
                    "events": [
                        {
                            "eventType": "CPI",
                            "scheduledUtc": "2026-03-25T12:00:00Z",
                            "importance": "high",
                        }
                    ],
                },
            },
        }

        vector = extract_features(payload)

        self.assertAlmostEqual(vector[self._idx("macro_volatility_pressure")], 1.0, places=6)
        self.assertAlmostEqual(vector[self._idx("macro_dollar_pressure")], 1.0, places=6)
        self.assertAlmostEqual(vector[self._idx("macro_crypto_risk")], 1.0, places=6)
        self.assertEqual(vector[self._idx("event_schedule_tension")], 1.0)

    def test_stale_or_missing_macro_sections_degrade_to_defaults(self):
        payload = self._base_payload()
        payload["macro"] = {
            "proxies": {
                "_status": "stale",
                "data": {
                    "proxies": {
                        "vix": {"available": True, "latestClose": 40.0},
                        "dxy": {"available": True, "latestClose": 110.0},
                        "btc": {"available": True, "latestClose": 90000.0},
                    }
                },
            },
            "calendar": {
                "_status": "error",
                "data": {
                    "events": [
                        {
                            "eventType": "NFP",
                            "scheduledUtc": "2026-03-24T18:00:00Z",
                            "importance": "high",
                        }
                    ]
                },
            },
        }

        vector = extract_features(payload)

        self.assertEqual(vector[self._idx("macro_volatility_pressure")], 0.0)
        self.assertEqual(vector[self._idx("macro_dollar_pressure")], 0.0)
        self.assertEqual(vector[self._idx("macro_crypto_risk")], 0.0)
        self.assertEqual(vector[self._idx("event_schedule_tension")], 0.0)

    def test_legacy_macro_paths_still_work(self):
        payload = self._base_payload()
        payload["macro"] = {
            "cross_asset": {
                "dollar_pressure": 0.42,
                "volatility_pressure": 0.33,
                "crypto_risk": -0.15,
            }
        }
        payload["events"] = {"schedule_tension": 0.25}

        vector = extract_features(payload)

        self.assertAlmostEqual(vector[self._idx("macro_dollar_pressure")], 0.42, places=6)
        self.assertAlmostEqual(vector[self._idx("macro_volatility_pressure")], 0.33, places=6)
        self.assertAlmostEqual(vector[self._idx("macro_crypto_risk")], -0.15, places=6)
        self.assertAlmostEqual(vector[self._idx("event_schedule_tension")], 0.25, places=6)

    def test_non_finite_proxy_values_do_not_leak_into_vector(self):
        payload = self._base_payload()
        payload["macro"] = {
            "proxies": {
                "_status": "fresh",
                "data": {
                    "proxies": {
                        "vix": {"available": True, "latestClose": float("nan")},
                        "dxy": {"available": True, "latestClose": float("inf")},
                        "btc": {"available": True, "latestClose": -1.0},
                    }
                },
            },
            "calendar": {
                "_status": "fresh",
                "data": {"events": []},
            },
        }

        vector = extract_features(payload)

        self.assertEqual(vector[self._idx("macro_volatility_pressure")], 0.0)
        self.assertEqual(vector[self._idx("macro_dollar_pressure")], 0.0)
        self.assertEqual(vector[self._idx("macro_crypto_risk")], 0.0)
        self.assertEqual(vector[self._idx("event_schedule_tension")], 0.0)


if __name__ == "__main__":
    unittest.main(verbosity=2)
