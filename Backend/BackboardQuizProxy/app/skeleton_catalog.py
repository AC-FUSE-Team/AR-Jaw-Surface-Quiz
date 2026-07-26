"""Verified skeletal selection catalogue for the synthetic semester demo.

The whole-body entries below are derived from enabled BoxCollider GameObjects in
Assets/Scenes/SkeletonHitboxCalibration_AR.unity.  RibCage is intentionally a
single educational answer ID backed by three overlapping calibration hitboxes.
The jaw pair is a focused module backed by the active jaw question bank and
painted surface-region map; it is not merged with skeleton-hitbox identifiers.
"""
from __future__ import annotations

from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[3]
SKELETON_SCENE = PROJECT_ROOT / "Assets/Scenes/SkeletonHitboxCalibration_AR.unity"
JAW_BANK = PROJECT_ROOT / "Assets/JawAR/Quiz/Data/JawQuizStarterBank.asset"
JAW_MAP = PROJECT_ROOT / "Assets/JawAR/SurfaceRegions/Data/JawSurfaceRegionMap_CodexDraft.asset"

SKELETON_REGIONS = {
    "Skull": {"body_area": "axial", "side": "midline", "module": "whole_skeleton",
              "selection": ["SkullHitbox"]},
    "RibCage": {"body_area": "axial", "side": "midline", "module": "whole_skeleton",
                "selection": ["RibCageUpperFrontHitbox", "RibCageLowerFrontLeftHitbox",
                              "RibCageLowerFrontRightHitbox"]},
    "Pelvis": {"body_area": "axial", "side": "midline", "module": "whole_skeleton",
               "selection": ["PelvisHitbox"]},
    "LeftHumerus": {"body_area": "upper_limbs", "side": "left", "module": "whole_skeleton",
                    "selection": ["LeftHumerusHitbox"]},
    "RightHumerus": {"body_area": "upper_limbs", "side": "right", "module": "whole_skeleton",
                     "selection": ["RightHumerusHitbox"]},
    "LeftRadius": {"body_area": "upper_limbs", "side": "left", "module": "whole_skeleton",
                   "selection": ["LeftRadiusHitbox"]},
    "RightRadius": {"body_area": "upper_limbs", "side": "right", "module": "whole_skeleton",
                    "selection": ["RightRadiusHitbox"]},
    "LeftUlna": {"body_area": "upper_limbs", "side": "left", "module": "whole_skeleton",
                 "selection": ["LeftUlnaHitbox"]},
    "RightUlna": {"body_area": "upper_limbs", "side": "right", "module": "whole_skeleton",
                  "selection": ["RightUlnaHitbox"]},
    "LeftFemur": {"body_area": "lower_limbs", "side": "left", "module": "whole_skeleton",
                  "selection": ["LeftFemurHitbox"]},
    "RightFemur": {"body_area": "lower_limbs", "side": "right", "module": "whole_skeleton",
                   "selection": ["RightFemurHitbox"]},
    "LeftFoot": {"body_area": "lower_limbs", "side": "left", "module": "whole_skeleton",
                 "selection": ["LeftFootHitbox"]},
    "RightFoot": {"body_area": "lower_limbs", "side": "right", "module": "whole_skeleton",
                  "selection": ["RightFootHitbox"]},
}

JAW_MODULE_REGIONS = {
    "LeftRamus": {"body_area": "jaw", "side": "left", "module": "jaw",
                  "selection": ["painted-surface:LeftRamus"]},
    "RightRamus": {"body_area": "jaw", "side": "right", "module": "jaw",
                   "selection": ["painted-surface:RightRamus"]},
}

REGIONS = SKELETON_REGIONS | JAW_MODULE_REGIONS
ALLOWLIST = frozenset(REGIONS)
NON_LATERALIZED = frozenset({"Skull", "RibCage", "Pelvis"})
UNSUPPORTED_NAMES = frozenset({
    "Tibia", "Fibula", "Patella", "Clavicle", "Scapula", "Vertebra",
    "VertebralColumn", "LeftHand", "RightHand", "IndividualRib",
})

PRESERVED_PDF = (PROJECT_ROOT / "Artifacts/SyntheticTeacherDemo_5Rounds_2026-07-24/pdf/"
                 "Synthetic_Student_1_GPT55_Backboard_Tailored_Jaw_Test_"
                 "20260724T065229_833255Z.pdf")
PRESERVED_PDF_SHA256 = "cf5275261effe514f541c29a874c2de646c669feda31c479189c0c0b4260405b"
PRESERVED_PDF_TIMESTAMP = "2026-07-24T06:52:29.833255Z"
PRESERVED_PDF_TITLE = "Synthetic Student 1 GPT-5.5/Backboard Tailored Jaw Test"


def validate_project_sources() -> None:
    scene = SKELETON_SCENE.read_text(encoding="utf-8")
    for region, metadata in SKELETON_REGIONS.items():
        for hitbox in metadata["selection"]:
            marker = f"m_Name: {hitbox}"
            if marker not in scene:
                raise RuntimeError(f"Verified region source disappeared: {region}/{hitbox}")
    bank = JAW_BANK.read_text(encoding="utf-8")
    mapped = JAW_MAP.read_text(encoding="utf-8")
    for region in JAW_MODULE_REGIONS:
        if f"expectedRegionId: {region}" not in bank or f"stableId: {region}" not in mapped:
            raise RuntimeError(f"Verified jaw module source disappeared: {region}")

