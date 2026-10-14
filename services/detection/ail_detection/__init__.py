"""Detection engine: detectors, cohort intelligence and change correlation."""

from ail_detection.config import DetectionConfig
from ail_detection.detectors import (
    DEFAULT_DETECTORS,
    ChangePointDetector,
    DetectionContext,
    Detector,
    DistributionShiftDetector,
    EwmaCusumDetector,
    MLDetector,
    ZScoreDetector,
)
from ail_detection.pipeline import analyze
from ail_detection.workspace import Workspace

__all__ = [
    "DEFAULT_DETECTORS",
    "ChangePointDetector",
    "DetectionConfig",
    "DetectionContext",
    "Detector",
    "DistributionShiftDetector",
    "EwmaCusumDetector",
    "MLDetector",
    "Workspace",
    "ZScoreDetector",
    "analyze",
]
