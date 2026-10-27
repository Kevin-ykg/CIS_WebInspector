"""Freeze exact ROI pixels and draw human-review figures; never estimate a transform.

Run from the project root. The template is Alpha, the captured input is the
pre-local-alignment CIS crop. Coordinates on the 3x3 figures are original ROI
pixels so a reviewer can enter independent corresponding landmarks.
"""

from __future__ import annotations

import hashlib
import json
import shutil
import csv
from pathlib import Path

import cv2
import numpy as np


ROOT = Path(__file__).resolve().parents[3]
HERE = Path(__file__).resolve().parent
FIGURES = ROOT / "obj" / "PatchNativeValidation" / "UnifiedRegistrationReview"
SOURCES = (
    (
        "img2",
        ROOT / "obj" / "PatchNativeValidation" / "periodic-img2-before" / "inputs",
        (
            "04200c88c8634c2388bc931c65cdb1cb",
            "2b72867d30ea4244a700bdca93cd8522",
            "3b72867d30ea4244a700bdca93cd8522",
            "4b72867d30ea4244a700bdca93cd8522",
            "5b72867d30ea4244a700bdca93cd8522",
        ),
    ),
    (
        "img3_origin62",
        ROOT / "obj" / "PatchNativeValidation" / "origin-before-62-20261022" / "inputs",
        ("04200c88c8634c2388bc931c65cdb1cb",),
    ),
    (
        "batch171315",
        ROOT / "obj" / "PatchNativeValidation" / "batch171315-diagnosis-20261022" / "inputs",
        (
            "d73d865b7e2d47f5904c9e5a67ca84bf",
            "ed18db0a1fdb4102b95e9dddb742bc28",
        ),
    ),
)


def digest(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()


def read_gray(path: Path) -> np.ndarray:
    image = cv2.imdecode(np.fromfile(str(path), dtype=np.uint8), cv2.IMREAD_UNCHANGED)
    if image is None:
        raise ValueError(f"Cannot decode {path}")
    if image.ndim == 3:
        return cv2.cvtColor(image, cv2.COLOR_BGRA2GRAY if image.shape[2] == 4 else cv2.COLOR_BGR2GRAY)
    return image


def write_png(path: Path, image: np.ndarray) -> None:
    encoded, data = cv2.imencode(".png", image)
    if not encoded:
        raise ValueError(f"Cannot encode {path}")
    data.tofile(str(path))


def fit(image: np.ndarray, width: int, height: int) -> np.ndarray:
    color = cv2.cvtColor(image, cv2.COLOR_GRAY2BGR) if image.ndim == 2 else image
    ratio = min(width / color.shape[1], height / color.shape[0])
    scaled = cv2.resize(
        color,
        (max(1, round(color.shape[1] * ratio)), max(1, round(color.shape[0] * ratio))),
        interpolation=cv2.INTER_AREA if ratio < 1 else cv2.INTER_NEAREST,
    )
    panel = np.full((height, width, 3), 22, dtype=np.uint8)
    x = (width - scaled.shape[1]) // 2
    y = (height - scaled.shape[0]) // 2
    panel[y : y + scaled.shape[0], x : x + scaled.shape[1]] = scaled
    return panel


def overlay(alpha: np.ndarray, cis: np.ndarray, alpha_threshold: int, cis_threshold: int) -> np.ndarray:
    a = alpha > alpha_threshold
    c = cis > cis_threshold
    # Green: template only; magenta: CIS only; white: overlap.
    result = np.zeros((*a.shape, 3), dtype=np.uint8)
    result[a & ~c] = (0, 220, 0)
    result[c & ~a] = (200, 0, 200)
    result[a & c] = (220, 220, 220)
    return result


def caption(canvas: np.ndarray, title: str, x: int, y: int, size: float = 0.60) -> None:
    cv2.putText(canvas, title, (x + 8, y + 23), cv2.FONT_HERSHEY_SIMPLEX, size, (235, 235, 235), 1, cv2.LINE_AA)


def overview(alpha: np.ndarray, cis: np.ndarray, combined: np.ndarray, tag: str) -> np.ndarray:
    cell_width, cell_height = 630, 760
    out = np.full((cell_height + 52, cell_width * 3, 3), 20, dtype=np.uint8)
    for index, (title, picture) in enumerate(
        (("ALPHA TEMPLATE", alpha), ("CIS BEFORE LOCAL ALIGN", cis), ("H0 OVERLAY: GREEN=A MAGENTA=C", combined))
    ):
        out[52:, index * cell_width : (index + 1) * cell_width] = fit(picture, cell_width, cell_height)
        caption(out, title, index * cell_width, 25, .53)
    caption(out, f"{tag}  ROI {alpha.shape[1]}x{alpha.shape[0]} original pixels", 0, 0, .7)
    return out


def suggested_template_points(alpha: np.ndarray) -> list[tuple[int, int]]:
    """Suggest only template-side corners; never claim a CIS correspondence."""
    height, width = alpha.shape[:2]
    binary = np.uint8(alpha > 60) * 255
    corners = cv2.goodFeaturesToTrack(binary, 400, .02, max(20, min(width, height) // 35))
    if corners is None:
        return []
    selected: dict[tuple[int, int], tuple[int, int]] = {}
    for point in corners[:, 0]:
        x, y = map(lambda value: int(round(float(value))), point)
        if min(x, y, width - 1 - x, height - 1 - y) < 20:
            continue
        cell = (min(2, 3 * y // height), min(2, 3 * x // width))
        selected.setdefault(cell, (x, y))
    return [selected[cell] for cell in sorted(selected)]


def grid(
    alpha: np.ndarray, cis: np.ndarray, combined: np.ndarray, tag: str, suggestions: list[tuple[int, int]]
) -> np.ndarray:
    tile_width, tile_height = 220, 205
    label_height = 30
    out = np.full((3 * (tile_height + label_height) + 45, 9 * tile_width, 3), 20, dtype=np.uint8)
    caption(out, f"{tag}: nine ROI zones; each group = ALPHA / CIS / H0 OVERLAY", 0, 0, .7)
    height, width = alpha.shape[:2]
    for row in range(3):
        y0, y1 = row * height // 3, (row + 1) * height // 3
        for col in range(3):
            x0, x1 = col * width // 3, (col + 1) * width // 3
            for variant, picture in enumerate((alpha, cis, combined)):
                cell = fit(picture[y0:y1, x0:x1], tile_width, tile_height)
                if variant == 0:
                    ratio = min(tile_width / (x1 - x0), tile_height / (y1 - y0))
                    offset_x = (tile_width - round((x1 - x0) * ratio)) // 2
                    offset_y = (tile_height - round((y1 - y0) * ratio)) // 2
                    for label, (point_x, point_y) in enumerate(suggestions, start=1):
                        if x0 <= point_x < x1 and y0 <= point_y < y1:
                            px_local = offset_x + round((point_x - x0) * ratio)
                            py_local = offset_y + round((point_y - y0) * ratio)
                            cv2.drawMarker(cell, (px_local, py_local), (0, 170, 255), cv2.MARKER_CROSS, 18, 2)
                            cv2.putText(cell, str(label), (px_local + 5, py_local - 7),
                                        cv2.FONT_HERSHEY_SIMPLEX, .55, (0, 170, 255), 2, cv2.LINE_AA)
                px = (col * 3 + variant) * tile_width
                py = 45 + row * (tile_height + label_height)
                out[py + label_height : py + label_height + tile_height, px : px + tile_width] = cell
                caption(out, f"{x0}:{x1} {y0}:{y1} {'A' if variant == 0 else 'C' if variant == 1 else 'O'}", px, py, .46)
                cv2.rectangle(out, (px, py), (px + tile_width - 1, py + tile_height + label_height - 1), (70, 70, 70), 1)
    return out


def main() -> None:
    FIGURES.mkdir(parents=True, exist_ok=True)
    manifest = {"purpose": "human review; no alignment transform estimated", "cases": []}
    proposed = []
    for set_name, source_dir, names in SOURCES:
        thresholds = json.loads((source_dir / "thresholds.json").read_text(encoding="utf-8"))
        destination = HERE / "inputs" / set_name
        destination.mkdir(parents=True, exist_ok=True)
        for name in names:
            files = {}
            for suffix in ("alpha", "raw"):
                source = source_dir / f"{name}_{suffix}.png"
                target = destination / source.name
                if not source.is_file():
                    raise FileNotFoundError(source)
                if not target.exists():
                    shutil.copy2(source, target)
                if digest(source) != digest(target):
                    raise ValueError(f"Frozen copy differs: {target}")
                files[suffix] = {"path": str(target.relative_to(ROOT)).replace("\\", "/"), "sha256": digest(target)}
            alpha = read_gray(destination / f"{name}_alpha.png")
            cis = read_gray(destination / f"{name}_raw.png")
            if alpha.shape != cis.shape:
                raise ValueError(f"ROI shapes differ: {set_name}/{name}: {alpha.shape} {cis.shape}")
            combined = overlay(alpha, cis, 60, int(thresholds[name]))
            suggestions = suggested_template_points(alpha)
            proposed.extend((set_name, name, index, x, y, "", "", "unconfirmed template-only suggestion")
                            for index, (x, y) in enumerate(suggestions, start=1))
            tag = f"{set_name}/{name}"
            write_png(FIGURES / f"{set_name}_{name}_overview.png", overview(alpha, cis, combined, tag))
            write_png(FIGURES / f"{set_name}_{name}_grid.png", grid(alpha, cis, combined, tag, suggestions))
            manifest["cases"].append(
                {
                    "set": set_name,
                    "part_id": name,
                    "width": int(alpha.shape[1]),
                    "height": int(alpha.shape[0]),
                    "alpha_threshold": 60,
                    "cis_threshold": int(thresholds[name]),
                    "source": str(source_dir.relative_to(ROOT)).replace("\\", "/"),
                    "files": files,
                }
            )
    (HERE / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    with (FIGURES / "candidate-landmarks.csv").open("w", newline="", encoding="utf-8-sig") as stream:
        writer = csv.writer(stream)
        writer.writerow(("set", "part_id", "candidate", "alpha_x", "alpha_y", "cis_x", "cis_y", "status"))
        writer.writerows(proposed)
    print(f"Frozen {len(manifest['cases'])} cases; review figures: {FIGURES}")


if __name__ == "__main__":
    main()
