using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using OpenCvSharp;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 零件级局部配准。包含轮廓平移、SIFT 匹配、RANSAC、距离场评分与最终单次 Warp。
    /// </summary>
    public static partial class PatchDefectDetector
    {
        private static bool TryLocalAlign(
            Mat alphaFeature,
            Mat cisFeature,
            Mat alphaScaled,
            Mat cisScaled,
            Mat cisImgOrig,
            double defectScale,
            double alignmentScale,
            bool needOriginalWarp,
            int alphaBinaryThreshold,
            int cisBinaryThreshold,
            PatchSiftWorker worker,
            string partId,
            IAppLogger logger,
            out Mat cisAligned,
            out Mat cisAlignedOrig)
        {
            cisAligned = null;
            cisAlignedOrig = null;
            var stopwatch = Stopwatch.StartNew();

            try
            {
                // 第一级：全局对齐后的零件通常只剩少量整体平移。先使用设计/实拍轮廓的
                // 双向距离场直接求解，不创建 SIFT 描述子；只有该低自由度模型不能可靠改善时，
                // 才进入后面的 SIFT 相似变换。这样简单零件更快，周期纹理也不必先承担特征误配风险。
                using (Mat fastTranslation = Mat.Eye(2, 3, MatType.CV_64FC1))
                {
                    bool fastTranslationAccepted = TryRefineAffineTranslationByEdges(
                        alphaFeature,
                        cisFeature,
                        alphaBinaryThreshold,
                        cisBinaryThreshold,
                        alignmentScale,
                        fastTranslation,
                        true,
                        LocalAlignmentFastTranslationMinImprovementRatio,
                        LocalAlignmentTranslationRefineRadiusOriginalPx,
                        out double initialEdgeScore,
                        out double translatedEdgeScore,
                        out double fastRefineX,
                        out double fastRefineY,
                        out double fastWorstLocalRegression,
                        out int fastCheckedLocalCells);

                    double initialEdgeScoreOriginal = initialEdgeScore / Math.Max(alignmentScale, 1e-6);
                    double fastShiftOriginal = Math.Sqrt(
                        fastRefineX * fastRefineX + fastRefineY * fastRefineY) /
                        Math.Max(alignmentScale, 1e-6);
                    double fastImprovementRatio =
                        (initialEdgeScore - translatedEdgeScore) /
                        Math.Max(initialEdgeScore, 1e-6);
                    bool finiteInitialScore = !double.IsInfinity(initialEdgeScoreOriginal) &&
                                              !double.IsNaN(initialEdgeScoreOriginal);
                    bool alreadyAlignedByAbsoluteScore = finiteInitialScore &&
                        initialEdgeScoreOriginal <= LocalAlignmentNotNeededScoreOriginalPx;
                    bool noUsefulTranslationRemaining = finiteInitialScore &&
                        !double.IsInfinity(translatedEdgeScore) &&
                        !double.IsNaN(translatedEdgeScore) &&
                        fastShiftOriginal <= LocalAlignmentNotNeededShiftOriginalPx &&
                        fastImprovementRatio < LocalAlignmentFastTranslationMinImprovementRatio;
                    if (alreadyAlignedByAbsoluteScore || noUsefulTranslationRemaining)
                    {
                        // 已经足够对齐时不再重采样；直接使用 H0 裁图能最大限度保留原始缺陷边缘。
                        stopwatch.Stop();
                        AppLog.Diagnostic(logger,
                            $"[LocalAlign] {FormatPartId(partId)} NotNeeded: " +
                            $"edge={initialEdgeScore:F3}workPx/{initialEdgeScoreOriginal:F2}origPx, " +
                            $"bestShift={fastShiftOriginal:F2}px, improve={fastImprovementRatio:P1}, " +
                            $"time={stopwatch.ElapsedMilliseconds}ms");
                        return false;
                    }

                    string fastTranslationDiagnostic = string.Empty;
                    double fastRefineMagnitudeWork = Math.Sqrt(
                        fastRefineX * fastRefineX + fastRefineY * fastRefineY);
                    double fastSearchRadiusWork =
                        LocalAlignmentTranslationRefineRadiusOriginalPx * alignmentScale;
                    bool fastTouchesSearchBoundary = fastRefineMagnitudeWork >=
                        fastSearchRadiusWork * LocalAlignmentFastTranslationBoundaryRatio;
                    if (fastTranslationAccepted &&
                        !fastTouchesSearchBoundary &&
                        TryValidateLocalAffine(
                            fastTranslation,
                            alignmentScale,
                            out fastTranslationDiagnostic))
                    {
                        Mat fastScaledOutput = null;
                        Mat fastOriginalOutput = null;
                        try
                        {
                            CreateAlignedOutputs(
                                alphaScaled,
                                cisScaled,
                                cisImgOrig,
                                defectScale,
                                alignmentScale,
                                needOriginalWarp,
                                fastTranslation,
                                out fastScaledOutput,
                                out fastOriginalOutput);

                            cisAligned = fastScaledOutput;
                            cisAlignedOrig = fastOriginalOutput;
                            fastScaledOutput = null;
                            fastOriginalOutput = null;

                            double dxOriginal = fastTranslation.At<double>(0, 2) / alignmentScale;
                            double dyOriginal = fastTranslation.At<double>(1, 2) / alignmentScale;
                            stopwatch.Stop();
                            AppLog.Diagnostic(logger,
                                $"[LocalAlign] {FormatPartId(partId)} Applied/FastTranslation: " +
                                $"workScale={alignmentScale:F3}, defectScale={defectScale:F3}, " +
                                $"move=({dxOriginal:F2},{dyOriginal:F2})px, " +
                                $"refine=({fastRefineX:F2},{fastRefineY:F2})workPx, " +
                                $"edge={initialEdgeScore:F3}->{translatedEdgeScore:F3}, " +
                                $"localWorst={fastWorstLocalRegression:+0.000;-0.000;0.000}px/" +
                                $"{fastCheckedLocalCells}, time={stopwatch.ElapsedMilliseconds}ms");
                            return true;
                        }
                        finally
                        {
                            fastScaledOutput?.Dispose();
                            fastOriginalOutput?.Dispose();
                        }
                    }

                    if (fastTranslationAccepted && fastTouchesSearchBoundary)
                    {
                        AppLog.Diagnostic(logger,
                            $"[LocalAlign] {FormatPartId(partId)} FastTranslation rejected: " +
                            $"best shift touches search boundary " +
                            $"({fastRefineMagnitudeWork:F2}/{fastSearchRadiusWork:F2}workPx), " +
                            $"fallback to SIFT, time={stopwatch.ElapsedMilliseconds}ms");
                    }
                    else if (fastTranslationAccepted)
                    {
                        LogAlignmentFailure(
                            logger,
                            partId,
                            "快速平移矩阵越界: " + fastTranslationDiagnostic,
                            stopwatch.ElapsedMilliseconds);
                    }
                }

                // 第二级：快速平移不足以解释残差时，才提取 SIFT 特征估计少量旋转/统一缩放。
                PatchSiftTemplateFeatures template = worker.TemplateCache.GetOrCreate(alphaFeature, worker.Sift);
                if (template.KeyPoints.Length == 0 || template.Descriptors == null || template.Descriptors.Empty())
                {
                    LogAlignmentFailure(logger, partId, "模板特征为空", stopwatch.ElapsedMilliseconds);
                    return false;
                }

                using (var cisDescriptors = new Mat())
                {
                    worker.Sift.DetectAndCompute(cisFeature, null, out KeyPoint[] cisKeyPoints, cisDescriptors);
                    if (cisKeyPoints.Length == 0 || cisDescriptors.Empty())
                    {
                        LogAlignmentFailure(logger, partId, "CIS 特征为空", stopwatch.ElapsedMilliseconds);
                        return false;
                    }

                    // 双向 KNN + ratio + 互相一致：周期纹理中常见的一对多匹配必须在两个方向
                    // 都把对方选为最佳候选，才允许进入几何估计。
                    const float ratioThreshold = 0.70f;
                    List<DMatch> forwardMatches = SelectRatioMatches(
                        worker.Matcher.KnnMatch(template.Descriptors, cisDescriptors, 2),
                        ratioThreshold);
                    List<DMatch> reverseMatches = SelectRatioMatches(
                        worker.Matcher.KnnMatch(cisDescriptors, template.Descriptors, 2),
                        ratioThreshold);
                    var reverseByCisIndex = reverseMatches.ToDictionary(
                        match => match.QueryIdx,
                        match => match);
                    var goodMatches = new List<DMatch>(forwardMatches.Count);
                    foreach (DMatch match in forwardMatches)
                    {
                        if (!reverseByCisIndex.TryGetValue(match.TrainIdx, out DMatch reverse) ||
                            reverse.TrainIdx != match.QueryIdx)
                        {
                            continue;
                        }

                        Point2f templatePoint = template.KeyPoints[match.QueryIdx].Pt;
                        Point2f cisPoint = cisKeyPoints[match.TrainIdx].Pt;
                        double displacementXOriginal =
                            (templatePoint.X - cisPoint.X) / alignmentScale;
                        double displacementYOriginal =
                            (templatePoint.Y - cisPoint.Y) / alignmentScale;
                        if (Math.Abs(displacementXOriginal) <= LocalAlignmentMaxMatchDisplacementOriginalPx &&
                            Math.Abs(displacementYOriginal) <= LocalAlignmentMaxMatchDisplacementOriginalPx)
                        {
                            goodMatches.Add(match);
                        }
                    }

                    const int minimumMatches = 6;
                    if (goodMatches.Count < minimumMatches)
                    {
                        LogAlignmentFailure(logger, partId, $"有效匹配不足({goodMatches.Count}/{minimumMatches})", stopwatch.ElapsedMilliseconds);
                        return false;
                    }

                    Point2f[] templatePoints = goodMatches
                        .Select(match => template.KeyPoints[match.QueryIdx].Pt)
                        .ToArray();
                    Point2f[] cisPoints = goodMatches
                        .Select(match => cisKeyPoints[match.TrainIdx].Pt)
                        .ToArray();

                    // 零件在全局对齐后主要剩余平移、少量旋转和统一缩放。使用相似变换而不是
                    // 6 自由度完整仿射，可从模型层面禁止匹配噪声被拟合成非等比拉伸或剪切，
                    // 避免主体平均分数改善、局部细线却被拉开。CIS→TIFF 方向保持不变。
                    double ransacThreshold = Math.Max(
                        0.5,
                        LocalAlignmentRansacThresholdOriginalPx * alignmentScale);
                    using (InputArray affineSource = InputArray.Create(cisPoints))
                    using (InputArray affineTarget = InputArray.Create(templatePoints))
                    using (var affineInlierMask = new Mat())
                    {
                        Mat estimatedTransform;
                        // 固定随机种子并保护 RANSAC 求解，保证并行度和线程调度变化时，
                        // 相同匹配点仍产生相同内点集合。锁内不做特征提取或图像 Warp。
                        lock (LocalAlignmentRansacSync)
                        {
                            Cv2.SetTheRNG(LocalAlignmentRansacSeed);
                            estimatedTransform = Cv2.EstimateAffinePartial2D(
                                affineSource,
                                affineTarget,
                                affineInlierMask,
                                RobustEstimationAlgorithms.RANSAC,
                                ransacThreshold,
                                2000,
                                0.99,
                                10);
                        }

                        using (Mat transform = estimatedTransform)
                        {
                            if (transform == null || transform.Empty() || affineInlierMask.Empty())
                            {
                                LogAlignmentFailure(logger, partId, "Similarity RANSAC 未得到矩阵或内点", stopwatch.ElapsedMilliseconds);
                                return false;
                            }

                            affineInlierMask.GetArray(out byte[] maskValues);
                            var templateInliers = new List<Point2f>(goodMatches.Count);
                            var cisInliers = new List<Point2f>(goodMatches.Count);
                            int maskLength = Math.Min(maskValues.Length, goodMatches.Count);
                            for (int i = 0; i < maskLength; i++)
                            {
                                if (maskValues[i] != 0)
                                {
                                    templateInliers.Add(templatePoints[i]);
                                    cisInliers.Add(cisPoints[i]);
                                }
                            }

                            double inlierRatio = templateInliers.Count / (double)goodMatches.Count;
                            if (templateInliers.Count < minimumMatches)
                            {
                                LogAlignmentFailure(
                                    logger,
                                    partId,
                                    $"Similarity 内点不足({templateInliers.Count}/{goodMatches.Count}, {inlierRatio:P0})",
                                    stopwatch.ElapsedMilliseconds);
                                return false;
                            }

                        if (!HasSufficientInlierCoverage(
                                templateInliers,
                                alphaFeature.Size(),
                                out double inlierCoverage))
                        {
                            LogAlignmentFailure(
                                logger,
                                partId,
                                $"内点空间覆盖不足(coverage={inlierCoverage:P1})",
                                stopwatch.ElapsedMilliseconds);
                            return false;
                        }

                        double reprojectionRmsOriginal = CalculateAffineRmsOriginalPixels(
                            transform,
                            cisInliers,
                            templateInliers,
                            alignmentScale);
                        if (reprojectionRmsOriginal > LocalAlignmentMaxResidualRmsOriginalPx)
                        {
                            LogAlignmentFailure(
                                logger,
                                partId,
                                $"内点重投影RMS过大({reprojectionRmsOriginal:F2}px)",
                                stopwatch.ElapsedMilliseconds);
                            return false;
                        }

                        // 内点率仍是首选指标，但不能单独否决“候选很多、绝对内点也很多”的复杂图案。
                        // 低比例候选必须同时满足：不少于 12 个内点、比例不低于 35%、覆盖模板
                        // 至少 8%，且 RMS 比常规上限更严格。通过这里只代表允许进入后面的矩阵范围
                        // 与双向轮廓改善检查；任一后续门控失败仍回退到全局对齐裁图。
                        bool standardInlierConfidence =
                            inlierRatio >= LocalAlignmentMinimumInlierRatio;
                        bool conditionalInlierConfidence =
                            templateInliers.Count >= LocalAlignmentConditionalMinimumInlierCount &&
                            inlierRatio >= LocalAlignmentConditionalMinimumInlierRatio &&
                            inlierCoverage >= LocalAlignmentConditionalMinimumBoundingCoverage &&
                            reprojectionRmsOriginal <=
                                LocalAlignmentConditionalMaxResidualRmsOriginalPx;
                        if (!standardInlierConfidence && !conditionalInlierConfidence)
                        {
                            LogAlignmentFailure(
                                logger,
                                partId,
                                $"Similarity 内点置信度不足(" +
                                $"{templateInliers.Count}/{goodMatches.Count}, {inlierRatio:P0}, " +
                                $"coverage={inlierCoverage:P1}, rms={reprojectionRmsOriginal:F2}px)",
                                stopwatch.ElapsedMilliseconds);
                            return false;
                        }

                        if (!TryValidateLocalAffine(
                                transform,
                                alignmentScale,
                                out string transformDiagnostic))
                        {
                            LogAlignmentFailure(logger, partId, transformDiagnostic, stopwatch.ElapsedMilliseconds);
                            return false;
                        }

                        // 相似变换负责旋转和统一缩放，小范围边缘 Chamfer 精修剩余整体平移。
                        // 如果相似变换因微小旋转/缩放让某个局部区域变差，再尝试一个由匹配点
                        // 位移中位数确定的纯平移模型；它能稳定处理“实际只有整体错位”的零件。
                        string alignmentModel = standardInlierConfidence
                            ? "Similarity"
                            : "SimilarityConditional";
                        bool refinementAccepted = TryRefineAffineTranslationByEdges(
                                alphaFeature,
                                cisFeature,
                                alphaBinaryThreshold,
                                cisBinaryThreshold,
                                alignmentScale,
                                transform,
                                true,
                                LocalAlignmentMinEdgeImprovementRatio,
                                LocalAlignmentCandidateRefineRadiusOriginalPx,
                                out double edgeScoreBefore,
                                out double edgeScoreAfter,
                                out double refineX,
                                out double refineY,
                                out double worstLocalRegression,
                                out int checkedLocalCells);
                        string similarityFailure = FormatEdgeGateDiagnostic(
                            edgeScoreBefore,
                            edgeScoreAfter,
                            worstLocalRegression,
                            checkedLocalCells);

                        if (!refinementAccepted)
                        {
                            Mat translationFallback = null;
                            try
                            {
                                if (TryBuildTranslationFallback(
                                        cisInliers,
                                        templateInliers,
                                        alignmentScale,
                                        out translationFallback,
                                        out double translationConsensusP80) &&
                                    TryRefineAffineTranslationByEdges(
                                        alphaFeature,
                                        cisFeature,
                                        alphaBinaryThreshold,
                                        cisBinaryThreshold,
                                        alignmentScale,
                                        translationFallback,
                                        !HasStrongTranslationConsensus(
                                            translationFallback,
                                            translationConsensusP80,
                                            alignmentScale),
                                        LocalAlignmentMinEdgeImprovementRatio,
                                        LocalAlignmentCandidateRefineRadiusOriginalPx,
                                        out edgeScoreBefore,
                                        out edgeScoreAfter,
                                        out refineX,
                                        out refineY,
                                        out worstLocalRegression,
                                        out checkedLocalCells))
                                {
                                    translationFallback.CopyTo(transform);
                                    alignmentModel = "Translation";
                                    refinementAccepted = true;
                                }
                                else
                                {
                                    LogAlignmentFailure(
                                        logger,
                                        partId,
                                        $"相似变换与纯平移均未通过轮廓质量门控：" +
                                        $"similarity({similarityFailure}), " +
                                        $"translation({FormatEdgeGateDiagnostic(edgeScoreBefore, edgeScoreAfter, worstLocalRegression, checkedLocalCells)}, " +
                                        $"P80={translationConsensusP80:F2}px, " +
                                        $"move=({translationFallback?.At<double>(0, 2) / alignmentScale:F2}," +
                                        $"{translationFallback?.At<double>(1, 2) / alignmentScale:F2})px)",
                                        stopwatch.ElapsedMilliseconds);
                                    return false;
                                }
                            }
                            finally
                            {
                                translationFallback?.Dispose();
                            }
                        }

                        // 精修平移也属于最终矩阵的一部分；再次检查，避免初始矩阵已接近
                        // 最大平移边界时，被后续 ±10 px 搜索推到安全范围之外。
                        if (!TryValidateLocalAffine(
                                transform,
                                alignmentScale,
                                out string refinedTransformDiagnostic))
                        {
                            LogAlignmentFailure(
                                logger,
                                partId,
                                "边缘精修后" + refinedTransformDiagnostic,
                                stopwatch.ElapsedMilliseconds);
                            return false;
                        }

                        // 只有所有质量检查通过后才创建输出，失败路径继续使用 H0 裁出的原始小图。
                        Mat scaledOutput = null;
                        Mat originalOutput = null;
                        try
                        {
                            CreateAlignedOutputs(
                                alphaScaled,
                                cisScaled,
                                cisImgOrig,
                                defectScale,
                                alignmentScale,
                                needOriginalWarp,
                                transform,
                                out scaledOutput,
                                out originalOutput);

                            cisAligned = scaledOutput;
                            cisAlignedOrig = originalOutput;
                            scaledOutput = null;
                            originalOutput = null;

                            double dxOriginal = transform.At<double>(0, 2) / alignmentScale;
                            double dyOriginal = transform.At<double>(1, 2) / alignmentScale;
                            stopwatch.Stop();
                            AppLog.Diagnostic(logger,
                                $"[LocalAlign] {FormatPartId(partId)} Applied/{alignmentModel}: " +
                                $"workScale={alignmentScale:F3}, defectScale={defectScale:F3}, " +
                                $"kp={template.KeyPoints.Length}/{cisKeyPoints.Length}, " +
                                $"mutual={goodMatches.Count}, inliers={templateInliers.Count}({inlierRatio:P0}), " +
                                $"coverage={inlierCoverage:P1}, rms={reprojectionRmsOriginal:F2}px, " +
                                $"move=({dxOriginal:F2},{dyOriginal:F2})px, refine=({refineX:F2},{refineY:F2})workPx, " +
                                $"edge={edgeScoreBefore:F3}->{edgeScoreAfter:F3}, " +
                                $"localWorst={worstLocalRegression:+0.000;-0.000;0.000}px/{checkedLocalCells}, " +
                                $"time={stopwatch.ElapsedMilliseconds}ms");
                            return true;
                        }
                        finally
                        {
                            scaledOutput?.Dispose();
                            originalOutput?.Dispose();
                        }
                    }
                }
            }
            }
            catch (Exception ex)
            {
                LogAlignmentFailure(logger, partId, $"异常: {ex.Message}", stopwatch.ElapsedMilliseconds);
                cisAligned?.Dispose();
                cisAlignedOrig?.Dispose();
                cisAligned = null;
                cisAlignedOrig = null;
                return false;
            }
        }

        private static List<DMatch> SelectRatioMatches(DMatch[][] knnMatches, float ratioThreshold)
        {
            var selected = new List<DMatch>(knnMatches.Length);
            foreach (DMatch[] matches in knnMatches)
            {
                if (matches.Length >= 2 &&
                    matches[0].Distance < ratioThreshold * matches[1].Distance)
                {
                    selected.Add(matches[0]);
                }
            }
            return selected;
        }

        /// <summary>
        /// 内点不能全部挤在一个局部重复纹理块内。这里要求至少跨越三个 3x3 网格，
        /// 并在 X/Y 至少一个方向覆盖图像 20%，兼容细长图案而不强制二维铺满。
        /// </summary>
        private static bool HasSufficientInlierCoverage(
            List<Point2f> points,
            Size imageSize,
            out double boundingCoverage)
        {
            boundingCoverage = 0;
            if (points == null || points.Count == 0 || imageSize.Width <= 0 || imageSize.Height <= 0)
                return false;

            float minX = points.Min(point => point.X);
            float maxX = points.Max(point => point.X);
            float minY = points.Min(point => point.Y);
            float maxY = points.Max(point => point.Y);
            double spanX = Math.Max(0, maxX - minX) / imageSize.Width;
            double spanY = Math.Max(0, maxY - minY) / imageSize.Height;
            boundingCoverage = spanX * spanY;

            var occupiedCells = new HashSet<int>();
            foreach (Point2f point in points)
            {
                int cellX = Math.Max(0, Math.Min(2, (int)(point.X * 3 / imageSize.Width)));
                int cellY = Math.Max(0, Math.Min(2, (int)(point.Y * 3 / imageSize.Height)));
                occupiedCells.Add(cellY * 3 + cellX);
            }
            return occupiedCells.Count >= 3 && Math.Max(spanX, spanY) >= 0.20;
        }

        private static double CalculateAffineRmsOriginalPixels(
            Mat transform,
            List<Point2f> sourcePoints,
            List<Point2f> targetPoints,
            double alignmentScale)
        {
            double a = transform.At<double>(0, 0);
            double b = transform.At<double>(0, 1);
            double tx = transform.At<double>(0, 2);
            double c = transform.At<double>(1, 0);
            double d = transform.At<double>(1, 1);
            double ty = transform.At<double>(1, 2);
            double squaredError = 0;
            int count = Math.Min(sourcePoints.Count, targetPoints.Count);
            for (int index = 0; index < count; index++)
            {
                Point2f source = sourcePoints[index];
                Point2f target = targetPoints[index];
                double errorX = a * source.X + b * source.Y + tx - target.X;
                double errorY = c * source.X + d * source.Y + ty - target.Y;
                squaredError += errorX * errorX + errorY * errorY;
            }
            return count == 0
                ? double.PositiveInfinity
                : Math.Sqrt(squaredError / count) / alignmentScale;
        }

        /// <summary>检查完整 2x2 线性部分，防止镜像、过大旋转/缩放或剪切仅靠对角元素漏检。</summary>
        private static bool TryValidateLocalAffine(
            Mat transform,
            double alignmentScale,
            out string diagnostic)
        {
            double a = transform.At<double>(0, 0);
            double b = transform.At<double>(0, 1);
            double tx = transform.At<double>(0, 2);
            double c = transform.At<double>(1, 0);
            double d = transform.At<double>(1, 1);
            double ty = transform.At<double>(1, 2);
            double determinant = a * d - b * c;
            double firstColumnScale = Math.Sqrt(a * a + c * c);
            double secondColumnScale = Math.Sqrt(b * b + d * d);
            double shearCosine = firstColumnScale > 0 && secondColumnScale > 0
                ? Math.Abs((a * b + c * d) / (firstColumnScale * secondColumnScale))
                : double.PositiveInfinity;
            double rotationDeg = Math.Atan2(c, a) * 180.0 / Math.PI;
            double dxOriginal = tx / alignmentScale;
            double dyOriginal = ty / alignmentScale;

            bool finite = new[]
            {
                a, b, c, d, tx, ty, determinant,
                firstColumnScale, secondColumnScale, shearCosine, rotationDeg
            }.All(value => !double.IsNaN(value) && !double.IsInfinity(value));
            bool accepted = finite && determinant > 0 &&
                            firstColumnScale >= 0.90 && firstColumnScale <= 1.10 &&
                            secondColumnScale >= 0.90 && secondColumnScale <= 1.10 &&
                            shearCosine <= 0.15 &&
                            Math.Abs(rotationDeg) <= 5.0 &&
                            Math.Abs(dxOriginal) <= LocalAlignmentMaxTranslationOriginalPx &&
                            Math.Abs(dyOriginal) <= LocalAlignmentMaxTranslationOriginalPx;
            diagnostic = accepted
                ? string.Empty
                : $"仿射矩阵越界: scale=({firstColumnScale:F4},{secondColumnScale:F4}), " +
                  $"rot={rotationDeg:F2}deg, shear={shearCosine:F3}, " +
                  $"move=({dxOriginal:F2},{dyOriginal:F2})px, det={determinant:F4}";
            return accepted;
        }

        /// <summary>
        /// 当相似变换对局部结构产生不利影响时，使用 RANSAC 内点位移的中位数构造纯平移候选。
        /// P80 残差限制保证大多数内点确实支持同一个平移；存在真实旋转或缩放时不会误走该分支。
        /// 返回的矩阵由调用方负责释放。
        /// </summary>
        private static bool TryBuildTranslationFallback(
            List<Point2f> sourcePoints,
            List<Point2f> targetPoints,
            double alignmentScale,
            out Mat translationTransform,
            out double residualP80OriginalPixels)
        {
            translationTransform = null;
            residualP80OriginalPixels = double.PositiveInfinity;
            int count = Math.Min(sourcePoints?.Count ?? 0, targetPoints?.Count ?? 0);
            if (count < 3 || alignmentScale <= 0)
                return false;

            var displacementX = new double[count];
            var displacementY = new double[count];
            for (int index = 0; index < count; index++)
            {
                displacementX[index] = targetPoints[index].X - sourcePoints[index].X;
                displacementY[index] = targetPoints[index].Y - sourcePoints[index].Y;
            }
            Array.Sort(displacementX);
            Array.Sort(displacementY);
            double medianX = MedianOfSorted(displacementX);
            double medianY = MedianOfSorted(displacementY);

            var residuals = new double[count];
            for (int index = 0; index < count; index++)
            {
                double dx = targetPoints[index].X - sourcePoints[index].X - medianX;
                double dy = targetPoints[index].Y - sourcePoints[index].Y - medianY;
                residuals[index] = Math.Sqrt(dx * dx + dy * dy) / alignmentScale;
            }
            Array.Sort(residuals);
            int percentileIndex = Math.Max(
                0,
                Math.Min(count - 1, (int)Math.Ceiling(count * 0.80) - 1));
            residualP80OriginalPixels = residuals[percentileIndex];
            if (residualP80OriginalPixels > LocalAlignmentTranslationConsensusP80OriginalPx)
                return false;

            translationTransform = Mat.Eye(2, 3, MatType.CV_64FC1);
            translationTransform.Set(0, 2, medianX);
            translationTransform.Set(1, 2, medianY);
            return true;
        }

        private static bool HasStrongTranslationConsensus(
            Mat translationTransform,
            double residualP80OriginalPixels,
            double alignmentScale)
        {
            if (translationTransform == null || translationTransform.Empty() || alignmentScale <= 0)
                return false;

            double translationX = translationTransform.At<double>(0, 2);
            double translationY = translationTransform.At<double>(1, 2);
            double magnitudeOriginal = Math.Sqrt(
                translationX * translationX + translationY * translationY) / alignmentScale;
            return residualP80OriginalPixels <= LocalAlignmentStrongTranslationConsensusP80OriginalPx &&
                   magnitudeOriginal >= LocalAlignmentStrongTranslationMinMagnitudeOriginalPx;
        }

        private static double MedianOfSorted(double[] sortedValues)
        {
            int count = sortedValues?.Length ?? 0;
            if (count == 0)
                return double.NaN;
            int middle = count / 2;
            return (count & 1) == 0
                ? 0.5 * (sortedValues[middle - 1] + sortedValues[middle])
                : sortedValues[middle];
        }

        private static string FormatEdgeGateDiagnostic(
            double scoreBefore,
            double scoreAfter,
            double worstLocalRegression,
            int checkedLocalCells)
        {
            double improvementRatio = (scoreBefore - scoreAfter) / Math.Max(scoreBefore, 1e-6);
            if (checkedLocalCells <= 0)
            {
                return $"global={scoreBefore:F3}->{scoreAfter:F3}, " +
                       $"improve={improvementRatio:P1}/{LocalAlignmentMinEdgeImprovementRatio:P0}";
            }

            return $"global={scoreBefore:F3}->{scoreAfter:F3}, improve={improvementRatio:P1}, " +
                   $"localWorst={worstLocalRegression:+0.000;-0.000;0.000}px, " +
                   $"cells={checkedLocalCells}";
        }

        /// <summary>
        /// 在候选变换附近，以模板/CIS 二值轮廓的双向距离场为目标做小范围平移精修。
        /// 先搜索工作图整像素，再在最优点附近进行亚像素搜索；距离采用截断损失，
        /// 使少量真实缺陷不会为了降低配准分数而牵引整张零件图。
        /// 若最终评分没有优于未做局部配准的输入，则拒绝矩阵，避免错误匹配使结果变差。
        /// </summary>
        private static bool TryRefineAffineTranslationByEdges(
            Mat alphaFeature,
            Mat cisFeature,
            int alphaThreshold,
            int cisThreshold,
            double alignmentScale,
            Mat transform,
            bool requireLocalStability,
            double minimumImprovementRatio,
            double maxRefinementOriginalPixels,
            out double scoreBefore,
            out double scoreAfter,
            out double refineX,
            out double refineY,
            out double worstLocalRegression,
            out int checkedLocalCells)
        {
            scoreBefore = double.PositiveInfinity;
            scoreAfter = double.PositiveInfinity;
            refineX = 0.0;
            refineY = 0.0;
            worstLocalRegression = double.PositiveInfinity;
            checkedLocalCells = 0;
            double maxRefinementWorkPixels = Math.Max(
                LocalAlignmentSubpixelStepWorkPx,
                maxRefinementOriginalPixels * alignmentScale);
            int searchRadius = Math.Max(1, (int)Math.Ceiling(maxRefinementWorkPixels));

            using (var alphaBinary = new Mat())
            using (var cisBinary = new Mat())
            using (var cisWarpedBinary = new Mat())
            using (var alphaEdges = new Mat())
            using (var cisEdgesBefore = new Mat())
            using (var cisEdgesAfter = new Mat())
            using (var cisWarpedFinalBinary = new Mat())
            using (var cisEdgesFinal = new Mat())
            using (Mat edgeKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3)))
            {
                Cv2.Threshold(alphaFeature, alphaBinary, alphaThreshold, 255, ThresholdTypes.Binary);
                Cv2.Threshold(cisFeature, cisBinary, cisThreshold, 255, ThresholdTypes.Binary);
                Cv2.MorphologyEx(alphaBinary, alphaEdges, MorphTypes.Gradient, edgeKernel);
                Cv2.MorphologyEx(cisBinary, cisEdgesBefore, MorphTypes.Gradient, edgeKernel);
                scoreBefore = FindBestRobustChamferShift(
                    alphaEdges,
                    cisEdgesBefore,
                    0,
                    searchRadius,
                    0.0,
                    true,
                    out _,
                    out _);

                Cv2.WarpAffine(
                    cisBinary,
                    cisWarpedBinary,
                    transform,
                    alphaBinary.Size(),
                    InterpolationFlags.Nearest);
                Cv2.MorphologyEx(cisWarpedBinary, cisEdgesAfter, MorphTypes.Gradient, edgeKernel);
                scoreAfter = FindBestRobustChamferShift(
                    alphaEdges,
                    cisEdgesAfter,
                    searchRadius,
                    searchRadius,
                    maxRefinementWorkPixels,
                    true,
                    out refineX,
                    out refineY);

                double improvementRatio = (scoreBefore - scoreAfter) /
                                          Math.Max(scoreBefore, 1e-6);
                if (double.IsInfinity(scoreBefore) || double.IsInfinity(scoreAfter) ||
                    double.IsNaN(scoreBefore) || double.IsNaN(scoreAfter) ||
                    improvementRatio < minimumImprovementRatio)
                {
                    return false;
                }

                // 不直接修改调用方矩阵：只有全局改善和局部稳定性都通过后，才提交精修结果。
                // 这样失败分支继续尝试其他模型时，不会继承一个已被拒绝的平移量。
                using (Mat refinedTransform = transform.Clone())
                {
                    refinedTransform.Set(0, 2, refinedTransform.At<double>(0, 2) + refineX);
                    refinedTransform.Set(1, 2, refinedTransform.At<double>(1, 2) + refineY);

                    if (requireLocalStability)
                    {
                        // 全局平均值可能掩盖“主体更好、某条细线更差”的情况。用最终矩阵重新生成
                        // 边缘后，将模板划成 3x3 区域，逐区确认设计轮廓到 CIS 轮廓的距离没有显著增大。
                        // 这道门控不直接判断缺陷，只保证局部配准不会制造新的局部错位。
                        Cv2.WarpAffine(
                            cisBinary,
                            cisWarpedFinalBinary,
                            refinedTransform,
                            alphaBinary.Size(),
                            InterpolationFlags.Nearest);
                        Cv2.MorphologyEx(
                            cisWarpedFinalBinary,
                            cisEdgesFinal,
                            MorphTypes.Gradient,
                            edgeKernel);
                        bool localStabilityAccepted = HasNoSignificantLocalEdgeRegression(
                                alphaEdges,
                                cisEdgesBefore,
                                cisEdgesFinal,
                                alignmentScale,
                                improvementRatio,
                                out worstLocalRegression,
                                out checkedLocalCells);
                        if (!localStabilityAccepted)
                            return false;
                    }
                    else
                    {
                        worstLocalRegression = 0;
                        checkedLocalCells = 0;
                    }

                    refinedTransform.CopyTo(transform);
                    return true;
                }
            }
        }

        /// <summary>
        /// 将配准工作尺度上的最终矩阵一次性换算到缺陷检测尺度和原始尺度。
        /// 线性部分（旋转/统一缩放）保持不变，仅平移按尺度比换算；调用方拥有返回 Mat。
        /// </summary>
        private static void CreateAlignedOutputs(
            Mat alphaScaled,
            Mat cisScaled,
            Mat cisImgOrig,
            double defectScale,
            double alignmentScale,
            bool needOriginalWarp,
            Mat transform,
            out Mat scaledOutput,
            out Mat originalOutput)
        {
            scaledOutput = new Mat();
            originalOutput = null;
            try
            {
                using (Mat defectScaleTransform = transform.Clone())
                {
                    double translationScale = defectScale / alignmentScale;
                    defectScaleTransform.Set(
                        0,
                        2,
                        transform.At<double>(0, 2) * translationScale);
                    defectScaleTransform.Set(
                        1,
                        2,
                        transform.At<double>(1, 2) * translationScale);
                    Cv2.WarpAffine(
                        cisScaled,
                        scaledOutput,
                        defectScaleTransform,
                        alphaScaled.Size(),
                        InterpolationFlags.Cubic);
                }

                if (needOriginalWarp)
                {
                    originalOutput = new Mat();
                    using (Mat originalTransform = transform.Clone())
                    {
                        originalTransform.Set(
                            0,
                            2,
                            transform.At<double>(0, 2) / alignmentScale);
                        originalTransform.Set(
                            1,
                            2,
                            transform.At<double>(1, 2) / alignmentScale);
                        Cv2.WarpAffine(
                            cisImgOrig,
                            originalOutput,
                            originalTransform,
                            cisImgOrig.Size(),
                            InterpolationFlags.Cubic);
                    }
                }
            }
            catch
            {
                scaledOutput?.Dispose();
                originalOutput?.Dispose();
                scaledOutput = null;
                originalOutput = null;
                throw;
            }
        }

        /// <summary>
        /// 对固定的模板边缘点，比较局部配准前后到 CIS 最近边缘的平均距离。
        /// 使用固定模板点集而不是比较两幅差分图，可避免某个区域因 CIS 偏暗、边缘点变少而
        /// 获得虚假的“分数改善”。任一有足够模板结构的区域明显退化，整张局部矩阵即拒绝。
        /// </summary>
        private static bool HasNoSignificantLocalEdgeRegression(
            Mat referenceEdges,
            Mat movingEdgesBefore,
            Mat movingEdgesAfter,
            double alignmentScale,
            double globalImprovementRatio,
            out double worstRegression,
            out int checkedCells)
        {
            worstRegression = double.NegativeInfinity;
            checkedCells = 0;
            using (var inverseBefore = new Mat())
            using (var inverseAfter = new Mat())
            using (var distanceBefore = new Mat())
            using (var distanceAfter = new Mat())
            {
                Cv2.BitwiseNot(movingEdgesBefore, inverseBefore);
                Cv2.BitwiseNot(movingEdgesAfter, inverseAfter);
                Cv2.DistanceTransform(
                    inverseBefore,
                    distanceBefore,
                    DistanceTypes.L2,
                    DistanceTransformMasks.Mask3);
                Cv2.DistanceTransform(
                    inverseAfter,
                    distanceAfter,
                    DistanceTypes.L2,
                    DistanceTransformMasks.Mask3);

                referenceEdges.GetArray(out byte[] referenceValues);
                distanceBefore.GetArray(out float[] beforeDistances);
                distanceAfter.GetArray(out float[] afterDistances);
                int width = referenceEdges.Width;
                int height = referenceEdges.Height;
                // Warp 后图像外侧会因源图越界自然缺失。该现象属于裁切边界条件，不能拿来
                // 判断内部配准质量；忽略最大允许平移对应的安全边框，只评价可完整采样区域。
                int safeBorder = Math.Max(
                    1,
                    (int)Math.Ceiling(LocalAlignmentMaxTranslationOriginalPx * alignmentScale));

                int significantlyRegressedCells = 0;
                for (int gridY = 0; gridY < LocalAlignmentValidationGridSize; gridY++)
                {
                    int top = gridY * height / LocalAlignmentValidationGridSize;
                    int bottom = (gridY + 1) * height / LocalAlignmentValidationGridSize;
                    for (int gridX = 0; gridX < LocalAlignmentValidationGridSize; gridX++)
                    {
                        int left = gridX * width / LocalAlignmentValidationGridSize;
                        int right = (gridX + 1) * width / LocalAlignmentValidationGridSize;
                        int referenceCount = 0;
                        double beforeSum = 0;
                        double afterSum = 0;
                        for (int y = top; y < bottom; y++)
                        {
                            int rowOffset = y * width;
                            for (int x = left; x < right; x++)
                            {
                                if (x < safeBorder || x >= width - safeBorder ||
                                    y < safeBorder || y >= height - safeBorder)
                                {
                                    continue;
                                }

                                int index = rowOffset + x;
                                if (referenceValues[index] == 0)
                                    continue;

                                referenceCount++;
                                // 局部稳定性与全局评分使用相同的截断距离。真实断口或漏印处
                                // 可能天然找不到对应 CIS 边缘，不能让少数极大距离支配整个网格。
                                beforeSum += Math.Min(
                                    beforeDistances[index],
                                    LocalAlignmentChamferDistanceCapWorkPx);
                                afterSum += Math.Min(
                                    afterDistances[index],
                                    LocalAlignmentChamferDistanceCapWorkPx);
                            }
                        }

                        if (referenceCount < LocalAlignmentMinReferenceEdgesPerCell)
                            continue;

                        checkedCells++;
                        double beforeMean = beforeSum / referenceCount;
                        double afterMean = afterSum / referenceCount;
                        double regression = afterMean - beforeMean;
                        worstRegression = Math.Max(worstRegression, regression);
                        double allowedRegression = LocalAlignmentMaxLocalRegressionPixels;
                        if (regression > allowedRegression)
                            significantlyRegressedCells++;
                    }
                }

                // 没有任何可评价区域时无法证明局部矩阵安全；保守回退到全局对齐裁图。
                if (checkedCells == 0)
                {
                    worstRegression = double.PositiveInfinity;
                    return false;
                }

                if (double.IsNegativeInfinity(worstRegression))
                    worstRegression = 0;

                if (significantlyRegressedCells == 0)
                    return true;

                // 当整体轮廓改善超过 20% 时，单个网格可能因为真实缺陷或二值化波动略有变差。
                // 仅允许 1 个网格且退化不超过 0.75 工作像素；多个区域同时变差仍然拒绝。
                return globalImprovementRatio >= LocalAlignmentStrongEdgeImprovementRatio &&
                       significantlyRegressedCells == 1 &&
                       worstRegression <= LocalAlignmentStrongCaseMaxLocalRegressionPixels;
            }
        }

        /// <summary>
        /// 在两组二值轮廓之间搜索双向 Chamfer 最优平移。整数搜索负责覆盖范围，
        /// 亚像素搜索通过双线性采样距离场细化结果；每个方向最多固定采样 6000 个点，
        /// 保证不同轮廓复杂度下耗时可控且结果可复现。
        /// </summary>
        private static double FindBestRobustChamferShift(
            Mat referenceEdges,
            Mat movingEdges,
            int searchRadius,
            int samplingMarginRadius,
            double maximumShiftMagnitude,
            bool enableSubpixelRefinement,
            out double bestShiftX,
            out double bestShiftY)
        {
            bestShiftX = 0.0;
            bestShiftY = 0.0;
            using (var inverseReference = new Mat())
            using (var inverseMoving = new Mat())
            using (var referenceDistance = new Mat())
            using (var movingDistance = new Mat())
            {
                Cv2.BitwiseNot(referenceEdges, inverseReference);
                Cv2.BitwiseNot(movingEdges, inverseMoving);
                Cv2.DistanceTransform(
                    inverseReference,
                    referenceDistance,
                    DistanceTypes.L2,
                    DistanceTransformMasks.Mask3);
                Cv2.DistanceTransform(
                    inverseMoving,
                    movingDistance,
                    DistanceTypes.L2,
                    DistanceTransformMasks.Mask3);

                referenceEdges.GetArray(out byte[] referenceValues);
                movingEdges.GetArray(out byte[] movingValues);
                referenceDistance.GetArray(out float[] referenceDistances);
                movingDistance.GetArray(out float[] movingDistances);
                int width = referenceEdges.Width;
                int height = referenceEdges.Height;
                // 双线性采样需要额外保留 1 px 边界；亚像素邻域再额外预留搜索半径。
                int safeMargin = Math.Max(0, samplingMarginRadius) + (enableSubpixelRefinement
                    ? (int)Math.Ceiling(LocalAlignmentSubpixelRadiusWorkPx) + 1
                    : 1);
                var referencePoints = new List<Point>();
                var movingPoints = new List<Point>();
                for (int y = safeMargin; y < height - safeMargin; y++)
                {
                    int rowOffset = y * width;
                    for (int x = safeMargin; x < width - safeMargin; x++)
                    {
                        int index = rowOffset + x;
                        if (referenceValues[index] != 0)
                            referencePoints.Add(new Point(x, y));
                        if (movingValues[index] != 0)
                            movingPoints.Add(new Point(x, y));
                    }
                }
                if (referencePoints.Count == 0 || movingPoints.Count == 0)
                    return double.PositiveInfinity;

                int referenceStride = Math.Max(
                    1,
                    (int)Math.Ceiling(referencePoints.Count /
                        (double)LocalAlignmentMaxEdgeSamplesPerDirection));
                int movingStride = Math.Max(
                    1,
                    (int)Math.Ceiling(movingPoints.Count /
                        (double)LocalAlignmentMaxEdgeSamplesPerDirection));

                double bestScore = double.PositiveInfinity;
                double bestMagnitudeSquared = double.PositiveInfinity;
                for (int shiftY = -searchRadius; shiftY <= searchRadius; shiftY++)
                {
                    for (int shiftX = -searchRadius; shiftX <= searchRadius; shiftX++)
                    {
                        if (Math.Sqrt(shiftX * shiftX + shiftY * shiftY) >
                            maximumShiftMagnitude + 1e-9)
                        {
                            continue;
                        }

                        UpdateBestRobustChamferCandidate(
                            referencePoints,
                            movingPoints,
                            referenceDistances,
                            movingDistances,
                            width,
                            height,
                            referenceStride,
                            movingStride,
                            shiftX,
                            shiftY,
                            ref bestScore,
                            ref bestShiftX,
                            ref bestShiftY,
                            ref bestMagnitudeSquared);
                    }
                }

                if (enableSubpixelRefinement && searchRadius > 0 && !double.IsInfinity(bestScore))
                {
                    // 仅在整数最优点周围做小网格精修，避免把搜索复杂度扩展到整个二维区域。
                    double integerBestX = bestShiftX;
                    double integerBestY = bestShiftY;
                    for (double offsetY = -LocalAlignmentSubpixelRadiusWorkPx;
                         offsetY <= LocalAlignmentSubpixelRadiusWorkPx + 1e-9;
                         offsetY += LocalAlignmentSubpixelStepWorkPx)
                    {
                        for (double offsetX = -LocalAlignmentSubpixelRadiusWorkPx;
                             offsetX <= LocalAlignmentSubpixelRadiusWorkPx + 1e-9;
                             offsetX += LocalAlignmentSubpixelStepWorkPx)
                        {
                            double shiftX = integerBestX + offsetX;
                            double shiftY = integerBestY + offsetY;
                            if (Math.Abs(shiftX) > searchRadius + 1e-9 ||
                                Math.Abs(shiftY) > searchRadius + 1e-9 ||
                                Math.Sqrt(shiftX * shiftX + shiftY * shiftY) >
                                    maximumShiftMagnitude + 1e-9)
                            {
                                continue;
                            }

                            UpdateBestRobustChamferCandidate(
                                referencePoints,
                                movingPoints,
                                referenceDistances,
                                movingDistances,
                                width,
                                height,
                                referenceStride,
                                movingStride,
                                shiftX,
                                shiftY,
                                ref bestScore,
                                ref bestShiftX,
                                ref bestShiftY,
                                ref bestMagnitudeSquared);
                        }
                    }
                }
                return bestScore;
            }
        }

        private static void UpdateBestRobustChamferCandidate(
            List<Point> referencePoints,
            List<Point> movingPoints,
            float[] referenceDistances,
            float[] movingDistances,
            int width,
            int height,
            int referenceStride,
            int movingStride,
            double shiftX,
            double shiftY,
            ref double bestScore,
            ref double bestShiftX,
            ref double bestShiftY,
            ref double bestMagnitudeSquared)
        {
            double movingToReference = 0.0;
            int movingCount = 0;
            for (int index = 0; index < movingPoints.Count; index += movingStride)
            {
                Point point = movingPoints[index];
                double distance = BilinearSample(
                    referenceDistances,
                    width,
                    height,
                    point.X + shiftX,
                    point.Y + shiftY);
                movingToReference += Math.Min(distance, LocalAlignmentChamferDistanceCapWorkPx);
                movingCount++;
            }

            double referenceToMoving = 0.0;
            int referenceCount = 0;
            for (int index = 0; index < referencePoints.Count; index += referenceStride)
            {
                Point point = referencePoints[index];
                double distance = BilinearSample(
                    movingDistances,
                    width,
                    height,
                    point.X - shiftX,
                    point.Y - shiftY);
                referenceToMoving += Math.Min(distance, LocalAlignmentChamferDistanceCapWorkPx);
                referenceCount++;
            }

            if (movingCount == 0 || referenceCount == 0)
                return;

            double score = 0.5 *
                (movingToReference / movingCount + referenceToMoving / referenceCount);
            double magnitudeSquared = shiftX * shiftX + shiftY * shiftY;
            if (score < bestScore - 1e-9 ||
                (Math.Abs(score - bestScore) <= 1e-9 && magnitudeSquared < bestMagnitudeSquared))
            {
                bestScore = score;
                bestShiftX = shiftX;
                bestShiftY = shiftY;
                bestMagnitudeSquared = magnitudeSquared;
            }
        }

        private static double BilinearSample(
            float[] values,
            int width,
            int height,
            double x,
            double y)
        {
            int x0 = (int)Math.Floor(x);
            int y0 = (int)Math.Floor(y);
            int x1 = x0 + 1;
            int y1 = y0 + 1;
            if (x0 < 0 || y0 < 0 || x1 >= width || y1 >= height)
                return LocalAlignmentChamferDistanceCapWorkPx;

            double fx = x - x0;
            double fy = y - y0;
            double top = values[y0 * width + x0] * (1.0 - fx) +
                         values[y0 * width + x1] * fx;
            double bottom = values[y1 * width + x0] * (1.0 - fx) +
                            values[y1 * width + x1] * fx;
            return top * (1.0 - fy) + bottom * fy;
        }

        private static void LogAlignmentFailure(
            IAppLogger logger,
            string partId,
            string reason,
            long elapsedMilliseconds)
        {
            AppLog.Diagnostic(
                logger,
                $"[LocalAlign] {FormatPartId(partId)} skipped: {reason}, time={elapsedMilliseconds}ms");
        }

        private static string FormatPartId(string partId)
        {
            return string.IsNullOrWhiteSpace(partId) ? "<unknown>" : partId;
        }

        /// <summary>
        /// 连通域分析：统计超过面积阈值的缺陷数量与最大面积，返回缺陷外接矩形列表。
        /// </summary>
    }
}
