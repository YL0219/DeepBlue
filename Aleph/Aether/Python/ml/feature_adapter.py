"""
feature_adapter.py - Maps nested metabolic payloads to a fixed-order feature vector.

Design goals for the incumbent adapter:
- Preserve the incumbent vector contract (shape and ordering).
- Keep all feature engineering in Python, with C# as transport only.
- Support both legacy macro paths and the newer raw perception payloads.
- Degrade safely to neutral defaults for missing/stale/unusable macro data.

The Phase 10.6 synapse layer wires the first live perception signals into
existing incumbent feature slots:
- VIX latest close  -> macro_volatility_pressure
- DXY latest close  -> macro_dollar_pressure
- BTC latest close  -> macro_crypto_risk
- Upcoming calendar -> event_schedule_tension

New signals are extracted from:
- macro.proxies.data.proxies
- macro.calendar.data

Future extensions can add new proxy bindings, calendar features, or new profile
extractors without changing C# payload plumbing.
"""

from __future__ import annotations

import math
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from typing import Any, Mapping, Sequence


# ============================================================================
# Canonical feature order (append-only for backward compatibility)
# v1 features (0-17): technical indicators
# v2 features (18+): macro/event scores
# ============================================================================

FEATURE_NAMES_V1 = [
    "rsi_14",
    "macd_line",
    "macd_signal",
    "macd_histogram",
    "dist_sma_20",
    "dist_sma_50",
    "dist_sma_200",
    "atr_pct",
    "volatility_20",
    "bb_bandwidth",
    "factor_trend",
    "factor_momentum",
    "factor_volatility",
    "factor_participation",
    "composite_bullish",
    "composite_bearish",
    "composite_neutral",
    "composite_confidence",
]

FEATURE_NAMES_V2_MACRO = [
    "macro_equities_risk",
    "macro_bonds_risk",
    "macro_gold_strength",
    "macro_dollar_pressure",
    "macro_volatility_pressure",
    "macro_crypto_risk",
    "macro_liquidity_stress",
    "macro_correlation_stress",
    "regime_risk_on",
    "regime_risk_off",
    "regime_inflation_pressure",
    "regime_growth_scare",
    "regime_policy_shock",
    "regime_flight_to_safety",
    "event_materiality",
    "event_shock",
    "event_schedule_tension",
    "crypto_risk",
    "crypto_volatility",
    "crypto_weekend_stress",
]

FEATURE_NAMES = FEATURE_NAMES_V1 + FEATURE_NAMES_V2_MACRO

FEATURE_VERSION = "v2.0.0"


# ============================================================================
# Synapse extraction config (incumbent profile)
# ============================================================================

CALENDAR_EVENT_LOOKAHEAD_HOURS = 72
USABLE_SECTION_STATUSES = {"fresh", "ok"}


@dataclass(frozen=True)
class AffineNormalizer:
    """Simple center/scale transform with symmetric clipping."""

    center: float
    scale: float
    clamp: float = 3.0


@dataclass(frozen=True)
class LogRatioNormalizer:
    """Log-ratio transform around a positive pivot, with clipping."""

    pivot: float
    denominator: float
    clamp: float = 3.0


@dataclass(frozen=True)
class ProxyFeatureBinding:
    """Mapping of a proxy key to an incumbent macro feature slot."""

    feature_name: str
    proxy_key: str
    normalizer: AffineNormalizer | LogRatioNormalizer


@dataclass(frozen=True)
class MacroSynapseContext:
    """Extracted macro sections and reference timestamp for feature wiring."""

    proxies_data: Mapping[str, Any] | None
    calendar_data: Mapping[str, Any] | None
    reference_utc: datetime | None


INCUMBENT_PROXY_BINDINGS: tuple[ProxyFeatureBinding, ...] = (
    ProxyFeatureBinding(
        feature_name="macro_volatility_pressure",
        proxy_key="vix",
        normalizer=AffineNormalizer(center=20.0, scale=10.0, clamp=3.0),
    ),
    ProxyFeatureBinding(
        feature_name="macro_dollar_pressure",
        proxy_key="dxy",
        normalizer=AffineNormalizer(center=100.0, scale=5.0, clamp=3.0),
    ),
    ProxyFeatureBinding(
        feature_name="macro_crypto_risk",
        proxy_key="btc",
        normalizer=LogRatioNormalizer(pivot=30000.0, denominator=math.log(2.0), clamp=3.0),
    ),
)

PROXY_KEY_ALIASES: dict[str, tuple[str, ...]] = {
    "vix": ("vix", "^vix"),
    "dxy": ("dxy", "dx-y.nyb"),
    "btc": ("btc", "btc-usd"),
}


def _as_mapping(value: Any) -> Mapping[str, Any] | None:
    return value if isinstance(value, Mapping) else None


def _safe_get(mapping: Mapping[str, Any] | None, *path: str) -> Any:
    current: Any = mapping
    for key in path:
        if not isinstance(current, Mapping):
            return None
        current = current.get(key)
    return current


def _coerce_float(value: Any) -> float | None:
    """Convert to float, returning None for None/non-finite/invalid values."""
    if value is None or isinstance(value, bool):
        return None

    if isinstance(value, (int, float)):
        f = float(value)
        return f if math.isfinite(f) else None

    try:
        f = float(value)
        return f if math.isfinite(f) else None
    except (TypeError, ValueError):
        return None


def _safe_float(value: Any) -> float:
    """Final conversion for the vector: invalid values map to 0.0."""
    f = _coerce_float(value)
    return 0.0 if f is None else f


def _clip(value: float, clamp: float) -> float:
    if clamp <= 0:
        return value
    return max(-clamp, min(clamp, value))


def _apply_normalizer(
    value: float | None,
    normalizer: AffineNormalizer | LogRatioNormalizer,
) -> float | None:
    if value is None:
        return None

    if isinstance(normalizer, AffineNormalizer):
        if normalizer.scale == 0:
            return None
        normalized = (value - normalizer.center) / normalizer.scale
        return _clip(normalized, normalizer.clamp)

    if normalizer.pivot <= 0 or normalizer.denominator == 0 or value <= 0:
        return None

    normalized = math.log(value / normalizer.pivot) / normalizer.denominator
    return _clip(normalized, normalizer.clamp)


def _parse_utc(timestamp: Any) -> datetime | None:
    if not isinstance(timestamp, str):
        return None

    text = timestamp.strip()
    if not text:
        return None

    if text.endswith("Z"):
        text = f"{text[:-1]}+00:00"

    try:
        dt = datetime.fromisoformat(text)
    except ValueError:
        return None

    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=timezone.utc)

    return dt.astimezone(timezone.utc)


def _section_is_usable(section: Mapping[str, Any] | None) -> bool:
    """
    Fresh sections are usable. Missing/error/stale sections degrade to defaults.

    If a section has no explicit _status (legacy payload shapes), treat it as usable.
    """
    if section is None:
        return False

    raw_status = section.get("_status")
    if not isinstance(raw_status, str) or not raw_status.strip():
        return True

    return raw_status.strip().lower() in USABLE_SECTION_STATUSES


def _section_data(section: Mapping[str, Any] | None) -> Mapping[str, Any] | None:
    """Return the section payload dictionary if usable, else None."""
    if not _section_is_usable(section):
        return None

    if section is None:
        return None

    data = section.get("data")
    if isinstance(data, Mapping):
        return data

    # Legacy shape fallback where payload may already be at this level.
    if "data" not in section:
        return section

    return None


def _build_macro_synapse_context(payload: Mapping[str, Any]) -> MacroSynapseContext:
    macro = _as_mapping(payload.get("macro"))

    proxy_section = _as_mapping(_safe_get(macro, "proxies"))
    calendar_section = _as_mapping(_safe_get(macro, "calendar"))

    proxies_data = _section_data(proxy_section)
    calendar_data = _section_data(calendar_section)

    meta = _as_mapping(payload.get("meta"))
    temporal = _as_mapping(payload.get("temporal"))

    reference_utc = (
        _parse_utc(_safe_get(meta, "asof_utc"))
        or _parse_utc(_safe_get(temporal, "observation_cutoff_utc"))
        or _parse_utc(_safe_get(calendar_data, "fetchedAtUtc"))
        or _parse_utc(_safe_get(proxies_data, "fetchedAtUtc"))
    )

    return MacroSynapseContext(
        proxies_data=proxies_data,
        calendar_data=calendar_data,
        reference_utc=reference_utc,
    )


def _find_proxy_record(proxies_data: Mapping[str, Any] | None, proxy_key: str) -> Mapping[str, Any] | None:
    proxies = _as_mapping(_safe_get(proxies_data, "proxies"))
    if proxies is None:
        return None

    lower_map = {
        str(k).strip().lower(): v
        for k, v in proxies.items()
        if isinstance(k, str)
    }

    aliases = PROXY_KEY_ALIASES.get(proxy_key, (proxy_key,))
    for alias in aliases:
        candidate = lower_map.get(alias.lower())
        if isinstance(candidate, Mapping):
            return candidate

    return None


def _extract_proxy_latest_close(context: MacroSynapseContext, proxy_key: str) -> float | None:
    record = _find_proxy_record(context.proxies_data, proxy_key)
    if record is None:
        return None

    available = record.get("available")
    if isinstance(available, bool) and not available:
        return None

    latest_close = record.get("latestClose")
    if latest_close is None:
        latest_close = record.get("latest_close")

    return _coerce_float(latest_close)


def _extract_event_near_signal(context: MacroSynapseContext) -> float | None:
    """
    Binary schedule pressure feature.

    Returns:
      - 1.0 if a calendar event is scheduled within the lookahead window
      - 0.0 if calendar section is usable and no event is near
      - None if calendar section is unavailable/unusable
    """
    if context.calendar_data is None:
        return None

    if context.reference_utc is None:
        return 0.0

    events = context.calendar_data.get("events")
    if not isinstance(events, Sequence) or isinstance(events, (str, bytes, bytearray)):
        return 0.0

    window_end = context.reference_utc + timedelta(hours=CALENDAR_EVENT_LOOKAHEAD_HOURS)

    for event in events:
        event_map = _as_mapping(event)
        if event_map is None:
            continue

        scheduled = (
            event_map.get("scheduledUtc")
            or event_map.get("scheduled_utc")
            or event_map.get("scheduledForUtc")
        )
        event_time = _parse_utc(scheduled)
        if event_time is None:
            continue

        if context.reference_utc <= event_time <= window_end:
            return 1.0

    return 0.0


def _extract_incumbent_macro_synapse_features(payload: Mapping[str, Any]) -> dict[str, float]:
    """Extract perception-driven macro features for the incumbent profile."""
    context = _build_macro_synapse_context(payload)
    features: dict[str, float] = {}

    for binding in INCUMBENT_PROXY_BINDINGS:
        raw_value = _extract_proxy_latest_close(context, binding.proxy_key)
        normalized = _apply_normalizer(raw_value, binding.normalizer)
        if normalized is not None:
            features[binding.feature_name] = normalized

    event_near = _extract_event_near_signal(context)
    if event_near is not None:
        features["event_schedule_tension"] = event_near

    return features


# ============================================================================
# Legacy flattening (backward compatibility)
# ============================================================================


def _flatten_legacy_payload(payload: Mapping[str, Any]) -> dict[str, Any]:
    """
    Flatten known nested sections used by the incumbent feature map.

    Supports both:
    - new nested format (meta/technical/macro/events)
    - legacy flat payloads
    """
    flat: dict[str, Any] = {}

    tech = _as_mapping(payload.get("technical"))
    if tech is not None:
        for key, value in tech.items():
            if key == "factors" and isinstance(value, Mapping):
                flat["factor_trend"] = value.get("trend")
                flat["factor_momentum"] = value.get("momentum")
                flat["factor_volatility"] = value.get("volatility")
                flat["factor_participation"] = value.get("participation")
            elif key == "composite" and isinstance(value, Mapping):
                flat["composite_bullish"] = value.get("bullish")
                flat["composite_bearish"] = value.get("bearish")
                flat["composite_neutral"] = value.get("neutral")
                flat["composite_confidence"] = value.get("confidence")
            else:
                flat[key] = value

        macro = _as_mapping(payload.get("macro"))
        if macro is not None:
            cross_asset = _as_mapping(macro.get("cross_asset"))
            if cross_asset is not None:
                flat["macro_equities_risk"] = cross_asset.get("equities_risk")
                flat["macro_bonds_risk"] = cross_asset.get("bonds_risk")
                flat["macro_gold_strength"] = cross_asset.get("gold_strength")
                flat["macro_dollar_pressure"] = cross_asset.get("dollar_pressure")
                flat["macro_volatility_pressure"] = cross_asset.get("volatility_pressure")
                flat["macro_crypto_risk"] = cross_asset.get("crypto_risk")
                flat["macro_liquidity_stress"] = cross_asset.get("liquidity_stress")
                flat["macro_correlation_stress"] = cross_asset.get("correlation_stress")

            regime_hints = _as_mapping(macro.get("regime_hints"))
            if regime_hints is not None:
                flat["regime_risk_on"] = regime_hints.get("risk_on")
                flat["regime_risk_off"] = regime_hints.get("risk_off")
                flat["regime_inflation_pressure"] = regime_hints.get("inflation_pressure")
                flat["regime_growth_scare"] = regime_hints.get("growth_scare")
                flat["regime_policy_shock"] = regime_hints.get("policy_shock")
                flat["regime_flight_to_safety"] = regime_hints.get("flight_to_safety")

        events = _as_mapping(payload.get("events"))
        if events is not None:
            flat["event_materiality"] = events.get("materiality")
            flat["event_shock"] = events.get("shock")
            flat["event_schedule_tension"] = events.get("schedule_tension")

            crypto_stress = _as_mapping(events.get("crypto_stress"))
            if crypto_stress is not None:
                flat["crypto_risk"] = crypto_stress.get("risk")
                flat["crypto_volatility"] = crypto_stress.get("volatility")
                flat["crypto_weekend_stress"] = crypto_stress.get("weekend_stress")

        return flat

    # Legacy flat format passthrough
    return dict(payload)


def _flatten_nested_payload(payload: Mapping[str, Any]) -> dict[str, Any]:
    """
    Build the flattened feature map used by the incumbent vector.

    Order of precedence:
      1) Legacy flattened values
      2) Perception synapse overlays for mapped incumbent slots

    The overlay is intentionally sparse, only overriding slots with extracted
    usable values.
    """
    flat = _flatten_legacy_payload(payload)
    flat.update(_extract_incumbent_macro_synapse_features(payload))
    return flat


def extract_features(payload: dict[str, Any]) -> list[float]:
    """
    Extract a fixed-order feature vector from a metabolic payload dict.

    Missing/invalid values are replaced with 0.0 (safe default for SGD flow).
    """
    if not isinstance(payload, Mapping):
        return [0.0 for _ in FEATURE_NAMES]

    flat = _flatten_nested_payload(payload)
    return [_safe_float(flat.get(name)) for name in FEATURE_NAMES]


def feature_count() -> int:
    return len(FEATURE_NAMES)


def has_meaningful_features(payload: dict[str, Any]) -> bool:
    """Check if the payload has at least some non-null technical features."""
    if not isinstance(payload, Mapping):
        return False

    flat = _flatten_nested_payload(payload)
    meaningful = 0
    for name in FEATURE_NAMES_V1:
        value = flat.get(name)
        if value is None:
            continue

        converted = _coerce_float(value)
        if converted is None:
            continue

        meaningful += 1

    return meaningful >= 3
