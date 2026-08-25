using System;
using System.Collections.Generic;
using System.Diagnostics;
using CIS_WebInspector.Models;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 图像拼接引擎 v2：基于双帧重叠区域的二维码检测 + 流式分段累积。
    /// 
    /// 内存优化策略：
    ///   - 废弃预分配 N 帧 Ring Buffer（旧方案 8×297MB ≈ 2.4GB 常驻）
    ///   - 仅保留上一帧底部 OverlapRows 行作为"尾部缓存"（~30MB 常驻）
    ///   - 段数据按需累积，完成后立即释放 chunk 引用
    /// 
    /// QR 检测策略：
    ///   - 每帧到达时，先检测当前帧整体（覆盖 QR 完全位于帧内的常见情况）
    ///   - 当前帧未命中时，再检测“上一帧尾部 + 当前帧头部”的重叠图像
    ///   - 双重检测确保跨帧边界的 QR 码不会被遗漏
    /// 
    /// 状态机：
    ///   SCANNING   → 等待首个 QR，检测到后记录段起始，进入 COLLECTING
    ///   COLLECTING → 累积帧数据，检测到下一个 QR 后输出完整段，同时开始新段
    /// </summary>
    public sealed class ImageStitcher : IDisposable
    {
        /// <summary>拼接完成时触发</summary>
        public event EventHandler<StitchedImageResult> StitchCompleted;

        /// <summary>连续多帧未检测到二维码时触发报警</summary>
        public event EventHandler<string> QrTimeoutWarning;

        /// <summary>实时日志事件（推送到 UI 终端）</summary>
        public event EventHandler<string> LogMessageEvent;

        // ---- 配置参数 ----
        /// <summary>QR 中心 Y 往下的固定偏移行数（自动换算后）</summary>
        public int QrOffsetRows => _qrOffsetRows;

        /// <summary>重叠检测区域行数（自动换算后）</summary>
        public int OverlapRows => _overlapRows;

        private int _downscaleFactor = 1;
        private int _qrOffsetRows;
        private int _overlapRows;
        private int _maxFramesWithoutQr = 10;

        // ---- 状态机 ----
        private enum State { Scanning, Collecting }
        private State _state = State.Scanning;

        // ---- 上一帧尾部缓存 ----
        private byte[] _prevTail;
        private int _prevTailRows;
        // 单线程状态机同步复用跨帧组合缓冲区，避免每个“当前帧未命中”场景都在 LOH 上申请大数组。
        private byte[] _overlapBuffer;

        // ---- 全局行坐标追踪（用于防止二维码重复检测） ----
        // 坐标单位始终是 DownscaleFactor 处理后的行；写日志/映射原图时再统一换算，避免混用尺度。
        private long _globalProcessedRows = 0;
        private long _lastQrGlobalY = -999999;
        
        // ---- 异常监控 ----
        private int _framesSinceLastQr = 0;

        // ---- 延迟切割（当切割点超出当前帧时暂存） ----
        private bool _hasDeferredCut = false;
        private int _deferredCutRemaining = 0;
        private QrDetectionResult _deferredQrResult;
        private long _deferredQrGlobalY = long.MinValue;

        // ---- 段累积（按需增长，完成后释放） ----
        private readonly List<SegmentChunk> _segChunks = new List<SegmentChunk>();
        private long _segTotalRows;
        private string _segStartQrText;
        private long _segStartGlobalY = long.MinValue;

        // ---- 图像参数 ----
        private int _width, _height, _stride, _bpp;

        // ---- QR 检测器 ----
        private readonly QrCodeDetector _qrDetector = new QrCodeDetector();

        /// <summary>在开始采集前加载 WeChatQRCode 模型，避免阻塞首帧处理。</summary>
        public bool InitializeQrDetector(out string error)
        {
            bool initialized = _qrDetector.Initialize();
            error = _qrDetector.LastError;
            return initialized;
        }

        /// <summary>
        /// 设置帧几何和本轮二维码/拼接配置。所有值复制到内部字段，
        /// 单线程状态机运行期间不再读取可变的全局配置。
        /// </summary>
        public void Configure(
            int width,
            int height,
            int stride,
            int bitsPerPixel,
            AppConfig configSnapshot)
        {
            if (configSnapshot == null)
                throw new ArgumentNullException(nameof(configSnapshot));

            _width = width;
            _height = height;
            _stride = stride;
            _bpp = bitsPerPixel;
            _downscaleFactor = Math.Max(1, configSnapshot.DownscaleFactor);
            _qrOffsetRows = configSnapshot.BaseQrOffsetRows / _downscaleFactor;
            _overlapRows = configSnapshot.BaseOverlapRows / _downscaleFactor;
            _maxFramesWithoutQr = configSnapshot.MaxFramesWithoutQr;
            _qrDetector.Configure(configSnapshot);
        }

        /// <summary>重置状态机（新一轮采集前调用）</summary>
        public void Reset()
        {
            _state = State.Scanning;
            _prevTail = null;
            _prevTailRows = 0;
            _overlapBuffer = null;
            _segStartQrText = null;
            _segStartGlobalY = long.MinValue;
            _segTotalRows = 0;
            _globalProcessedRows = 0;
            _lastQrGlobalY = -999999;
            _framesSinceLastQr = 0;
            _hasDeferredCut = false;
            _deferredCutRemaining = 0;
            _deferredQrResult = null;
            _deferredQrGlobalY = long.MinValue;
            ClearChunks();
        }

        /// <summary>
        /// 处理由调用方独占持有的托管帧缓冲区。
        /// 本方法在返回前不会修改或保留该缓冲区；需要跨帧保存的数据仍会复制到内部块中。
        /// </summary>
        public void ProcessOwnedFrame(byte[] dataArray, int width, int height, int stride, int bpp)
        {
            if (dataArray == null)
                throw new ArgumentNullException(nameof(dataArray));
            if (width <= 0 || height <= 0 || stride <= 0)
                throw new ArgumentException("帧尺寸或步长无效。", nameof(dataArray));

            int requiredBytes = checked(stride * height);
            if (dataArray.Length < requiredBytes)
                throw new ArgumentException("托管帧缓冲区长度不足。", nameof(dataArray));

            ProcessFrameCore(dataArray, width, height, stride, bpp);
        }

        /// <summary>
        /// 单线程执行一帧的二维码检测与状态机推进。当前帧缓冲区只在本调用栈内读取；
        /// 任何要跨帧保留的尾部和段数据都会由本类重新分配。
        /// </summary>
        private void ProcessFrameCore(
            byte[] dataArray,
            int width,
            int height,
            int stride,
            int bpp)
        {
            if (_width != width || _height != height || _stride != stride || _bpp != bpp)
            {
                // 保留原有的防御性几何同步；仅更新帧尺寸，不改变本轮固定的二维码/拼接参数。
                _width = width;
                _height = height;
                _stride = stride;
                _bpp = bpp;
            }

            byte[] frameData = dataArray;

            // ======== 双重 QR 检测 ========
            QrDetectionResult qrResult = null;
            long qrGlobalY = long.MinValue;
            int cutRowInCurr = -1;       // 切割点在当前帧的行号
            bool cutInPrevTail = false;  // 切割点是否落在上一帧的尾部
            int rowsToDiscardFromLastChunk = 0; // 段结束时，需要从上一帧退回的行数
            int rowsToKeepFromPrevTail = 0;     // 段开始时，需要从上一帧抢捞的行数
            bool skipDetection = false;  // 延迟切割命中时跳过本帧检测

            int downscaleFactor = _downscaleFactor;

            Log($"[ProcessFrame] Start processing frame, _globalProcessedRows={_globalProcessedRows * downscaleFactor} (Original)");

            // ======== 延迟切割执行（上一帧遗留） ========
            if (_hasDeferredCut)
            {
                if (_deferredCutRemaining <= height)
                {
                    // 延迟切割点落在当前帧内，执行切割
                    Log($"  [Deferred] Executing deferred cut at row {_deferredCutRemaining * downscaleFactor} (Original) in current frame");
                    qrResult = _deferredQrResult;
                    qrGlobalY = _deferredQrGlobalY;
                    cutRowInCurr = _deferredCutRemaining;
                    _hasDeferredCut = false;
                    _deferredQrResult = null;
                    _deferredQrGlobalY = long.MinValue;
                    skipDetection = true;
                }
                else
                {
                    // 切割点仍然超出当前帧（极端情况），继续延迟，整帧入队
                    Log($"  [Deferred] Still exceeds frame! remaining={_deferredCutRemaining * downscaleFactor} > height={height * downscaleFactor} (Original). Continuing deferral.");
                    _deferredCutRemaining -= height;
                    // 根据状态机，如果在 Collecting 状态就把整帧数据入队
                    if (_state == State.Collecting)
                    {
                        AddChunk(frameData, 0, height);
                    }
                    SaveTail(frameData, height);
                    _globalProcessedRows += height;
                    return; // 提前退出，等待下一帧
                }
            }

            // 检测1: 当前帧整体（优先检测，覆盖 QR 完全在帧内部的常见情况）
            if (!skipDetection)
            {
                var currentDetectWatch = Stopwatch.StartNew();
                var fResult = _qrDetector.Detect(frameData, _width, height, _stride, _bpp);
                currentDetectWatch.Stop();
                string currentQrDiagnostic = fResult.Found ? _qrDetector.LastDecodeStrategy : _qrDetector.LastError;
                Log($"  [Current] Detect: Found={fResult.Found}, Cost={currentDetectWatch.Elapsed.TotalMilliseconds:F1}ms, Attempts={_qrDetector.LastDecodeAttemptCount}, X={fResult.CenterX * downscaleFactor}, Y={fResult.CenterY * downscaleFactor} (Original), Width={fResult.PixelWidth * downscaleFactor:F1}, Height={fResult.PixelHeight * downscaleFactor:F1} (Original), Text={fResult.DecodedText}, Diagnostic={currentQrDiagnostic}");
                if (fResult.Found)
                {
                    long globalY = _globalProcessedRows + fResult.CenterY;
                    // 同一个二维码可能在当前帧与重叠图中重复命中，全局 Y 间隔用于去重。
                    if (globalY - _lastQrGlobalY > 1000) // 防重复检测
                    {
                        int cutRow = fResult.CenterY + QrOffsetRows;
                        
                        // 如果切割点超出了当前帧的高度，说明段的结尾在下一帧。
                        // 我们在这里拒绝采纳，它将在下一帧的重叠区域被 Detection 2 捕获并切割。
                        if (cutRow <= height)
                        {
                            Log($"  [Current] Accepted! cutRow={cutRow * downscaleFactor} (Original)");
                            qrResult = fResult;
                            qrGlobalY = globalY;
                            _lastQrGlobalY = globalY;
                            cutRowInCurr = cutRow;
                        }
                        else
                        {
                            // 切割点超出当前帧，启动延迟切割
                            Log($"  [Current] Deferred! cutRow={cutRow * downscaleFactor} > height={height * downscaleFactor} (Original). Deferring to next frame.");
                            _hasDeferredCut = true;
                            _deferredCutRemaining = cutRow - height;
                            _deferredQrResult = fResult;
                            _deferredQrGlobalY = globalY;
                            _lastQrGlobalY = globalY;
                        }
                    }
                }
            }

            // 检测2: 重叠区域（当前帧未发现时，检测跨帧边界的 QR 码）
            if (!skipDetection && (qrResult == null || !qrResult.Found) && _prevTail != null && _prevTailRows > 0)
            {
                int currTopRows = Math.Min(OverlapRows, height);
                int overlapH = _prevTailRows + currTopRows;
                int overlapBytes = checked(_stride * overlapH);
                if (_overlapBuffer == null || _overlapBuffer.Length != overlapBytes)
                    _overlapBuffer = new byte[overlapBytes];

                Buffer.BlockCopy(_prevTail, 0, _overlapBuffer, 0, _stride * _prevTailRows);
                Buffer.BlockCopy(frameData, 0, _overlapBuffer, _stride * _prevTailRows, _stride * currTopRows);

                // 跨帧组合与普通单帧进入同一个检测器；形变增强由定位框几何证据统一触发。
                var overlapDetectWatch = Stopwatch.StartNew();
                var ovResult = _qrDetector.Detect(
                    _overlapBuffer,
                    _width,
                    overlapH,
                    _stride,
                    _bpp);
                overlapDetectWatch.Stop();
                string overlapQrDiagnostic = ovResult.Found ? _qrDetector.LastDecodeStrategy : _qrDetector.LastError;
                Log($"  [Overlap] Detect: Found={ovResult.Found}, Cost={overlapDetectWatch.Elapsed.TotalMilliseconds:F1}ms, Attempts={_qrDetector.LastDecodeAttemptCount}, X={ovResult.CenterX * downscaleFactor}, Y={ovResult.CenterY * downscaleFactor} (Original), Width={ovResult.PixelWidth * downscaleFactor:F1}, Height={ovResult.PixelHeight * downscaleFactor:F1} (Original), Text={ovResult.DecodedText}, Diagnostic={overlapQrDiagnostic}");
                if (ovResult.Found)
                {
                    long globalY = _globalProcessedRows - _prevTailRows + ovResult.CenterY;
                    if (globalY - _lastQrGlobalY > 1000) // 防重复检测
                    {
                        int cutRowInOverlap = ovResult.CenterY + QrOffsetRows;

                        // 确保切割点不会超出当前拼接的视野
                        if (cutRowInOverlap <= overlapH)
                        {
                            Log($"  [Overlap] Accepted! cutRowInOverlap={cutRowInOverlap * downscaleFactor} (Original)");
                            qrResult = ovResult;
                            qrGlobalY = globalY;
                            _lastQrGlobalY = globalY;

                            if (cutRowInOverlap < _prevTailRows)
                            {
                                // 切割点物理上位于上一帧的尾部
                                cutInPrevTail = true;
                                rowsToDiscardFromLastChunk = _prevTailRows - cutRowInOverlap;
                                rowsToKeepFromPrevTail = _prevTailRows - cutRowInOverlap;
                            }
                            else
                            {
                                // 切割点物理上位于当前帧的头部
                                cutRowInCurr = cutRowInOverlap - _prevTailRows;
                            }
                        }
                        else
                        {
                            // 切割点超出重叠区域，启动延迟切割
                            Log($"  [Overlap] Deferred! cutRowInOverlap={cutRowInOverlap * downscaleFactor} > overlapH={overlapH * downscaleFactor} (Original). Deferring to next frame.");
                            _hasDeferredCut = true;
                            _deferredCutRemaining = cutRowInOverlap - overlapH;
                            _deferredQrResult = ovResult;
                            _deferredQrGlobalY = globalY;
                            _lastQrGlobalY = globalY;
                        }
                    }
                }
            }

            // 只有切割位置已经落入已拥有的像素范围才算 qrFound；仅识别但仍在延迟切割中的 QR 不推进状态机。
            bool qrFound = cutInPrevTail || (cutRowInCurr >= 0);

            // ---- 异常监控逻辑 ----
            if (qrFound)
            {
                _framesSinceLastQr = 0;
            }
            else
            {
                _framesSinceLastQr++;
                if (_framesSinceLastQr >= _maxFramesWithoutQr)
                {
                    int missingFrameCount = _framesSinceLastQr;
                    bool abandonedCollectingSegment = _state == State.Collecting;

                    // 已经找到首个二维码但迟迟找不到结束二维码时，不能只重置告警计数。
                    // 否则恢复采集后仍会沿用旧首码继续累积，最终可能生成高度异常的大图并耗尽内存。
                    // 这里立即丢弃当前未闭合段；下一次有效二维码将按 Scanning 状态重新作为首码。
                    if (abandonedCollectingSegment)
                        AbandonCurrentSegmentAfterQrTimeout();

                    string msg = abandonedCollectingSegment
                        ? $"连续 {missingFrameCount} 张图像未识别到二维码！当前未闭合拼接段已放弃，下一次识别到的二维码将作为新的起始二维码。"
                        : $"连续 {missingFrameCount} 张图像未识别到二维码！";
                    Log($"[WARNING] {msg}");
                    QrTimeoutWarning?.Invoke(this, msg);
                    _framesSinceLastQr = 0; // 避免重复弹窗
                }
            }

            // ======== 状态机驱动 ========
            switch (_state)
            {
                case State.Scanning:
                    if (qrFound)
                    {
                        _segStartQrText = qrResult.DecodedText;
                        _segStartGlobalY = qrGlobalY + QrOffsetRows;
                        _segTotalRows = 0;
                        ClearChunks();

                        if (cutInPrevTail)
                        {
                            // 从尾部捞回需要的像素作为段起始
                            byte[] keptTail = new byte[_stride * rowsToKeepFromPrevTail];
                            int startRowInTail = _prevTailRows - rowsToKeepFromPrevTail;
                            Buffer.BlockCopy(_prevTail, startRowInTail * _stride, keptTail, 0, _stride * rowsToKeepFromPrevTail);
                            _segChunks.Add(new SegmentChunk { Data = keptTail, Rows = rowsToKeepFromPrevTail });
                            _segTotalRows += rowsToKeepFromPrevTail;

                            // 加上整帧当前帧
                            AddChunk(frameData, 0, height);
                        }
                        else
                        {
                            int rows = height - cutRowInCurr;
                            if (rows > 0) AddChunk(frameData, cutRowInCurr, rows);
                        }
                        _state = State.Collecting;
                    }
                    break;

                case State.Collecting:
                    if (!qrFound)
                    {
                        AddChunk(frameData, 0, height);
                    }
                    else
                    {
                        // ---- 结束当前段 ----
                        if (cutInPrevTail)
                        {
                            // 上一帧已经入队，需回退多出的行数
                            TrimLastChunkTail(rowsToDiscardFromLastChunk);
                        }
                        else
                        {
                            if (cutRowInCurr > 0) AddChunk(frameData, 0, cutRowInCurr);
                        }

                        // 当前结束二维码同时作为下一段的起始二维码，连续材料段之间不会产生空洞。
                        EmitSegment(qrResult, qrGlobalY);

                        // ---- 开启新一段 ----
                        _segStartQrText = qrResult.DecodedText;
                        _segStartGlobalY = qrGlobalY + QrOffsetRows;
                        _segTotalRows = 0;

                        if (cutInPrevTail)
                        {
                            byte[] keptTail = new byte[_stride * rowsToKeepFromPrevTail];
                            int startRowInTail = _prevTailRows - rowsToKeepFromPrevTail;
                            Buffer.BlockCopy(_prevTail, startRowInTail * _stride, keptTail, 0, _stride * rowsToKeepFromPrevTail);
                            _segChunks.Add(new SegmentChunk { Data = keptTail, Rows = rowsToKeepFromPrevTail });
                            _segTotalRows += rowsToKeepFromPrevTail;

                            AddChunk(frameData, 0, height);
                        }
                        else
                        {
                            int rows = height - cutRowInCurr;
                            if (rows > 0) AddChunk(frameData, cutRowInCurr, rows);
                        }
                    }
                    break;
            }

            // ======== 保存当前帧尾部用于下一帧的重叠检测 ========
            SaveTail(frameData, height);
            
            _globalProcessedRows += height;
        }

        // ---- 辅助方法 ----

        /// <summary>
        /// 二维码等待超时后放弃当前未闭合段，并恢复为等待首码的状态。
        /// 保留 <see cref="_prevTail"/>，使告警恢复后的下一帧仍可检测跨帧二维码；
        /// 其余与旧首码有关的锚点、延迟切割和大块图像缓存均立即清除。
        /// </summary>
        private void AbandonCurrentSegmentAfterQrTimeout()
        {
            long discardedRows = _segTotalRows;
            int discardedChunks = _segChunks.Count;
            double discardedMegabytes = _stride > 0
                ? discardedRows * _stride / (1024d * 1024d)
                : 0d;
            string abandonedQrText = _segStartQrText;

            _state = State.Scanning;
            _segStartQrText = null;
            _segStartGlobalY = long.MinValue;

            // 极端配置下，二维码可能已被识别但切点仍在后续帧。
            // 既然当前段已经放弃，该延迟切割也必须一并作废，避免它在恢复后错误开启旧段。
            _hasDeferredCut = false;
            _deferredCutRemaining = 0;
            _deferredQrResult = null;
            _deferredQrGlobalY = long.MinValue;

            // 允许恢复后的下一个二维码无条件成为新的首码。
            _lastQrGlobalY = -999999;
            ClearChunks();

            Log(
                $"[ImageStitcher] QR wait timeout: abandoned unclosed segment. " +
                $"StartQr={abandonedQrText ?? "<unknown>"}, " +
                $"DiscardedRows={discardedRows}, Chunks={discardedChunks}, " +
                $"ApproxMemory={discardedMegabytes:F1} MB. State reset to Scanning.");
        }

        /// <summary>将帧的指定行范围作为 chunk 加入段累积</summary>
        private void AddChunk(byte[] src, int startRow, int rows)
        {
            byte[] chunkData = new byte[_stride * rows];
            Buffer.BlockCopy(src, startRow * _stride, chunkData, 0, _stride * rows);
            _segChunks.Add(new SegmentChunk { Data = chunkData, Rows = rows });
            _segTotalRows += rows;
        }

        /// <summary>回溯裁剪：跨帧重叠检测发现切点位于上一帧时，去掉最后一个 chunk 末尾的多余行。</summary>
        private void TrimLastChunkTail(int discardRows)
        {
            if (_segChunks.Count == 0 || discardRows <= 0) return;

            var last = _segChunks[_segChunks.Count - 1];
            int newRows = last.Rows - discardRows;

            if (newRows <= 0)
            {
                _segTotalRows -= last.Rows;
                _segChunks.RemoveAt(_segChunks.Count - 1);
            }
            else
            {
                _segTotalRows -= discardRows;
                // EmitSegment 只复制 Rows 指定的有效前缀，因此缩短逻辑长度即可；
                // 不再为回溯裁剪复制一次可能很大的末块。
                last.Rows = newRows;
            }
        }

        /// <summary>保存当前帧底部 OverlapRows 行作为下一帧的重叠检测区</summary>
        private void SaveTail(byte[] frameData, int frameHeight)
        {
            _prevTailRows = Math.Min(OverlapRows, frameHeight);
            int tailBytes = _stride * _prevTailRows;
            if (_prevTail == null || _prevTail.Length != tailBytes)
                _prevTail = new byte[tailBytes];

            int srcOffset = (frameHeight - _prevTailRows) * _stride;
            Buffer.BlockCopy(frameData, srcOffset, _prevTail, 0, tailBytes);
        }

        /// <summary>
        /// 将累积块按行顺序复制成连续缓冲区并触发事件；结果对象拥有该新缓冲区。
        /// 同时把结束二维码的全局坐标换算成拼接段局部坐标，供后续 Mark 对准使用。
        /// </summary>
        private void EmitSegment(QrDetectionResult endQrResult, long endQrGlobalY)
        {
            if (_segTotalRows <= 0 || _segChunks.Count == 0) return;

            if (endQrResult == null || endQrGlobalY == long.MinValue || _segStartGlobalY == long.MinValue)
            {
                LogMessageEvent?.Invoke(this, "[ImageStitcher] 拼接段缺少二维码全局坐标，已拒绝输出。");
                ClearChunks();
                return;
            }

            int totalHeight = (int)_segTotalRows;
            double endQrCenterY = endQrGlobalY - _segStartGlobalY;
            byte[] stitched = new byte[_stride * totalHeight];
            int offset = 0;

            foreach (var chunk in _segChunks)
            {
                int bytes = _stride * chunk.Rows;
                Buffer.BlockCopy(chunk.Data, 0, stitched, offset, bytes);
                offset += bytes;
            }

            // 立即释放 chunks 引用，减轻 GC 压力
            ClearChunks();

            StitchCompleted?.Invoke(this, new StitchedImageResult
            {
                Data = stitched,
                Width = _width,
                Height = totalHeight,
                Stride = _stride,
                BitsPerPixel = _bpp,
                StartQrText = _segStartQrText,
                EndQrText = endQrResult.DecodedText,
                SegmentStartGlobalY = _segStartGlobalY,
                EndQrGlobalY = endQrGlobalY,
                EndQrCenterX = endQrResult.CenterX,
                EndQrCenterY = endQrCenterY,
                EndQrPixelWidth = endQrResult.PixelWidth,
                EndQrPixelHeight = endQrResult.PixelHeight
            });
        }

        /// <summary>释放段块对大数组的引用并清零累计行数，便于 GC 尽快回收上一段。</summary>
        private void ClearChunks()
        {
            // List.Clear 会同时释放 SegmentChunk 及其 Data 引用，逐个置空不会更早回收，反而增加遍历开销。
            _segChunks.Clear();
            _segTotalRows = 0;
        }

        /// <summary>集中转发状态机诊断，避免每帧创建临时 Action 委托。</summary>
        private void Log(string message)
        {
            LogMessageEvent?.Invoke(this, message);
        }

        public void Dispose()
        {
            Reset();
            _qrDetector?.Dispose();
        }

        /// <summary>段累积数据块</summary>
        private class SegmentChunk
        {
            public byte[] Data { get; set; }
            public int Rows { get; set; }
        }
    }
}
