"""
label_resolver.py - Foundation for delayed label resolution.

In v1, this is a minimal scaffold. The full self-supervision pipeline
(where we check if the prediction came true N days later) will be built
in a future sprint.

Label assignment rules (v1):
  - bullish:  price went up > threshold (e.g., +1%)
  - bearish:  price went down > threshold (e.g., -1%)
  - neutral:  price stayed within threshold
"""


# Default thresholds for label assignment
DEFAULT_BULLISH_THRESHOLD = 0.01   # +1%
DEFAULT_BEARISH_THRESHOLD = -0.01  # -1%


def assign_label(
    price_at_prediction: float,
    price_at_resolution: float,
    bullish_threshold: float = DEFAULT_BULLISH_THRESHOLD,
    bearish_threshold: float = DEFAULT_BEARISH_THRESHOLD,
) -> str:
    """
    Assign a 3-class label based on price change.
    Returns: "bullish", "bearish", or "neutral".
    """
    if price_at_prediction <= 0:
        return "neutral"

    pct_change = (price_at_resolution - price_at_prediction) / price_at_prediction

    if pct_change > bullish_threshold:
        return "bullish"
    elif pct_change < bearish_threshold:
        return "bearish"
    else:
        return "neutral"
