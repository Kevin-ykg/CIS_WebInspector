using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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
    ///   - 每帧到达时，先构造重叠图像（上一帧尾部 + 当前帧头部）进行 QR 检测
    ///   - 再对当前帧整体进行 QR 检测（覆盖 QR 完全在帧内部的情况）
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
        public int QrOffsetRows => ConfigManager.Config.BaseQrOffsetRows / ConfigManager.Config.DownscaleFactor;

        /// <summary>重叠检测区域行数（自动换算后）</summary>
        public int OverlapRows => ConfigManager.Config.BaseOverlapRows / ConfigManager.Config.DownscaleFactor;

        // ---- 状态机 ----
        private enum State { Scanning, Collecting }
        private State _state = State.Scanning;

        // ---- 上一帧尾部缓存 ----
        private byte[] _prevTail;
        private int _prevTailRows;

        // ---- 全局行坐标追踪（用于防止二维码重复检测） ----
        private long _globalProcessedRows = 0;
        private long _lastQrGlobalY = -999999;
        
        // ---- 异常监控 ----
        private int _framesSinceLastQr = 0;

        // ---- 延迟切割（当切割点超出当前帧时暂存） ----
        private bool _hasDeferredCut = false;
        private int _deferredCutRemaining = 0;
        private QrDetectionResult _deferredQrResult;

        // ---- 段累积（按需增长，完成后释放） ----
        private readonly List<SegmentChunk> _segChunks = new List<SegmentChunk>();
        private long _segTotalRows;
        private string _segStartQrText;

        // ---- 图像参数 ----
        private int _width, _height, _stride, _bpp;

        // ---- QR 检测器 ----
        private readonly QrCodeDetector _qrDetector = new QrCodeDetector();

        /// <summary>设置图像参数</summary>
        public void Configure(int width, int height, int stride, int bitsPerPixel)
        {
            _width = width;
            _height = height;
            _stride = stride;
            _bpp = bitsPerPixel;
        }

        /// <summary>重置状态机（新一轮采集前调用）</summary>
        public void Reset()
        {
            _state = State.Scanning;
            _prevTail = null;
            _prevTailRows = 0;
            _segStartQrText = null;
            _segTotalRows = 0;
            _globalProcessedRows = 0;
            _lastQrGlobalY = -999999;
            _framesSinceLastQr = 0;
            _hasDeferredCut = false;
            _deferredCutRemaining = 0;
            _deferredQrResult = null;
            ClearChunks();
        }

        /// <summary>
        /// 处理一帧新到达的图像。
        /// </summary>
        public void ProcessFrame(IntPtr dataPtr, byte[] dataArray, int width, int height, int stride, int bpp)
        {
            if (_width != width || _height != height)
                Configure(width, height, stride, bpp);

            // 将帧数据拷贝到托管内存
            int totalBytes = stride * height;
            byte[] frameData = new byte[totalBytes];
            if (dataArray != null)
                Buffer.BlockCopy(dataArray, 0, frameData, 0, Math.Min(dataArray.Length, totalBytes));
            else if (dataPtr != IntPtr.Zero)
                Marshal.Copy(dataPtr, frameData, 0, totalBytes);
            else
                return;

            // ======== 双重 QR 检测 ========
            QrDetectionResult qrResult = null;
            int cutRowInCurr = -1;       // 切割点在当前帧的行号
            bool cutInPrevTail = false;  // 切割点是否落在上一帧的尾部
            int rowsToDiscardFromLastChunk = 0; // 段结束时，需要从上一帧退回的行数
            int rowsToKeepFromPrevTail = 0;     // 段开始时，需要从上一帧抢捞的行数
            bool skipDetection = false;  // 延迟切割命中时跳过本帧检测

            string logDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "日志");
            if (!System.IO.Directory.Exists(logDir))
                System.IO.Directory.CreateDirectory(logDir);
            
            string timeStamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string dateStr = DateTime.Now.ToString("yyyyMMdd");
            string dbgLog = System.IO.Path.Combine(logDir, $"SysRunLog_{dateStr}.txt");
            
            int df = ConfigManager.Config.DownscaleFactor;

            Action<string> logAction = (msg) =>
            {
                string fullMsg = $"[{timeStamp}] {msg}";
                System.IO.File.AppendAllText(dbgLog, fullMsg + "\n");
                LogMessageEvent?.Invoke(this, msg);
            };

            logAction($"[ProcessFrame] Start processing frame, _globalProcessedRows={_globalProcessedRows * df} (Original)");

            // ======== 延迟切割执行（上一帧遗留） ========
            if (_hasDeferredCut)
            {
                if (_deferredCutRemaining <= height)
                {
                    // 延迟切割点落在当前帧内，执行切割
                    logAction($"  [Deferred] Executing deferred cut at row {_deferredCutRemaining * df} (Original) in current frame");
                    qrResult = _deferredQrResult;
                    cutRowInCurr = _deferredCutRemaining;
                    _hasDeferredCut = false;
                    _deferredQrResult = null;
                    skipDetection = true;
                }
                else
                {
                    // 切割点仍然超出当前帧（极端情况），继续延迟，整帧入队
                    logAction($"  [Deferred] Still exceeds frame! remaining={_deferredCutRemaining * df} > height={height * df} (Original). Continuing deferral.");
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
                var fResult = _qrDetector.Detect(frameData, _width, height, _stride, _bpp);
                logAction($"  [Current] Detect: Found={fResult.Found}, Y={fResult.CenterY * df} (Original), Text={fResult.DecodedText}");
                if (fResult.Found)
                {
                    long globalY = _globalProcessedRows + fResult.CenterY;
                    if (globalY - _lastQrGlobalY > 1000) // 防重复检测
                    {
                        int cutRow = fResult.CenterY + QrOffsetRows;
                        
                        // 如果切割点超出了当前帧的高度，说明段的结尾在下一帧。
                        // 我们在这里拒绝采纳，它将在下一帧的重叠区域被 Detection 2 捕获并切割。
                        if (cutRow <= height)
                        {
                            logAction($"  [Current] Accepted! cutRow={cutRow * df} (Original)");
                            qrResult = fResult;
                            _lastQrGlobalY = globalY;
                            cutRowInCurr = cutRow;
                        }
                        else
                        {
                            // 切割点超出当前帧，启动延迟切割
                            logAction($"  [Current] Deferred! cutRow={cutRow * df} > height={height * df} (Original). Deferring to next frame.");
                            _hasDeferredCut = true;
                            _deferredCutRemaining = cutRow - height;
                            _deferredQrResult = fResult;
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
                byte[] overlapImg = new byte[_stride * overlapH];

                Buffer.BlockCopy(_prevTail, 0, overlapImg, 0, _stride * _prevTailRows);
                Buffer.BlockCopy(frameData, 0, overlapImg, _stride * _prevTailRows, _stride * currTopRows);

                var ovResult = _qrDetector.Detect(overlapImg, _width, overlapH, _stride, _bpp);
                logAction($"  [Overlap] Detect: Found={ovResult.Found}, Y={ovResult.CenterY * df} (Original), Text={ovResult.DecodedText}");
                if (ovResult.Found)
                {
                    long globalY = _globalProcessedRows - _prevTailRows + ovResult.CenterY;
                    if (globalY - _lastQrGlobalY > 1000) // 防重复检测
                    {
                        int cutRowInOverlap = ovResult.CenterY + QrOffsetRows;

                        // 确保切割点不会超出当前拼接的视野
                        if (cutRowInOverlap <= overlapH)
                        {
                            logAction($"  [Overlap] Accepted! cutRowInOverlap={cutRowInOverlap * df} (Original)");
                            qrResult = ovResult;
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
                            logAction($"  [Overlap] Deferred! cutRowInOverlap={cutRowInOverlap * df} > overlapH={overlapH * df} (Original). Deferring to next frame.");
                            _hasDeferredCut = true;
                            _deferredCutRemaining = cutRowInOverlap - overlapH;
                            _deferredQrResult = ovResult;
                            _lastQrGlobalY = globalY;
                        }
                    }
                }
            }

            bool qrFound = cutInPrevTail || (cutRowInCurr >= 0);

            // ---- 异常监控逻辑 ----
            if (qrFound)
            {
                _framesSinceLastQr = 0;
            }
            else
            {
                _framesSinceLastQr++;
                if (_framesSinceLastQr >= ConfigManager.Config.MaxFramesWithoutQr)
                {
                    string msg = $"连续 {_framesSinceLastQr} 张图像未识别到二维码！";
                    logAction($"[WARNING] {msg}");
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

                        EmitSegment(qrResult.DecodedText);

                        // ---- 开启新一段 ----
                        _segStartQrText = qrResult.DecodedText;
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

        /// <summary>将帧的指定行范围作为 chunk 加入段累积</summary>
        private void AddChunk(byte[] src, int startRow, int rows)
        {
            byte[] chunkData = new byte[_stride * rows];
            Buffer.BlockCopy(src, startRow * _stride, chunkData, 0, _stride * rows);
            _segChunks.Add(new SegmentChunk { Data = chunkData, Rows = rows });
            _segTotalRows += rows;
        }

        /// <summary>回溯裁剪：去掉最后一个 chunk 末尾的多余行</summary>
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
                byte[] trimmed = new byte[_stride * newRows];
                Buffer.BlockCopy(last.Data, 0, trimmed, 0, _stride * newRows);
                last.Data = trimmed;
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

        /// <summary>将累积的段数据组装为最终图像并触发事件</summary>
        private void EmitSegment(string endQrText)
        {
            if (_segTotalRows <= 0 || _segChunks.Count == 0) return;

            int totalHeight = (int)_segTotalRows;
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
                EndQrText = endQrText
            });
        }

        private void ClearChunks()
        {
            foreach (var c in _segChunks)
                c.Data = null;
            _segChunks.Clear();
            _segTotalRows = 0;
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
