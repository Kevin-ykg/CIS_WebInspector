"""
图像对齐与差分 v7 (Alpha掩膜 + 分级检测)

核心策略：
  1. Alpha通道：直接提取TIFF设计图的Alpha通道作为完美二值掩膜
  2. 标志点检测：上下端各3个高圆度单圆 + Y轴聚类过滤
  3. 变换矩阵：6点RANSAC Homography，带先验sy回退
  4. 差分：SSIM结构对比 + 分块归一化 + 融合策略
  5. 分级检测：利用Alpha掩膜分区(边界屏蔽 + 内外分级容差)
"""

import cv2
import numpy as np
from pathlib import Path

# ============================================================
# 配置
# ============================================================
TIFF_PATH = "8.tiff"
JPG_PATH  = "8.jpg"
OUTPUT_DIR = Path("output")
OUTPUT_DIR.mkdir(exist_ok=True)

STRIP_RATIO_TIFF = 0.08      # TIFF 设计图的检索区域比例
STRIP_RATIO_JPG_TOP = 0.2   # JPG 实拍图上端检索区域比例 (如果 mark 点比较靠下，请调大此值)
STRIP_RATIO_JPG_BOT = 0.08   # JPG 实拍图下端检索区域比例
MIN_CIRC_TIFF = 0.85  # TIFF: 只保留高圆度单圆(排除pairs=0.52, QR码)
MIN_CIRC_JPG  = 0.75  # JPG:  保留清晰单圆(排除edge artifacts)


# ============================================================
# Step 0: 加载
# ============================================================
print("[Step 0] 加载图像...")
img_tiff = cv2.imread(TIFF_PATH, cv2.IMREAD_UNCHANGED)
img_jpg  = cv2.imread(JPG_PATH, cv2.IMREAD_UNCHANGED)

if img_tiff is None or img_jpg is None:
    raise FileNotFoundError("图像加载失败")

# 统一通道数，避免后续彩色转换及颜色距离计算报错
if len(img_jpg.shape) == 2:
    img_jpg = cv2.cvtColor(img_jpg, cv2.COLOR_GRAY2BGR)
elif len(img_jpg.shape) == 4:
    img_jpg = cv2.cvtColor(img_jpg, cv2.COLOR_BGRA2BGR)

if len(img_tiff.shape) == 2:
    img_tiff = cv2.cvtColor(img_tiff, cv2.COLOR_GRAY2BGR)

if len(img_tiff.shape) > 2 and img_tiff.shape[2] == 4:
    # 借鉴同事思路：分离Alpha通道作为原始设计二值掩膜
    channels = cv2.split(img_tiff)
    alpha_mask = channels[3]  # Alpha通道 = 原图设计二值图像（完美无损）
    bgr = cv2.merge(channels[:3])  
    
    # 终极修复：正确处理 Alpha 混合 (消除半透明边缘导致的黑边残留)
    # 你看到的微小黑色残留，是因为设计图在图案边缘做了“抗锯齿(Anti-aliasing)”平滑。
    # 这些边缘像素的 Alpha 值并不是绝对的 0，而是例如 5、20、100 这样的“半透明”灰阶值（肉眼看也是黑的）。
    # 如果用 == 0 强切，半透明部分就不会被涂白，从而直接暴露出底层的黑色。
    # 解决办法：按照物理学标准，将 BGR 图像根据 Alpha 通道与纯白背景(255)进行数学混合(Alpha Blending)。
    alpha_f = alpha_mask.astype(np.float32) / 255.0
    alpha_f = alpha_f[:, :, np.newaxis]  # 扩展为3通道用于广播运算
    img_tiff = (bgr.astype(np.float32) * alpha_f + 255.0 * (1.0 - alpha_f)).astype(np.uint8)
    
    print(f"  提取Alpha通道: 非零像素={np.count_nonzero(alpha_mask)}, "
          f"覆盖率={np.count_nonzero(alpha_mask) / alpha_mask.size * 100:.1f}%")
else:
    alpha_mask = None
    print("  [WARN] TIFF无Alpha通道，将使用统一阈值检测")

h_tiff, w_tiff = img_tiff.shape[:2]
h_jpg, w_jpg = img_jpg.shape[:2]
print(f"  TIFF: {w_tiff}x{h_tiff},  JPG: {w_jpg}x{h_jpg}")


# ============================================================
# Step 1: 标志点检测
# ============================================================
def detect_tiff(strip_bgr, y_offset, label=""):
    """
    TIFF模板图提取标志点:
    采用“白色距离法”。由于设计图(TIFF)背景是纯白，标志圆可能是黑色也可能是彩色(蓝/红等)，
    不能单纯用黑白灰度阈值。计算每个像素距离纯白[255, 255, 255]的欧氏距离，
    距离大(非白色)即认为是有效点，以此完美提取带颜色的设计标志圆。
    """
    sh, sw = strip_bgr.shape[:2]
    strip_area = sh * sw
    
    strip_f = strip_bgr.astype(np.float32)
    white = np.array([255, 255, 255], dtype=np.float32)
    # 计算与纯白色的距离
    dist = np.sqrt(np.sum((strip_f - white) ** 2, axis=2))
    dist_u8 = np.clip(dist / 441.7 * 255, 0, 255).astype(np.uint8)
    # 距离大于25的(即偏离白色的图案)设为白色(255)
    _, binary = cv2.threshold(dist_u8, 25, 255, cv2.THRESH_BINARY)
    # kernel = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (5, 5))
    # binary = cv2.morphologyEx(binary, cv2.MORPH_OPEN, kernel, iterations=2)
    # binary = cv2.morphologyEx(binary, cv2.MORPH_CLOSE, kernel, iterations=3)
    contours, _ = cv2.findContours(binary, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    
    circles = []
    for cnt in contours:
        area = cv2.contourArea(cnt)
        perimeter = cv2.arcLength(cnt, True)
        if perimeter == 0: continue
        ratio = area / strip_area
        if ratio < 0.001 or ratio > 0.05: continue
        circ = 4 * np.pi * area / (perimeter ** 2)
        if circ < MIN_CIRC_TIFF: continue
        
        M = cv2.moments(cnt)
        if M["m00"] == 0: continue
        cx = M["m10"] / M["m00"]
        cy = M["m01"] / M["m00"] + y_offset
        circles.append((cx, cy, area, circ))
        
    # Y聚类找同行
    if len(circles) > 3:
        ys = np.array([c[1] for c in circles])
        tol = sh * 0.12
        clusters = []
        used = set()
        for idx in np.argsort(ys):
            i = int(idx)
            if i in used: continue
            cl = [i]
            used.add(i)
            for j in range(len(ys)):
                if j in used: continue
                if abs(ys[j] - ys[i]) < tol:
                    cl.append(j)
                    used.add(j)
            clusters.append(cl)
            
        best = max(clusters, key=lambda cl: (
            min(len(cl), 5),  
            sum(circles[i][2] for i in cl)
        ))
        circles = [circles[i] for i in best]
        
    circles.sort(key=lambda c: c[0])
    print(f"  [{label}] {len(circles)}个")
    for c in circles:
        print(f"    x={c[0]:.0f}, y={c[1]:.0f}, area={c[2]:.0f}, circ={c[3]:.3f}")
    return circles


def detect_jpg(strip_gray, y_offset, restrict_lower_half=False, ref_area=None, label=""):
    """
    JPG实拍图提取标志点:
    核心技术：多阈值试探法。
    因为不同相机、不同环境打光会导致纸张亮度不均，单纯的Otsu阈值极易将阴影和纸张灰阶判定为背景或前景，
    导致标志点与背景阴影粘连。
    本函数直接遍历从极低(20)到高(180)的一系列阈值，对每次二值化的结果执行找圆逻辑，
    一旦在某个阈值下能清晰地提取出同行的3个高圆度圆点，即认定该阈值为最佳阈值并停止试探。
    """
    sh, sw = strip_gray.shape[:2]
    strip_area = sh * sw
    
    # CLAHE用于局部对比度增强，让暗部的圆更清晰
    clahe = cv2.createCLAHE(clipLimit=2.0, tileGridSize=(4, 4))
    enhanced = clahe.apply(strip_gray)
    blurred = cv2.GaussianBlur(enhanced, (3, 3), 0)
    
    best_circles = []
    best_binary = None
    best_thresh = 120
    # 试探一系列阈值，直到找到 >= 3 个合法圆的为止
    # 扩大阈值范围，包含极低的阈值(如20, 30)，这对消除深色阴影干扰极其有效
    for thresh in [20, 30, 40, 50, 60, 70, 80, 100, 120, 140, 160, 180]:
        _, binary = cv2.threshold(blurred, thresh, 255, cv2.THRESH_BINARY)
        
        # 极为关键：判断背景极性 (实拍图可能是黑底白点，也可能是白底黑点)
        # 通过统计全图白色像素比例，如果>50%是白底，就反转图像，让待检测的圆点变成白色(255)
        if np.count_nonzero(binary) / binary.size > 0.5:
            binary = cv2.bitwise_not(binary)
            
        
        # 清边
        border = 15
        binary_clean = binary.copy()
        binary_clean[:border, :] = 0
        binary_clean[-border:, :] = 0
        binary_clean[:, :border] = 0
        binary_clean[:, -border:] = 0
        
        # 如果限制下半部，清除上半部
        if restrict_lower_half:
            binary_clean[:sh//3, :] = 0  
            
        kernel = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (5, 5))
        binary_clean = cv2.morphologyEx(binary_clean, cv2.MORPH_OPEN, kernel, iterations=2)
        # binary_clean = cv2.morphologyEx(binary_clean, cv2.MORPH_CLOSE, kernel, iterations=3)
        contours, _ = cv2.findContours(binary_clean, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        circles = []
        for cnt in contours:
            area = cv2.contourArea(cnt)
            perimeter = cv2.arcLength(cnt, True)
            if perimeter == 0: continue
            ratio = area / strip_area
            if ratio < 0.001 or ratio > 0.05: continue
            circ = 4 * np.pi * area / (perimeter ** 2)
            if circ < MIN_CIRC_JPG: continue
            
            # 面积过滤：如果有参考面积，拒绝偏差超过100%的
            if ref_area is not None:
                if area < ref_area * 0.3 or area > ref_area * 3.0:
                    continue
            
            M = cv2.moments(cnt)
            if M["m00"] == 0: continue
            cx = M["m10"] / M["m00"]
            cy = M["m01"] / M["m00"] + y_offset
            circles.append((cx, cy, area, circ))
            
        # Y聚类
        if len(circles) > 3:
            ys = np.array([c[1] for c in circles])
            tol = sh * 0.12
            clusters = []
            used = set()
            for idx in np.argsort(ys):
                i = int(idx)
                if i in used: continue
                cl = [i]
                used.add(i)
                for j in range(len(ys)):
                    if j in used: continue
                    if abs(ys[j] - ys[i]) < tol:
                        cl.append(j)
                        used.add(j)
                clusters.append(cl)
                
            # 选最佳行：优先选circle数3-5且面积最大的
            if clusters:
                best = max(clusters, key=lambda cl: (
                    min(len(cl), 5),  # 优先3-5个的行
                    sum(circles[i][2] for i in cl)  # 总面积
                ))
                circles = [circles[i] for i in best]
                
        if len(circles) > len(best_circles):
            best_circles = circles
            best_binary = binary_clean
            best_thresh = thresh
            
        # 如果找到了至少3个，说明这个阈值完美匹配，直接退出试探
        if len(best_circles) >= 7:
            print(f"  [{label}] 成功匹配最佳二值化阈值: {thresh}")
            # cv2.imwrite("binary.jpg", binary)
            break
            
    # 保存最好情况的debug
    if best_binary is not None:
        cv2.imwrite(str(OUTPUT_DIR / f"v6_debug_{label}_bin.jpg"), best_binary)
        
    best_circles.sort(key=lambda c: c[0])
    print(f"  [{label}] 最终找到 {len(best_circles)}个")
    for c in best_circles:
        print(f"    x={c[0]:.0f}, y={c[1]:.0f}, area={c[2]:.0f}, circ={c[3]:.3f}")
    return best_circles, best_thresh

# 灰度
gray_tiff = cv2.cvtColor(img_tiff, cv2.COLOR_BGR2GRAY)
gray_jpg  = cv2.cvtColor(img_jpg, cv2.COLOR_BGR2GRAY)

strip_h_tiff = int(h_tiff * STRIP_RATIO_TIFF)
strip_h_jpg_top = int(h_jpg * STRIP_RATIO_JPG_TOP)
strip_h_jpg_bot = int(h_jpg * STRIP_RATIO_JPG_BOT)

print("\n--- TIFF 上端 ---")
tiff_top = detect_tiff(img_tiff[:strip_h_tiff, :], 0, "T_top")
print("\n--- TIFF 下端 ---")
tiff_bot = detect_tiff(img_tiff[h_tiff-strip_h_tiff:, :], h_tiff-strip_h_tiff, "T_bot")

print("\n--- JPG 上端 ---")
jpg_top_raw, top_thresh = detect_jpg(gray_jpg[:strip_h_jpg_top, :], 0, restrict_lower_half=False, label="J_top")

# 过滤异常面积(边缘伪影): 先计算面积中位数，然后剔除面积>3倍中位数的
if len(jpg_top_raw) >= 3:
    med_area = np.median([c[2] for c in jpg_top_raw])
    jpg_top = [c for c in jpg_top_raw if c[2] < med_area * 2.5]
    if len(jpg_top) < len(jpg_top_raw):
        print(f"  过滤掉 {len(jpg_top_raw)-len(jpg_top)} 个面积异常圆 (med={med_area:.0f})")
else:
    jpg_top = jpg_top_raw

top_areas = [c[2] for c in jpg_top]
ref_area = np.median(top_areas) if top_areas else None
print(f"  上端参考面积: {ref_area}")

print("\n--- JPG 下端 (限制下半部 + 面积过滤) ---")
jpg_bot, bot_thresh = detect_jpg(gray_jpg[h_jpg-strip_h_jpg_bot:, :], h_jpg-strip_h_jpg_bot,
                                 restrict_lower_half=False, ref_area=ref_area, label="J_bot")
optimal_thresh = int((top_thresh + bot_thresh) / 2)
print(f"  --> 全局二值化采用自适应平均阈值: {optimal_thresh} (上端:{top_thresh}, 下端:{bot_thresh})")

# 转为点列表
def to_pts(circles):
    return [(c[0], c[1]) for c in circles]

tiff_top_pts = to_pts(tiff_top)
tiff_bot_pts = to_pts(tiff_bot)
jpg_top_pts  = to_pts(jpg_top)
jpg_bot_pts  = to_pts(jpg_bot)

print(f"\n  TIFF: 上={len(tiff_top_pts)}, 下={len(tiff_bot_pts)}")
print(f"  JPG:  上={len(jpg_top_pts)}, 下={len(jpg_bot_pts)}")


# ============================================================
# Step 2: 匹配
# ============================================================
print("\n[Step 2] 匹配...")

def match_rows(pts_tiff, pts_jpg, label=""):
    """
    匹配同一水平行上的标志点对 (例如：将TIFF上端的3个点与JPG上端的3个点一一对应)
    
    核心逻辑：
    1. 如果两边的点数量完全一致 (例如都是3个)，直接按X坐标从左到右一一配对。
    2. 如果数量不一致 (例如TIFF有3个，JPG由于破损只找到2个，或者找到了4个噪点)：
       - 将两边的X坐标分别映射到 [0.0, 1.0] 的相对比例尺上（归一化）。
       - 利用相对位置的欧氏距离，为较少的一方在较多的一方中寻找最近的匹配点。
       - 这是一种非常轻量的 1D 相对位置拓扑匹配，完美兼容图像缩放和平移。
    """
    n_s, n_d = len(pts_tiff), len(pts_jpg)
    if not pts_tiff or not pts_jpg:
        return [], []
        
    # Sort points by x-coordinate to process them left-to-right
    s = sorted(pts_tiff, key=lambda p: p[0])
    d = sorted(pts_jpg, key=lambda p: p[0])
    
    # 情况1：数量完全一致，直接顺序硬匹配
    if n_s == n_d:
        for a, b in zip(s, d):
            print(f"    TIFF({a[0]:.0f},{a[1]:.0f}) → JPG({b[0]:.0f},{b[1]:.0f})")
        return list(s), list(d)
    
    # 情况2：数量不一致，启用一维归一化(0~1)拓扑匹配
    s_xs = np.array([p[0] for p in s])
    d_xs = np.array([p[0] for p in d])
    # 转换为 0.0 ~ 1.0 的相对位置
    s_norm = (s_xs - s_xs[0]) / max(s_xs[-1] - s_xs[0], 1)
    d_norm = (d_xs - d_xs[0]) / max(d_xs[-1] - d_xs[0], 1)
    
    ms, md = [], []
    # 如果源图点少于目标图点
    if n_s <= n_d:
        used = set()
        for i in range(n_s):
            dists = np.abs(d_norm - s_norm[i])
            for j in np.argsort(dists):
                if j not in used:
                    ms.append(s[i]); md.append(d[j])
                    used.add(j); break
    # 如果源图点多于目标图点
    else:
        used = set()
        for j in range(n_d):
            dists = np.abs(s_norm - d_norm[j])
            for i in np.argsort(dists):
                if i not in used:
                    ms.append(s[i]); md.append(d[j])
                    used.add(i); break
    
    print(f"  [{label}] {n_s}:{n_d} → {len(ms)} 对")
    for a, b in zip(ms, md):
        print(f"    TIFF({a[0]:.0f},{a[1]:.0f}) → JPG({b[0]:.0f},{b[1]:.0f})")
    return ms, md


matched_tiff_top, matched_jpg_top = match_rows(tiff_top_pts, jpg_top_pts, "上端")
matched_tiff_bot, matched_jpg_bot = match_rows(tiff_bot_pts, jpg_bot_pts, "下端")

# 验证下端匹配质量 (防止实拍图底部被裁切或畸变严重导致误匹配噪点)
bottom_ok = True
if len(matched_tiff_bot) >= 2 and len(matched_jpg_bot) >= 2:
    # 检查X间距缩放比(sx)，如果下端两点的间距比例与上端差别巨大(>15%)，
    # 说明下端匹配到了错误的噪点，必须丢弃下端匹配。
    if len(matched_tiff_top) >= 2 and len(matched_jpg_top) >= 2:
        top_sx = (matched_jpg_top[-1][0] - matched_jpg_top[0][0]) / max(matched_tiff_top[-1][0] - matched_tiff_top[0][0], 1)
        bot_sx = (matched_jpg_bot[-1][0] - matched_jpg_bot[0][0]) / max(matched_tiff_bot[-1][0] - matched_tiff_bot[0][0], 1)
        print(f"\n  上端sx={top_sx:.4f}, 下端sx={bot_sx:.4f}")
        if abs(top_sx - bot_sx) / max(top_sx, 0.01) > 0.15:
            print(f"  [WARN] 上下端X缩放极不一致(可能是错误噪点)，触发降级保护，完全丢弃下端匹配")
            bottom_ok = False
else:
    bottom_ok = False

if bottom_ok:
    matched_tiff_all = matched_tiff_top + matched_tiff_bot
    matched_jpg_all = matched_jpg_top + matched_jpg_bot
else:
    matched_tiff_all = matched_tiff_top
    matched_jpg_all = matched_jpg_top
    print(f"  仅使用上端 {len(matched_tiff_all)} 对匹配")

print(f"\n  总匹配: {len(matched_tiff_all)} 对")


# ============================================================
# Step 3: 计算变换矩阵 (将 JPG 变换到 TIFF 空间)
# ============================================================
print("\n[Step 3] 变换矩阵 (JPG -> TIFF)...")

# 原本 matched_tiff_all 是 TIFF 点，matched_jpg_all 是 JPG 点。
# 现在我们要把 JPG 变换到 TIFF 坐标系，所以源和目标互换：
src_np = np.array(matched_jpg_all, dtype=np.float32) # 源点: JPG
dst_np = np.array(matched_tiff_all, dtype=np.float32) # 目标点: TIFF

expected_sy = h_tiff / h_jpg  # 预期的 Y 缩放比：现在是 TIFF 高度 / JPG 高度

if len(matched_tiff_all) >= 6:
    # 足够多的点 → 用RANSAC Homography
    H, mask = cv2.findHomography(src_np, dst_np, cv2.RANSAC, 5.0)
    inliers = mask.ravel().sum() if mask is not None else 0
    sx = np.sqrt(H[0,0]**2 + H[1,0]**2) 
    sy = np.sqrt(H[0,1]**2 + H[1,1]**2)
    angle = np.arctan2(H[1,0], H[0,0]) * 180 / np.pi
    print(f"  Homography: {inliers}/{len(matched_tiff_all)} inliers")
    print(f"  sx={sx:.4f}, sy={sy:.4f}, angle={angle:.2f}°")
    
    # 检查Homography结果是否荒谬(例如严重的透视导致Y轴压扁，或者异常旋转)
    if abs(angle) > 3 or abs(sy - expected_sy) / expected_sy > 0.2:
        print(f"  [WARN] Homography矩阵畸变过大，降级为仿射变换(Affine2D)")
        A, inl = cv2.estimateAffine2D(src_np, dst_np, method=cv2.RANSAC,
                                       ransacReprojThreshold=5.0)
        H = np.vstack([A, [0, 0, 1]])
        sx = np.sqrt(H[0,0]**2 + H[1,0]**2)
        sy = np.sqrt(H[0,1]**2 + H[1,1]**2)
        angle = np.arctan2(H[1,0], H[0,0]) * 180 / np.pi
        inliers = inl.ravel().sum() if inl is not None else 0
        print(f"  仿射: {inliers}/{len(matched_tiff_all)} inliers, sx={sx:.4f}, sy={sy:.4f}")

elif len(matched_tiff_all) >= 4:
    # 用仿射
    A, inl = cv2.estimateAffine2D(src_np, dst_np, method=cv2.RANSAC,
                                   ransacReprojThreshold=5.0)
    if A is not None:
        H = np.vstack([A, [0, 0, 1]])
        sx = np.sqrt(H[0,0]**2 + H[1,0]**2)
        sy = np.sqrt(H[0,1]**2 + H[1,1]**2)
        angle = np.arctan2(H[1,0], H[0,0]) * 180 / np.pi
        inliers = inl.ravel().sum() if inl is not None else 0
        print(f"  仿射: {inliers}/{len(matched_tiff_all)} inliers, sx={sx:.4f}, sy={sy:.4f}")
    else:
        print("  [WARN] estimateAffine2D 返回 None，降级为约束仿射")
        src_np = np.array(matched_jpg_top, dtype=np.float32) # 源点: JPG 上端
        dst_np = np.array(matched_tiff_top, dtype=np.float32) # 目标点: TIFF 上端

else:
    # 仅3点或更少 → 手动构建约束仿射
    print("  约束仿射(先验sy)")
    # 计算sx, angle, tx从上端点(如有)
    if len(src_np) >= 2:
        sx_est = (dst_np[-1,0] - dst_np[0,0]) / max(src_np[-1,0] - src_np[0,0], 1)
    else:
        sx_est = 1.0
    
    # 估算角度
    if len(src_np) >= 2:
        dy = dst_np[-1,1] - dst_np[0,1]
        dx = dst_np[-1,0] - dst_np[0,0]
        angle_est = np.arctan2(dy, dx) - np.arctan2(
            src_np[-1,1] - src_np[0,1], src_np[-1,0] - src_np[0,0])
    else:
        angle_est = 0.0
    
    sy_est = expected_sy  # 先验Y缩放
    
    cos_a, sin_a = np.cos(angle_est), np.sin(angle_est)
    
    A_est = np.array([
        [sx_est * cos_a, -sy_est * sin_a, 0],
        [sx_est * sin_a,  sy_est * cos_a, 0]
    ], dtype=np.float64)
    
    src_mean = src_np.mean(axis=0)
    dst_mean = dst_np.mean(axis=0)
    tx = dst_mean[0] - (sx_est * cos_a * src_mean[0] - sy_est * sin_a * src_mean[1])
    ty = dst_mean[1] - (sx_est * sin_a * src_mean[0] + sy_est * cos_a * src_mean[1])
    A_est[0, 2] = tx
    A_est[1, 2] = ty
    
    H = np.vstack([A_est, [0, 0, 1]])
    sx = sx_est
    sy = sy_est
    angle = np.degrees(angle_est)
    inliers = len(src_np)
    
    print(f"  sx={sx:.4f}, sy={sy:.4f}, angle={angle:.2f}°")
    print(f"  tx={tx:.1f}, ty={ty:.1f}")

print(f"\n  H =\n{H}")

# 合理性最终检查
if abs(sy - expected_sy) / expected_sy > 0.15:
    print(f"\n  [CRITICAL] sy={sy:.4f} 严重偏离期望值 {expected_sy:.4f}")
    print(f"  强制使用先验sy重建变换矩阵...")
    
    s_cx = np.mean([p[0] for p in matched_jpg_top]) # 源点: JPG 上端
    s_cy = np.mean([p[1] for p in matched_jpg_top])
    d_cx = np.mean([p[0] for p in matched_tiff_top]) # 目标点: TIFF 上端
    d_cy = np.mean([p[1] for p in matched_tiff_top])
    
    if len(matched_tiff_top) >= 2:
        sx = (matched_tiff_top[-1][0] - matched_tiff_top[0][0]) / (matched_jpg_top[-1][0] - matched_jpg_top[0][0])
    else:
        sx = 1.0
        
    sy = expected_sy
    tx = d_cx - sx * s_cx
    ty = d_cy - sy * s_cy
    
    H = np.array([[sx, 0, tx], [0, sy, ty], [0, 0, 1]], dtype=np.float64)
    angle = 0.0
    inliers = len(matched_tiff_top)
    print(f"  修正: sx={sx:.4f}, sy={sy:.4f}, tx={tx:.1f}, ty={ty:.1f}")
    print(f"  H =\n{H}")


# ============================================================
# Step 4: 变换实拍图 (JPG -> TIFF)
# ============================================================
print("\n[Step 4] 变换 JPG 到 TIFF 空间...")

# 将实拍图变换到无损的TIFF空间
jpg_warped = cv2.warpPerspective(img_jpg, H, (w_tiff, h_tiff))

# 有效区域: 将实拍图的尺寸区域作为纯白蒙版变换过去，得到实际的覆盖范围
jpg_ones = np.ones((h_jpg, w_jpg), dtype=np.uint8) * 255
valid_warped = cv2.warpPerspective(jpg_ones, H, (w_tiff, h_tiff))
erode_k = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (3, 3))
mask_valid = cv2.erode(valid_warped, erode_k, iterations=1) > 127

gray_tiff = cv2.cvtColor(img_tiff, cv2.COLOR_BGR2GRAY)
gray_jpg_warped = cv2.cvtColor(jpg_warped, cv2.COLOR_BGR2GRAY)

# 二值图案提取
print("  二值图案提取...")

# 模板: Alpha通道直接作为设计图案二值掩膜 (原生无损)
if alpha_mask is not None:
    _, alpha_mask = cv2.threshold(alpha_mask, 60, 255, cv2.THRESH_BINARY)
    binary_tiff = alpha_mask.copy()
else:
    _, binary_tiff = cv2.threshold(gray_tiff, 127, 255, cv2.THRESH_BINARY)

# 实拍: 对变换后的JPG提取图案信息
_, binary_jpg = cv2.threshold(gray_jpg_warped, optimal_thresh, 255, cv2.THRESH_BINARY)

binary_jpg[~mask_valid] = 0
print(f"  模板图案像素: {np.count_nonzero(binary_tiff[mask_valid])}")
print(f"  实拍图案像素: {np.count_nonzero(binary_jpg[mask_valid])}")


# ============================================================
# Step 5: 双重差分
# ============================================================
print("\n[Step 5] 差分...")

# 像素差分 (形态学容差差分法)
TOLERANCE_inner = 5  # 允许的最大物理错位像素数，不宜太大，会把内部缺陷淹没掉
TOLERANCE_outer = 80  # 允许的最大物理错位像素数
kernel_tol_inner = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (TOLERANCE_inner, TOLERANCE_inner))
kernel_tol_outer = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (TOLERANCE_outer, TOLERANCE_outer))

# 1. 内部缺陷 (断墨/漏印): 模板有，实拍没有。
binary_jpg_dilated = cv2.dilate(binary_jpg, kernel_tol_inner)
diff_inner_pix = cv2.subtract(binary_tiff, binary_jpg_dilated)
# cv2.imwrite("diff_inner_pix.jpg", diff_inner_pix) 
# 2. 外部缺陷 (飞墨/脏污): 实拍有，模板没有。
binary_tiff_dilated = cv2.dilate(binary_tiff, kernel_tol_outer)
diff_outer_pix = cv2.subtract(binary_jpg, binary_tiff_dilated)
# cv2.imwrite("diff_outer_pix.jpg", diff_outer_pix) 
# 合并屏蔽掩膜
if alpha_mask is not None:
    # 1. 大包围圈屏蔽 (Fill up)
    contours_ext, _ = cv2.findContours(alpha_mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    alpha_filled = np.zeros_like(alpha_mask)
    cv2.drawContours(alpha_filled, contours_ext, -1, 255, thickness=cv2.FILLED)
    contours_filled, _ = cv2.findContours(alpha_filled, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    EDGE_EXCLUSION_THICKNESS = 150
    edge_exclusion_mask = np.zeros_like(alpha_mask)
    cv2.drawContours(edge_exclusion_mask, contours_filled, -1, 255, thickness=EDGE_EXCLUSION_THICKNESS)
    
    # 2. 内外全细节屏蔽
    contours_all, _ = cv2.findContours(alpha_mask, cv2.RETR_TREE, cv2.CHAIN_APPROX_SIMPLE)
    EDGE_EXCLUSION_THICKNESS_SMALL = 60
    edge_exclusion_mask_small = np.zeros_like(alpha_mask)
    cv2.drawContours(edge_exclusion_mask_small, contours_all, -1, 255, thickness=EDGE_EXCLUSION_THICKNESS_SMALL)
    
    # 3. 合并双重屏蔽区
    edge_mask = cv2.bitwise_or(edge_exclusion_mask, edge_exclusion_mask_small)
    
    # 4. 直接在已分离的内部和外部缺陷图上执行屏蔽
    diff_inner_pix[edge_mask > 0] = 0
    diff_outer_pix[edge_mask > 0] = 0
else:
    edge_mask = None

# 去除视野外区域
diff_inner_pix[~mask_valid] = 0
diff_outer_pix[~mask_valid] = 0
# cv2.imwrite("diff_inner_pix.jpg", diff_inner_pix)
# cv2.imwrite("diff_outer_pix.jpg", diff_outer_pix)
# cv2.imwrite("edge_mask.jpg", edge_mask)
# 融合两层差异作为总差异图
diff_pixels_raw = cv2.bitwise_or(diff_inner_pix, diff_outer_pix)
print(f"  像素差异: {np.count_nonzero(diff_pixels_raw)} 像素")

# ============================================================
# Step 6: 缺陷提取 (轮廓过滤)
# ============================================================
print("\n[Step 6] 缺陷提取...")
diff_fused = diff_pixels_raw.copy()
# cv2.imwrite("diff_fused.jpg", diff_fused)

AREA_THRESH_INNER = 2  # 墨区内部判定阈值: 比较严格，抓微小的断墨/漏印
AREA_THRESH_OUTER = 2  # 空白区域判定阈值: 比较宽容，只抓较大块的飞墨/脏污

# 因为在 Step 5 我们已经完美分离了 inner 和 outer，且各自容差不同，这里直接分别提取轮廓即可！
c_inner, _ = cv2.findContours(diff_inner_pix, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
c_outer, _ = cv2.findContours(diff_outer_pix, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)

defects_inner = sorted([c for c in c_inner if cv2.contourArea(c) > AREA_THRESH_INNER],
                 key=cv2.contourArea, reverse=True)
defects_outer = sorted([c for c in c_outer if cv2.contourArea(c) > AREA_THRESH_OUTER],
                 key=cv2.contourArea, reverse=True)

# 合并所有缺陷
defects_fused = sorted(defects_inner + defects_outer, key=cv2.contourArea, reverse=True)

if edge_mask is not None:
    print(f"  边界屏蔽: 去除边界区 {np.count_nonzero(edge_mask)} 像素")
print(f"  内部缺陷(>{AREA_THRESH_INNER}px): {len(defects_inner)}")
print(f"  外部缺陷(>{AREA_THRESH_OUTER}px): {len(defects_outer)}")
print(f"  总缺陷: {len(defects_fused)}")



# ============================================================
# Step 7: 保存 (基于完美TIFF坐标系)
# ============================================================
print("\n[Step 7] 保存...")

# sr = 3000 / max(h_tiff, w_tiff)
sr = 1.0

def draw_defects(img, defects, max_n=80):
    vis = img.copy()
    for i, cnt in enumerate(defects[:max_n]):
        x, y, w, h = cv2.boundingRect(cnt)
        a = cv2.contourArea(cnt)
        col = (0,0,255) if a > 5000 else (0,165,255) if a > 1500 else (0,255,255)
        th = 4 if a > 5000 else 3 if a > 1500 else 2
        cv2.rectangle(vis, (x,y), (x+w,y+h), col, th)
        cv2.putText(vis, f"{i+1}", (x,y-5), cv2.FONT_HERSHEY_SIMPLEX, 1.0, col, 2)
    return vis

def draw_defects_classified(img, defects_inner, defects_outer, max_n=80):
    """分类绘制: 内部缺陷(红/断墨漏印) vs 外部缺陷(黄/飞墨脏污)"""
    vis = img.copy()
    idx = 0
    for cnt in defects_inner[:max_n]:
        x, y, w, h = cv2.boundingRect(cnt)
        a = cv2.contourArea(cnt)
        col = (0, 0, 255) if a > 3000 else (0, 100, 255)
        th = 4 if a > 3000 else 2
        cv2.rectangle(vis, (x, y), (x+w, y+h), col, th)
        cv2.putText(vis, f"I{idx+1}", (x, y-5), cv2.FONT_HERSHEY_SIMPLEX, 0.8, col, 2)
        idx += 1
    for cnt in defects_outer[:max_n]:
        x, y, w, h = cv2.boundingRect(cnt)
        a = cv2.contourArea(cnt)
        col = (0, 255, 255) if a > 3000 else (0, 200, 200)
        th = 4 if a > 3000 else 2
        cv2.rectangle(vis, (x, y), (x+w, y+h), col, th)
        cv2.putText(vis, f"O{idx+1}", (x, y-5), cv2.FONT_HERSHEY_SIMPLEX, 0.8, col, 2)
        idx += 1
    return vis

overlay = cv2.addWeighted(
    cv2.cvtColor(binary_tiff, cv2.COLOR_GRAY2BGR), 0.5,
    cv2.cvtColor(binary_jpg, cv2.COLOR_GRAY2BGR), 0.5, 0)

def draw_pts(img, top, bot, s, color_top=(0,255,0), color_bot=(0,0,255)):
    vis = img.copy()
    for i, pt in enumerate(top):
        cv2.circle(vis, (int(pt[0]), int(pt[1])), 20, color_top, 4)
        cv2.putText(vis, f"T{i}", (int(pt[0])+25, int(pt[1])+10),
                    cv2.FONT_HERSHEY_SIMPLEX, 2.0, color_top, 3)
    for i, pt in enumerate(bot):
        cv2.circle(vis, (int(pt[0]), int(pt[1])), 20, color_bot, 4)
        cv2.putText(vis, f"B{i}", (int(pt[0])+25, int(pt[1])+10),
                    cv2.FONT_HERSHEY_SIMPLEX, 2.0, color_bot, 3)
    return cv2.resize(vis, None, fx=s, fy=s)

st = 2000 / max(h_tiff, w_tiff)
sj = 2000 / max(h_jpg, w_jpg)
cv2.imwrite(str(OUTPUT_DIR/"v8_tiff_fid.jpg"), draw_pts(img_tiff, tiff_top_pts, tiff_bot_pts, st, color_top=(255,255,255), color_bot=(255,255,255)))
cv2.imwrite(str(OUTPUT_DIR/"v8_jpg_fid.jpg"), draw_pts(img_jpg, jpg_top_pts, jpg_bot_pts, sj))

diff_hm = cv2.applyColorMap(diff_pixels_raw, cv2.COLORMAP_JET)
diff_hm[~mask_valid] = 0

saves = {
    "v8_jpg_warped.jpg":     jpg_warped,
    "v8_overlay.jpg":        overlay,
    "v8_pixel_heatmap.jpg":  diff_hm,
    "v8_fused_defects.jpg":  draw_defects_classified(jpg_warped, defects_inner, defects_outer),
    "v8_pixel_binary.jpg":   cv2.cvtColor(diff_pixels_raw, cv2.COLOR_GRAY2BGR),
    "v8_fused_binary.jpg":   cv2.cvtColor(diff_fused, cv2.COLOR_GRAY2BGR),
    "v8_tiff_binary.jpg":    cv2.cvtColor(binary_tiff, cv2.COLOR_GRAY2BGR),
    "v8_jpg_binary.jpg":     cv2.cvtColor(binary_jpg, cv2.COLOR_GRAY2BGR),
}

if alpha_mask is not None:
    saves["v8_alpha_mask.jpg"] = cv2.cvtColor(alpha_mask, cv2.COLOR_GRAY2BGR)
    saves["v8_edge_mask.jpg"] = cv2.cvtColor(edge_mask, cv2.COLOR_GRAY2BGR)

for name, img in saves.items():
    small = cv2.resize(img, None, fx=sr, fy=sr)
    cv2.imwrite(str(OUTPUT_DIR/name), small, [cv2.IMWRITE_JPEG_QUALITY, 95])

print(f"\n{'='*60}")
print(f"  变换(JPG->TIFF): sx={sx:.4f}, sy={sy:.4f}, angle={angle:.2f}°")
print(f"  分级: 内部={len(defects_inner)}, 外部={len(defects_outer)}, 总计={len(defects_fused)}")
if defects_fused:
    print(f"\n  前10大缺陷(分级):")
    for i, cnt in enumerate(defects_fused[:10]):
        x, y, w, h = cv2.boundingRect(cnt)
        print(f"    #{i+1}: ({x},{y}) {w}x{h}, area={cv2.contourArea(cnt):.0f}")


