"""
incremental_model.py - SGDClassifier wrapper with partial_fit for online learning.

v1 uses 3-class classification: bullish / neutral / bearish.
Designed for incremental learning — new samples are added via partial_fit,
never requiring a full retrain from scratch.
"""

import sys
import numpy as np
from sklearn.linear_model import SGDClassifier
from sklearn.preprocessing import StandardScaler

CLASSES = np.array(["bearish", "bullish", "neutral"])

# Thresholds for model state determination
COLD_START_THRESHOLD = 10
WARMING_THRESHOLD = 50


class IncrementalCortexModel:
    """
    Thin wrapper around SGDClassifier with StandardScaler for online learning.
    Supports cold-start predictions with uniform probabilities.
    """

    def __init__(self):
        self.model = SGDClassifier(
            loss="modified_huber",  # gives probability estimates
            penalty="l2",
            alpha=1e-4,
            max_iter=1,
            tol=None,
            warm_start=True,
            random_state=42,
        )
        self.scaler = StandardScaler()
        self.trained_samples = 0
        self.model_version = "v1.0.0"
        self._fitted = False
        self._scaler_fitted = False

    @property
    def model_state(self) -> str:
        if self.trained_samples < COLD_START_THRESHOLD:
            return "cold_start"
        elif self.trained_samples < WARMING_THRESHOLD:
            return "warming"
        return "active"

    def predict(self, features: list[float]) -> dict:
        """
        Predict class probabilities from a feature vector.
        Returns uniform probabilities during cold start.
        """
        X = np.array([features])

        if not self._fitted:
            # Cold start — return uniform prior
            return {
                "predicted_class": "neutral",
                "probabilities": {
                    "bullish": 0.333,
                    "neutral": 0.334,
                    "bearish": 0.333,
                },
                "confidence": 0.0,
                "action_tendency": 0.0,
            }

        # Scale features
        if self._scaler_fitted:
            X_scaled = self.scaler.transform(X)
        else:
            X_scaled = X

        # Predict probabilities
        try:
            proba = self.model.predict_proba(X_scaled)[0]
            # Map probabilities to class names (model.classes_ order)
            class_prob = {}
            for cls, p in zip(self.model.classes_, proba):
                class_prob[cls] = float(p)

            # Ensure all 3 classes are present
            for cls in ["bullish", "neutral", "bearish"]:
                if cls not in class_prob:
                    class_prob[cls] = 0.0

            predicted_class = max(class_prob, key=class_prob.get)
            confidence = class_prob[predicted_class]

            # Action tendency: bullish_prob - bearish_prob, clamped to [-1, 1]
            action_tendency = class_prob.get("bullish", 0.0) - class_prob.get("bearish", 0.0)
            action_tendency = max(-1.0, min(1.0, action_tendency))

        except Exception as ex:
            print(f"[MlCortex] Prediction error: {ex}", file=sys.stderr)
            return {
                "predicted_class": "neutral",
                "probabilities": {"bullish": 0.333, "neutral": 0.334, "bearish": 0.333},
                "confidence": 0.0,
                "action_tendency": 0.0,
            }

        return {
            "predicted_class": predicted_class,
            "probabilities": class_prob,
            "confidence": round(confidence, 4),
            "action_tendency": round(action_tendency, 4),
        }

    def partial_fit(self, features_batch: list[list[float]], labels: list[str]) -> int:
        """
        Incrementally train the model with a batch of labeled samples.
        Returns the number of samples successfully fitted.
        """
        if not features_batch or not labels:
            return 0

        X = np.array(features_batch)
        y = np.array(labels)

        # Fit or update the scaler
        if not self._scaler_fitted:
            self.scaler.fit(X)
            self._scaler_fitted = True
        else:
            self.scaler.partial_fit(X)

        X_scaled = self.scaler.transform(X)

        # partial_fit the classifier
        self.model.partial_fit(X_scaled, y, classes=CLASSES)
        self._fitted = True
        self.trained_samples += len(labels)

        return len(labels)

    def get_state_dict(self) -> dict:
        """Return serializable state for persistence."""
        return {
            "trained_samples": self.trained_samples,
            "model_version": self.model_version,
            "model_state": self.model_state,
            "fitted": self._fitted,
            "scaler_fitted": self._scaler_fitted,
        }
