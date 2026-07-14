using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CIS_WebInspector.Models;

namespace CIS_WebInspector.Services
{
    public class DebugLogParser
    {
        /// <summary>
        /// 根据二维码文本在 Debug.log 中检索排版信息。
        /// </summary>
        /// <param name="logFilePath">Debug.log 的绝对路径</param>
        /// <param name="qrCodeText">拼接完成时识别到的结束二维码文本 (如 "ZJ_202606240060006_RT")</param>
        /// <param name="tiffImageDir">TIFF 原始排版图存放的基础目录</param>
        /// <returns>解析结果 LayoutInfo，未找到或失败返回 null</returns>
        public static LayoutInfo ParseForQrCode(string logFilePath, string qrCodeText, string tiffImageDir)
        {
            if (string.IsNullOrWhiteSpace(logFilePath) || !File.Exists(logFilePath))
            {
                return null;
            }

            string keyword1 = qrCodeText;
            string keyword2 = "formattingFilename";
            
            string lastMatchLine = null;
            
            // 逐行读取，寻找同时包含二维码和 formattingFilename 的最后一行
            using (var reader = new StreamReader(logFilePath))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Contains(keyword1) && line.Contains(keyword2))
                    {
                        lastMatchLine = line;
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(lastMatchLine))
            {
                return null;
            }

            // 提取 "[" 以后的 JSON 字符串
            int pos = lastMatchLine.IndexOf('[');
            if (pos < 0)
            {
                return null;
            }

            string jsonStr = lastMatchLine.Substring(pos);

            try
            {
                // 解析 JSON
                using (var document = JsonDocument.Parse(jsonStr))
                {
                    var root = document.RootElement;
                    if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                    {
                        var firstItem = root[0];
                        if (firstItem.TryGetProperty("cuttingInput", out var cuttingInput))
                        {
                            var layoutInfo = new LayoutInfo();

                            // 1. 获取文件名
                            if (cuttingInput.TryGetProperty("formattingFilename", out var formatFileElem))
                            {
                                string formatFileName = formatFileElem.GetString();
                                layoutInfo.TiffFileName = GetBaseFilename(formatFileName);
                                layoutInfo.TiffFullPath = Path.Combine(tiffImageDir, layoutInfo.TiffFileName);
                            }

                            // 2. 获取排版坐标
                            if (cuttingInput.TryGetProperty("sourceFileLocation", out var sourceFileLocation) && 
                                sourceFileLocation.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var locElem in sourceFileLocation.EnumerateArray())
                                {
                                    var part = new PartLocation();
                                    if (locElem.TryGetProperty("hotInkTaskID", out var idElem))
                                        part.HotInkTaskID = idElem.GetString();
                                    
                                    if (locElem.TryGetProperty("relativeCenterX", out var cxElem))
                                        part.RelativeCenterX = cxElem.GetDouble();
                                        
                                    if (locElem.TryGetProperty("relativeCenterY", out var cyElem))
                                        part.RelativeCenterY = cyElem.GetDouble();
                                        
                                    if (locElem.TryGetProperty("relativeTopLeftX", out var tlxElem))
                                        part.RelativeTopLeftX = tlxElem.GetDouble();
                                        
                                    if (locElem.TryGetProperty("relativeTopLeftY", out var tlyElem))
                                        part.RelativeTopLeftY = tlyElem.GetDouble();
                                        
                                    if (locElem.TryGetProperty("relativeBottomRightX", out var brxElem))
                                        part.RelativeBottomRightX = brxElem.GetDouble();
                                        
                                    if (locElem.TryGetProperty("relativeBottomRightY", out var bryElem))
                                        part.RelativeBottomRightY = bryElem.GetDouble();
                                        
                                    layoutInfo.Parts.Add(part);
                                }
                            }
                            
                            return layoutInfo;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DebugLogParser] 解析 JSON 异常: {ex.Message}");
            }

            return null;
        }

        private static string GetBaseFilename(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return string.Empty;
            int lastSlash = fullPath.LastIndexOfAny(new char[] { '\\', '/' });
            if (lastSlash >= 0 && lastSlash < fullPath.Length - 1)
            {
                return fullPath.Substring(lastSlash + 1);
            }
            return fullPath;
        }
    }
}
