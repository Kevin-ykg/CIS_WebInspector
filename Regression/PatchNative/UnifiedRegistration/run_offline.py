"""独立局部配准实验：只读冻结 ROI，不加载或替换生产 DLL。

坐标约定：原图和工作图均为 x 向右、y 向下；矩阵把 CIS 映到 TIFF Alpha。
人工点只用于末尾评价，绝不参与特征匹配、矩阵估计或候选选择。
"""

from __future__ import annotations

import argparse
import json
import math
import time
from pathlib import Path

import cv2
import numpy as np


ROOT = Path(__file__).resolve().parent
DEFAULT_LABELS = ROOT / "annotations-confirmed-20261024.json"
WORK_PIXELS_PER_MM = 3.0
MAX_SIDE = 1200
MAX_PIXELS = 1_000_000
FEATURE_BUDGET = 240
FEATURE_BORDER = 32
MAX_DISPLACEMENT_ORIGINAL = 150.0
RANSAC_WORK_PIXELS = 2.0
PROVISIONAL_ALIGNMENT_TARGET_ORIGINAL_PX = 6.0  # 仅用于人工点的离线诊断，不放宽模型门控。
MAX_REGION_LEAVE_OUT_CORNER_CHANGE_ORIGINAL_PX = 6.0


def work_size(width: int, height: int, dpi: float) -> tuple[int, int]:
    """只依赖已知目标尺寸与物理像素密度；不能按匹配结果动态改采样。"""
    density = WORK_PIXELS_PER_MM / (dpi / 25.4)
    minimum_width = 500.0 / width
    scale = min(1.0, max(density, minimum_width), MAX_SIDE / max(width, height),
                math.sqrt(MAX_PIXELS / (width * height)))
    return max(1, int(round(width * scale))), max(1, int(round(height * scale)))


def preprocess(image: np.ndarray, threshold: int, size: tuple[int, int]) -> np.ndarray:
    """使用同一种二值结构图；滤波支持固定，不随零件外部留白改变。"""
    binary = cv2.threshold(image, threshold, 255, cv2.THRESH_BINARY)[1]
    resized = cv2.resize(binary, size, interpolation=cv2.INTER_AREA)
    return cv2.GaussianBlur(resized, (3, 3), 0.8)


def features(image: np.ndarray, sift: cv2.SIFT) -> tuple[list[cv2.KeyPoint], np.ndarray | None]:
    """先均衡 4×4 空间预算，再计算描述子，避免一处纹理占满名额。"""
    extended = cv2.copyMakeBorder(image, FEATURE_BORDER, FEATURE_BORDER, FEATURE_BORDER,
                                  FEATURE_BORDER, cv2.BORDER_CONSTANT, value=0)
    found = sift.detect(extended, None)
    cells: dict[tuple[int, int], list[cv2.KeyPoint]] = {}
    for keypoint in found:
        x = keypoint.pt[0] - FEATURE_BORDER
        y = keypoint.pt[1] - FEATURE_BORDER
        if not (8 <= x < image.shape[1] - 8 and 8 <= y < image.shape[0] - 8):
            continue
        cell = (min(3, int(4 * x / image.shape[1])), min(3, int(4 * y / image.shape[0])))
        cells.setdefault(cell, []).append(keypoint)
    selected = []
    quota = max(1, math.ceil(FEATURE_BUDGET / 16))
    for cell in sorted(cells):
        selected.extend(sorted(cells[cell], key=lambda k: (-k.response, k.pt[1], k.pt[0]))[:quota])
    selected = sorted(selected, key=lambda k: (-k.response, k.pt[1], k.pt[0]))[:FEATURE_BUDGET]
    if not selected:
        return [], None
    computed, descriptions = sift.compute(extended, selected)
    for keypoint in computed:
        keypoint.pt = (keypoint.pt[0] - FEATURE_BORDER, keypoint.pt[1] - FEATURE_BORDER)
    return computed, descriptions


def correspondences(reference: tuple, moving: tuple, sx: float, sy: float) -> tuple[np.ndarray, np.ndarray]:
    ref_keys, ref_desc = reference
    mov_keys, mov_desc = moving
    if ref_desc is None or mov_desc is None or len(ref_keys) < 2 or len(mov_keys) < 2:
        return np.empty((0, 2), np.float32), np.empty((0, 2), np.float32)
    matcher = cv2.BFMatcher(cv2.NORM_L2)

    def good_knn(a: np.ndarray, b: np.ndarray) -> dict[int, int]:
        good = {}
        for neighbors in matcher.knnMatch(a, b, k=2):
            if len(neighbors) < 2:
                continue
            best, second = neighbors
            if best.distance < .70 * second.distance and best.distance <= 200:
                good[best.queryIdx] = best.trainIdx
        return good

    forward = good_knn(mov_desc, ref_desc)
    backward = good_knn(ref_desc, mov_desc)
    pairs = []
    for mov_index, ref_index in forward.items():
        if backward.get(ref_index) != mov_index:
            continue
        src = np.array(mov_keys[mov_index].pt, np.float32)
        dst = np.array(ref_keys[ref_index].pt, np.float32)
        dx = (dst[0] - src[0]) / sx
        dy = (dst[1] - src[1]) / sy
        if math.hypot(dx, dy) <= MAX_DISPLACEMENT_ORIGINAL:
            pairs.append((src, dst))
    pairs.sort(key=lambda p: (float(p[1][1]), float(p[1][0]), float(p[0][1]), float(p[0][0])))
    if not pairs:
        return np.empty((0, 2), np.float32), np.empty((0, 2), np.float32)
    return np.stack([p[0] for p in pairs]), np.stack([p[1] for p in pairs])


def transform_points(matrix: np.ndarray, points: np.ndarray) -> np.ndarray:
    return np.c_[points, np.ones(len(points))] @ matrix.T


def original_matrix(work_matrix: np.ndarray, sx: float, sy: float) -> np.ndarray:
    """工作像素中心 u=s*x+(s-1)/2；T_original=W^-1*T_work*W。"""
    w = np.array([[sx, 0, (sx - 1) / 2], [0, sy, (sy - 1) / 2], [0, 0, 1]], np.float64)
    t = np.eye(3)
    t[:2, :] = work_matrix
    return (np.linalg.inv(w) @ t @ w)[:2, :]


def geometry_ok(matrix: np.ndarray, kind: str, sx: float, sy: float,
                width: int, height: int) -> bool:
    original = original_matrix(matrix, sx, sy)
    linear = original[:, :2]
    determinant = float(np.linalg.det(linear))
    if determinant <= 0:
        return False
    singular = np.linalg.svd(linear, compute_uv=False)
    if not np.all((.97 <= singular) & (singular <= 1.03)):
        return False
    if kind == "affine":
        if singular.max() / singular.min() > 1.03:
            return False
        first, second = linear[:, 0], linear[:, 1]
        shear = math.degrees(math.acos(np.clip(np.dot(first, second) / (np.linalg.norm(first) * np.linalg.norm(second)), -1, 1)))
        if abs(shear - 90) > 1.0:
            return False
    rotation = math.degrees(math.atan2(linear[1, 0], linear[0, 0]))
    if abs(rotation) > 3:
        return False
    corners = np.array([[0, 0], [width - 1, 0], [0, height - 1], [width - 1, height - 1]], np.float64)
    return bool(np.linalg.norm(transform_points(original, corners) - corners, axis=1).max() <= 150)


def refit_least_squares(kind: str, source: np.ndarray, destination: np.ndarray) -> np.ndarray | None:
    """固定内点的无 RANSAC 重拟合；仅用于检查某一区特征被移除后矩阵是否飘移。"""
    if kind == "affine":
        design = np.c_[source, np.ones(len(source))]
        if len(source) < 4 or np.linalg.matrix_rank(design) < 3:
            return None
        return np.linalg.lstsq(design, destination, rcond=None)[0].T
    if len(source) < 3:
        return None
    design = np.zeros((2 * len(source), 4), np.float64)
    design[0::2, 0], design[0::2, 1], design[0::2, 2] = source[:, 0], -source[:, 1], 1
    design[1::2, 0], design[1::2, 1], design[1::2, 3] = source[:, 1], source[:, 0], 1
    if np.linalg.matrix_rank(design) < 4:
        return None
    a, b, tx, ty = np.linalg.lstsq(design, destination.reshape(-1), rcond=None)[0]
    return np.array([[a, -b, tx], [b, a, ty]], np.float64)


def leave_out_stability(kind: str, matrix: np.ndarray, source: np.ndarray, destination: np.ndarray,
                        inliers: np.ndarray, sx: float, sy: float,
                        size: tuple[int, int]) -> float:
    """去掉一片空间证据后，原图四角/中心最多会漂移多少像素。

    这能识别“一个局部区域决定了整件矩阵”的不稳定性；不使用人工标注或轮廓
    生成新矩阵。稀疏样本每格点不足时退化为逐点留一，避免无从评价就默认可信。
    """
    matched_source, matched_destination = source[inliers], destination[inliers]
    grid = np.minimum(2, (3 * matched_destination / np.asarray(size)).astype(int))
    cell_id = grid[:, 1] * 3 + grid[:, 0]
    omitted = [cell_id == cell for cell in np.unique(cell_id) if np.sum(cell_id == cell) >= 3]
    if not omitted:
        omitted = [np.arange(len(matched_source)) == index for index in range(len(matched_source))]
    width, height = size[0] / sx, size[1] / sy
    probes = np.array([[0, 0], [width - 1, 0], [0, height - 1],
                       [width - 1, height - 1], [width / 2, height / 2]], np.float64)
    original = transform_points(original_matrix(matrix, sx, sy), probes)
    worst = 0.0
    for removed in omitted:
        refitted = refit_least_squares(kind, matched_source[~removed], matched_destination[~removed])
        if refitted is None:
            return math.inf
        changed = transform_points(original_matrix(refitted, sx, sy), probes)
        worst = max(worst, float(np.linalg.norm(changed - original, axis=1).max()))
    return worst


def score(reference: np.ndarray, moving: np.ndarray, matrix: np.ndarray) -> tuple[float, list[float]]:
    """双向分区轮廓距离仅作质量检查；不从轮廓产生/修正最终矩阵。"""
    binary_ref = cv2.threshold(reference, 127, 255, cv2.THRESH_BINARY)[1]
    binary_mov = cv2.threshold(moving, 127, 255, cv2.THRESH_BINARY)[1]
    warped = cv2.warpAffine(binary_mov, matrix, (reference.shape[1], reference.shape[0]),
                            flags=cv2.INTER_NEAREST, borderMode=cv2.BORDER_CONSTANT, borderValue=0)
    ref_edges = cv2.Canny(binary_ref, 80, 160) != 0
    mov_edges = cv2.Canny(warped, 80, 160) != 0
    if not np.any(ref_edges) or not np.any(mov_edges):
        return math.inf, [math.inf] * 9
    distance_to_moving = cv2.distanceTransform((~mov_edges).astype(np.uint8), cv2.DIST_L2, 3)
    distance_to_reference = cv2.distanceTransform((~ref_edges).astype(np.uint8), cv2.DIST_L2, 3)
    per_cell = []
    for row in range(3):
        for col in range(3):
            ys = slice(row * reference.shape[0] // 3, (row + 1) * reference.shape[0] // 3)
            xs = slice(col * reference.shape[1] // 3, (col + 1) * reference.shape[1] // 3)
            reference_values = distance_to_moving[ys, xs][ref_edges[ys, xs]]
            moving_values = distance_to_reference[ys, xs][mov_edges[ys, xs]]
            if len(reference_values) >= 25 and len(moving_values) >= 25:
                per_cell.append(.5 * float(np.minimum(reference_values, 8).mean() +
                                         np.minimum(moving_values, 8).mean()))
            elif len(reference_values) >= 25 or len(moving_values) >= 25:
                # 一侧结构大量消失/新增时不能用单侧低分伪装成质量改善。
                per_cell.append(8.0)
            else:
                per_cell.append(math.nan)
    finite = [value for value in per_cell if math.isfinite(value)]
    return (float(np.mean(finite)) if finite else math.inf), per_cell


def estimate(kind: str, source: np.ndarray, destination: np.ndarray, sx: float, sy: float,
             size: tuple[int, int], before: tuple[float, list[float]],
             reference: np.ndarray, moving: np.ndarray,
             rejections: dict[str, str]) -> dict | None:
    if len(source) < (8 if kind == "similarity" else 12):
        rejections[kind] = "InsufficientMatches"
        return None
    cv2.setRNGSeed(0x5EED2026 & 0x7FFFFFFF)
    estimator = cv2.estimateAffinePartial2D if kind == "similarity" else cv2.estimateAffine2D
    matrix, mask = estimator(source, destination, method=cv2.RANSAC,
                             ransacReprojThreshold=RANSAC_WORK_PIXELS,
                             maxIters=5000, confidence=.995, refineIters=10)
    if matrix is None or mask is None:
        rejections[kind] = "RansacFailed"
        return None
    inliers = mask.ravel().astype(bool)
    minimum = 6 if kind == "similarity" else 10
    if inliers.sum() < minimum or inliers.mean() < .25:
        rejections[kind] = "InsufficientInliers"
        return None
    if not geometry_ok(matrix, kind, sx, sy, round(size[0] / sx), round(size[1] / sy)):
        rejections[kind] = "GeometryOutOfBounds"
        return None
    points = destination[inliers]
    cells = {(min(2, int(3 * x / size[0])), min(2, int(3 * y / size[1]))) for x, y in points}
    if len(cells) < 3:
        rejections[kind] = "InsufficientSpatialCoverage"
        return None
    residual = np.linalg.norm(transform_points(matrix, source[inliers]) - destination[inliers], axis=1)
    if np.sqrt(np.mean(residual ** 2)) > 1.5:
        rejections[kind] = "HighInlierRms"
        return None
    stability = leave_out_stability(kind, matrix, source, destination, inliers, sx, sy, size)
    if stability > MAX_REGION_LEAVE_OUT_CORNER_CHANGE_ORIGINAL_PX:
        rejections[kind] = f"SpatialModelInstability:{stability:.1f}px"
        return None
    after = score(reference, moving, matrix)
    if not math.isfinite(after[0]) or after[0] > before[0] * .90:
        rejections[kind] = "InsufficientSymmetricEdgeImprovement"
        return None
    regressions = [a - b for a, b in zip(after[1], before[1]) if math.isfinite(a) and math.isfinite(b)]
    if not regressions or max(regressions) > .25:
        rejections[kind] = "LocalEdgeRegression"
        return None
    return {"kind": kind, "matrix": matrix, "inliers": int(inliers.sum()),
            "matches": len(source), "cells": len(cells), "rmsWorkPx": float(np.sqrt(np.mean(residual ** 2))),
            "edgeBeforeWorkPx": before[0], "edgeAfterWorkPx": after[0],
            "worstCellRegressionWorkPx": max(regressions),
            "leaveOutCornerChangeOriginalPx": stability, "cellAfter": after[1]}


def run_case(case: dict, manifest: dict, dpi: float, click_uncertainty: float) -> dict:
    started = time.perf_counter()
    folder = ROOT / "inputs" / case["set"]
    name = case["partId"]
    # 当前 Windows 路径含中文；imdecode + fromfile 避免 OpenCV imread 的窄字符路径失败。
    alpha = cv2.imdecode(np.fromfile(str(folder / f"{name}_alpha.png"), dtype=np.uint8),
                         cv2.IMREAD_GRAYSCALE)
    cis = cv2.imdecode(np.fromfile(str(folder / f"{name}_raw.png"), dtype=np.uint8),
                       cv2.IMREAD_GRAYSCALE)
    if alpha is None or cis is None or alpha.shape != cis.shape:
        raise RuntimeError(f"missing/mismatched ROI: {case['set']}/{name}")
    height, width = alpha.shape
    size = work_size(width, height, dpi)
    sx, sy = size[0] / width, size[1] / height
    reference = preprocess(alpha, manifest["alpha_threshold"], size)
    moving = preprocess(cis, manifest["cis_threshold"], size)
    sift = cv2.SIFT_create(nfeatures=0, contrastThreshold=.04, edgeThreshold=10, sigma=1.6)
    ref_features = features(reference, sift)
    mov_features = features(moving, sift)
    source, destination = correspondences(ref_features, mov_features, sx, sy)
    identity = np.array([[1, 0, 0], [0, 1, 0]], np.float64)
    before = score(reference, moving, identity)
    rejections: dict[str, str] = {}
    candidates = [candidate for kind in ("similarity", "affine")
                  if (candidate := estimate(kind, source, destination, sx, sy, size, before,
                                            reference, moving, rejections)) is not None]
    chosen = None
    similarity = next((c for c in candidates if c["kind"] == "similarity"), None)
    affine = next((c for c in candidates if c["kind"] == "affine"), None)
    if similarity and affine:
        # 仿射必须跨区域明显更好；评分接近时保留自由度更少的相似变换。
        valid = [(a, b) for a, b in zip(affine["cellAfter"], similarity["cellAfter"])
                 if math.isfinite(a) and math.isfinite(b)]
        improved = sum(a < b - .15 for a, b in valid)
        # 候选间允许 0.15 个工作像素的插值/边缘离散波动；比对 H0 时的
        # 0.25 px 严格门槛仍不变，不能允许某一区明显恶化换取平均分。
        chosen = affine if (affine["edgeAfterWorkPx"] < .90 * similarity["edgeAfterWorkPx"]
                            and improved >= 2 and all(a <= b + .15 for a, b in valid)) else similarity
    else:
        chosen = similarity or affine
    matrix = original_matrix(chosen["matrix"], sx, sy) if chosen else identity
    source_labels = np.array([[p["cisX"], p["cisY"]] for p in case["points"]], np.float64)
    dest_labels = np.array([[p["alphaX"], p["alphaY"]] for p in case["points"]], np.float64)
    before_error = np.linalg.norm(source_labels - dest_labels, axis=1)
    after_error = np.linalg.norm(transform_points(matrix, source_labels) - dest_labels, axis=1)
    # 点级诊断只写入结果，不参与本件模型的拟合、筛选或门控。保留位置可定位
    # 到底是全件偏移、某一区域形变，还是周期结构标注需要复核。
    manual_points = [{"alpha": [float(x), float(y)],
                      "cis": [float(cx), float(cy)],
                      "beforePx": float(prior), "afterPx": float(posterior),
                      "cell": [min(2, int(3 * x / width)), min(2, int(3 * y / height))]}
                     for (x, y), (cx, cy), prior, posterior in
                     zip(dest_labels, source_labels, before_error, after_error)]
    metric = lambda values: {"p50": float(np.median(values)), "p95": float(np.percentile(values, 95)),
                             "max": float(np.max(values))}
    # 人工点仅在此处评价。单侧约 ±3px 的标点误差，两个坐标相减时可能叠加到
    # 约 6px；这是保守的量级提示，不是已校准的概率分布或严格误差上界。
    pair_uncertainty = 2.0 * click_uncertainty
    observed = metric(after_error)
    # 目标已由 2px 修订为约 5～6px；人工误差同量级，不能输出“几何通过”。
    # 这里只标记远超两者之和的明显残差，仍须独立高精度真值/功能测试确认。
    verdict = ("NoCandidate" if chosen is None else
               "UnresolvedAtLabelPrecision" if observed["p95"] <=
               PROVISIONAL_ALIGNMENT_TARGET_ORIGINAL_PX + pair_uncertainty else
               "LargeResidualBeyondApproxNoise")
    result = {"set": case["set"], "partId": name, "status": "Candidate" if chosen else "Review",
              "candidateModel": chosen["kind"] if chosen else None,
              "externalVerdict": verdict,
              "approxPairAnnotationUncertaintyPx": pair_uncertainty,
              "optimisticExcessAfterPx": metric(np.maximum(0.0, after_error - pair_uncertainty)),
              "workSize": list(size), "templateFeatures": len(ref_features[0]),
              "movingFeatures": len(mov_features[0]), "matches": len(source),
              "rejectedModels": rejections,
              "manualBeforePx": metric(before_error), "manualAfterPx": metric(after_error),
              "manualPointErrors": manual_points,
              "candidateModels": [{k: v for k, v in c.items() if k not in ("matrix", "cellAfter")}
                                  for c in candidates], "matrixCisToAlpha": matrix.tolist(),
              "milliseconds": (time.perf_counter() - started) * 1000}
    return result


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--labels", type=Path, default=DEFAULT_LABELS)
    parser.add_argument("--dpi", type=float, default=300)
    parser.add_argument("--annotation-click-uncertainty-px", type=float, default=3.0)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    labels = json.loads(args.labels.read_text(encoding="utf-8"))
    manifest = json.loads((ROOT / "manifest.json").read_text(encoding="utf-8"))
    entries = {(m["set"], m["part_id"]): m for m in manifest["cases"]}
    if args.annotation_click_uncertainty_px < 0:
        parser.error("annotation-click-uncertainty-px must be non-negative")
    results = [run_case(c, entries[c["set"], c["partId"]], args.dpi,
                        args.annotation_click_uncertainty_px) for c in labels["cases"]]
    report = {"prototype": "standalone; no production DLL or defect decision changed",
              "dpiAssumption": args.dpi, "opencv": cv2.__version__,
              "manualClickUncertaintyApproxPxPerImage": args.annotation_click_uncertainty_px,
              "provisionalAlignmentTargetOriginalPx": PROVISIONAL_ALIGNMENT_TARGET_ORIGINAL_PX,
              "uncertaintyCaveat": "Approximate, not a hard error bound; no case can certify 5-6px true geometry from these labels alone.",
              "cases": results}
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    for item in results:
        a = item["manualAfterPx"]
        print(f"{item['set']:15} {item['partId'][:8]} {(item['candidateModel'] or 'Review'):10} "
              f"match={item['matches']:3} p50={a['p50']:5.1f} p95={a['p95']:5.1f} "
              f"max={a['max']:5.1f} t={item['milliseconds']:6.0f}ms")


if __name__ == "__main__":
    main()
